# wasapi-loopmix (C# / WPF)

A Windows app to record your computer's **system audio (WASAPI loopback)** and your **microphone** into an **MP3** file.  
Includes live meters, dBFS readouts, clip indicators, and adjustable gains for mic/output. Settings persist in `~\.audiorecorder_config.json`.

## Build & Run

- **Requirements:** .NET 8 SDK
- **Dependencies:** NuGet restores automatically (`NAudio`, `NAudio.Lame`)

```bash
dotnet restore
dotnet run --project WasapiLoopMix
