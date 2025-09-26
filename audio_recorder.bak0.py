# file: audio_recorder.py

# --- NumPy 2.x compatibility shim for legacy binary np.fromstring callers ---
# (soundcard uses np.fromstring on a CFFI buffer; this restores binary-mode via frombuffer)
import numpy as np
_old_fromstring = np.fromstring
def _compat_fromstring(string, dtype=float, count=-1, sep=""):
    if sep == "" or sep is None:
        try:
            mv = memoryview(string)  # works for bytes/bytearray/CFFI buffers
        except TypeError:
            pass
        else:
            return np.frombuffer(mv, dtype=dtype, count=count)
    return _old_fromstring(string, dtype=dtype, count=count, sep=sep)
np.fromstring = _compat_fromstring
# --- end shim ---

import sys
import tkinter as tk
import time
from tkinter import messagebox, filedialog, ttk
import soundcard as sc
from scipy.io.wavfile import write as write_wav
import threading
import queue
import os
from pathlib import Path
from datetime import datetime
from contextlib import contextmanager
import traceback

# ---------- Mix / limiter settings (tweak if you like) ----------
MIC_GAIN = 0.9          # pre-gain for mic stream
LOOP_GAIN = 0.9         # pre-gain for system/loopback stream
TARGET_PEAK = 0.98      # final peak-normalization target
LIMITER_DRIVE = 1.5     # soft-clip drive; higher -> more limiting (1.0–2.0 is sane)
MIN_ACTIVE_PEAK = 1e-4  # below this, a stream is "very low" (likely muted/off)

# ------------------ Controller thread (soundcard-based) ------------------

