param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ApiArgs
)

$ErrorActionPreference = "Stop"
$script:manager = $null
$script:hotspotStarted = $false
$script:stopped = $false
$script:stopLock = New-Object object

function Quote-CmdArg([string]$text) {
    return '"' + ($text.Replace('"', '""')) + '"'
}

function Wait-ForEnter {
    Write-Host "Pressione Enter para fechar."
    $null = [Console]::ReadLine()
}

function Initialize-WinRtAwait {
    $null = Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $script:asTaskOp = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq "AsTask" -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq ("IAsyncOperation" + [char]96 + "1")
    } | Select-Object -First 1
    $script:asTaskAction = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq "AsTask" -and -not $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq "IAsyncAction"
    } | Select-Object -First 1
    if ($null -eq $script:asTaskOp -or $null -eq $script:asTaskAction) {
        throw "Não foi possível aguardar as operações WinRT do hotspot."
    }
}

function Wait-WinRtAction($action) {
    $task = $script:asTaskAction.Invoke($null, @($action))
    if (-not $task.Wait(20000)) {
        throw "Tempo esgotado ao configurar o hotspot."
    }
    if ($task.IsFaulted) {
        throw $task.Exception.GetBaseException().Message
    }
}

function Wait-WinRtOp($operation, $resultType) {
    $method = $script:asTaskOp.MakeGenericMethod($resultType)
    $task = $method.Invoke($null, @($operation))
    if (-not $task.Wait(20000)) {
        throw "Tempo esgotado na operação do hotspot."
    }
    if ($task.IsFaulted) {
        throw $task.Exception.GetBaseException().Message
    }
    return $task.Result
}

function Stop-Hotspot {
    $run = $false
    [System.Threading.Monitor]::Enter($script:stopLock)
    try {
        if (-not $script:stopped -and $script:hotspotStarted -and $null -ne $script:manager) {
            $script:stopped = $true
            $run = $true
        }
    }
    finally {
        [System.Threading.Monitor]::Exit($script:stopLock)
    }
    if (-not $run) {
        return
    }
    try {
        $resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]
        $result = Wait-WinRtOp ($script:manager.StopTetheringAsync()) $resultType
        if ($result.Status.ToString() -ne "Success") {
            Write-Host "Falha ao encerrar o hotspot: $($result.Status)"
        }
    }
    catch {
        Write-Host "Falha ao encerrar o hotspot: $($_.Exception.Message)"
    }
}

function Get-FlagValue([string[]]$argv, [string]$name, [int]$default) {
    if (-not $argv) {
        return $default
    }
    for ($i = 0; $i -lt $argv.Count - 1; $i++) {
        if ($argv[$i] -eq $name) {
            $n = 0
            if ([int]::TryParse($argv[$i + 1], [ref]$n)) {
                return $n
            }
            return $default
        }
    }
    return $default
}

