import sys
import os
import time
import threading
import queue
import traceback
import wave
import signal
from math import gcd, log10
from pathlib import Path
from datetime import datetime
from contextlib import contextmanager

import numpy as np
from scipy.io.wavfile import write as write_wav
from scipy.signal import resample_poly

import tkinter as tk
from tkinter import ttk, filedialog, messagebox

# ---- PyAudio (WASAPI) ----
try:
    import pyaudiowpatch as pyaudio  # preferred (supports as_loopback on Win)
except Exception:
    import pyaudio  # fallback; may NOT support as_loopback


# ================== Global config ==================
TARGET_SR = 48000           # always save at 48k
BLOCK = 1024                # frames per read
TARGET_PEAK = 0.98          # soft limiter trims only if > TARGET_PEAK (never boosts)
MIN_ACTIVE_PEAK = 1e-4

# try 48k first, then common fallbacks
SR_CANDIDATES = [48000, 44100, 32000, 24000]

SAVE_STEMS = False  # set True to write mic/loop/mix stems to ~/Recordings/_stems

# Slider ranges
GAIN_MIN = 0.0
GAIN_MAX = 3.0
GAIN_DEFAULT = 1.0


# ================== Helpers ==================
def as_2d(x: np.ndarray) -> np.ndarray:
    if x.ndim == 1:
        return x.reshape(-1, 1)
    return x