class RecordingController(threading.Thread):
    """
    Records mic (Microphone) and system-audio loopback (Speaker -> loopback Microphone) using soundcard.
    """
    def __init__(self, command_queue, status_queue):
        super().__init__(daemon=True)
        self.command_queue = command_queue
        self.status_queue = status_queue
        self.is_recording = False
        self.mic_queue = queue.Queue()
        self.loopback_queue = queue.Queue()
        self.mic_thread = None
        self.loopback_thread = None

    def run(self):
        while True:
            command, args = self.command_queue.get()
            if command == "START":
                self.start_recording(**args)
            elif command == "STOP":
                self.stop_recording()
            elif command == "EXIT":
                if self.is_recording:
                    self.stop_recording()
                break

    def start_recording(self, mic_device=None, speaker_device=None):
        """
        mic_device: sc.Microphone (physical mic)
        speaker_device: sc.Speaker (playback device to capture via loopback)
        """
        if mic_device is None or speaker_device is None:
            self.status_queue.put(("ERROR", "Internal error: missing mic or speaker device."))
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        # Fast type sanity (helps catch wrong wiring)
        if not hasattr(mic_device, "recorder"):
            self.status_queue.put(("ERROR", f"Selected mic isn't a soundcard Microphone object: {type(mic_device)}"))
            self.status_queue.put(("STATUS", "Ready to record"))
            return
        if not hasattr(speaker_device, "id"):
            self.status_queue.put(("ERROR", f"Selected speaker isn't a soundcard Speaker object: {type(speaker_device)}"))
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        # Convert Speaker -> loopback Microphone
        try:
            loopback_mic = sc.get_microphone(id=speaker_device.id, include_loopback=True)
        except Exception as e:
            self.status_queue.put(("ERROR", f"Loopback not available for '{getattr(speaker_device, 'name', '<unknown>')}':\n{e}"))
            self.status_queue.put(("STATUS", "Ready to record"))
            return

        self.is_recording = True
        self.status_queue.put(("STATUS", "Recording..."))

        # Launch threads (both arguments expose .recorder())
        self.mic_thread = threading.Thread(
            target=self._record_audio, args=(mic_device, self.mic_queue, "mic"), daemon=True
        )
        self.loopback_thread = threading.Thread(
            target=self._record_audio, args=(loopback_mic, self.loopback_queue, "sys"), daemon=True
        )
        self.mic_thread.start()
        self.loopback_thread.start()

    def stop_recording(self):
        if not self.is_recording:
            return
        self.is_recording = False
        self.status_queue.put(("STATUS", "Processing audio..."))

        if self.mic_thread:
            self.mic_thread.join()
        if self.loopback_thread:
            self.loopback_thread.join()

        self.process_audio()

    def _record_audio(self, mic_like_device, data_queue, tag):
        """mic_like_device must be an sc.Microphone (physical or loopback). tag in {'mic','sys'}."""
        try:
            with mic_like_device.recorder(samplerate=48000) as recorder:
                last = 0.0
                while self.is_recording:
                    data = recorder.record(numframes=1024)
                    data_queue.put(data.copy())
                    # Throttle level updates to ~10 Hz
                    now = time.time()
                    if now - last >= 0.1:
                        peak = float(np.max(np.abs(data))) if data.size else 0.0
                        self.status_queue.put(("LEVEL", (tag, peak)))
                        last = now
        except Exception as e:
            if self.is_recording:
                name = getattr(mic_like_device, "name", "<unknown mic>")
                self.status_queue.put(("ERROR", f"Error on device {name}:\n{e}"))

    @staticmethod
    def _drain_queue(q: queue.Queue):
        frames = []
        while True:
            try:
                frames.append(q.get_nowait())
            except queue.Empty:
                break
        return frames

    def process_audio(self):
        mic_frames = self._drain_queue(self.mic_queue)
        loopback_frames = self._drain_queue(self.loopback_queue)

        if not mic_frames or not loopback_frames:
            self.status_queue.put(("STATUS", "Ready to record"))
            self.status_queue.put(("INFO", "One stream was empty. Nothing saved."))
            return

        mic_np = np.concatenate(mic_frames, axis=0).astype(np.float32, copy=False)
        loopback_np = np.concatenate(loopback_frames, axis=0).astype(np.float32, copy=False)

        # Ensure stereo
        def ensure_stereo(x):
            if x.ndim == 1:
                x = np.column_stack([x, x])
            elif x.ndim == 2 and x.shape[1] == 1:
                x = np.repeat(x, 2, axis=1)
            return x

        mic_np = ensure_stereo(mic_np)
        loopback_np = ensure_stereo(loopback_np)

        # Align lengths (truncate to shortest)
        min_len = min(len(mic_np), len(loopback_np))
        mic_np = mic_np[:min_len]
        loopback_np = loopback_np[:min_len]

        # Pre-gain each stream (user-tweakable)
        mic_np *= MIC_GAIN
        loopback_np *= LOOP_GAIN

        # Diagnostics: show peaks so you can see if mic is present/too quiet
        mic_peak = float(np.max(np.abs(mic_np))) if mic_np.size else 0.0
        loop_peak = float(np.max(np.abs(loopback_np))) if loopback_np.size else 0.0
        self.status_queue.put(("INFO", f"Mic peak: {mic_peak:.3f} | System peak: {loop_peak:.3f}"))

        if mic_peak < MIN_ACTIVE_PEAK:
            self.status_queue.put(("INFO", "Mic level is extremely low. Check OS input level / mute / device choice."))

        # Mix
        mixed = mic_np + loopback_np

        # Soft limiter: gentle saturation to avoid harsh clipping
        if LIMITER_DRIVE is not None and LIMITER_DRIVE > 0:
            mixed = np.tanh(mixed * LIMITER_DRIVE) / np.tanh(LIMITER_DRIVE)

        # Peak normalize if needed
        peak = float(np.max(np.abs(mixed))) if mixed.size else 0.0
        if peak > 1e-9 and peak > TARGET_PEAK:
            mixed *= (TARGET_PEAK / peak)

        # Convert to int16 and hand back to UI to save
        mixed_i16 = (np.clip(mixed, -1.0, 1.0) * 32767).astype(np.int16)
        self.status_queue.put(("SAVE_FILE", mixed_i16))


