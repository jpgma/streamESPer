param(
    [string]$Port
)

$ErrorActionPreference = "Stop"
if (-not $env:IDF_PATH) {
    $env:IDF_PATH = Join-Path $env:USERPROFILE "esp\esp-idf"
}
$export = Join-Path $env:IDF_PATH "export.bat"
if (-not (Test-Path $export)) {
    Write-Error "ESP-IDF export.bat não está em $($env:IDF_PATH)"
}
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
cmd /c "`"$export`" && idf.py -C `"$fw`" -p $Port build flash monitor"
exit $LASTEXITCODE
