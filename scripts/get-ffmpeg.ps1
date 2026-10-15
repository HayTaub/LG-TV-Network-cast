# Downloads a Windows FFmpeg build (gyan.dev "essentials", includes ddagrab and the
# NVENC / Quick Sync / AMF hardware encoders) into tools\ffmpeg.exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'tools\ffmpeg.exe'
if (Test-Path $target) { Write-Host "ffmpeg already present: $target"; exit 0 }

$url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'ffmpeg.zip'
    Write-Host "Downloading $url ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $tmp
    $exe = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $exe.FullName $target
    Write-Host "Saved $target"
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
