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

# Slider ranges
GAIN_MIN = 0.0
GAIN_MAX = 3.0
GAIN_DEFAULT = 1.0


# ================== Helpers ==================
def as_2d(x: np.ndarray) -> np.ndarray:
    if x.ndim == 1:
        return x.reshape(-1, 1)
    return x

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


# ================== Device listing (PyAudio) ==================
def list_devices():
    """
    Return three dicts:
      inputs   : all input-capable devices (not loopback)
      outputs  : render devices (output-capable, not loopback)
      loopbacks: devices flagged isLoopbackDevice=True (use these to capture system audio)
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
        self.command_queue = command_queue
        self.status_queue = status_queue
        self.is_recording = False
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

    def _ensure_pyaudio(self):
        if self.p is None: self.p = pyaudio.PyAudio()

    def _find_supported(self, device_index, suggested_sr, channels, is_input):
        self._ensure_pyaudio()
        for sr_try in [suggested_sr] + SR_CANDIDATES:
            for ch_try in [channels, 2, 1]:
                try:
                    self.p.is_format_supported(
                        sr_try,
                        input_device=device_index if is_input else None,
                        output_device=device_index if not is_input else None,
                        input_channels=ch_try if is_input else 0,
                        output_channels=ch_try if not is_input else 0,
                        input_format=pyaudio.paInt16
                    )
                    return True, ch_try, sr_try
                except ValueError: pass
        return False, 0, suggested_sr

    def _open_mic(self, mic_dev):
        ok, ch, sr = self._find_supported(mic_dev['index'], int(mic_dev['defaultSampleRate']), 2, is_input=True)
        if not ok: raise RuntimeError(f"Mic format not supported: {mic_dev['name']}")
        self.capture_sr_mic = sr
        self.mic_stream = self.p.open(
            format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
            input_device_index=mic_dev['index'], frames_per_buffer=BLOCK
        )

    def _open_loopback(self, lb_dev):
        ok, ch, sr = self._find_supported(lb_dev['index'], int(lb_dev['defaultSampleRate']), 2, is_input=True)
        if not ok: raise RuntimeError(f"Loopback format not supported: {lb_dev['name']}")
        self.capture_sr_spk = sr
        try:
            self.spk_stream = self.p.open(
                format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
                input_device_index=lb_dev['index'], frames_per_buffer=BLOCK, as_loopback=True
            )
        except TypeError: # Fallback for older pyaudio builds
            self.spk_stream = self.p.open(
                format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
                input_device_index=lb_dev['index'], frames_per_buffer=BLOCK
            )

    def run(self):
        while True:
            cmd, args = self.command_queue.get()
            if cmd == "START": self.start_recording(**args)
            elif cmd == "STOP": self.stop_recording()
            elif cmd == "EXIT":
                if self.is_recording: self.stop_recording()
                self._teardown()
                break
            elif cmd == "SET_GAINS":
                self.mic_gain = float(args.get("mic_gain", self.mic_gain))
                self.loop_gain = float(args.get("loop_gain", self.loop_gain))

    def _teardown(self):
        for stream in [self.mic_stream, self.spk_stream]:
            if stream:
                try: stream.stop_stream(); stream.close()
                except Exception: pass
        if self.p:
            try: self.p.terminate()
            except Exception: pass
        self.p, self.mic_stream, self.spk_stream = None, None, None

    def start_recording(self, mic_device=None, spk_device=None, loopback_device=None,
                        include_mic=True, mic_gain=GAIN_DEFAULT, loop_gain=GAIN_DEFAULT,
                        output_path=None, bitrate='192k'):
        self._ensure_pyaudio()
        if not output_path:
            return self.status_queue.put(("ERROR", "No output file was selected."))
        if not spk_device and not loopback_device:
            return self.status_queue.put(("ERROR", "Select an output or a loopback device."))
        if include_mic and not mic_device:
            return self.status_queue.put(("ERROR", "Select a microphone to include it."))

        self.mic_gain, self.loop_gain = float(mic_gain), float(loop_gain)
        self.output_path = output_path

        try:
            if loopback_device:
                lb_choice = loopback_device
            else:
                _, _, loopbacks = list_devices()
                if not loopbacks: raise RuntimeError("No WASAPI loopback devices found.")
                base = spk_device['name']
                lb_choice = next((d for n, d in loopbacks.items() if n.startswith(base)), next(iter(loopbacks.values())))

            self._open_loopback(lb_choice)
            if include_mic:
                self._open_mic(mic_device)

            out_channels = getattr(self.spk_stream, "_channels", 2)
            command = [
                'ffmpeg', '-y', '-f', 's16le', '-ar', str(TARGET_SR),
                '-ac', str(out_channels), '-i', 'pipe:0',
                '-b:a', bitrate, self.output_path
            ]
            startupinfo = None
            if sys.platform == "win32":
                startupinfo = subprocess.STARTUPINFO()
                startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            self.ffmpeg_process = subprocess.Popen(
                command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE, startupinfo=startupinfo
            )
        except Exception as e:
            self.status_queue.put(("ERROR", f"Failed to start recording: {traceback.format_exc()}"))
            self._teardown()
            return

        self.is_recording = True
        self.status_queue.put(("STATUS", f"Recording to file...\nDevice: {lb_choice['name']}"))
        self.loop_thread = threading.Thread(target=self._read_loop, daemon=True)
        self.loop_thread.start()
        if include_mic:
            self.mic_thread = threading.Thread(target=self._read_mic, daemon=True)
            self.mic_thread.start()
        self.processing_thread = threading.Thread(target=self._processing_loop, daemon=True)
        self.processing_thread.start()

    def stop_recording(self):
        if not self.is_recording: return
        self.is_recording = False
        self.status_queue.put(("STATUS", "Finalizing file…"))

        for t in [self.mic_thread, self.loop_thread, self.processing_thread]:
            if t: t.join()

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
                x = float_from_int16_bytes(data, self.spk_stream._channels) * self.loop_gain
                self.loop_queue.put(x)
                self.status_queue.put(("LEVEL", ("sys", np.max(np.abs(x)) if x.size else 0.0)))
            except Exception as e:
                if self.is_recording: self.status_queue.put(("ERROR", f"Loopback read error: {e}"))

    def _read_mic(self):
        while self.is_recording and self.mic_stream and self.mic_stream.is_active():
            try:
                data = self.mic_stream.read(BLOCK, exception_on_overflow=False)
                x = float_from_int16_bytes(data, self.mic_stream._channels) * self.mic_gain
                self.mic_queue.put(x)
                self.status_queue.put(("LEVEL", ("mic", np.max(np.abs(x)) if x.size else 0.0)))
            except Exception as e:
                if self.is_recording: self.status_queue.put(("ERROR", f"Mic read error: {e}"))

    def _drain(self, q):
        chunks = []
        while not q.empty():
            try: chunks.append(q.get_nowait())
            except queue.Empty: break
        return np.concatenate(chunks, axis=0) if chunks else np.array([])

    def _processing_loop(self):
        while self.is_recording:
            self._process_chunk()
            time.sleep(0.05)
        self._process_chunk() # Final drain

    def _process_chunk(self):
        loop_chunks = self._drain(self.loop_queue)
        mic_chunks = self._drain(self.mic_queue)

        if loop_chunks.size == 0 and mic_chunks.size == 0: return

        loop_rs = resample_to(loop_chunks, self.capture_sr_spk, TARGET_SR)
        mic_rs = resample_to(mic_chunks, self.capture_sr_mic, TARGET_SR)

        max_len = max(len(as_2d(mic_rs)), len(as_2d(loop_rs)))
        out_ch = as_2d(loop_rs).shape[1] if loop_rs.size > 0 else 2
        
        mic_buf = np.zeros((max_len, out_ch), dtype=np.float32)
        if mic_rs.size > 0: mic_buf[:len(as_2d(mic_rs)), :min(out_ch, as_2d(mic_rs).shape[1])] = as_2d(mic_rs)[:,:out_ch]

        loop_buf = np.zeros((max_len, out_ch), dtype=np.float32)
        if loop_rs.size > 0: loop_buf[:len(as_2d(loop_rs)), :] = as_2d(loop_rs)

        mixed = mic_buf + loop_buf
        peak = np.max(np.abs(mixed)) if mixed.size else 0.0
        if peak > TARGET_PEAK: mixed *= (TARGET_PEAK / peak)

        if mixed.size > 0 and self.ffmpeg_process and self.ffmpeg_process.stdin:
            try:
                self.ffmpeg_process.stdin.write(to_int16(mixed).tobytes())
            except (BrokenPipeError, OSError):
                if self.is_recording: self.status_queue.put(("ERROR", "FFmpeg pipe broke."))
                self.is_recording = False


# ================== Tk App ==================
class AudioRecorderApp:
    MP3_QUALITY_PRESETS = {
        "Good (192kbps)": "192k",
        "Standard Voice (128kbps)": "128k",
        "Low (96kbps)": "96k",
        "Lowest - Fast (64kbps)": "64k"
    }
    
    def __init__(self, root):
        self.root = root
        self.root.title("Audio Recorder -> MP3")
        self.root.geometry("860x600")
        self.root.resizable(False, False)

        sys.excepthook = self._tk_exception_hook
        self.root.report_callback_exception = self._tk_exception_hook

        self.command_queue = queue.Queue()
        self.status_queue = queue.Queue()
        self.controller = RecordingController(self.command_queue, self.status_queue)
        self.controller.start()

        self._poll_id = None
        self.mic_devices, self.spk_devices, self.lb_devices = {}, {}, {}
        self.selected_mic_name = tk.StringVar()
        self.selected_spk_name = tk.StringVar()
        self.selected_lb_name = tk.StringVar(value="Auto (match speaker)")
        self.output_directory = tk.StringVar()
        self.mic_gain_var = tk.DoubleVar(value=GAIN_DEFAULT)
        self.loop_gain_var = tk.DoubleVar(value=GAIN_DEFAULT)
        self.include_mic_var = tk.BooleanVar(value=True)
        self.mp3_quality_var = tk.StringVar(value=next(iter(self.MP3_QUALITY_PRESETS)))

        self._create_ui()
        self.populate_device_lists()
        self._load_settings()
        self._schedule_poll()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

    def _tk_exception_hook(self, exc, val, tb):
        msg = "".join(traceback.format_exception(exc, val, tb))
        messagebox.showerror("Unhandled Error", msg)

    def _create_ui(self):
        main = tk.Frame(self.root, padx=15, pady=15)
        main.pack(expand=True, fill=tk.BOTH)

        # --- Top row: Devices and Output Settings ---
        top_row = tk.Frame(main)
        top_row.pack(fill=tk.X, expand=True, pady=(0, 10))
        top_row.grid_columnconfigure(0, weight=1)
        top_row.grid_columnconfigure(1, weight=1)
        
        # --- Device Selection ---
        devf = tk.LabelFrame(top_row, text="Audio Devices", padx=10, pady=10)
        devf.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        tk.Label(devf, text="Microphone (Input):").grid(row=0, column=0, sticky="w")
        self.mic_menu = tk.OptionMenu(devf, self.selected_mic_name, "...")
        self.mic_menu.grid(row=1, column=0, sticky="ew", pady=(0, 5))
        tk.Label(devf, text="Audio Output (device to capture):").grid(row=2, column=0, sticky="w")
        self.spk_menu = tk.OptionMenu(devf, self.selected_spk_name, "...")
        self.spk_menu.grid(row=3, column=0, sticky="ew", pady=(0, 5))
        tk.Label(devf, text="Loopback device (override):").grid(row=4, column=0, sticky="w")
        self.lb_menu = tk.OptionMenu(devf, self.selected_lb_name, "...")
        self.lb_menu.grid(row=5, column=0, sticky="ew")
        tk.Checkbutton(devf, text="Include microphone in recording", variable=self.include_mic_var)\
            .grid(row=6, column=0, sticky="w", pady=(10, 0))

        # --- Output Settings ---
        outf = tk.LabelFrame(top_row, text="Output Settings", padx=10, pady=10)
        outf.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        tk.Label(outf, text="Save Location:").grid(row=0, column=0, sticky="w")
        tk.Entry(outf, textvariable=self.output_directory, state='readonly').grid(row=1, column=0, sticky="ew", padx=(0, 5))
        self.browse_btn = tk.Button(outf, text="Browse...", command=self.browse_directory)
        self.browse_btn.grid(row=1, column=1, sticky="e")
        tk.Label(outf, text="MP3 Quality:").grid(row=2, column=0, sticky="w", pady=(8,0))
        qual_menu = tk.OptionMenu(outf, self.mp3_quality_var, *self.MP3_QUALITY_PRESETS.keys())
        qual_menu.grid(row=3, column=0, columnspan=2, sticky="ew")
        outf.grid_columnconfigure(0, weight=1)

        # --- Bottom row: Gain and Levels ---
        bottom_row = tk.Frame(main)
        bottom_row.pack(fill=tk.X, expand=True, pady=10)
        bottom_row.grid_columnconfigure(0, weight=1)
        bottom_row.grid_columnconfigure(1, weight=1)

        # --- Gain Controls ---
        gains = tk.LabelFrame(bottom_row, text="Live Gain Controls", padx=10, pady=10)
        gains.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        self._create_gain_slider(gains, "Mic Gain", self.mic_gain_var)
        self._create_gain_slider(gains, "Output Gain", self.loop_gain_var)

        # --- Live Levels ---
        meters = tk.LabelFrame(bottom_row, text="Live Levels", padx=10, pady=10)
        meters.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        self.mic_bar, self.mic_db = self._create_meter_row(meters, "Mic:")
        self.sys_bar, self.sys_db = self._create_meter_row(meters, "System:")

        # --- Action Buttons & Status (at the bottom) ---
        self.refresh_btn = tk.Button(main, text="Refresh Devices", command=self.populate_device_lists)
        self.refresh_btn.pack(pady=5)
        self.status_label = tk.Label(main, text="Ready to record", font=("Arial", 12))
        self.status_label.pack(pady=5)
        btnrow = tk.Frame(main); btnrow.pack(pady=6)
        self.start_btn = tk.Button(btnrow, text="Start Recording", command=self.start_clicked)
        self.stop_btn = tk.Button(btnrow, text="Stop Recording", command=self.stop_clicked, state=tk.DISABLED)
        self.start_btn.pack(side=tk.LEFT, padx=5)
        self.stop_btn.pack(side=tk.LEFT, padx=5)
    
    def _create_gain_slider(self, parent, label_text, var):
        row = tk.Frame(parent); row.pack(fill=tk.X, pady=2)
        tk.Label(row, text=label_text, width=10).pack(side=tk.LEFT)
        scale = ttk.Scale(row, from_=GAIN_MIN, to=GAIN_MAX, orient="horizontal", variable=var, command=self._on_gain_change)
        scale.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        var.label_widget = tk.Label(row, text=f"{var.get():.2f}×", width=12)
        var.label_widget.pack(side=tk.LEFT, padx=(6, 0))

    def _create_meter_row(self, parent, label_text):
        r = tk.Frame(parent); r.pack(fill=tk.X, pady=2)
        tk.Label(r, text=label_text, width=8, anchor="e").pack(side=tk.LEFT)
        bar = ttk.Progressbar(r, orient="horizontal", mode="determinate", maximum=100)
        bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        db_label = tk.Label(r, text="-inf dBFS", width=10, anchor="w")
        db_label.pack(side=tk.LEFT)
        return bar, db_label
    
    def _on_gain_change(self, _=None):
        for var in [self.mic_gain_var, self.loop_gain_var]:
            db = "-inf" if var.get() <= 0 else f"{20 * log10(var.get()):.1f}"
            var.label_widget.config(text=f"{var.get():.2f}× ({db} dB)")
        self.command_queue.put(("SET_GAINS", {
            "mic_gain": self.mic_gain_var.get(), "loop_gain": self.loop_gain_var.get()
        }))

    def _schedule_poll(self):
        self._poll_id = self.root.after(100, self.process_status)

    def start_clicked(self):
        default_filename = f"rec-{datetime.now().strftime('%Y%m%d_%H%M%S')}.mp3"
        path = filedialog.asksaveasfilename(
            parent=self.root, initialdir=self.output_directory.get(), initialfile=default_filename,
            defaultextension=".mp3", filetypes=[("MP3 files", "*.mp3")], title="Save recording as..."
        )
        if not path: return

        self.start_btn.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)
        
        self.command_queue.put(("START", {
            "output_path": path,
            "mic_device": self.mic_devices.get(self.selected_mic_name.get()),
            "spk_device": self.spk_devices.get(self.selected_spk_name.get()),
            "loopback_device": self.lb_devices.get(self.selected_lb_name.get()),
            "include_mic": self.include_mic_var.get(),
            "mic_gain": self.mic_gain_var.get(), "loop_gain": self.loop_gain_var.get(),
            "bitrate": self.MP3_QUALITY_PRESETS[self.mp3_quality_var.get()]
        }))

    def stop_clicked(self):
        self.command_queue.put(("STOP", {}))

    def process_status(self):
        try:
            while True:
                msg, data = self.status_queue.get_nowait()
                if msg == "STATUS":
                    self.status_label.config(text=data)
                    if "Ready" in data:
                        self.start_btn.config(state=tk.NORMAL)
                        self.stop_btn.config(state=tk.DISABLED)
                elif msg == "INFO": messagebox.showinfo("Info", data)
                elif msg == "ERROR": messagebox.showerror("Recording Error", data)
                elif msg == "LEVEL":
                    tag, peak = data
                    bar, label = (self.mic_bar, self.mic_db) if tag == "mic" else (self.sys_bar, self.sys_db)
                    db = fmt_dbfs(peak)
                    bar['value'] = int(min(100, round(100.0 * peak, 1)))
                    label.config(text=f"{db:.1f} dBFS")
        except queue.Empty:
            pass
        finally:
            self._poll_id = self.root.after(100, self.process_status)

    def _save_settings(self):
        settings = {
            "mic_device": self.selected_mic_name.get(),
            "spk_device": self.selected_spk_name.get(),
            "lb_device": self.selected_lb_name.get(),
            "mic_gain": self.mic_gain_var.get(),
            "loop_gain": self.loop_gain_var.get(),
            "include_mic": self.include_mic_var.get(),
            "output_dir": self.output_directory.get(),
            "mp3_quality": self.mp3_quality_var.get()
        }
        try:
            CONFIG_FILE.write_text(json.dumps(settings, indent=4))
        except Exception as e: print(f"Warning: Could not save settings: {e}", file=sys.stderr)

    def _load_settings(self):
        default_dir = Path.home() / "Music" / "Recordings"
        if not CONFIG_FILE.exists():
            if not default_dir.exists():
                default_dir.mkdir(parents=True, exist_ok=True)
            self.output_directory.set(str(default_dir))
            return
        
        try:
            settings = json.loads(CONFIG_FILE.read_text())
            if settings.get("mic_device") in self.mic_devices: self.selected_mic_name.set(settings["mic_device"])
            if settings.get("spk_device") in self.spk_devices: self.selected_spk_name.set(settings["spk_device"])
            if settings.get("lb_device") in self.lb_devices: self.selected_lb_name.set(settings["lb_device"])
            if settings.get("mp3_quality") in self.MP3_QUALITY_PRESETS: self.mp3_quality_var.set(settings["mp3_quality"])
                
            self.mic_gain_var.set(float(settings.get("mic_gain", GAIN_DEFAULT)))
            self.loop_gain_var.set(float(settings.get("loop_gain", GAIN_DEFAULT)))
            self.include_mic_var.set(bool(settings.get("include_mic", True)))
            
            saved_dir = settings.get("output_dir", str(default_dir))
            if Path(saved_dir).exists():
                self.output_directory.set(saved_dir)
            else:
                self.output_directory.set(str(default_dir))
                if not default_dir.exists(): default_dir.mkdir(parents=True, exist_ok=True)

            self._on_gain_change()
        except Exception as e: print(f"Warning: Could not load settings: {e}", file=sys.stderr)

    def browse_directory(self):
        d = filedialog.askdirectory(initialdir=self.output_directory.get(), title="Choose default folder")
        if d: self.output_directory.set(d)

    def populate_device_lists(self):
        self.mic_devices, self.spk_devices, self.lb_devices = list_devices()
        
        p = pyaudio.PyAudio()
        try:
            default_mic = p.get_default_input_device_info()['name']
            default_spk = p.get_default_output_device_info()['name']
        except Exception: default_mic, default_spk = None, None
        finally: p.terminate()

        def _populate(menu, var, devices, default_name):
            menu['menu'].delete(0, 'end')
            if devices:
                for name in devices: menu['menu'].add_command(label=name, command=lambda v=name: var.set(v))
                var.set(default_name if default_name in devices else next(iter(devices), ""))
            else: var.set("No devices found")

        _populate(self.mic_menu, self.selected_mic_name, self.mic_devices, default_mic)
        _populate(self.spk_menu, self.selected_spk_name, self.spk_devices, default_spk)
        
        self.lb_menu['menu'].delete(0, 'end')
        self.lb_menu['menu'].add_command(label="Auto (match speaker)", command=lambda: self.selected_lb_name.set("Auto (match speaker)"))
        for name in self.lb_devices: self.lb_menu['menu'].add_command(label=name, command=lambda v=name: self.selected_lb_name.set(v))

    def on_close(self):
        self._save_settings()
        self.command_queue.put(("EXIT", {}))
        self.root.destroy()

if __name__ == "__main__":
    root = tk.Tk()
    app = AudioRecorderApp(root)
    root.mainloop()
