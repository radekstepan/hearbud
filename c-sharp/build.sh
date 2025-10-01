#!/usr/bin/env bash
set -euo pipefail

# Path to Windows dotnet
WIN_DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"

# Convert current WSL repo path to a Windows path (e.g., C:\...\c-sharp)
WIN_REPO_ROOT="$(wslpath -w "$(pwd)")"

# Windows output folder (change if you like)
WIN_OUT="$(wslpath -w "/mnt/c/Users/radek/dev/wasapi-publish")"

# Kill any running app (ignore errors). UNC warning is harmless.
cmd.exe /c "taskkill /IM WasapiLoopMix.exe /F >NUL 2>&1" || true

# Restore + publish (note: no ^ line continuations; bash uses backslashes, but we keep it single-line)
"$WIN_DOTNET" restore "$WIN_REPO_ROOT\\WasapiLoopMix.sln"
"$WIN_DOTNET" publish "$WIN_REPO_ROOT\\WasapiLoopMix\\WasapiLoopMix.csproj" -c Release -r win-x64 \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$WIN_OUT"

echo
echo "Published to: $WIN_OUT"
explorer.exe "$WIN_OUT" >/dev/null 2>&1 || true