def match_channels(a: np.ndarray, b: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    a2, b2 = as_2d(a), as_2d(b)
    c = min(a2.shape[1], b2.shape[1])
    return a2[:, :c], b2[:, :c]

def to_int16(x: np.ndarray) -> np.ndarray:
    return (np.clip(x, -1.0, 1.0) * 32767).astype(np.int16)

def resample_to(x: np.ndarray, src_sr: int, dst_sr: int) -> np.ndarray:
    if src_sr == dst_sr or x.size == 0:
        return x.astype(np.float32, copy=False)
    x2d = as_2d(x).astype(np.float32, copy=False)
    g = gcd(src_sr, dst_sr)
    up, down = dst_sr // g, src_sr // g
    y = np.column_stack([resample_poly(x2d[:, c], up, down) for c in range(x2d.shape[1])])
    return y if x.ndim == 2 else y[:, 0]

def float_from_int16_bytes(b: bytes, channels: int) -> np.ndarray:
    arr = np.frombuffer(b, dtype=np.int16)
    if channels > 1:
        arr = arr.reshape(-1, channels)
    return (arr.astype(np.float32) / 32767.0)

def fmt_dbfs(peak_lin: float) -> float:
    return -60.0 if peak_lin <= 1e-6 else max(-60.0, 20.0 * np.log10(peak_lin))

def fmt_dev_line(d: dict) -> str:
    return (f"[{d['index']:>3}] {d['name']} | hostApi={d['hostApi']} "
            f"in={d['maxInputChannels']} out={d['maxOutputChannels']} "
            f"sr={int(d['defaultSampleRate'])} "
            f"{'LOOPBACK' if d.get('isLoopbackDevice', False) else ''}")


# ================== Device listing (PyAudio) ==================
def list_devices():
    """
    Return three dicts:
      inputs   : all input-capable devices (not loopback)
      outputs  : render devices (output-capable, not loopback)
      loopbacks: devices flagged isLoopbackDevice=True (use these to capture system audio)
    Prefer WASAPI devices when available.
    """
    p = pyaudio.PyAudio()
    inputs, outputs, loopbacks = {}, {}, {}
    try:
        try:
            wasapi_idx = p.get_host_api_info_by_type(pyaudio.paWASAPI)["index"]
        except Exception:
            wasapi_idx = None

        for i in range(p.get_device_count()):
            d = p.get_device_info_by_index(i)
            info = {
                "index": d["index"],
                "name": d["name"],
                "hostApi": d["hostApi"],
                "maxInputChannels": d["maxInputChannels"],
                "maxOutputChannels": d["maxOutputChannels"],
                "defaultSampleRate": int(d["defaultSampleRate"]),
                "isLoopbackDevice": d.get("isLoopbackDevice", False)
            }

            # Prefer WASAPI for outputs/loopbacks; always collect inputs
            if wasapi_idx is not None and d["hostApi"] != wasapi_idx:
                if d["maxInputChannels"] > 0 and not info["isLoopbackDevice"]:
                    inputs[info["name"]] = info
                continue

            if d["maxInputChannels"] > 0 and not info["isLoopbackDevice"]:
                inputs[info["name"]] = info
            if d["maxOutputChannels"] > 0 and not info["isLoopbackDevice"]:
                outputs[info["name"]] = info
            if info["isLoopbackDevice"]:
                loopbacks[info["name"]] = info
    finally:
        p.terminate()
    return inputs, outputs, loopbacks


# ================== Controller (PyAudio) ==================
class RecordingController(threading.Thread):
    """
    Captures:
      - Mic (input device) via PyAudio
      - Loopback (WASAPI loopback device you select explicitly, or auto-match)
    Live gain: mic_gain and loop_gain can be changed while recording via SET_GAINS
    Compatibility: If PyAudio build doesn't support 'as_loopback', retry without it.
    """
    def __init__(self, command_queue, status_queue):
        super().__init__(daemon=True)
        self.command_queue = command_queue
        self.status_queue = status_queue
        self.is_recording = False

        self.include_mic = True
        self.raw_dump = False

        self.p = None
        self.mic_stream = None
        self.spk_stream = None

        self.mic_queue = queue.Queue()
        self.loop_queue = queue.Queue()

        self.capture_sr_mic = TARGET_SR
        self.capture_sr_spk = TARGET_SR

        # live gains
        self.mic_gain = GAIN_DEFAULT
        self.loop_gain = GAIN_DEFAULT

        # raw dump
        self.raw_wave = None
        self.raw_frames_written = 0
        self.raw_path = None

    # ---------- PyAudio helpers ----------
    def _ensure_pyaudio(self):
        if self.p is None:
            self.p = pyaudio.PyAudio()

    def _find_supported(self, device_index, suggested_sr, channels, is_input):
        """Probe for a workable samplerate/channels."""
        self._ensure_pyaudio()
        for sr_try in [suggested_sr] + SR_CANDIDATES:
            for ch_try in [channels, 2, 1]:
                try:
                    self.p.is_format_supported(
                        sr_try,
                        input_device=device_index if is_input else None,
                        output_device=device_index if not is_input else None,
                        input_channels=ch_try if is_input else None,
                        output_channels=ch_try if not is_input else None,
                        input_format=pyaudio.paInt16 if is_input else None,
                        output_format=pyaudio.paInt16 if not is_input else None
                    )
                    return True, ch_try, sr_try
                except ValueError:
                    pass
        return False, 0, suggested_sr

    def _open_mic(self, mic_dev):
        mic_idx = mic_dev['index']
        channels = max(1, min(mic_dev['maxInputChannels'], 2))
        sr = int(mic_dev['defaultSampleRate'])
        ok, ch, sr = self._find_supported(mic_idx, sr, channels, is_input=True)
        if not ok:
            raise RuntimeError(f"Mic format not supported: {mic_dev['name']}")
        self.capture_sr_mic = sr
        self.mic_stream = self.p.open(
            format=pyaudio.paInt16,
            channels=ch,
            rate=sr,
            input=True,
            input_device_index=mic_idx,
            frames_per_buffer=BLOCK
        )
        return ch, sr

    def _open_loopback_by_index(self, lb_dev):
        """Open a loopback device, compatible with PyAudio builds lacking 'as_loopback'."""
        lb_idx = lb_dev['index']
        channels = max(1, min(lb_dev['maxInputChannels'], 2)) or 2
        sr = int(lb_dev['defaultSampleRate'])
        ok, ch, sr = self._find_supported(lb_idx, sr, channels, is_input=True)
        if not ok:
            raise RuntimeError(f"Loopback format not supported: {lb_dev['name']}")

        self.capture_sr_spk = sr
        last_err = None
        for _ in range(5):
            try:
                self.spk_stream = self.p.open(
                    format=pyaudio.paInt16,
                    channels=ch,
                    rate=sr,
                    input=True,
                    input_device_index=lb_idx,
                    frames_per_buffer=BLOCK,
                    as_loopback=True  # may fail on some builds
                )
                return ch, sr
            except TypeError:
                try:
                    self.spk_stream = self.p.open(
                        format=pyaudio.paInt16,
                        channels=ch,
                        rate=sr,
                        input=True,
                        input_device_index=lb_idx,
                        frames_per_buffer=BLOCK
                    )
                    return ch, sr
                except Exception as e2:
                    last_err = e2
                    time.sleep(0.4)
            except Exception as e:
                last_err = e
                time.sleep(0.4)
        raise RuntimeError(
            f"Cannot open loopback device [{lb_idx}] {lb_dev['name']}.\n"
            f"Last error: {last_err}\n"
            f"Tip: Pick an entry under 'Loopback device (what to capture)'. "
            f"If you only have outputs and no loopback entries, ensure your driver exposes WASAPI loopback "
            f"or install pyaudiowpatch."
        )

    def _match_loopback(self, spk_dev, loopbacks: dict):
        base = spk_dev['name'] if spk_dev else ""
        for name, d in loopbacks.items():
            if base and name.startswith(base):
                return d
        for name, d in loopbacks.items():
            if base and (base in name or name in base):
                return d
        return None

    # ---------- Thread lifecycle ----------
    def run(self):
        while True:
            cmd, args = self.command_queue.get()
            if cmd == "START":
                self.start_recording(**args)
            elif cmd == "STOP":
                self.stop_recording()
            elif cmd == "EXIT":
                if self.is_recording:
                    self.stop_recording()
                self._teardown()
                break
            elif cmd == "SET_GAINS":
                mg = float(args.get("mic_gain", self.mic_gain))
                lg = float(args.get("loop_gain", self.loop_gain))
                self.mic_gain = max(GAIN_MIN, min(GAIN_MAX, mg))
                self.loop_gain = max(GAIN_MIN, min(GAIN_MAX, lg))
                # lightweight status update
                self.status_queue.put(("STATUS",
                    f"Recording… (mic {self.mic_gain:.2f}×, out {self.loop_gain:.2f}×)"))

    def _teardown(self):
        try:
            if self.mic_stream:
                if self.mic_stream.is_active(): self.mic_stream.stop_stream()
                self.mic_stream.close()
        except Exception:
            pass
        try:
            if self.spk_stream:
                if self.spk_stream.is_active(): self.spk_stream.stop_stream()
                self.spk_stream.close()
        except Exception:
            pass
        if self.p:
            try: self.p.terminate()
            except Exception: pass
        self.p = None
        self.mic_stream = None
        self.spk_stream = None

    def start_recording(self, mic_device=None, spk_device=None, loopback_device=None,
                        include_mic=True, raw_dump=False, mic_gain=GAIN_DEFAULT, loop_gain=GAIN_DEFAULT):
        self._ensure_pyaudio()

        if spk_device is None and loopback_device is None:
            self.status_queue.put(("ERROR", "Select an output or a loopback device."))
            self.status_queue.put(("STATUS", "Ready to record"))
            return
        if include_mic and mic_device is None:
            self.status_queue.put(("ERROR", "Include microphone is enabled but no mic selected."))
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        self.include_mic = bool(include_mic and not raw_dump)  # force mic off in raw mode
        self.raw_dump = bool(raw_dump)
        self.mic_gain = float(mic_gain)
        self.loop_gain = float(loop_gain)

        # Open loopback
        try:
            if loopback_device is not None:
                lb_ch, lb_sr = self._open_loopback_by_index(loopback_device)
                chosen_lb_name = loopback_device['name']
            else:
                _, _, loopbacks = list_devices()
                if not loopbacks:
                    raise RuntimeError("No WASAPI loopback devices found. "
                                       "Pick a device under 'Loopback device' after clicking Refresh.")
                lb_choice = self._match_loopback(spk_device, loopbacks) or next(iter(loopbacks.values()))
                lb_ch, lb_sr = self._open_loopback_by_index(lb_choice)
                chosen_lb_name = lb_choice['name']
        except Exception as e:
            self.status_queue.put(("ERROR", f"Loopback error: {e}"))
            self._teardown()
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        if self.include_mic:
            try:
                self._open_mic(mic_device)
            except Exception as e:
                self.status_queue.put(("ERROR", f"Mic error: {e}"))
                self._teardown()
                self.status_queue.put(("STATUS", "Ready to record"))
                return

        if self.raw_dump:
            dump_dir = Path.home() / "Music" / "Recordings"
            dump_dir.mkdir(parents=True, exist_ok=True)
            ts = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
            self.raw_path = str(dump_dir / f"raw_loopback_{ts}_{lb_sr}Hz.wav")
            try:
                self.raw_wave = wave.open(self.raw_path, "wb")
                self.raw_wave.setnchannels(lb_ch)
                self.raw_wave.setsampwidth(2)
                self.raw_wave.setframerate(lb_sr)
                self.raw_frames_written = 0
                self.status_queue.put(("INFO", f"Raw dump → {self.raw_path}"))
            except Exception as e:
                self.status_queue.put(("ERROR", f"Cannot open raw WAV: {e}"))
                self._teardown()
                self.status_queue.put(("STATUS", "Ready to record"))
                return

        self.is_recording = True
        mic_note = " + Mic" if self.include_mic else ""
        self.status_queue.put(("STATUS", f"Recording… (Loopback {lb_sr/1000:.1f} kHz{mic_note}, "
                                         f"mic {self.mic_gain:.2f}×, out {self.loop_gain:.2f}×)\n"
                                         f"Loopback device: {chosen_lb_name}"))

        # launch readers
        self.loop_thread = threading.Thread(target=self._read_loopback, daemon=True)
        self.loop_thread.start()
        if self.include_mic:
            self.mic_thread = threading.Thread(target=self._read_mic, daemon=True)
            self.mic_thread.start()
        else:
            self.mic_thread = None

    def stop_recording(self):
        if not self.is_recording:
            return
        self.is_recording = False
        self.status_queue.put(("STATUS", "Processing audio…"))

        if self.mic_thread: self.mic_thread.join()
        if self.loop_thread: self.loop_thread.join()

        if self.raw_dump:
            try:
                if self.raw_wave:
                    self.raw_wave.close()
                    self.status_queue.put(("INFO", f"Raw dump wrote {self.raw_frames_written} frames @ {self.capture_sr_spk} Hz"))
                    self.status_queue.put(("INFO", f"Saved raw file:\n{self.raw_path}"))
            except Exception as e:
                self.status_queue.put(("ERROR", f"Error closing raw WAV: {e}"))
            finally:
                self.raw_wave = None
                self.raw_frames_written = 0
                self.raw_path = None
            self._teardown()
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        self._process_and_emit()
        self._teardown()

    # ---------- Readers ----------
    def _read_loopback(self):
        last = 0.0
        try:
            while self.is_recording and self.spk_stream and self.spk_stream.is_active():
                data = self.spk_stream.read(BLOCK, exception_on_overflow=False)
                ch = getattr(self.spk_stream, "_channels", None) or 2
                x = float_from_int16_bytes(data, ch)

                if self.raw_dump:
                    self.raw_wave.writeframes(data)
                    self.raw_frames_written += len(x) if x.ndim == 1 else x.shape[0]
                else:
                    x *= self.loop_gain
                    self.loop_queue.put(x.copy())

                # METERING: reflect current *gained* level
                now = time.time()
                if now - last >= 0.1:
                    g = self.loop_gain
                    peak = float(np.max(np.abs(x))) if x.size else 0.0
                    self.status_queue.put(("LEVEL", ("sys", peak)))
                    last = now
        except Exception as e:
            if self.is_recording:
                self.status_queue.put(("ERROR", f"Loopback read error: {e}"))

    def _read_mic(self):
        last = 0.0
        try:
            while self.is_recording and self.mic_stream and self.mic_stream.is_active():
                data = self.mic_stream.read(BLOCK, exception_on_overflow=False)
                ch = getattr(self.mic_stream, "_channels", None) or 1
                x = float_from_int16_bytes(data, ch)
                x *= self.mic_gain
                self.mic_queue.put(x.copy())

                # METERING: reflect current *gained* level
                now = time.time()
                if now - last >= 0.1:
                    peak = float(np.max(np.abs(x))) if x.size else 0.0
                    self.status_queue.put(("LEVEL", ("mic", peak)))
                    last = now
        except Exception as e:
            if self.is_recording:
                self.status_queue.put(("ERROR", f"Mic read error: {e}"))

    # ---------- Processing ----------
    def _drain(self, q):
        out = []
        while True:
            try:
                out.append(q.get_nowait())
            except queue.Empty:
                break
        return out

    def _limit_down(self, x: np.ndarray) -> np.ndarray:
        """Soft limiter: only reduces if peak > TARGET_PEAK."""
        peak = float(np.max(np.abs(x))) if x.size else 0.0
        if peak > TARGET_PEAK and peak > 1e-9:
            x = x * (TARGET_PEAK / peak)
        return x

    def _process_and_emit(self):
        loop_frames = self._drain(self.loop_queue)
        mic_frames = self._drain(self.mic_queue)

        if not loop_frames and not mic_frames:
            self.status_queue.put(("INFO", "No audio captured. Nothing saved."))
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        # System-only
        if not mic_frames:
            loop_np = np.concatenate(loop_frames, axis=0).astype(np.float32, copy=False)
            loop_rs = resample_to(loop_np, self.capture_sr_spk, TARGET_SR)
            loop_rs = self._limit_down(loop_rs)
            self.status_queue.put(("INFO", f"System-only peak(gained): {float(np.max(np.abs(loop_rs))):.3f} (saved @ {TARGET_SR} Hz)"))
            self.status_queue.put(("SAVE_FILE", to_int16(loop_rs)))
            return

        # Mic-only
        if not loop_frames:
            mic_np = np.concatenate(mic_frames, axis=0).astype(np.float32, copy=False)
            mic_rs = resample_to(mic_np, self.capture_sr_mic, TARGET_SR)
            mic_rs = self._limit_down(mic_rs)
            self.status_queue.put(("INFO", f"Mic-only peak(gained): {float(np.max(np.abs(mic_rs))):.3f} (saved @ {TARGET_SR} Hz)"))
            self.status_queue.put(("SAVE_FILE", to_int16(mic_rs)))
            return

        # Both streams
        mic_np = np.concatenate(mic_frames, axis=0).astype(np.float32, copy=False)
        loop_np = np.concatenate(loop_frames, axis=0).astype(np.float32, copy=False)

        self.status_queue.put(("INFO", f"Max peaks (after applied gains, pre-resample): mic {float(np.max(np.abs(mic_np))):.3f}, "
                                       f"sys {float(np.max(np.abs(loop_np))):.3f}"))

        mic_rs  = resample_to(mic_np,  self.capture_sr_mic, TARGET_SR)
        loop_rs = resample_to(loop_np, self.capture_sr_spk, TARGET_SR)

        mic2, loop2 = match_channels(mic_rs, loop_rs)
        L = min(len(mic2), len(loop2))
        mic2, loop2 = mic2[:L], loop2[:L]

        mixed = mic2 + loop2
        mixed = self._limit_down(mixed)

        mixed_i16 = to_int16(mixed)

        if SAVE_STEMS:
            try:
                dbg = Path.home() / "Recordings" / "_stems"
                dbg.mkdir(parents=True, exist_ok=True)
                ts = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
                write_wav(str(dbg / f"{ts}-mic.wav"), TARGET_SR, to_int16(self._limit_down(mic2)))
                write_wav(str(dbg / f"{ts}-loop.wav"), TARGET_SR, to_int16(self._limit_down(loop2)))
                write_wav(str(dbg / f"{ts}-mix.wav"), TARGET_SR, mixed_i16)
            except Exception:
                pass

        self.status_queue.put(("SAVE_FILE", mixed_i16))
        self.status_queue.put(("STATUS", "Ready to record"))


# ================== Tk App ==================
class AudioRecorderApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Audio Recorder (WASAPI/PyAudio)")
        self.root.geometry("680x840")
        self.root.resizable(False, False)

        def _tk_exception_hook(exc, val, tb):
            msg = "".join(traceback.format_exception(exc, val, tb))
            try:
                messagebox.showerror("Tk Error", msg)
            except Exception:
                print(msg, file=sys.stderr)
        self.root.report_callback_exception = _tk_exception_hook
        sys.excepthook = lambda exc, val, tb: _tk_exception_hook(exc, val, tb)

        self.command_queue = queue.Queue()
        self.status_queue = queue.Queue()
        self.controller = RecordingController(self.command_queue, self.status_queue)
        self.controller.start()

        self._poll_after_id = None

        self.mic_devices, self.spk_devices, self.lb_devices = {}, {}, {}
        self.selected_mic_name = tk.StringVar()
        self.selected_spk_name = tk.StringVar()
        self.selected_lb_name = tk.StringVar(value="Auto (match speaker)")
        self.output_directory = tk.StringVar()
        self.set_default_output_directory()

        # Gain controls (live)
        self.mic_gain_var = tk.DoubleVar(value=GAIN_DEFAULT)
        self.loop_gain_var = tk.DoubleVar(value=GAIN_DEFAULT)

        # UI layout
        self.main = tk.Frame(self.root, padx=20, pady=15)
        self.main.pack(expand=True, fill=tk.BOTH)

        devf = tk.LabelFrame(self.main, text="Audio Devices", padx=10, pady=10)
        devf.pack(pady=5, fill=tk.X, expand=True)

        tip = (
            "Pick your SPEAKERS/HEADPHONES as output. For reliability, pick a specific WASAPI "
            "entry under 'Loopback device (what to capture)'. If one fails, try another.\n"
            "Use the sliders to adjust Mic and Output gains live while recording."
        )
        tk.Label(devf, text=tip, wraplength=620, justify=tk.LEFT, fg="darkblue")\
            .grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 8))

        tk.Label(devf, text="Microphone (Input):").grid(row=1, column=0, sticky="w")
        self.mic_menu = tk.OptionMenu(devf, self.selected_mic_name, "No devices found")
        self.mic_menu.grid(row=2, column=0, columnspan=3, sticky="ew", pady=(0, 5))

        tk.Label(devf, text="Audio Output (render device):").grid(row=3, column=0, sticky="w")
        self.spk_menu = tk.OptionMenu(devf, self.selected_spk_name, "No devices found")
        self.spk_menu.grid(row=4, column=0, columnspan=3, sticky="ew")

        tk.Label(devf, text="Loopback device (what to capture):").grid(row=5, column=0, sticky="w", pady=(8,0))
        self.lb_menu = tk.OptionMenu(devf, self.selected_lb_name, "Auto (match speaker)")
        self.lb_menu.grid(row=6, column=0, columnspan=3, sticky="ew")

        self.include_mic_var = tk.BooleanVar(value=True)
        ck = tk.Checkbutton(devf, text="Include microphone in recording", variable=self.include_mic_var)
        ck.grid(row=7, column=0, columnspan=3, sticky="w", pady=(10, 0))

        self.raw_dump_var = tk.BooleanVar(value=False)
        raw_ck = tk.Checkbutton(devf, text="Raw loopback dump (no processing; auto-saves)", variable=self.raw_dump_var)
        raw_ck.grid(row=8, column=0, columnspan=3, sticky="w", pady=(4, 0))

        # Live gain controls
        gains = tk.LabelFrame(self.main, text="Live Gain Controls", padx=10, pady=10)
        gains.pack(pady=8, fill=tk.X, expand=True)

        # Mic Gain
        row = tk.Frame(gains); row.pack(fill=tk.X, pady=(0,6))
        tk.Label(row, text="Mic Gain").pack(side=tk.LEFT)
        self.mic_gain_scale = ttk.Scale(row, from_=GAIN_MIN, to=GAIN_MAX, orient="horizontal",
                                        variable=self.mic_gain_var, command=self._on_gain_change)
        self.mic_gain_scale.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        self.mic_gain_label = tk.Label(row, text=self._fmt_gain_label(self.mic_gain_var.get()))
        self.mic_gain_label.pack(side=tk.LEFT, padx=(6,0))

        # Output Gain
        row = tk.Frame(gains); row.pack(fill=tk.X)
        tk.Label(row, text="Output Gain").pack(side=tk.LEFT)
        self.loop_gain_scale = ttk.Scale(row, from_=GAIN_MIN, to=GAIN_MAX, orient="horizontal",
                                         variable=self.loop_gain_var, command=self._on_gain_change)
        self.loop_gain_scale.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        self.loop_gain_label = tk.Label(row, text=self._fmt_gain_label(self.loop_gain_var.get()))
        self.loop_gain_label.pack(side=tk.LEFT, padx=(6,0))

        savef = tk.LabelFrame(self.main, text="Save Location", padx=10, pady=10)
        savef.pack(pady=10, fill=tk.X, expand=True)
        self.save_entry = tk.Entry(savef, textvariable=self.output_directory, state='readonly')
        self.save_entry.grid(row=0, column=0, sticky="ew", padx=(0, 5))
        self.browse_btn = tk.Button(savef, text="Browse...", command=self.browse_directory)
        self.browse_btn.grid(row=0, column=1, sticky="e")
        savef.grid_columnconfigure(0, weight=1)

        self.refresh_btn = tk.Button(self.main, text="Refresh Devices", command=self.populate_device_lists)
        self.refresh_btn.pack(pady=5)

        self.status_label = tk.Label(self.main, text="Ready to record", font=("Arial", 12))
        self.status_label.pack(pady=5)

        btnrow = tk.Frame(self.main)
        btnrow.pack(pady=6)
        self.start_btn = tk.Button(btnrow, text="Start Recording", command=self.start_clicked)
        self.stop_btn = tk.Button(btnrow, text="Stop Recording", command=self.stop_clicked, state=tk.DISABLED)
        self.start_btn.pack(side=tk.LEFT, padx=5)
        self.stop_btn.pack(side=tk.LEFT, padx=5)

        meters = tk.LabelFrame(self.main, text="Live Levels", padx=10, pady=8)
        meters.pack(pady=6, fill=tk.X)
        r = tk.Frame(meters); r.pack(fill=tk.X, pady=2)
        tk.Label(r, text="Mic:", width=8, anchor="e").pack(side=tk.LEFT)
        self.mic_bar = ttk.Progressbar(r, orient="horizontal", length=360, mode="determinate", maximum=100)
        self.mic_bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        self.mic_db = tk.Label(r, text="-inf dBFS", width=10, anchor="w"); self.mic_db.pack(side=tk.LEFT)

        r = tk.Frame(meters); r.pack(fill=tk.X, pady=2)
        tk.Label(r, text="System:", width=8, anchor="e").pack(side=tk.LEFT)
        self.sys_bar = ttk.Progressbar(r, orient="horizontal", length=360, mode="determinate", maximum=100)
        self.sys_bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        self.sys_db = tk.Label(r, text="-inf dBFS", width=10, anchor="w"); self.sys_db.pack(side=tk.LEFT)

        # Handle window close + Ctrl+C
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

        # Populate and loop
        self.populate_device_lists()
        self._schedule_poll()

        # Graceful Ctrl+C in console
        try:
            signal.signal(signal.SIGINT, lambda s, f: self.root.after(0, self.on_close))
        except Exception:
            pass  # not available in some embedded envs

    # ---------- UI actions ----------
    def _fmt_gain_label(self, g: float) -> str:
        db = "-inf" if g <= 0 else f"{20*log10(g):.1f}"
        return f"{g:.2f}× ({db} dB)"

    def _on_gain_change(self, _evt=None):
        mg = float(self.mic_gain_var.get())
        lg = float(self.loop_gain_var.get())
        self.mic_gain_label.config(text=self._fmt_gain_label(mg))
        self.loop_gain_label.config(text=self._fmt_gain_label(lg))
        self.command_queue.put(("SET_GAINS", {"mic_gain": mg, "loop_gain": lg}))

    def _schedule_poll(self):
        if getattr(self, "_poll_after_id", None) is None:
            self._poll_after_id = self.root.after(100, self.process_status)

    def _cancel_poll(self):
        if getattr(self, "_poll_after_id", None) is not None:
            try:
                self.root.after_cancel(self._poll_after_id)
            except Exception:
                pass
            self._poll_after_id = None

    @contextmanager
    def modal_guard(self):
        self._cancel_poll()
        try:
            yield
        finally:
            self._schedule_poll()

    def start_clicked(self):
        mic_name = self.selected_mic_name.get()
        spk_name = self.selected_spk_name.get()
        lb_name = self.selected_lb_name.get()
        include_mic = self.include_mic_var.get()
        raw_dump = self.raw_dump_var.get()

        spk_dev = self.spk_devices.get(spk_name)
        mic_dev = self.mic_devices.get(mic_name)
        lb_dev = None if lb_name == "Auto (match speaker)" else self.lb_devices.get(lb_name)

        if not spk_dev and not lb_dev:
            with self.modal_guard():
                messagebox.showerror("Error", "Select an output or a loopback device.")
            return
        if include_mic and not raw_dump and mic_name not in self.mic_devices:
            with self.modal_guard():
                messagebox.showerror("Error", "Select a valid microphone or uncheck 'Include microphone'.")
            return
        if raw_dump:
            include_mic = False

        for w in [self.start_btn, self.refresh_btn, self.browse_btn]:
            w.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)

        args = {
            "mic_device": mic_dev,
            "spk_device": spk_dev,
            "loopback_device": lb_dev,
            "include_mic": include_mic,
            "raw_dump": raw_dump,
            "mic_gain": float(self.mic_gain_var.get()),
            "loop_gain": float(self.loop_gain_var.get()),
        }
        self.command_queue.put(("START", args))

    def stop_clicked(self):
        self.command_queue.put(("STOP", {}))

    def process_status(self):
        self._poll_after_id = None
        try:
            for _ in range(8):
                msgtype, data = self.status_queue.get_nowait()

                if msgtype == "STATUS":
                    self.status_label.config(text=data)
                    if data.startswith("Ready to record"):
                        for w in [self.start_btn, self.refresh_btn, self.browse_btn]:
                            w.config(state=tk.NORMAL)
                        self.stop_btn.config(state=tk.DISABLED)

                elif msgtype == "INFO":
                    with self.modal_guard():
                        messagebox.showinfo("Info", data)

                elif msgtype == "ERROR":
                    with self.modal_guard():
                        messagebox.showerror("Recording Error", data)
                    self.status_label.config(text="Ready to record")
                    for w in [self.start_btn, self.refresh_btn, self.browse_btn]:
                        w.config(state=tk.NORMAL)
                    self.stop_btn.config(state=tk.DISABLED)

                elif msgtype == "LEVEL":
                    tag, peak = data  # peak already multiplied by current gain
                    db = fmt_dbfs(peak)
                    pct = 0 if peak <= 0 else int(min(100, round(100.0 * peak, 1)))
                    if tag == "mic":
                        self.mic_bar['value'] = pct
                        self.mic_db.config(text=f"{db:.1f} dBFS")
                    else:
                        self.sys_bar['value'] = pct
                        self.sys_db.config(text=f"{db:.1f} dBFS")

                elif msgtype == "SAVE_FILE":
                    self.save_file_dialog(data)
                    self.status_label.config(text="Ready to record")
                    for w in [self.start_btn, self.refresh_btn, self.browse_btn]:
                        w.config(state=tk.NORMAL)
                    self.stop_btn.config(state=tk.DISABLED)

        except queue.Empty:
            pass
        finally:
            self._schedule_poll()

    # ---------- Save helpers ----------
    def set_default_output_directory(self):
        default_path = Path.home() / "Music" / "Recordings"
        if not default_path.parent.exists():
            default_path = Path.home() / "Recordings"
        os.makedirs(default_path, exist_ok=True)
        self.output_directory.set(str(default_path))

    def _validated_initial_dir(self) -> str:
        p = Path(self.output_directory.get())
        if p.exists():
            return str(p)
        music = Path.home() / "Music"
        return str(music if music.exists() else Path.home())

    def browse_directory(self):
        with self.modal_guard():
            d = filedialog.askdirectory(initialdir=self._validated_initial_dir(), title="Choose folder for recordings")
            if d:
                self.output_directory.set(d)

    def save_file_dialog(self, audio_i16: np.ndarray):
        default_filename = f"recording-{datetime.now().strftime('%Y-%m-%d_%H-%M-%S')}.wav"
        try:
            with self.modal_guard():
                path = filedialog.asksaveasfilename(
                    parent=self.root,
                    initialdir=self._validated_initial_dir(),
                    initialfile=default_filename,
                    defaultextension=".wav",
                    filetypes=[("WAV files", "*.wav")],
                    title="Save recording as..."
                )
        except Exception as e:
            msg = "".join(traceback.format_exception(type(e), e, e.__traceback__))
            with self.modal_guard():
                messagebox.showerror("File Dialog Error", msg)
            return
        if path:
            try:
                write_wav(path, TARGET_SR, audio_i16)
                with self.modal_guard():
                    messagebox.showinfo("Success", f"Recording saved to\n{path}")
            except Exception as e:
                with self.modal_guard():
                    messagebox.showerror("Error", f"Could not save file: {e}")

    # ---------- Devices & debug ----------
    def populate_device_lists(self):
        self.mic_devices, self.spk_devices, self.lb_devices = list_devices()

        mic_menu = self.mic_menu["menu"]; mic_menu.delete(0, "end")
        if self.mic_devices:
            for name in self.mic_devices.keys():
                mic_menu.add_command(label=name, command=lambda v=name: self.selected_mic_name.set(v))
            self.selected_mic_name.set(next(iter(self.mic_devices)))
        else:
            self.selected_mic_name.set("No devices found")

        spk_menu = self.spk_menu["menu"]; spk_menu.delete(0, "end")
        if self.spk_devices:
            for name in self.spk_devices.keys():
                spk_menu.add_command(label=name, command=lambda v=name: self.selected_spk_name.set(v))
            prefer = next((n for n in self.spk_devices if "Speakers" in n or "Headphones" in n), None)
            self.selected_spk_name.set(prefer or next(iter(self.spk_devices)))
        else:
            self.selected_spk_name.set("No devices found")

        lb_menu = self.lb_menu["menu"]; lb_menu.delete(0, "end")
        lb_menu.add_command(label="Auto (match speaker)", command=lambda: self.selected_lb_name.set("Auto (match speaker)"))
        if self.lb_devices:
            for name in self.lb_devices.keys():
                lb_menu.add_command(label=name, command=lambda v=name: self.selected_lb_name.set(v))
        if self.selected_lb_name.get() not in self.lb_devices and self.selected_lb_name.get() != "Auto (match speaker)":
            self.selected_lb_name.set("Auto (match speaker)")

    def on_close(self):
        # Graceful shutdown: stop controller thread and close window
        try:
            self.command_queue.put(("EXIT", {}))
        except Exception:
            pass
        try:
            self.root.destroy()
        except Exception:
            pass


if __name__ == "__main__":
    # Run Tk mainloop with KeyboardInterrupt safety
    root = tk.Tk()
    app = AudioRecorderApp(root)
    try:
        root.mainloop()
    except KeyboardInterrupt:
        # Allow Ctrl+C to exit cleanly if mainloop didn't catch signal
        app.on_close()
