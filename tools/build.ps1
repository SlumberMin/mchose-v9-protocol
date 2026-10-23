# Build the headset protocol probe (write colour + read back RGB via 0x07).
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /platform:x64 /codepage:65001 /out:(Join-Path $here "headset_test.exe") `
    /r:System.dll (Join-Path $here "Rgb.cs") (Join-Path $here "HidNative.cs") (Join-Path $here "headset_test.cs")
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Write-Host "OK -> tools\headset_test.exe"