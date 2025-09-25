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

import numpy as np
from scipy.signal import resample_poly

import tkinter as tk
from tkinter import ttk, filedialog, messagebox

# ---- Modern theme (optional) ----
# pip install sv-ttk
try:
    import sv_ttk
except Exception:
    sv_ttk = None

# ---- PyAudio (WASAPI) ----
try:
    import pyaudiowpatch as pyaudio  # preferred (supports as_loopback on Win)
except Exception:
    import pyaudio  # fallback; may NOT support as_loopback


# ================== Global config ==================
TARGET_SR = 48000
BLOCK = 1024
TARGET_PEAK = 0.98
CLIP_LEVEL = 1.0
CONFIG_FILE = Path.home() / ".audiorecorder_config.json"
SR_CANDIDATES = [48000, 44100, 32000, 24000]
GAIN_MIN, GAIN_MAX, GAIN_DEFAULT = 0.0, 3.0, 1.0

# Monospace font & fixed-width formatter for jitter-free labels
MONO_FONT = ("Consolas", 10)  # falls back to system monospace if Consolas missing


def _format_gain_text(val: float) -> str:
    """Stable-width text for gain labels, so the label never resizes."""
    if val <= 0:
        db_str = " -inf"
    else:
        db = 20 * log10(val)
        db_str = f"{db:+4.1f}"  # e.g., "+0.0", "-6.0"
    # value field width=5, dB field width=5; right-aligned sign/padding
    return f"{val:5.2f}× ({db_str:>5} dB)"


# ================== Helpers ==================
def as_2d(x: np.ndarray) -> np.ndarray:
    return x.reshape(-1, 1) if x.ndim == 1 else x

def to_int16(x: np.ndarray) -> np.ndarray:
    return (np.clip(x, -1.0, 1.0) * 32767).astype(np.int16)

