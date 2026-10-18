param(
    [string]$Port,
    [switch]$Monitor
)

$ErrorActionPreference = "Stop"
if (-not $env:IDF_PATH) {
    $env:IDF_PATH = Join-Path $env:USERPROFILE "esp\esp-idf"
}
$export = Join-Path $env:IDF_PATH "export.bat"
if (-not (Test-Path $export)) {
    Write-Error "ESP-IDF export.bat não está em $($env:IDF_PATH)"
}
function Start-W154Monitor {
    param(
        [string]$Port,
        [string]$Fw,
        [string]$Export
    )
    $descPath = Join-Path $Fw "build\project_description.json"
    if (-not (Test-Path $descPath)) {
        Write-Error "project_description.json não apareceu depois do build."
    }
    $desc = Get-Content $descPath -Raw | ConvertFrom-Json
    $build = Join-Path $Fw "build"
    $appElf = [IO.Path]::GetFullPath((Join-Path $build ([string]$desc.app_elf)))
    $seen = @{}
    $elfs = @()
    if (Test-Path $appElf) {
        $seen[$appElf] = $true
        $elfs += $appElf
    }
    foreach ($file in (Get-ChildItem -Path $build -Filter *.elf -Recurse -File)) {
        if (-not $seen.ContainsKey($file.FullName)) {
            $seen[$file.FullName] = $true
            $elfs += $file.FullName
        }
    }
    $baud = if ($desc.monitor_baud) { $desc.monitor_baud } else { 115200 }
    $prefix = [string]$desc.monitor_toolprefix
    $target = if ($desc.target) { [string]$desc.target } else { "esp32s3" }
    $rev = if ($null -ne $desc.min_rev -and "$($desc.min_rev)" -ne "") { $desc.min_rev } else { 0 }
    $elfArg = ($elfs | ForEach-Object { "`"$_`"" }) -join " "
    # A janela é cmd /c python, não um .bat. Ctrl+C encerra sem "Terminate batch job".
    # stdin NUL: o monitor não escreve a tecla na serial; Ctrl+C só sai.
    $inner = "call `"$Export`" && python -m esp_idf_monitor -p $Port -b $baud --toolchain-prefix $prefix --target $target --revision $rev --force-color $elfArg < NUL"
    $cmdArgs = "/c start `"w154 $Port`" /D `"$Fw`" cmd /c `"$inner`""
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "cmd.exe"
    $psi.Arguments = $cmdArgs
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [Diagnostics.Process]::Start($psi)
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) {
        Write-Error "Não abriu o monitor (cmd exit $($proc.ExitCode))."
    }
}

. (Join-Path $PSScriptRoot "hotspot_secrets.ps1")
$secretsPath = Join-Path $PSScriptRoot "w154\main\wifi_secrets.h"
$helloPort = Get-HelloPortFromSecrets $secretsPath
$hot = Get-RunningHotspot
Write-WifiSecrets -Path $secretsPath -Ssid $hot.Ssid -Passphrase $hot.Passphrase -Gateway $hot.Gateway -HelloPort $helloPort
Write-Host "wifi $($hot.Ssid)  gateway $($hot.Gateway)  hello $helloPort"

if (-not $Port) {
    $devices = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -match 'VID_303A' -and $_.Name -match '\(COM\d+\)' })
    if ($devices.Count -eq 0) {
        Write-Error "Nenhuma porta USB Espressif. Passe -Port COMx."
    }
    if ($devices.Count -gt 1) {
        $devices | ForEach-Object { Write-Host $_.Name }
        Write-Error "Mais de uma porta. Passe -Port COMx."
    }
    if ($devices[0].Name -match '\((COM\d+)\)') {
        $Port = $Matches[1]
    }
}
$fw = Join-Path $PSScriptRoot "w154"
Write-Host "ESP-IDF $($env:IDF_PATH)"
Write-Host "flash w154 em $Port"
cmd /c "`"$export`" && idf.py -C `"$fw`" -p $Port build flash"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
if ($Monitor) {
    Start-W154Monitor -Port $Port -Fw $fw -Export $export
    Write-Host "Monitor na janela `"w154 $Port`". Ctrl+C nela encerra."
}
exit 0
