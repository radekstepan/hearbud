<#
.SYNOPSIS
  Bootstrap parity check for the Windows build runner (ere-core preflight mirror).

.DESCRIPTION
  With -Check: read-only verification of build prerequisites. Throws on
  failure. Without -Check: prints install guidance.
  Safe to run as NETWORK SERVICE: performs no installs and no writes.

.EXAMPLE
  pwsh -File scripts/ensure-deps.ps1 -Check
#>
[CmdletBinding()]
param(
  [switch]$Check
)

$ErrorActionPreference = 'Stop'

function Test-DotNetSdk8 {
  $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
  if (-not $cmd) { return $false }
  $prev = $PSNativeCommandUseErrorActionPreference
  try {
    $PSNativeCommandUseErrorActionPreference = $false
    $sdks = dotnet --list-sdks 2>$null
    return ($sdks -match '8\.0\.1\d\d')
  } finally {
    $PSNativeCommandUseErrorActionPreference = $prev
  }
}

function Test-VcRuntime {
  return (Test-Path (Join-Path $env:SystemRoot 'System32\vcruntime140.dll'))
}

function Test-NuGetDns {
  try {
    [Net.Dns]::GetHostEntry('api.nuget.org') | Out-Null
    return $true
  } catch {
    return $false
  }
}

function Test-DiskSpace {
  $drive = Get-PSDrive -Name C -ErrorAction SilentlyContinue
  if (-not $drive -or $null -eq $drive.Free) { return $false }
  return ($drive.Free -ge 5GB)
}

if (-not $Check) {
  Write-Host 'Hearbud Windows build prerequisites:'
  Write-Host '  - git: Git for Windows (machine-wide): https://git-scm.com/download/win'
  Write-Host '  - .NET 8 SDK (8.0.1xx): https://dotnet.microsoft.com/download/dotnet/8.0'
  Write-Host '    (CI installs it via actions/setup-dotnet into $env:RUNNER_TOOL_CACHE\dotnet)'
  Write-Host '  - VC++ Redistributable (vcruntime140.dll): https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist'
  Write-Host '  - Network: must resolve api.nuget.org'
  Write-Host '  - Disk: >= 5GB free on C:'
  Write-Host 'Run with -Check to verify (read-only, no installs, no writes).'
  exit 0
}

# -Check: read-only verification. Throw on first failure with an actionable message.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  throw 'missing: git (install Git for Windows machine-wide: https://git-scm.com/download/win)'
}
if (-not (Test-DotNetSdk8)) {
  throw 'missing: dotnet SDK 8.0.1xx (install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0)'
}
if (-not (Test-VcRuntime)) {
  throw 'missing: vcruntime140.dll (install the VC++ Redistributable: https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)'
}
if (-not (Test-NuGetDns)) {
  throw 'no usable network: cannot resolve api.nuget.org'
}
if (-not (Test-DiskSpace)) {
  $drive = Get-PSDrive -Name C -ErrorAction SilentlyContinue
  $freeGB = if ($drive -and $null -ne $drive.Free) { [math]::Round($drive.Free / 1GB, 2) } else { 'unknown' }
  throw "insufficient disk space on C: (${freeGB}GB free, need >= 5GB)"
}
Write-Host 'ensure-deps -Check passed: git, dotnet 8.0.1xx, vcruntime140.dll, api.nuget.org DNS, >=5GB free on C:'