def resample_to(x: np.ndarray, src_sr: int, dst_sr: int) -> np.ndarray:
    if src_sr == dst_sr or x.size == 0:
        return x.astype(np.float32, copy=False)
    x2d = as_2d(x).astype(np.float32, copy=False)
    g = gcd(src_sr, dst_sr)
    y = np.column_stack([resample_poly(x2d[:, c], dst_sr // g, src_sr // g) for c in range(x2d.shape[1])])
    return y if x.ndim == 2 else y[:, 0]

def float_from_int16_bytes(b: bytes, channels: int) -> np.ndarray:
    arr = np.frombuffer(b, dtype=np.int16)
    if channels > 1:
        arr = arr.reshape(-1, channels)
    return (arr.astype(np.float32) / 32767.0)

def fmt_dbfs(peak_lin: float) -> float:
    return -60.0 if peak_lin <= 1e-6 else max(-60.0, 20.0 * np.log10(peak_lin))


# ================== Device listing (PyAudio) ==================
def list_devices():
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
                "index": d["index"], "name": d["name"], "hostApi": d["hostApi"],
                "maxInputChannels": d["maxInputChannels"], "maxOutputChannels": d["maxOutputChannels"],
                "defaultSampleRate": int(d["defaultSampleRate"]), "isLoopbackDevice": d.get("isLoopbackDevice", False)
            }
            is_wasapi = wasapi_idx is not None and d["hostApi"] == wasapi_idx
            is_input = d["maxInputChannels"] > 0 and not info["isLoopbackDevice"]
            if is_input:
                inputs[info["name"]] = info
            if not is_input or is_wasapi:
                if d["maxOutputChannels"] > 0 and not info["isLoopbackDevice"]:
                    outputs[info["name"]] = info
                if info["isLoopbackDevice"]:
                    loopbacks[info["name"]] = info
    finally:
        p.terminate()
    return inputs, outputs, loopbacks


# ================== Controller (PyAudio) ==================
class RecordingController(threading.Thread):
    def __init__(self, command_queue, status_queue):
        super().__init__(daemon=True)
        self.command_queue, self.status_queue = command_queue, status_queue
        self.is_recording = False
        self.p = None; self.mic_stream = None; self.spk_stream = None
        self.mic_queue, self.loop_queue = queue.Queue(), queue.Queue()
        self.capture_sr_mic, self.capture_sr_spk = TARGET_SR, TARGET_SR
        self.mic_channels, self.loop_channels = 2, 2
        self.mic_gain, self.loop_gain = GAIN_DEFAULT, GAIN_DEFAULT
        self.output_path, self.ffmpeg_process = None, None
        self.processing_thread, self.mic_thread, self.loop_thread = None, None, None
        self.include_mic = True

    def _ensure_pyaudio(self):
        if self.p is None:
            self.p = pyaudio.PyAudio()

    def _find_supported(self, dev_idx, sr, ch, is_input):
        self._ensure_pyaudio()
        for sr_try in [sr] + SR_CANDIDATES:
            for ch_try in [ch, 2, 1]:
                try:
                    if self.p.is_format_supported(
                        sr_try,
                        input_device=dev_idx if is_input else None,
                        output_device=dev_idx if not is_input else None,
                        input_channels=ch_try if is_input else 0,
                        output_channels=ch_try if not is_input else 0,
                        input_format=pyaudio.paInt16
                    ):
                        return True, ch_try, sr_try
                except ValueError:
                    pass
        return False, 0, sr

    def _open_mic(self, mic_dev):
        ok, ch, sr = self._find_supported(mic_dev['index'], int(mic_dev['defaultSampleRate']), 2, True)
        if not ok:
            raise RuntimeError(f"Mic format not supported: {mic_dev['name']}")
        self.capture_sr_mic, self.mic_channels = sr, ch
        self.mic_stream = self.p.open(
            format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
            input_device_index=mic_dev['index'], frames_per_buffer=BLOCK
        )

    def _open_loopback(self, lb_dev):
        ok, ch, sr = self._find_supported(lb_dev['index'], int(lb_dev['defaultSampleRate']), 2, True)
        if not ok:
            raise RuntimeError(f"Loopback format not supported: {lb_dev['name']}")
        self.capture_sr_spk, self.loop_channels = sr, ch
        try:
            self.spk_stream = self.p.open(
                format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
                input_device_index=lb_dev['index'], frames_per_buffer=BLOCK, as_loopback=True
            )
        except TypeError:  # Fallback for older pyaudio builds
            self.spk_stream = self.p.open(
                format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
                input_device_index=lb_dev['index'], frames_per_buffer=BLOCK
            )

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
                self.mic_gain = float(args.get("mic_gain", self.mic_gain))
                self.loop_gain = float(args.get("loop_gain", self.loop_gain))

    def _teardown(self):
        for stream in [self.mic_stream, self.spk_stream]:
            if stream:
                try:
                    stream.stop_stream(); stream.close()
                except Exception:
                    pass
        if self.p:
            try:
                self.p.terminate()
            except Exception:
                pass
        self.p, self.mic_stream, self.spk_stream = None, None, None

    def start_recording(self, mic_device=None, spk_device=None, loopback_device=None, include_mic=True,
                        mic_gain=GAIN_DEFAULT, loop_gain=GAIN_DEFAULT, output_path=None, bitrate='192k'):
        self._ensure_pyaudio()
        if not output_path:
            return self.status_queue.put(("ERROR", "No output file selected."))
        if not spk_device and not loopback_device:
            return self.status_queue.put(("ERROR", "Select an output/loopback device."))
        if include_mic and not mic_device:
            return self.status_queue.put(("ERROR", "Select a microphone."))
        
        self.include_mic = include_mic
        self.mic_gain, self.loop_gain = float(mic_gain), float(loop_gain)
        self.output_path = output_path
        
        try:
            if loopback_device:
                lb_choice = loopback_device
            else:
                _, _, loopbacks = list_devices()
                if not loopbacks:
                    raise RuntimeError("No WASAPI loopback devices found.")
                base = spk_device['name']
                lb_choice = next((d for n, d in loopbacks.items() if n.startswith(base)),
                                 next(iter(loopbacks.values())))
            
            self._open_loopback(lb_choice)
            if self.include_mic:
                self._open_mic(mic_device)
            
            command = [
                'ffmpeg', '-y', '-f', 's16le', '-ar', str(TARGET_SR), '-ac', str(self.loop_channels),
                '-i', 'pipe:0', '-b:a', bitrate, self.output_path
            ]
            si = subprocess.STARTUPINFO() if sys.platform == "win32" else None
            if si:
                si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            self.ffmpeg_process = subprocess.Popen(
                command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE, startupinfo=si
            )
        except Exception:
            self.status_queue.put(("ERROR", f"Failed to start recording: {traceback.format_exc()}"))
            self._teardown()
            return

        self.is_recording = True
        self.status_queue.put(("STATUS", f"Recording to file...\nDevice: {lb_choice['name']}"))
        self.loop_thread = threading.Thread(target=self._read_loop, daemon=True); self.loop_thread.start()
        if self.include_mic:
            self.mic_thread = threading.Thread(target=self._read_mic, daemon=True); self.mic_thread.start()
        self.processing_thread = threading.Thread(target=self._processing_loop, daemon=True); self.processing_thread.start()

    def stop_recording(self):
        if not self.is_recording:
            return
        self.is_recording = False
        self.status_queue.put(("STATUS", "Finalizing file…"))
        for t in [self.mic_thread, self.loop_thread, self.processing_thread]:
            if t:
                t.join()
        try:
            if self.ffmpeg_process and self.ffmpeg_process.stdin:
                self.ffmpeg_process.stdin.close()
            if self.ffmpeg_process:
                _, stderr = self.ffmpeg_process.communicate(timeout=10)
                if self.ffmpeg_process.returncode != 0:
                    self.status_queue.put(("ERROR", f"FFmpeg error:\n{stderr.decode('utf-8','ignore')}"))
                else:
                    self.status_queue.put(("INFO", f"Recording saved to\n{self.output_path}"))
        except Exception as e:
            self.status_queue.put(("ERROR", f"Error finalizing file: {e}"))
        finally:
            self.ffmpeg_process, self.output_path = None, None
        self._teardown()
        self.status_queue.put(("STATUS", "Ready to record"))

    def _read_loop(self):
        while self.is_recording and self.spk_stream and self.spk_stream.is_active():
            try:
                data = self.spk_stream.read(BLOCK, exception_on_overflow=False)
                x = float_from_int16_bytes(data, self.spk_stream._channels)
                self.status_queue.put(("LEVEL", ("sys", np.max(np.abs(x * self.loop_gain)) if x.size else 0.0)))
                self.loop_queue.put(x)
            except Exception as e:
                if self.is_recording:
                    self.status_queue.put(("ERROR", f"Loopback read error: {e}"))

    def _read_mic(self):
        while self.is_recording and self.mic_stream and self.mic_stream.is_active():
            try:
                data = self.mic_stream.read(BLOCK, exception_on_overflow=False)
                x = float_from_int16_bytes(data, self.mic_stream._channels)
                self.status_queue.put(("LEVEL", ("mic", np.max(np.abs(x * self.mic_gain)) if x.size else 0.0)))
                self.mic_queue.put(x)
            except Exception as e:
                if self.is_recording:
                    self.status_queue.put(("ERROR", f"Mic read error: {e}"))
    
    def _get_chunk_from_queue(self, q, block_size, num_channels):
        """Tries to get a chunk from a queue, generating silence on failure."""
        try:
            return q.get_nowait()
        except queue.Empty:
            return np.zeros((block_size, num_channels), dtype=np.float32)

    def _processing_loop(self):
        """Real-time processing loop driven by a fixed-timer 'metronome'."""
        block_duration = BLOCK / self.capture_sr_spk  # Approximate duration of a chunk
        
        while self.is_recording:
            start_time = time.monotonic()
            
            # --- Get chunks or generate silence ---
            loop_raw = self._get_chunk_from_queue(self.loop_queue, BLOCK, self.loop_channels)
            mic_raw = np.zeros((BLOCK, self.mic_channels), dtype=np.float32)
            if self.include_mic:
                mic_raw = self._get_chunk_from_queue(self.mic_queue, BLOCK, self.mic_channels)

            # --- Process this time-slice ---
            self._process_and_write_chunk(loop_raw, mic_raw)
            
            # --- Sleep to maintain real-time pace ---
            elapsed = time.monotonic() - start_time
            sleep_duration = block_duration - elapsed
            if sleep_duration > 0:
                time.sleep(sleep_duration)
        
        # After stopping, drain any remaining queued audio
        while not self.loop_queue.empty() or not self.mic_queue.empty():
            loop_raw = self._get_chunk_from_queue(self.loop_queue, BLOCK, self.loop_channels)
            mic_raw = (self._get_chunk_from_queue(self.mic_queue, BLOCK, self.mic_channels)
                       if self.include_mic else np.zeros((BLOCK, self.mic_channels), dtype=np.float32))
            self._process_and_write_chunk(loop_raw, mic_raw)

    def _process_and_write_chunk(self, loop_raw, mic_raw):
        """Processes and writes a single, synchronized chunk of audio data."""
        loop_rs = resample_to(loop_raw, self.capture_sr_spk, TARGET_SR) * self.loop_gain
        mic_rs = resample_to(mic_raw, self.capture_sr_mic, TARGET_SR) * self.mic_gain

        if loop_rs.size > 0 and np.max(np.abs(loop_rs)) >= CLIP_LEVEL:
            self.status_queue.put(("CLIP", "sys"))
        if mic_rs.size > 0 and np.max(np.abs(mic_rs)) >= CLIP_LEVEL:
            self.status_queue.put(("CLIP", "mic"))

        max_len = max(len(as_2d(mic_rs)), len(as_2d(loop_rs)))
        out_ch = self.loop_channels
        mic_buf = np.zeros((max_len, out_ch), dtype=np.float32)
        if mic_rs.size > 0:
            mic_buf[:len(as_2d(mic_rs)), :min(out_ch, as_2d(mic_rs).shape[1])] = as_2d(mic_rs)[:, :out_ch]
        loop_buf = np.zeros((max_len, out_ch), dtype=np.float32)
        if loop_rs.size > 0:
            loop_buf[:len(as_2d(loop_rs)), :] = as_2d(loop_rs)
        mixed = mic_buf + loop_buf
        
        peak = np.max(np.abs(mixed)) if mixed.size else 0.0
        if peak > TARGET_PEAK:
            mixed *= (TARGET_PEAK / peak)

        if mixed.size > 0 and self.ffmpeg_process and self.ffmpeg_process.stdin:
            try:
                self.ffmpeg_process.stdin.write(to_int16(mixed).tobytes())
            except (BrokenPipeError, OSError):
                if self.is_recording:
                    self.status_queue.put(("ERROR", "FFmpeg pipe broke."))
                self.is_recording = False


# ================== Tk App (Modernized UI, jitter-free labels) ==================
class AudioRecorderApp:
    MP3_QUALITY_PRESETS = {
        "Good (192kbps)": "192k",
        "Standard Voice (128kbps)": "128k",
        "Low (96kbps)": "96k",
        "Lowest - Fast (64kbps)": "64k"
    }
    
    def __init__(self, root):
        self.root = root
        self.root.title("Audio Recorder → MP3")
        self._init_theme()
        self._init_scaling()

        # Larger default + resizable
        self.root.geometry("960x640")
        self.root.minsize(840, 560)
        self.root.resizable(True, True)

        sys.excepthook = self._tk_exception_hook
        self.root.report_callback_exception = self._tk_exception_hook
        self.command_queue = queue.Queue(); self.status_queue = queue.Queue()
        self.controller = RecordingController(self.command_queue, self.status_queue); self.controller.start()
        self._poll_id = None; self.mic_clip_after_id = None; self.sys_clip_after_id = None
        self.mic_devices, self.spk_devices, self.lb_devices = {}, {}, {}
        self.selected_mic_name = tk.StringVar(); self.selected_spk_name = tk.StringVar()
        self.selected_lb_name = tk.StringVar(value="Auto (match speaker)")
        self.output_directory = tk.StringVar()
        self.mic_gain_var = tk.DoubleVar(value=GAIN_DEFAULT); self.loop_gain_var = tk.DoubleVar(value=GAIN_DEFAULT)
        self.include_mic_var = tk.BooleanVar(value=True)
        self.mp3_quality_var = tk.StringVar(value=next(iter(self.MP3_QUALITY_PRESETS)))
        self._create_ui()
        self._style_widgets()
        self.populate_device_lists()
        self._load_settings()
        self._schedule_poll()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

        # Shortcuts
        self.root.bind("<Control-r>", lambda _: self.start_clicked())
        self.root.bind("<Control-s>", lambda _: self.stop_clicked())
        self.root.bind("<Control-l>", lambda _: self.browse_directory())

    # --- Theme/scaling ---
    def _init_theme(self):
        if sv_ttk:
            sv_ttk.set_theme("dark")
        else:
            try:
                ttk.Style().theme_use("clam")
            except Exception:
                pass

    def _init_scaling(self):
        try:
            self.root.tk.call("tk", "scaling", self.root.tk.call("tk", "scaling"))
        except Exception:
            pass

    def toggle_theme(self):
        if not sv_ttk:
            return
        sv_ttk.set_theme("light" if sv_ttk.get_theme() == "dark" else "dark")

    # --- UI ---
    def _tk_exception_hook(self, exc, val, tb):
        messagebox.showerror("Unhandled Error", "".join(traceback.format_exception(exc, val, tb)))

    def _create_ui(self):
        container = ttk.Frame(self.root, padding=16)
        container.pack(expand=True, fill=tk.BOTH)

        # Header
        header = ttk.Frame(container)
        header.pack(fill=tk.X, pady=(0, 10))
        title = ttk.Label(header, text="Audio Recorder", font=("-size", 18, "-weight", "bold"))
        subtitle = ttk.Label(header, text="Capture system audio + mic to MP3 with live meters & gains")
        title.pack(side=tk.LEFT)
        subtitle.pack(side=tk.LEFT, padx=(12, 0))
        ttk.Button(header, text="Toggle theme", command=self.toggle_theme, style="Tertiary.TButton").pack(side=tk.RIGHT)

        ttk.Separator(container).pack(fill=tk.X, pady=6)

        # Top row: Devices + Output
        top = ttk.Frame(container)
        top.pack(fill=tk.BOTH, expand=True)

        devf = ttk.Labelframe(top, text="Audio Devices", padding=12)
        devf.grid(row=0, column=0, sticky="nsew", padx=(0, 8), pady=(0, 8))

        ttk.Label(devf, text="Microphone (Input)").grid(row=0, column=0, sticky="w", pady=(0, 4))
        self.mic_combo = ttk.Combobox(devf, textvariable=self.selected_mic_name, state="readonly")
        self.mic_combo.grid(row=1, column=0, sticky="ew")

        ttk.Label(devf, text="Audio Output (to capture)").grid(row=2, column=0, sticky="w", pady=(12, 4))
        self.spk_combo = ttk.Combobox(devf, textvariable=self.selected_spk_name, state="readonly")
        self.spk_combo.grid(row=3, column=0, sticky="ew")

        ttk.Label(devf, text="Loopback device (override)").grid(row=4, column=0, sticky="w", pady=(12, 4))
        self.lb_combo = ttk.Combobox(devf, textvariable=self.selected_lb_name, state="readonly")
        self.lb_combo.grid(row=5, column=0, sticky="ew")

        self.include_mic_check = ttk.Checkbutton(
            devf, text="Include microphone in recording", variable=self.include_mic_var
        )
        self.include_mic_check.grid(row=6, column=0, sticky="w", pady=(12, 0))

        devf.grid_columnconfigure(0, weight=1)

        outf = ttk.Labelframe(top, text="Output Settings", padding=12)
        outf.grid(row=0, column=1, sticky="nsew", padx=(8, 0), pady=(0, 8))

        ttk.Label(outf, text="Save Location").grid(row=0, column=0, sticky="w")
        out_row = ttk.Frame(outf); out_row.grid(row=1, column=0, sticky="ew", pady=(4, 0))
        self.output_entry = ttk.Entry(out_row, textvariable=self.output_directory, state='readonly')
        self.output_entry.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(out_row, text="Browse…", command=self.browse_directory).pack(side=tk.LEFT, padx=(8, 0))

        ttk.Label(outf, text="MP3 Quality").grid(row=2, column=0, sticky="w", pady=(12, 4))
        self.quality_combo = ttk.Combobox(outf, state="readonly", textvariable=self.mp3_quality_var,
                                          values=list(self.MP3_QUALITY_PRESETS.keys()))
        self.quality_combo.grid(row=3, column=0, sticky="ew")
        outf.grid_columnconfigure(0, weight=1)

        top.grid_columnconfigure(0, weight=1)
        top.grid_columnconfigure(1, weight=1)

        # Middle row: Gains + Meters
        mid = ttk.Frame(container)
        mid.pack(fill=tk.BOTH, expand=True)

        gains = ttk.Labelframe(mid, text="Live Gain Controls", padding=12)
        gains.grid(row=0, column=0, sticky="nsew", padx=(0, 8), pady=(0, 8))
        self._create_gain_slider(gains, "Mic Gain", self.mic_gain_var)
        self._create_gain_slider(gains, "Output Gain", self.loop_gain_var)

        meters = ttk.Labelframe(mid, text="Live Levels", padding=12)
        meters.grid(row=0, column=1, sticky="nsew", padx=(8, 0), pady=(0, 8))
        self.mic_bar, self.mic_db, self.mic_clip = self._create_meter_row(meters, "Mic")
        self.sys_bar, self.sys_db, self.sys_clip = self._create_meter_row(meters, "System")

        mid.grid_columnconfigure(0, weight=1)
        mid.grid_columnconfigure(1, weight=1)

        # Controls
        ctrl = ttk.Frame(container)
        ctrl.pack(pady=(4, 0), fill=tk.X)
        self.refresh_btn = ttk.Button(ctrl, text="Refresh Devices", command=self.populate_device_lists)
        self.refresh_btn.pack(side=tk.LEFT)
        ttk.Frame(ctrl).pack(side=tk.LEFT, expand=True, fill=tk.X)
        self.start_btn = ttk.Button(ctrl, text="Start Recording (Ctrl+R)", command=self.start_clicked, style="Accent.TButton")
        self.start_btn.pack(side=tk.LEFT, padx=(0, 8))
        self.stop_btn = ttk.Button(ctrl, text="Stop (Ctrl+S)", command=self.stop_clicked, state=tk.DISABLED)
        self.stop_btn.pack(side=tk.LEFT)

        # Status bar
        ttk.Separator(container).pack(fill=tk.X, pady=6)
        status_bar = ttk.Frame(container); status_bar.pack(fill=tk.X)
        self.status_label = ttk.Label(status_bar, text="Ready to record"); self.status_label.pack(side=tk.LEFT)

    def _style_widgets(self):
        style = ttk.Style()
        style.configure("Level.Horizontal.TProgressbar", thickness=14)
        if not sv_ttk:
            style.configure("Accent.TButton", font=("", 10, "bold"))

    def _create_gain_slider(self, parent, label_text, var):
        row = ttk.Frame(parent); row.pack(fill=tk.X, pady=6)
        ttk.Label(row, text=label_text, width=12).pack(side=tk.LEFT)
        scale = ttk.Scale(row, from_=GAIN_MIN, to=GAIN_MAX, orient="horizontal",
                          variable=var, command=self._on_gain_change)
        scale.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=10)
        # Jitter-free fixed-width label
        var.label_widget = ttk.Label(
            row,
            text=_format_gain_text(var.get()),
            width=18,            # character cells, not pixels
            font=MONO_FONT,
            anchor="e"
        )
        var.label_widget.pack(side=tk.LEFT, padx=(10, 0))

    def _create_meter_row(self, parent, label_text):
        r = ttk.Frame(parent); r.pack(fill=tk.X, pady=6)
        ttk.Label(r, text=label_text, width=10).pack(side=tk.LEFT)
        bar = ttk.Progressbar(r, orient="horizontal", mode="determinate",
                              maximum=100, style="Level.Horizontal.TProgressbar")
        bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=10)
        # Jitter-free dB label (monospace + fixed width)
        db_label = ttk.Label(r, text="-inf dBFS", width=12, font=MONO_FONT, anchor="e")
        db_label.pack(side=tk.LEFT, padx=(6, 0))
        clip_label = ttk.Label(r, text="CLIP"); clip_label.pack(side=tk.LEFT, padx=6)
        clip_label.configure(foreground="#9a9a9a")
        return bar, db_label, clip_label

    # --- Logic hooks ---
    def _on_gain_change(self, _=None):
        for var in [self.mic_gain_var, self.loop_gain_var]:
            var.label_widget.config(text=_format_gain_text(var.get()))
        self.command_queue.put(("SET_GAINS", {
            "mic_gain": self.mic_gain_var.get(),
            "loop_gain": self.loop_gain_var.get()
        }))

    def _schedule_poll(self):
        self._poll_id = self.root.after(100, self.process_status)

    def start_clicked(self):
        fname = f"rec-{datetime.now().strftime('%Y%m%d_%H%M%S')}.mp3"
        path = filedialog.asksaveasfilename(
            parent=self.root,
            initialdir=self.output_directory.get(),
            initialfile=fname,
            defaultextension=".mp3",
            filetypes=[("MP3 files", "*.mp3")]
        )
        if not path:
            return
        self.start_btn.config(state=tk.DISABLED); self.stop_btn.config(state=tk.NORMAL)
        self.command_queue.put(("START", {
            "output_path": path, "mic_device": self.mic_devices.get(self.selected_mic_name.get()),
            "spk_device": self.spk_devices.get(self.selected_spk_name.get()),
            "loopback_device": self.lb_devices.get(self.selected_lb_name.get()),
            "include_mic": self.include_mic_var.get(), "mic_gain": self.mic_gain_var.get(),
            "loop_gain": self.loop_gain_var.get(), "bitrate": self.MP3_QUALITY_PRESETS[self.mp3_quality_var.get()]
        }))

    def stop_clicked(self, *_):
        self.command_queue.put(("STOP", {}))

    def process_status(self):
        try:
            while True:
                msg, data = self.status_queue.get_nowait()
                if msg == "STATUS":
                    self.status_label.config(text=data)
                    if "Ready" in data:
                        self.start_btn.config(state=tk.NORMAL); self.stop_btn.config(state=tk.DISABLED)
                elif msg == "INFO":
                    messagebox.showinfo("Info", data)
                elif msg == "ERROR":
                    messagebox.showerror("Recording Error", data)
                elif msg == "LEVEL":
                    tag, peak = data
                    bar, label = (self.mic_bar, self.mic_db) if tag == "mic" else (self.sys_bar, self.sys_db)
                    bar['value'] = int(min(100, round(100.0 * peak, 1)))
                    # Stable width numeric formatting (5 chars incl. sign/space)
                    label.config(text=f"{fmt_dbfs(peak):>5.1f} dBFS")
                elif msg == "CLIP":
                    indicator, after_id = (
                        (self.mic_clip, self.mic_clip_after_id) if data == "mic" else (self.sys_clip, self.sys_clip_after_id)
                    )
                    if after_id:
                        self.root.after_cancel(after_id)
                    indicator.config(foreground="#ff4242")
                    new_id = self.root.after(1500, lambda ind=indicator: ind.config(foreground="#9a9a9a"))
                    if data == "mic":
                        self.mic_clip_after_id = new_id
                    else:
                        self.sys_clip_after_id = new_id
        except queue.Empty:
            pass
        finally:
            self._poll_id = self.root.after(100, self.process_status)

    # --- Settings & devices ---
    def _save_settings(self):
        settings = {
            "mic_device": self.selected_mic_name.get(), "spk_device": self.selected_spk_name.get(),
            "lb_device": self.selected_lb_name.get(), "mic_gain": self.mic_gain_var.get(),
            "loop_gain": self.loop_gain_var.get(), "include_mic": self.include_mic_var.get(),
            "output_dir": self.output_directory.get(), "mp3_quality": self.mp3_quality_var.get()
        }
        try:
            CONFIG_FILE.write_text(json.dumps(settings, indent=4))
        except Exception as e:
            print(f"Warning: Could not save settings: {e}", file=sys.stderr)

    def _load_settings(self):
        default_dir = Path.home() / "Music" / "Recordings"
        if not CONFIG_FILE.exists():
            if not default_dir.exists():
                default_dir.mkdir(parents=True, exist_ok=True)
            self.output_directory.set(str(default_dir))
            return
        try:
            settings = json.loads(CONFIG_FILE.read_text())
            if settings.get("mic_device") in self.mic_devices:
                self.selected_mic_name.set(settings["mic_device"])
            if settings.get("spk_device") in self.spk_devices:
                self.selected_spk_name.set(settings["spk_device"])
            if settings.get("lb_device") in self.lb_devices:
                self.selected_lb_name.set(settings["lb_device"])
            if settings.get("mp3_quality") in self.MP3_QUALITY_PRESETS:
                self.mp3_quality_var.set(settings["mp3_quality"])
            self.mic_gain_var.set(float(settings.get("mic_gain", GAIN_DEFAULT)))
            self.loop_gain_var.set(float(settings.get("loop_gain", GAIN_DEFAULT)))
            self.include_mic_var.set(bool(settings.get("include_mic", True)))
            saved_dir = settings.get("output_dir", str(default_dir))
            if Path(saved_dir).is_dir():
                self.output_directory.set(saved_dir)
            else:
                self.output_directory.set(str(default_dir))
                if not default_dir.exists():
                    default_dir.mkdir(parents=True, exist_ok=True)
            self._on_gain_change()
        except Exception as e:
            print(f"Warning: Could not load settings: {e}", file=sys.stderr)

    def browse_directory(self, *_):
        d = filedialog.askdirectory(initialdir=self.output_directory.get(), title="Choose default folder")
        if d:
            self.output_directory.set(d)

    def populate_device_lists(self):
        self.mic_devices, self.spk_devices, self.lb_devices = list_devices()
        p = pyaudio.PyAudio()
        try:
            default_mic = p.get_default_input_device_info()['name']
            default_spk = p.get_default_output_device_info()['name']
        except Exception:
            default_mic, default_spk = None, None
        finally:
            p.terminate()

        def first_or_default(devs, default_name):
            if not devs:
                return [], None
            names = list(devs.keys())
            return names, (default_name if default_name in devs else names[0])

        mic_names, mic_choice = first_or_default(self.mic_devices, default_mic)
        spk_names, spk_choice = first_or_default(self.spk_devices, default_spk)
        lb_names = ["Auto (match speaker)"] + list(self.lb_devices.keys())

        # Populate comboboxes
        self.mic_combo["values"] = mic_names
        self.spk_combo["values"] = spk_names
        self.lb_combo["values"] = lb_names

        self.selected_mic_name.set(mic_choice if mic_choice else "No devices found")
        self.selected_spk_name.set(spk_choice if spk_choice else "No devices found")
        if self.selected_lb_name.get() not in lb_names:
            self.selected_lb_name.set(lb_names[0])

    def on_close(self):
        self._save_settings()
        self.command_queue.put(("EXIT", {}))
        self.root.destroy()


if __name__ == "__main__":
    root = tk.Tk()
    app = AudioRecorderApp(root)
    root.mainloop()