function Read-Secret([string]$text, [string]$name) {
    $match = [regex]::Match($text, "(?m)^#define\s+$name\s+`"([^`"]*)`"")
    if (-not $match.Success) {
        return $null
    }
    return $match.Groups[1].Value.Trim()
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $rootArg = $RepoRoot.TrimEnd("\")
    $cmd = "-NoExit -NoProfile -ExecutionPolicy Bypass -File $(Quote-CmdArg $PSCommandPath) -RepoRoot $(Quote-CmdArg $rootArg)"
    foreach ($arg in @($ApiArgs)) {
        if ([string]::IsNullOrEmpty($arg)) {
            continue
        }
        $cmd += " $(Quote-CmdArg $arg)"
    }
    try {
        Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -Verb RunAs `
            -WorkingDirectory (Get-Location).Path `
            -ArgumentList $cmd
    }
    catch {
        Write-Host "A API precisa de administrador para o hotspot."
        Wait-ForEnter
        exit 1
    }
    exit 0
}

$code = 1
try {
    $RepoRoot = $RepoRoot.TrimEnd("\")
    $exe = Join-Path $RepoRoot "W154Stream\bin\Release\net10.0\w154stream.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Rode build.bat --api antes de iniciar a API."
    }

    $secretsPath = Join-Path $RepoRoot "firmware\w154\main\wifi_secrets.h"
    if (-not (Test-Path -LiteralPath $secretsPath)) {
        throw "Falta firmware\w154\main\wifi_secrets.h. Copie o exemplo:`r`ncopy firmware\w154\main\wifi_secrets.example.h firmware\w154\main\wifi_secrets.h"
    }

    $secrets = Get-Content -LiteralPath $secretsPath -Raw
    $ssid = Read-Secret $secrets "WIFI_SSID"
    if ([string]::IsNullOrWhiteSpace($ssid)) {
        throw "wifi_secrets.h precisa de WIFI_SSID."
    }
    $pass = [guid]::NewGuid().ToString()

    $drivers = (& netsh wlan show drivers | Out-String)
    # O hosted network legado fica No em radios que ainda sobem o hotspot por Wi-Fi Direct.
    $bandLine = @($drivers -split '\r?\n' | Where-Object { $_ -match "(?i)2\.4\s*GHz" })
    if ($bandLine.Count -eq 0) {
        $hostedLine = @($drivers -split '\r?\n' | Where-Object {
            $_ -match "(?i)hosted network supported|suporte a rede hospedada|rede hospedada com suporte"
        })
        if ($hostedLine.Count -gt 0) {
            throw $hostedLine[0].Trim()
        }
        throw $drivers.Trim()
    }

    Initialize-WinRtAwait
    $null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]
    $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]
    $resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]

    $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    if ($null -eq $profile) {
        throw "Nenhuma conexão com internet para compartilhar no hotspot."
    }

    $script:manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
    $deadline = (Get-Date).AddSeconds(15)
    while ($script:manager.TetheringOperationalState.ToString() -eq "InTransition" -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    if ($script:manager.TetheringOperationalState.ToString() -eq "On") {
        $stopped = Wait-WinRtOp ($script:manager.StopTetheringAsync()) $resultType
        if ($stopped.Status.ToString() -ne "Success") {
            throw "Falha ao parar o hotspot: $($stopped.Status)"
        }
    }

    $config = $script:manager.GetCurrentAccessPointConfiguration()
    $config.Ssid = $ssid
    $config.Passphrase = $pass
    try {
        $config.Band = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::TwoPointFourGigahertz
    }
    catch {
        throw "Este Windows não expõe a faixa 2.4 GHz. O AP não foi iniciado."
    }
    $config.AuthenticationKind = [Windows.Networking.NetworkOperators.TetheringWiFiAuthenticationKind]::Wpa2
    Wait-WinRtAction ($script:manager.ConfigureAccessPointAsync($config))

    $started = Wait-WinRtOp ($script:manager.StartTetheringAsync()) $resultType
    if ($started.Status.ToString() -ne "Success") {
        throw "Falha ao iniciar o hotspot: $($started.Status)"
    }
    $script:hotspotStarted = $true

    if (-not ("ConsoleCtrl" -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleCtrl {
    public delegate bool Handler(int ctrlType);
    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
}
"@
    }
    $script:ctrlHandler = [ConsoleCtrl+Handler] {
        param($ctrlType)
        Stop-Hotspot
        return $false
    }
    [void][ConsoleCtrl]::SetConsoleCtrlHandler($script:ctrlHandler, $true)

    $scopeKey = "HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters"
    $scopeProp = Get-ItemProperty -Path $scopeKey -Name ScopeAddress -ErrorAction SilentlyContinue
    if ($null -eq $scopeProp -or [string]::IsNullOrWhiteSpace([string]$scopeProp.ScopeAddress)) {
        throw "O Windows não publicou ScopeAddress do hotspot."
    }
    $scope = [string]$scopeProp.ScopeAddress
    $seen = $false
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $hit = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -eq $scope })
        if ($hit.Count -gt 0) {
            $seen = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $seen) {
        throw "O gateway $scope não apareceu na interface do hotspot."
    }

    $helloPort = Get-FlagValue $ApiArgs "--hello" 5091
    . (Join-Path $RepoRoot "firmware\hotspot_secrets.ps1")
    Write-WifiSecrets -Path $secretsPath -Ssid $ssid -Passphrase $pass -Gateway $scope -HelloPort $helloPort
    & netsh advfirewall firewall delete rule name="W154Stream UDP" | Out-Null
    & netsh advfirewall firewall add rule name="W154Stream UDP" dir=in action=allow protocol=UDP localport=$helloPort profile=any | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível abrir o UDP $helloPort."
    }

    $httpPort = Get-FlagValue $ApiArgs "--http" 5090
    Write-Host "Hotspot 2.4 GHz  SSID=$ssid  gateway=$scope"
    Write-Host "http://localhost:$httpPort/files"

    & $exe @($ApiArgs)
    if ($null -ne $LASTEXITCODE) {
        $code = $LASTEXITCODE
    }
    else {
        $code = 0
    }
}
catch {
    Write-Host $_.Exception.Message
    $code = 1
}
finally {
    Stop-Hotspot
}

if ($code -ne 0) {
    Wait-ForEnter
}
exit $code
