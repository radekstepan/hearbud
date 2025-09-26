import sys
import os
import time
import threading
import queue
import traceback
import subprocess
import signal
import json
from math import gcd, log10
from pathlib import Path
from datetime import datetime
from contextlib import contextmanager

import numpy as np
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
CONFIG_FILE = Path.home() / ".audiorecorder_config.json"

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
    Captures audio, processes it in chunks, and pipes it to FFmpeg for MP3 encoding.
    """
    def __init__(self, command_queue, status_queue):
        super().__init__(daemon=True)
        self.command_queue = command_queue
        self.status_queue = status_queue
        self.is_recording = False

        self.include_mic = True
        self.p = None
        self.mic_stream = None
        self.spk_stream = None

        self.mic_queue = queue.Queue()
        self.loop_queue = queue.Queue()

        self.capture_sr_mic = TARGET_SR
        self.capture_sr_spk = TARGET_SR

        self.mic_gain = GAIN_DEFAULT
        self.loop_gain = GAIN_DEFAULT

        self.output_path = None
        self.ffmpeg_process = None
        self.processing_thread = None
        self.mic_thread = None
        self.loop_thread = None


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
                        input_format=pyaudio.paInt16
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
                    as_loopback=True
                )
                return ch, sr
            except TypeError: # Fallback for builds without as_loopback
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
                except Exception as e2: last_err = e2; time.sleep(0.4)
            except Exception as e: last_err = e; time.sleep(0.4)
        raise RuntimeError(
            f"Cannot open loopback device [{lb_idx}] {lb_dev['name']}.\nLast error: {last_err}"
        )

    def _match_loopback(self, spk_dev, loopbacks: dict):
        base = spk_dev['name'] if spk_dev else ""
        for name, d in loopbacks.items():
            if base and name.startswith(base): return d
        for name, d in loopbacks.items():
            if base and (base in name or name in base): return d
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
                if self.is_recording:
                    self.status_queue.put(("STATUS",
                        f"Recording… (mic {self.mic_gain:.2f}×, out {self.loop_gain:.2f}×)"))

    def _teardown(self):
        for stream in [self.mic_stream, self.spk_stream]:
            if stream:
                try:
                    if stream.is_active(): stream.stop_stream()
                    stream.close()
                except Exception: pass
        if self.p:
            try: self.p.terminate()
            except Exception: pass
        self.p = None
        self.mic_stream, self.spk_stream = None, None

    def start_recording(self, mic_device=None, spk_device=None, loopback_device=None,
                        include_mic=True, mic_gain=GAIN_DEFAULT, loop_gain=GAIN_DEFAULT, output_path=None):
        self._ensure_pyaudio()
        if not output_path:
            self.status_queue.put(("ERROR", "No output file was selected."))
            return

        if spk_device is None and loopback_device is None:
            self.status_queue.put(("ERROR", "Select an output or a loopback device."))
            return
        if include_mic and mic_device is None:
            self.status_queue.put(("ERROR", "Select a microphone to include it in the recording."))
            return

        self.include_mic = bool(include_mic)
        self.mic_gain = float(mic_gain)
        self.loop_gain = float(loop_gain)
        self.output_path = output_path

        try:
            # Open audio streams
            chosen_lb_name = ""
            if loopback_device is not None:
                lb_ch, lb_sr = self._open_loopback_by_index(loopback_device)
                chosen_lb_name = loopback_device['name']
            else:
                _, _, loopbacks = list_devices()
                if not loopbacks: raise RuntimeError("No WASAPI loopback devices found.")
                lb_choice = self._match_loopback(spk_device, loopbacks) or next(iter(loopbacks.values()))
                lb_ch, lb_sr = self._open_loopback_by_index(lb_choice)
                chosen_lb_name = lb_choice['name']

            if self.include_mic:
                self._open_mic(mic_device)

            # Determine output format and start FFmpeg process
            out_channels = getattr(self.spk_stream, "_channels", 2)
            command = [
                'ffmpeg',
                '-y',  # Overwrite output file if it exists
                '-f', 's16le',  # Input format: signed 16-bit little-endian PCM
                '-ar', str(TARGET_SR),  # Input sample rate
                '-ac', str(out_channels),  # Input channels
                '-i', 'pipe:0',  # Input is from stdin
                '-b:a', '192k',  # Audio bitrate for MP3
                self.output_path
            ]
            self.ffmpeg_process = subprocess.Popen(
                command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE
            )

        except FileNotFoundError:
            self.status_queue.put(("ERROR", "FFmpeg not found. Please install FFmpeg and ensure it is in your system's PATH."))
            self._teardown()
            self.status_queue.put(("STATUS", "Ready to record"))
            return
        except Exception as e:
            self.status_queue.put(("ERROR", f"Failed to start recording: {e}"))
            self._teardown()
            if self.ffmpeg_process:
                self.ffmpeg_process.kill()
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        self.is_recording = True
        mic_note = " + Mic" if self.include_mic else ""
        self.status_queue.put(("STATUS", f"Recording to file… (Loopback{mic_note})\n"
                                         f"Device: {chosen_lb_name}"))

        # Launch reader and processing threads
        self.loop_thread = threading.Thread(target=self._read_loopback, daemon=True)
        self.loop_thread.start()
        if self.include_mic:
            self.mic_thread = threading.Thread(target=self._read_mic, daemon=True)
            self.mic_thread.start()
        self.processing_thread = threading.Thread(target=self._processing_and_writing_loop, daemon=True)
        self.processing_thread.start()

    def stop_recording(self):
        if not self.is_recording:
            return
        self.is_recording = False
        self.status_queue.put(("STATUS", "Finalizing file…"))

        # Join threads in order: producers, then consumer
        if self.mic_thread: self.mic_thread.join()
        if self.loop_thread: self.loop_thread.join()
        if self.processing_thread: self.processing_thread.join()

        try:
            if self.ffmpeg_process and self.ffmpeg_process.stdin:
                self.ffmpeg_process.stdin.close()
            
            if self.ffmpeg_process:
                # Wait for FFmpeg to finish and check for errors
                _, stderr_output = self.ffmpeg_process.communicate()
                if self.ffmpeg_process.returncode != 0:
                    error_msg = (f"FFmpeg error (code {self.ffmpeg_process.returncode}):\n"
                                 f"{stderr_output.decode('utf-8', 'ignore')}")
                    self.status_queue.put(("ERROR", error_msg))
                else:
                    self.status_queue.put(("INFO", f"Recording saved to\n{self.output_path}"))
        except Exception as e:
            self.status_queue.put(("ERROR", f"Error finalizing MP3 file: {e}"))
        finally:
            self.ffmpeg_process = None
            self.output_path = None

        self._teardown()
        self.status_queue.put(("STATUS", "Ready to record"))

    # ---------- Readers (Producers) ----------
    def _read_loopback(self):
        last_meter_update = 0.0
        try:
            while self.is_recording and self.spk_stream and self.spk_stream.is_active():
                data = self.spk_stream.read(BLOCK, exception_on_overflow=False)
                ch = getattr(self.spk_stream, "_channels", 2)
                x = float_from_int16_bytes(data, ch)
                gained_x = x * self.loop_gain
                self.loop_queue.put(gained_x.copy())

                now = time.time()
                if now - last_meter_update >= 0.1:
                    peak = float(np.max(np.abs(gained_x))) if gained_x.size else 0.0
                    self.status_queue.put(("LEVEL", ("sys", peak)))
                    last_meter_update = now
        except Exception as e:
            if self.is_recording: self.status_queue.put(("ERROR", f"Loopback read error: {e}"))

    def _read_mic(self):
        last_meter_update = 0.0
        try:
            while self.is_recording and self.mic_stream and self.mic_stream.is_active():
                data = self.mic_stream.read(BLOCK, exception_on_overflow=False)
                ch = getattr(self.mic_stream, "_channels", 1)
                x = float_from_int16_bytes(data, ch)
                gained_x = x * self.mic_gain
                self.mic_queue.put(gained_x.copy())

                now = time.time()
                if now - last_meter_update >= 0.1:
                    peak = float(np.max(np.abs(gained_x))) if gained_x.size else 0.0
                    self.status_queue.put(("LEVEL", ("mic", peak)))
                    last_meter_update = now
        except Exception as e:
            if self.is_recording: self.status_queue.put(("ERROR", f"Mic read error: {e}"))

    # ---------- Processing (Consumer) ----------
    def _drain(self, q):
        out = []
        while not q.empty():
            try: out.append(q.get_nowait())
            except queue.Empty: break
        return out

    def _limit_down(self, x: np.ndarray) -> np.ndarray:
        peak = float(np.max(np.abs(x))) if x.size else 0.0
        if peak > TARGET_PEAK and peak > 1e-9:
            x = x * (TARGET_PEAK / peak)
        return x

    def _processing_and_writing_loop(self):
        """Continuously processes and writes audio chunks until recording stops."""
        while self.is_recording:
            self._process_and_write_chunk()
            time.sleep(0.1)  # Process available audio every 100ms

        # After stopping, do one final pass to process any leftover audio
        self._process_and_write_chunk()

    def _process_and_write_chunk(self):
        """Processes one batch of audio from the queues and pipes it to FFmpeg."""
        loop_chunks = self._drain(self.loop_queue)
        mic_chunks = self._drain(self.mic_queue) if self.include_mic else []

        if not loop_chunks and not mic_chunks:
            return

        loop_ch_count = as_2d(loop_chunks[0]).shape[1] if loop_chunks else 2
        out_channels = loop_ch_count

        mic_rs = resample_to(np.concatenate(mic_chunks, axis=0), self.capture_sr_mic, TARGET_SR) if mic_chunks else np.array([])
        loop_rs = resample_to(np.concatenate(loop_chunks, axis=0), self.capture_sr_spk, TARGET_SR) if loop_chunks else np.array([])

        mic_len, loop_len = len(as_2d(mic_rs)), len(as_2d(loop_rs))
        max_len = max(mic_len, loop_len)
        if max_len == 0: return

        # Create buffers padded with silence to match the longest chunk
        mic_buf = np.zeros((max_len, out_channels), dtype=np.float32)
        if mic_len > 0:
            mic_2d = as_2d(mic_rs)
            ch_to_copy = min(mic_2d.shape[1], out_channels)
            mic_buf[:mic_len, :ch_to_copy] = mic_2d[:, :ch_to_copy]

        loop_buf = np.zeros((max_len, out_channels), dtype=np.float32)
        if loop_len > 0:
            loop_2d = as_2d(loop_rs)
            ch_to_copy = min(loop_2d.shape[1], out_channels)
            loop_buf[:loop_len, :ch_to_copy] = loop_2d[:, :ch_to_copy]

        # Mix, limit, convert, and write to ffmpeg process
        mixed = self._limit_down(mic_buf + loop_buf)
        if mixed.size > 0:
            try:
                if self.ffmpeg_process and self.ffmpeg_process.stdin:
                    self.ffmpeg_process.stdin.write(to_int16(mixed).tobytes())
            except (BrokenPipeError, OSError):
                # FFmpeg process has likely terminated
                if self.is_recording:
                    self.status_queue.put(("ERROR", "FFmpeg process terminated unexpectedly."))
                self.is_recording = False  # Stop the loop


# ================== Tk App ==================
class AudioRecorderApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Audio Recorder (WASAPI/PyAudio) -> MP3")
        self.root.geometry("680x780")
        self.root.resizable(False, False)

        def _tk_exception_hook(exc, val, tb):
            msg = "".join(traceback.format_exception(exc, val, tb))
            try: messagebox.showerror("Unhandled Error", msg)
            except Exception: print(msg, file=sys.stderr)
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

        self.mic_gain_var = tk.DoubleVar(value=GAIN_DEFAULT)
        self.loop_gain_var = tk.DoubleVar(value=GAIN_DEFAULT)
        self.include_mic_var = tk.BooleanVar(value=True)

        # --- UI Layout ---
        self.main = tk.Frame(self.root, padx=20, pady=15)
        self.main.pack(expand=True, fill=tk.BOTH)

        devf = tk.LabelFrame(self.main, text="Audio Devices", padx=10, pady=10)
        devf.pack(pady=5, fill=tk.X, expand=True)

        tip = ("Pick your SPEAKERS/HEADPHONES. For best results, also select the matching WASAPI "
               "entry under 'Loopback device'.\nUse sliders to adjust gain live while recording.")
        tk.Label(devf, text=tip, wraplength=620, justify=tk.LEFT, fg="darkblue")\
            .grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 8))

        tk.Label(devf, text="Microphone (Input):").grid(row=1, column=0, sticky="w")
        self.mic_menu = tk.OptionMenu(devf, self.selected_mic_name, "No devices found")
        self.mic_menu.grid(row=2, column=0, columnspan=3, sticky="ew", pady=(0, 5))

        tk.Label(devf, text="Audio Output (device to capture):").grid(row=3, column=0, sticky="w")
        self.spk_menu = tk.OptionMenu(devf, self.selected_spk_name, "No devices found")
        self.spk_menu.grid(row=4, column=0, columnspan=3, sticky="ew")

        tk.Label(devf, text="Loopback device (override):").grid(row=5, column=0, sticky="w", pady=(8,0))
        self.lb_menu = tk.OptionMenu(devf, self.selected_lb_name, "Auto (match speaker)")
        self.lb_menu.grid(row=6, column=0, columnspan=3, sticky="ew")

        
        tk.Checkbutton(devf, text="Include microphone in recording", variable=self.include_mic_var)\
            .grid(row=7, column=0, columnspan=3, sticky="w", pady=(10, 0))

        gains = tk.LabelFrame(self.main, text="Live Gain Controls", padx=10, pady=10)
        gains.pack(pady=8, fill=tk.X, expand=True)
        self._create_gain_slider(gains, "Mic Gain", self.mic_gain_var)
        self._create_gain_slider(gains, "Output Gain", self.loop_gain_var)

        savef = tk.LabelFrame(self.main, text="Default Save Location", padx=10, pady=10)
        savef.pack(pady=10, fill=tk.X, expand=True)
        tk.Entry(savef, textvariable=self.output_directory, state='readonly')\
            .grid(row=0, column=0, sticky="ew", padx=(0, 5))
        self.browse_btn = tk.Button(savef, text="Browse...", command=self.browse_directory)
        self.browse_btn.grid(row=0, column=1, sticky="e")
        savef.grid_columnconfigure(0, weight=1)

        self.refresh_btn = tk.Button(self.main, text="Refresh Devices", command=self.populate_device_lists)
        self.refresh_btn.pack(pady=5)

        self.status_label = tk.Label(self.main, text="Ready to record", font=("Arial", 12))
        self.status_label.pack(pady=5)

        btnrow = tk.Frame(self.main); btnrow.pack(pady=6)
        self.start_btn = tk.Button(btnrow, text="Start Recording", command=self.start_clicked)
        self.stop_btn = tk.Button(btnrow, text="Stop Recording", command=self.stop_clicked, state=tk.DISABLED)
        self.start_btn.pack(side=tk.LEFT, padx=5)
        self.stop_btn.pack(side=tk.LEFT, padx=5)

        self._create_meters()

        # --- Final setup ---
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.populate_device_lists() # First, populate devices and set OS defaults
        self._load_settings()         # Then, override with user's saved preferences
        self._schedule_poll()
        try: signal.signal(signal.SIGINT, lambda s, f: self.root.after(0, self.on_close))
        except Exception: pass

    def _create_gain_slider(self, parent, label_text, var):
        row = tk.Frame(parent); row.pack(fill=tk.X, pady=2)
        tk.Label(row, text=label_text, width=10).pack(side=tk.LEFT)
        scale = ttk.Scale(row, from_=GAIN_MIN, to=GAIN_MAX, orient="horizontal", variable=var, command=self._on_gain_change)
        scale.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        label = tk.Label(row, text=self._fmt_gain_label(var.get()), width=12)
        label.pack(side=tk.LEFT, padx=(6, 0))
        var.label_widget = label

    def _create_meters(self):
        meters = tk.LabelFrame(self.main, text="Live Levels", padx=10, pady=8)
        meters.pack(pady=6, fill=tk.X)
        self.mic_bar, self.mic_db = self._create_meter_row(meters, "Mic:")
        self.sys_bar, self.sys_db = self._create_meter_row(meters, "System:")

    def _create_meter_row(self, parent, label_text):
        r = tk.Frame(parent); r.pack(fill=tk.X, pady=2)
        tk.Label(r, text=label_text, width=8, anchor="e").pack(side=tk.LEFT)
        bar = ttk.Progressbar(r, orient="horizontal", length=360, mode="determinate", maximum=100)
        bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        db_label = tk.Label(r, text="-inf dBFS", width=10, anchor="w")
        db_label.pack(side=tk.LEFT)
        return bar, db_label

    # ---------- UI actions ----------
    def _fmt_gain_label(self, g: float) -> str:
        db = "-inf" if g <= 0 else f"{20 * log10(g):.1f}"
        return f"{g:.2f}× ({db} dB)"

    def _on_gain_change(self, _evt=None):
        for var in [self.mic_gain_var, self.loop_gain_var]:
            var.label_widget.config(text=self._fmt_gain_label(var.get()))
        self.command_queue.put(("SET_GAINS", {"mic_gain": self.mic_gain_var.get(), "loop_gain": self.loop_gain_var.get()}))

    def _schedule_poll(self):
        self._poll_after_id = self.root.after(100, self.process_status)

    @contextmanager
    def modal_guard(self):
        if self._poll_after_id: self.root.after_cancel(self._poll_after_id)
        try: yield
        finally: self._schedule_poll()

    def start_clicked(self):
        default_filename = f"recording-{datetime.now().strftime('%Y-%m-%d_%H-%M-%S')}.mp3"
        with self.modal_guard():
            path = filedialog.asksaveasfilename(
                parent=self.root,
                initialdir=self._validated_initial_dir(),
                initialfile=default_filename,
                defaultextension=".mp3",
                filetypes=[("MP3 files", "*.mp3"), ("All files", "*.*")],
                title="Save recording as..."
            )
        if not path: return

        spk_dev = self.spk_devices.get(self.selected_spk_name.get())
        mic_dev = self.mic_devices.get(self.selected_mic_name.get())
        lb_name = self.selected_lb_name.get()
        lb_dev = None if lb_name == "Auto (match speaker)" else self.lb_devices.get(lb_name)
        include_mic = self.include_mic_var.get()

        if not spk_dev and not lb_dev:
            with self.modal_guard(): messagebox.showerror("Error", "Select an output or loopback device.")
            return
        if include_mic and not mic_dev:
            with self.modal_guard(): messagebox.showerror("Error", "Select a valid microphone or uncheck 'Include microphone'.")
            return

        for w in [self.start_btn, self.refresh_btn, self.browse_btn]: w.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)

        self.command_queue.put(("START", {
            "output_path": path, "mic_device": mic_dev, "spk_device": spk_dev,
            "loopback_device": lb_dev, "include_mic": include_mic,
            "mic_gain": self.mic_gain_var.get(), "loop_gain": self.loop_gain_var.get(),
        }))

    def stop_clicked(self):
        self.command_queue.put(("STOP", {}))

    def process_status(self):
        try:
            while True:
                msgtype, data = self.status_queue.get_nowait()
                if msgtype == "STATUS":
                    self.status_label.config(text=data)
                    if data.startswith("Ready"):
                        for w in [self.start_btn, self.refresh_btn, self.browse_btn]: w.config(state=tk.NORMAL)
                        self.stop_btn.config(state=tk.DISABLED)
                elif msgtype == "INFO":
                    with self.modal_guard(): messagebox.showinfo("Info", data)
                elif msgtype == "ERROR":
                    with self.modal_guard(): messagebox.showerror("Recording Error", data)
                    self.status_label.config(text="Ready to record")
                    for w in [self.start_btn, self.refresh_btn, self.browse_btn]: w.config(state=tk.NORMAL)
                    self.stop_btn.config(state=tk.DISABLED)
                elif msgtype == "LEVEL":
                    tag, peak = data
                    db, pct = fmt_dbfs(peak), int(min(100, round(100.0 * peak, 1)))
                    bar, label = (self.mic_bar, self.mic_db) if tag == "mic" else (self.sys_bar, self.sys_db)
                    bar['value'] = pct
                    label.config(text=f"{db:.1f} dBFS")
        except queue.Empty:
            pass
        finally:
            self._poll_after_id = self.root.after(100, self.process_status)

    # ---------- File/Device/Settings Helpers ----------
    def _save_settings(self):
        """Saves current settings to the JSON config file."""
        settings = {
            "mic_device": self.selected_mic_name.get(),
            "spk_device": self.selected_spk_name.get(),
            "lb_device": self.selected_lb_name.get(),
            "mic_gain": self.mic_gain_var.get(),
            "loop_gain": self.loop_gain_var.get(),
            "include_mic": self.include_mic_var.get(),
            "output_dir": self.output_directory.get(),
        }
        try:
            with open(CONFIG_FILE, "w") as f:
                json.dump(settings, f, indent=4)
        except Exception as e:
            print(f"Warning: Could not save settings to {CONFIG_FILE}\n{e}", file=sys.stderr)

    def _load_settings(self):
        """Loads settings from the JSON config file if it exists."""
        if not CONFIG_FILE.exists():
            return
        try:
            with open(CONFIG_FILE, "r") as f:
                settings = json.load(f)

            # Restore device selections, but only if the device still exists
            if settings.get("mic_device") in self.mic_devices:
                self.selected_mic_name.set(settings["mic_device"])
            if settings.get("spk_device") in self.spk_devices:
                self.selected_spk_name.set(settings["spk_device"])
            
            lb_name = settings.get("lb_device", "Auto (match speaker)")
            if lb_name == "Auto (match speaker)" or lb_name in self.lb_devices:
                 self.selected_lb_name.set(lb_name)

            # Restore other settings
            self.mic_gain_var.set(float(settings.get("mic_gain", GAIN_DEFAULT)))
            self.loop_gain_var.set(float(settings.get("loop_gain", GAIN_DEFAULT)))
            self.include_mic_var.set(bool(settings.get("include_mic", True)))
            
            # Restore directory, if it's still a valid path
            saved_dir = settings.get("output_dir")
            if saved_dir and Path(saved_dir).exists():
                self.output_directory.set(saved_dir)

            self._on_gain_change() # Update gain labels in UI

        except (json.JSONDecodeError, TypeError, KeyError) as e:
            print(f"Warning: Could not load or parse settings from {CONFIG_FILE}\n{e}", file=sys.stderr)


    def set_default_output_directory(self):
        default_path = Path.home() / "Music" / "Recordings"
        if not default_path.parent.exists(): default_path = Path.home() / "Recordings"
        default_path.mkdir(parents=True, exist_ok=True)
        self.output_directory.set(str(default_path))

    def _validated_initial_dir(self) -> str:
        p = Path(self.output_directory.get())
        if p.exists(): return str(p)
        music = Path.home() / "Music"
        return str(music if music.exists() else Path.home())

    def browse_directory(self):
        with self.modal_guard():
            d = filedialog.askdirectory(initialdir=self._validated_initial_dir(), title="Choose default folder")
            if d: self.output_directory.set(d)

    def populate_device_lists(self):
        """Refreshes device lists and sets defaults."""
        self.mic_devices, self.spk_devices, self.lb_devices = list_devices()
        
        # Try to get OS default devices
        default_mic, default_spk = None, None
        try:
            p = pyaudio.PyAudio()
            default_mic = p.get_default_input_device_info()['name']
            default_spk = p.get_default_output_device_info()['name']
            p.terminate()
        except Exception:
            pass # Silently fail if defaults can't be found

        def _populate(menu, var, devices, default_name, default_msg):
            menu["menu"].delete(0, "end")
            if devices:
                for name in devices.keys(): 
                    menu["menu"].add_command(label=name, command=lambda v=name: var.set(v))
                
                # Set initial selection
                if default_name and default_name in devices:
                    var.set(default_name)
                else:
                    var.set(next(iter(devices)))
                return True
            else:
                var.set(default_msg)
                return False

        _populate(self.mic_menu, self.selected_mic_name, self.mic_devices, default_mic, "No mics found")
        _populate(self.spk_menu, self.selected_spk_name, self.spk_devices, default_spk, "No outputs found")

        # Repopulate loopback menu
        lb_menu = self.lb_menu["menu"]
        lb_menu.delete(0, "end")
        lb_menu.add_command(label="Auto (match speaker)", command=lambda: self.selected_lb_name.set("Auto (match speaker)"))
        if self.lb_devices:
            for name in self.lb_devices: 
                lb_menu.add_command(label=name, command=lambda v=name: self.selected_lb_name.set(v))


    def on_close(self):
        """Handles application shutdown, saving settings first."""
        self._save_settings()
        self.command_queue.put(("EXIT", {}))
        self.root.destroy()


if __name__ == "__main__":
    root = tk.Tk()
    app = AudioRecorderApp(root)
    try:
        root.mainloop()
    except KeyboardInterrupt:
        app.on_close()
