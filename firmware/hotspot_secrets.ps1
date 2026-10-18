function ConvertTo-CString([string]$text) {
    if ($text -match '["\\]') {
        throw "SSID ou senha não pode conter aspas ou barra invertida."
    }
    return $text
}

function Test-IPv4Address([string]$ip) {
    $parts = $ip.Split(".")
    if ($parts.Count -ne 4) {
        return $false
    }
    foreach ($part in $parts) {
        $n = 0
        if (-not [int]::TryParse($part, [ref]$n) -or $n -lt 0 -or $n -gt 255) {
            return $false
        }
    }
    return $true
}

function Get-HelloPortFromSecrets([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return 5091
    }
    $text = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($text, "(?m)^#define\s+HOST_HELLO_PORT\s+(\d+)\s*$")
    if (-not $match.Success) {
        return 5091
    }
    return [int]$match.Groups[1].Value
}

function Get-RunningHotspot {
    $null = Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]
    $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]

    $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    if ($null -eq $profile) {
        throw "Nenhuma conexão com internet. Suba o hotspot com w154stream.bat antes de gravar."
    }
    $manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
    if ($manager.TetheringOperationalState.ToString() -ne "On") {
        throw "O hotspot não está no ar. Suba a API com w154stream.bat antes de gravar."
    }

    $config = $manager.GetCurrentAccessPointConfiguration()
    $ssid = [string]$config.Ssid
    $pass = [string]$config.Passphrase
    if ([string]::IsNullOrWhiteSpace($ssid) -or [string]::IsNullOrWhiteSpace($pass)) {
        throw "O hotspot no ar não tem SSID ou senha."
    }
    if ($pass.Length -lt 8) {
        throw "A senha do hotspot precisa de pelo menos 8 caracteres para WPA2."
    }

    $scopeProp = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters" -Name ScopeAddress -ErrorAction SilentlyContinue
    if ($null -eq $scopeProp -or [string]::IsNullOrWhiteSpace([string]$scopeProp.ScopeAddress)) {
        throw "O Windows não publicou ScopeAddress do hotspot."
    }
    $gateway = [string]$scopeProp.ScopeAddress
    if (-not (Test-IPv4Address $gateway)) {
        throw "Gateway do hotspot inválido: $gateway"
    }
    $hit = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -eq $gateway })
    if ($hit.Count -eq 0) {
        throw "O gateway $gateway não está atribuído na interface do hotspot."
    }

    return [pscustomobject]@{
        Ssid       = $ssid
        Passphrase = $pass
        Gateway    = $gateway
    }
}

function Write-WifiSecrets {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Ssid,
        [Parameter(Mandatory = $true)][string]$Passphrase,
        [Parameter(Mandatory = $true)][string]$Gateway,
        [Parameter(Mandatory = $true)][int]$HelloPort
    )
    if (-not (Test-IPv4Address $Gateway)) {
        throw "Gateway do hotspot inválido: $Gateway"
    }
    if ($HelloPort -lt 1 -or $HelloPort -gt 65535) {
        throw "HOST_HELLO_PORT inválida: $HelloPort"
    }
    $ssid = ConvertTo-CString $Ssid
    $pass = ConvertTo-CString $Passphrase
    $text = @"
#pragma once

/* SSID, senha e HOST_IP do hotspot que estava no ar. O flash regrava este arquivo. */

#define WIFI_SSID "$ssid"
#define WIFI_PASS "$pass"
#define HOST_IP "$Gateway"
#define HOST_HELLO_PORT $HelloPort
"@
    $text = $text -replace "(?<!\r)\n", "`r`n"
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $text, $utf8)
}
