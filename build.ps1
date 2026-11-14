# Builds IdleVeil and its installer with the .NET Framework compiler that ships with Windows.
# No SDK or extra tools needed.
#
# Output (dist\):
#   IdleVeil-Setup-<version>.exe       installer (per-user, no admin)
#   IdleVeil-<version>-portable.zip    just the exe, for people who don't want an installer
#
# Usage: .\build.ps1 [-Version 1.2.3]
param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$obj = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc (.NET Framework 4.x is required)" }

New-Item -ItemType Directory -Force $obj, $dist | Out-Null

# ---- shared version info ----
$versionCs = Join-Path $obj 'Version.cs'
@"
using System.Reflection;
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
[assembly: AssemblyProduct("IdleVeil")]
[assembly: AssemblyTitle("IdleVeil")]
[assembly: AssemblyDescription("Burn-in protection overlay for OLED screens")]
static class AppInfo { public const string Version = "$Version"; }
"@ | Set-Content -Encoding UTF8 $versionCs

# ---- icon (same "matrix rain" design as the tray icon), PNG-in-ICO at several sizes ----
$ico = Join-Path $obj 'IdleVeil.ico'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($s / 32.0, $s / 32.0)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, 20, 12))
    $g.FillRectangle($bg, 1, 1, 30, 30)
    $xs = 5, 12, 19, 26; $tops = 4, 12, 2, 9; $lens = 22, 16, 26, 18
    for ($i = 0; $i -lt 4; $i++) {
        for ($y = 0; $y -lt $lens[$i]; $y += 4) {
            $b = [int](80 + 175 * $y / $lens[$i])
            $c = if ($y + 4 -ge $lens[$i]) { [System.Drawing.Color]::FromArgb(220, 255, 220) } else { [System.Drawing.Color]::FromArgb(0, $b, [int]($b / 4)) }
            $br = New-Object System.Drawing.SolidBrush $c
            $g.FillRectangle($br, $xs[$i] - 2, $tops[$i] + $y, 4, 3)
            $br.Dispose()
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($ico)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

# ---- app ----
$appExe = Join-Path $obj 'IdleVeil.exe'
& $csc /nologo /target:winexe /optimize+ /out:"$appExe" /win32icon:"$ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    (Get-ChildItem (Join-Path $root 'src') -Filter *.cs).FullName $versionCs
if ($LASTEXITCODE) { throw "IdleVeil.exe build failed" }

# ---- installer (embeds the app) ----
$setupExe = Join-Path $dist "IdleVeil-Setup-$Version.exe"
& $csc /nologo /target:winexe /optimize+ /out:"$setupExe" /win32icon:"$ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/resource:$appExe,IdleVeil.exe" `
    (Join-Path $root 'installer\Setup.cs') $versionCs
if ($LASTEXITCODE) { throw "Setup build failed" }

# ---- portable zip ----
$zip = Join-Path $dist "IdleVeil-$Version-portable.zip"
$stage = Join-Path $obj 'portable'
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage | Out-Null
Copy-Item $appExe, (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $stage -ErrorAction SilentlyContinue
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

# ---- updater assets: the bare exe plus checksums (the app's self-updater downloads these) ----
Copy-Item $appExe (Join-Path $dist 'IdleVeil.exe') -Force
$sums = foreach ($f in 'IdleVeil.exe', "IdleVeil-Setup-$Version.exe", "IdleVeil-$Version-portable.zip") {
    (Get-FileHash (Join-Path $dist $f) -Algorithm SHA256).Hash.ToLower() + '  ' + $f
}
Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Value ($sums -join "`n") -NoNewline

Get-ChildItem $dist | ForEach-Object { "{0,-40} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