# ------------------ Pure-Tk directory chooser ------------------

class DirectoryChooser(tk.Toplevel):
    """A simple, crash-proof directory chooser using only Tk widgets."""
    def __init__(self, parent, initial_path: Path):
        super().__init__(parent)
        self.title("Choose a folder for recordings")
        self.transient(parent)
        self.grab_set()
        self.resizable(True, True)

        self.parent = parent
        self.current = tk.StringVar(value=str(initial_path if initial_path.exists() else Path.home()))
        self.selected = None

        top = tk.Frame(self, padx=10, pady=10)
        top.pack(fill=tk.BOTH, expand=True)

        path_row = tk.Frame(top)
        path_row.pack(fill=tk.X)
        tk.Label(path_row, text="Path:").pack(side=tk.LEFT)
        self.path_entry = tk.Entry(path_row, textvariable=self.current)
        self.path_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(5, 5))
        tk.Button(path_row, text="Go", command=self._go_to_entry).pack(side=tk.LEFT)
        tk.Button(path_row, text="Up", command=self._go_up).pack(side=tk.LEFT, padx=(5, 0))

        self.listbox = tk.Listbox(top, selectmode=tk.BROWSE)
        self.listbox.pack(fill=tk.BOTH, expand=True, pady=(8, 8))
        self.listbox.bind("<Double-Button-1>", self._enter_dir)

        btns = tk.Frame(top)
        btns.pack(fill=tk.X)
        tk.Button(btns, text="Select", command=self._select).pack(side=tk.RIGHT)
        tk.Button(btns, text="Cancel", command=self._cancel).pack(side=tk.RIGHT, padx=(0, 5))

        self._populate()
        self.minsize(500, 350)
        self._center_on_parent()

    def _center_on_parent(self):
        self.update_idletasks()
        rp = self.parent.winfo_rootx(), self.parent.winfo_rooty()
        rw = self.parent.winfo_width()
        rh = self.parent.winfo_height()
        w = self.winfo_width()
        h = self.winfo_height()
        x = rp[0] + (rw - w) // 2
        y = rp[1] + (rh - h) // 2
        self.geometry(f"+{max(0,x)}+{max(0,y)}")

    def _safe_path(self) -> Path:
        try:
            return Path(self.current.get()).expanduser().resolve()
        except Exception:
            return Path.home()

    def _populate(self):
        base = self._safe_path()
        if not base.exists():
            base = Path.home()
            self.current.set(str(base))

        dirs = []
        try:
            for p in sorted(base.iterdir(), key=lambda p: (p.is_file(), p.name.lower())):
                if p.is_dir():
                    dirs.append(p.name + os.sep)
        except PermissionError:
            pass

        self.listbox.delete(0, tk.END)
        for name in dirs:
            self.listbox.insert(tk.END, name)

    def _go_up(self):
        base = self._safe_path()
        parent = base.parent if base.parent != base else base
        self.current.set(str(parent))
        self._populate()

    def _go_to_entry(self):
        self._populate()

    def _enter_dir(self, _event=None):
        sel = self.listbox.curselection()
        if not sel:
            return
        name = self.listbox.get(sel[0]).rstrip(os.sep)
        new_path = self._safe_path() / name
        self.current.set(str(new_path))
        self._populate()

    def _select(self):
        path = self._safe_path()
        if path.exists():
            self.selected = str(path)
            self.destroy()
        else:
            messagebox.showerror("Invalid folder", "Selected folder does not exist.")

    def _cancel(self):
        self.selected = None
        self.destroy()


# ------------------ Main UI (Tk) ------------------

class AudioRecorderApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Audio Recorder")
        self.root.geometry("500x560")
        self.root.resizable(False, False)

        # Show tracebacks instead of silent crash
        def _tk_exception_hook(exc, val, tb):
            msg = "".join(traceback.format_exception(exc, val, tb))
            try:
                messagebox.showerror("Tk Error", msg)
            except Exception:
                print(msg, file=sys.stderr)
        self.root.report_callback_exception = _tk_exception_hook
        sys.excepthook = lambda exc, val, tb: _tk_exception_hook(exc, val, tb)

        # Queues + controller
        self.command_queue = queue.Queue()
        self.status_queue = queue.Queue()
        self.controller = RecordingController(self.command_queue, self.status_queue)
        self.controller.start()

        # Polling state
        self._modal_open = False
        self._poll_after_id = None

        # Device state (maps: name -> soundcard object)
        self.mic_devices, self.speaker_devices = {}, {}
        self.selected_mic_name, self.selected_speaker_name = tk.StringVar(), tk.StringVar()
        self.output_directory = tk.StringVar()
        self.set_default_output_directory()

        # --- Layout ---
        self.main_frame = tk.Frame(self.root, padx=20, pady=15)
        self.main_frame.pack(expand=True, fill=tk.BOTH)

        device_frame = tk.LabelFrame(self.main_frame, text="Audio Devices", padx=10, pady=10)
        device_frame.pack(pady=5, fill=tk.X, expand=True)

        bt_warning = ("If using Bluetooth headphones, choose a different mic (e.g., laptop mic) "
                      "to prevent audio quality drop.")
        tk.Label(device_frame, text=bt_warning, wraplength=420, justify=tk.LEFT, fg="darkblue")\
            .grid(row=0, column=0, columnspan=2, sticky="w", pady=(0, 8))

        tk.Label(device_frame, text="Microphone (Input):").grid(row=1, column=0, sticky="w")
        self.mic_menu = tk.OptionMenu(device_frame, self.selected_mic_name, "No devices found")
        self.mic_menu.grid(row=2, column=0, columnspan=2, sticky="ew", pady=(0, 5))

        tk.Label(device_frame, text="Audio Output (to record):").grid(row=3, column=0, sticky="w")
        self.speaker_menu = tk.OptionMenu(device_frame, self.selected_speaker_name, "No devices found")
        self.speaker_menu.grid(row=4, column=0, columnspan=2, sticky="ew")

        device_frame.grid_columnconfigure(0, weight=1)

        save_frame = tk.LabelFrame(self.main_frame, text="Save Location", padx=10, pady=10)
        save_frame.pack(pady=10, fill=tk.X, expand=True)
        self.save_path_entry = tk.Entry(save_frame, textvariable=self.output_directory, state='readonly')
        self.save_path_entry.grid(row=0, column=0, sticky="ew", padx=(0, 5))
        self.browse_button = tk.Button(save_frame, text="Browse...", command=self.browse_directory_custom)
        self.browse_button.grid(row=0, column=1, sticky="e")
        save_frame.grid_columnconfigure(0, weight=1)

        self.refresh_button = tk.Button(self.main_frame, text="Refresh Devices", command=self.populate_device_lists)
        self.refresh_button.pack(pady=5)

        self.status_label = tk.Label(self.main_frame, text="Ready to record", font=("Arial", 12))
        self.status_label.pack(pady=5)

        # Buttons row (keep this ABOVE the meters so it doesn't get pushed off-screen)
        button_frame = tk.Frame(self.main_frame)
        button_frame.pack(pady=6)
        self.start_button = tk.Button(button_frame, text="Start Recording", command=self.start_recording_clicked)
        self.stop_button = tk.Button(button_frame, text="Stop Recording", command=self.stop_recording_clicked, state=tk.DISABLED)
        self.start_button.pack(side=tk.LEFT, padx=5)
        self.stop_button.pack(side=tk.LEFT, padx=5)

        # Live meters (after buttons)
        meters = tk.LabelFrame(self.main_frame, text="Live Levels", padx=10, pady=8)
        meters.pack(pady=6, fill=tk.X)

        row = tk.Frame(meters); row.pack(fill=tk.X, pady=2)
        tk.Label(row, text="Mic:", width=6, anchor="e").pack(side=tk.LEFT)
        self.mic_bar = ttk.Progressbar(row, orient="horizontal", length=300, mode="determinate", maximum=100)
        self.mic_bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        self.mic_db = tk.Label(row, text="-inf dBFS", width=10, anchor="w"); self.mic_db.pack(side=tk.LEFT)

        row = tk.Frame(meters); row.pack(fill=tk.X, pady=2)
        tk.Label(row, text="System:", width=6, anchor="e").pack(side=tk.LEFT)
        self.sys_bar = ttk.Progressbar(row, orient="horizontal", length=300, mode="determinate", maximum=100)
        self.sys_bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=8)
        self.sys_db = tk.Label(row, text="-inf dBFS", width=10, anchor="w"); self.sys_db.pack(side=tk.LEFT)

        self.root.protocol("WM_DELETE_WINDOW", self.on_closing)
        self.populate_device_lists()
        self._schedule_poll()

    # ---------- Poll management ----------
    def _schedule_poll(self):
        if self._poll_after_id is None:
            self._poll_after_id = self.root.after(100, self.process_status_queue)

    def _cancel_poll(self):
        if self._poll_after_id is not None:
            try:
                self.root.after_cancel(self._poll_after_id)
            except Exception:
                pass
            self._poll_after_id = None

    @contextmanager
    def modal_guard(self):
        self._modal_open = True
        self._cancel_poll()
        try:
            yield
        finally:
            self._modal_open = False
            self._schedule_poll()

    # ---------- Actions ----------
    def start_recording_clicked(self):
        mic_name = self.selected_mic_name.get()
        spk_name = self.selected_speaker_name.get()
        if mic_name not in self.mic_devices or spk_name not in self.speaker_devices:
            with self.modal_guard():
                messagebox.showerror("Error", "Please select valid devices.")
            return

        for w in [self.start_button, self.refresh_button, self.browse_button]:
            w.config(state=tk.DISABLED)
        self.stop_button.config(state=tk.NORMAL)

        args = {
            "mic_device": self.mic_devices[mic_name],           # sc.Microphone object
            "speaker_device": self.speaker_devices[spk_name],   # sc.Speaker object
        }
        self.command_queue.put(("START", args))

    def stop_recording_clicked(self):
        self.command_queue.put(("STOP", {}))

    def process_status_queue(self):
        self._poll_after_id = None  # consume tick
        try:
            for _ in range(5):
                message_type, data = self.status_queue.get_nowait()

                if message_type == "STATUS":
                    self.status_label.config(text=data)
                    if data == "Ready to record":
                        for w in [self.start_button, self.refresh_button, self.browse_button]:
                            w.config(state=tk.NORMAL)
                        self.stop_button.config(state=tk.DISABLED)

                elif message_type == "ERROR":
                    if not self._modal_open:
                        with self.modal_guard():
                            messagebox.showerror("Recording Error", data)

                elif message_type == "INFO":
                    if not self._modal_open:
                        with self.modal_guard():
                            messagebox.showinfo("Info", data)

                elif message_type == "SAVE_FILE":
                    self.save_file_standard(data)
                    self.status_label.config(text="Ready to record")
                    for w in [self.start_button, self.refresh_button, self.browse_button]:
                        w.config(state=tk.NORMAL)
                    self.stop_button.config(state=tk.DISABLED)

                elif message_type == "LEVEL":
                    tag, peak = data  # 'mic' or 'sys', and linear 0..1
                    # Convert to dBFS; floor at -60 dB to keep UI nice
                    db = -60.0 if peak <= 1e-6 else max(-60.0, 20.0 * np.log10(peak))
                    pct = 0 if peak <= 0 else int(min(100, round(100.0 * peak, 1)))
                    if tag == "mic":
                        self.mic_bar['value'] = pct
                        self.mic_db.config(text=f"{db:.1f} dBFS")
                    else:
                        self.sys_bar['value'] = pct
                        self.sys_db.config(text=f"{db:.1f} dBFS")

        except queue.Empty:
            pass
        finally:
            if not self._modal_open:
                self._schedule_poll()

    # ---------- Helpers ----------
    def _validated_initial_dir(self) -> str:
        current = Path(self.output_directory.get())
        if current.exists():
            return str(current)
        music = Path.home() / "Music"
        return str(music if music.exists() else Path.home())

    def browse_directory_custom(self):
        with self.modal_guard():
            chooser = DirectoryChooser(self.root, Path(self._validated_initial_dir()))
            self.root.wait_window(chooser)
            if chooser.selected:
                self.output_directory.set(chooser.selected)

    def save_file_standard(self, audio_data):
        default_filename = f"recording-{datetime.now().strftime('%Y-%m-%d_%H-%M-%S')}.wav"
        self.root.update_idletasks()
        try:
            with self.modal_guard():
                file_path = filedialog.asksaveasfilename(
                    parent=self.root,
                    initialdir=self._validated_initial_dir(),
                    initialfile=default_filename,
                    defaultextension=".wav",
                    filetypes=[("WAV files", "*.wav")],
                    title="Save recording as...",
                )
        except Exception as e:
            msg = "".join(traceback.format_exception(type(e), e, e.__traceback__))
            with self.modal_guard():
                messagebox.showerror("File Dialog Error", msg)
            return

        if file_path:
            try:
                write_wav(file_path, 48000, audio_data)
                with self.modal_guard():
                    messagebox.showinfo("Success", f"Recording saved to\n{file_path}")
            except Exception as e:
                with self.modal_guard():
                    messagebox.showerror("Error", f"Could not save file: {e}")

    def on_closing(self):
        self._cancel_poll()
        self.command_queue.put(("EXIT", {}))
        self.root.destroy()

    def set_default_output_directory(self):
        default_path = Path.home() / "Music" / "Recordings"
        if not default_path.parent.exists():
            default_path = Path.home() / "Recordings"
        os.makedirs(default_path, exist_ok=True)
        self.output_directory.set(str(default_path))

    def populate_device_lists(self):
        # Build name->object maps using soundcard
        self.mic_devices = {mic.name: mic for mic in sc.all_microphones(include_loopback=False)}
        self.speaker_devices = {spk.name: spk for spk in sc.all_speakers()}

        # Microphones menu
        mic_menu = self.mic_menu["menu"]; mic_menu.delete(0, "end")
        if self.mic_devices:
            for name in self.mic_devices.keys():
                mic_menu.add_command(label=name, command=lambda v=name: self.selected_mic_name.set(v))
            try:
                self.selected_mic_name.set(sc.default_microphone().name)
            except Exception:
                self.selected_mic_name.set(next(iter(self.mic_devices)))
        else:
            self.selected_mic_name.set("No microphones found")

        # Speakers menu (we include all; loopback availability is tested when starting)
        spk_menu = self.speaker_menu["menu"]; spk_menu.delete(0, "end")
        if self.speaker_devices:
            for name in self.speaker_devices.keys():
                spk_menu.add_command(label=name, command=lambda v=name: self.selected_speaker_name.set(v))
            try:
                self.selected_speaker_name.set(sc.default_speaker().name)
            except Exception:
                self.selected_speaker_name.set(next(iter(self.speaker_devices)))
        else:
            self.selected_speaker_name.set("No speakers found")


if __name__ == "__main__":
    root = tk.Tk()
    app = AudioRecorderApp(root)
    root.mainloop()
