# Compila o demux .w154 e instala libw154_plugin.dll no VLC 3.0.24.
param(
    [string]$VlcDir = "f:\Program Files\VideoLAN\VLC"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cache = Join-Path $root ".cache"
$out = Join-Path $root "out"
New-Item -ItemType Directory -Force -Path $cache, $out | Out-Null

function Get-File([string]$Url, [string]$Dest) {
    if (Test-Path $Dest) {
        return
    }
    Write-Host "Baixando $Url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
}

$mingwZip = "winlibs-x86_64-posix-seh-gcc-16.2.0-mingw-w64ucrt-14.0.0-r2.zip"
$mingwUrl = "https://github.com/brechtsanders/winlibs_mingw/releases/download/16.2.0posix-14.0.0-ucrt-r2/$mingwZip"
$mingwRoot = Join-Path $cache "mingw64"
$gcc = Join-Path $mingwRoot "bin\gcc.exe"
if (-not (Test-Path $gcc)) {
    $zip = Join-Path $cache $mingwZip
    Get-File $mingwUrl $zip
    $shaFile = Join-Path $cache "mingw.zip.sha256"
    Get-File "$mingwUrl.sha256" $shaFile
    $expected = (Get-Content $shaFile -Raw).Split()[0].Trim()
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected.ToLower()) {
        Remove-Item $zip
        throw "SHA256 do MinGW não confere."
    }
    Write-Host "Extraindo MinGW"
    Expand-Archive -Path $zip -DestinationPath $cache -Force
}
if (-not (Test-Path $gcc)) {
    throw "gcc não apareceu em $gcc"
}

$vlcSrc = Join-Path $cache "vlc-3.0.24"
$vlcInclude = Join-Path $vlcSrc "include"
if (-not (Test-Path (Join-Path $vlcInclude "vlc_common.h"))) {
    $tar = Join-Path $cache "vlc-3.0.24.tar.xz"
    Get-File "https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz" $tar
    Write-Host "Extraindo headers do VLC 3.0.24"
    tar -xf $tar -C $cache
}

$libvlc = Join-Path $VlcDir "libvlccore.dll"
if (-not (Test-Path $libvlc)) {
    throw "libvlccore.dll não está em $VlcDir"
}
$import = Join-Path $cache "libvlccore.dll.a"
if (-not (Test-Path $import)) {
    Write-Host "Gerando import lib de libvlccore.dll"
    $gendef = Join-Path $mingwRoot "bin\gendef.exe"
    $dlltool = Join-Path $mingwRoot "bin\dlltool.exe"
    Push-Location $cache
    try {
        & $gendef $libvlc
        if ($LASTEXITCODE -ne 0) { throw "gendef saiu $LASTEXITCODE" }
        & $dlltool -d "libvlccore.def" -l "libvlccore.dll.a" -D "libvlccore.dll"
        if ($LASTEXITCODE -ne 0) { throw "dlltool saiu $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

$env:PATH = (Join-Path $mingwRoot "bin") + [IO.Path]::PathSeparator + $env:PATH

Write-Host "Compilando o teste"
$test = Join-Path $out "decode_test.exe"
& $gcc "-std=c11" "-Wall" "-Wextra" "-O2" "-o" $test (Join-Path $root "decode.c") (Join-Path $root "decode_test.c")
if ($LASTEXITCODE -ne 0) { throw "teste não compilou" }
$sample = Join-Path $out "smoke.w154"
& $test $sample
if ($LASTEXITCODE -ne 0) { throw "teste falhou" }

Write-Host "Compilando libw154_plugin.dll"
$plugin = Join-Path $out "libw154_plugin.dll"
$rsp = Join-Path $out "plugin.rsp"
@(
    "-std=c11"
    "-Wall"
    "-Wextra"
    "-O2"
    "-shared"
    "-D__PLUGIN__"
    '-DMODULE_STRING=\"w154\"'
    "-I$($vlcInclude -replace '\\','/')"
    "-I$($root -replace '\\','/')"
    "-o"
    ($plugin -replace '\\','/')
    ((Join-Path $root "demux.c") -replace '\\','/')
    ((Join-Path $root "decode.c") -replace '\\','/')
    "-L$($cache -replace '\\','/')"
    "-lvlccore"
    "-static-libgcc"
    "-Wl,-Bstatic"
    "-lpthread"
    "-Wl,-Bdynamic"
) | Set-Content -Encoding ascii $rsp
& $gcc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "plugin não compilou" }

$objdump = Join-Path $mingwRoot "bin\objdump.exe"
$exports = & $objdump "-p" $plugin | Select-String "vlc_entry__3_0_0f"
if (-not $exports) {
    throw "a DLL não exporta vlc_entry__3_0_0f"
}

$destDir = Join-Path $VlcDir "plugins\demux"
$dest = Join-Path $destDir "libw154_plugin.dll"
$cacheGen = Join-Path $VlcDir "vlc-cache-gen.exe"
try {
    Copy-Item $plugin $dest -Force
    & $cacheGen (Join-Path $VlcDir "plugins")
    if ($LASTEXITCODE -ne 0) { throw "vlc-cache-gen saiu $LASTEXITCODE" }
    Write-Host "Instalado em $dest"
} catch {
    Write-Host "Não foi possível instalar em $dest."
    Write-Host "A DLL ficou em $plugin. Rode num prompt elevado:"
    Write-Host "Copy-Item '$plugin' '$dest' -Force"
    Write-Host "& '$cacheGen' '$(Join-Path $VlcDir 'plugins')'"
    throw
}

$vlc = Join-Path $VlcDir "vlc.exe"
$log = Join-Path $out "vlc-smoke.log"
if (Test-Path $log) { Remove-Item $log -Force }
Write-Host "Abrindo o arquivo de teste no VLC"
& $vlc "--ignore-config" "--no-one-instance" "--intf" "dummy" "--aout=adummy" "--vout=dummy" "--play-and-exit" "-vv" "--file-logging" "--logfile=$log" $sample
if ($LASTEXITCODE -ne 0) { throw "VLC saiu $LASTEXITCODE" }
$deadline = (Get-Date).AddSeconds(15)
$text = ""
do {
    if (Test-Path $log) {
        $text = Get-Content $log -Raw -ErrorAction SilentlyContinue
        if ($text -match "using demux module `"w154`"") { break }
    }
    Start-Sleep -Milliseconds 200
} while ((Get-Date) -lt $deadline)
if (-not $text) { throw "VLC não gravou o log" }
if ($text -match "no suitable demux") { throw "VLC não achou demux para o .w154" }
if ($text -notmatch "w154 aberto") { throw "o log não cita o demux w154" }
Write-Host "VLC reproduziu o .w154"
