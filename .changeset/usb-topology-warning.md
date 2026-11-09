---
"hearbud": patch
---

Detect a microphone and mouse sharing one USB hub

What: new `Hearbud/UsbTopology.cs`. At startup, after the window is up, a
  pool thread reads the selected capture endpoint's USB device instance
  (MMDevice property store, container-id fallback), enumerates the present
  SetupAPI device tree with parent links, and walks the hub chain of that
  device and of every `Mouse` device. If they share a non-root USB hub it
  logs `UiDiagnostics.Usb`, shows an inline warning banner in the Devices
  group with the mic, mouse and hub, sets the mic tooltip, and shows one
  tray balloon. The probe also reports whether USB selective suspend is
  enabled on the active power scheme. It is best-effort: any native/COM
  failure means "no warning", never a startup error. The check runs at
  startup and again from Refresh Devices, so rewiring can be re-tested
  without restarting. `UsbTopologyProbe.FindSharedHub` is pure topology
  math and covered by tests; `Describe` dumps the resolved chains for
  support logs.

Why: the `13:30:47` log proved the close click never reached the window
  for ~36 s while the UI thread stayed healthy (`maxLag=144ms`, 1863
  frames); the one delivered `WM_NCLBUTTONDOWN` closed the app in 0.8 ms.
  On that machine the mic, keyboard receiver and mouse were all behind one
  generic USB hub, so HID reports were dropped before Windows saw them.
  No software can recover input the OS never received; the only useful
  action is to detect the risky topology and tell the user before it
  happens.

Verification: `dotnet publish Hearbud/Hearbud.csproj -c Release -r win-x64
  --self-contained -o ./dist/win-x64` - 0 warnings/errors (SDK 8.0.133).
  `dotnet test Hearbud.sln -c Release` - 239/239 passed (11 new USB
  topology tests). Live detection confirmed on the bad wiring:
  `13:53:51` logged `Microphone "Razer Seiren Mini" and mouse share USB
  hub "USB\VID_1A40&PID_0101\6&13211795&0&1"`. The fixed wiring (mic
  directly on the root hub) correctly returns no warning.
