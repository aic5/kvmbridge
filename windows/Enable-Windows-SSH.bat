@echo off
setlocal DisableDelayedExpansion
title Enable Windows SSH
set "KVM_SSH_SETUP_FILE=%~f0"
set "KVM_SSH_ORIGINAL_USER=%USERNAME%"
set "KVM_SSH_ELEVATED_CHILD="
set "KVM_SSH_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "KVM_SSH_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%KVM_SSH_PS%" -NoProfile -ExecutionPolicy Bypass -Command "$raw=[IO.File]::ReadAllText($env:KVM_SSH_SETUP_FILE); $parts=$raw -split '(?m)^# POWERSHELL_PAYLOAD\r?$',2; if($parts.Count -ne 2){throw 'Embedded setup script is missing.'}; & ([scriptblock]::Create($parts[1]))"
set "KVM_SSH_RESULT=%ERRORLEVEL%"
echo.
if "%KVM_SSH_RESULT%"=="3010" echo Restart Windows, then run this file again to finish setup.
if not "%KVM_SSH_RESULT%"=="0" if not "%KVM_SSH_RESULT%"=="3010" echo Setup did not finish successfully. Review the message above.
pause
exit /b %KVM_SSH_RESULT%

# POWERSHELL_PAYLOAD
# Windows PowerShell 5.1 compatible. No external script downloads.
# Reference: https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$exitCode = 0
$transcriptStarted = $false
$logPath = $null

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $identity.Dispose()

    if (-not $isAdministrator) {
        if ($env:KVM_SSH_ELEVATED_CHILD -eq '1') {
            throw 'Administrator access was not granted. Right-click this file and choose Run as administrator.'
        }
        Write-Host 'Requesting administrator access. Approve the Windows UAC prompt.'
        # Embed paths as Base64 data, so spaces, apostrophes and shell characters are safe.
        $file64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($env:KVM_SSH_SETUP_FILE))
        $user64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($env:KVM_SSH_ORIGINAL_USER))
        $bootstrap = @'
$env:KVM_SSH_SETUP_FILE=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__FILE64__'))
$env:KVM_SSH_ORIGINAL_USER=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__USER64__'))
$env:KVM_SSH_ELEVATED_CHILD='1'
$raw=[IO.File]::ReadAllText($env:KVM_SSH_SETUP_FILE)
$parts=$raw -split '(?m)^# POWERSHELL_PAYLOAD\r?$',2
if($parts.Count -ne 2){throw 'Embedded setup script is missing.'}
& ([scriptblock]::Create($parts[1]))
'@
        $bootstrap = $bootstrap.Replace('__FILE64__', $file64).Replace('__USER64__', $user64)
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
        $powershell = Join-Path $PSHOME 'powershell.exe'
        $child = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        exit $child.ExitCode
    }

    $logPath = Join-Path $env:TEMP ('Windows-SSH-Setup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    Start-Transcript -Path $logPath -Force | Out-Null
    $transcriptStarted = $true
    Write-Host ''
    Write-Host 'Enable OpenSSH Server for local-network access' -ForegroundColor Cyan
    Write-Host 'Installation can take several minutes and may require Windows Update access.'

    $capabilityName = 'OpenSSH.Server~~~~0.0.1.0'
    $capability = Get-WindowsCapability -Online -Name $capabilityName
    if ($null -eq $capability) {
        throw 'OpenSSH Server is unavailable on this Windows version. Windows 10 build 1809 or later is required.'
    }
    if ([string]$capability.State -ne 'Installed') {
        Write-Host 'Installing OpenSSH Server...'
        $installation = Add-WindowsCapability -Online -Name $capabilityName
        if ($installation.RestartNeeded) {
            Write-Host 'Windows requires a restart. Restart, then run this file again.' -ForegroundColor Yellow
            $exitCode = 3010
        }
    } else {
        Write-Host 'OpenSSH Server is already installed.'
    }

    if ($exitCode -eq 0) {
        # Configure the firewall before starting a newly installed service.
        Write-Host 'Configuring SSH firewall access: local subnet, Private/Domain profiles, TCP 22...'
        $ruleName = 'OpenSSH-Server-In-TCP'
        $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
        if ($null -ne $rule) {
            Set-NetFirewallRule -Name $ruleName -Enabled True -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 -RemoteAddress LocalSubnet -Profile Private,Domain | Out-Null
        } else {
            New-NetFirewallRule -Name $ruleName -DisplayName 'OpenSSH Server (local subnet)' -Enabled True -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 -RemoteAddress LocalSubnet -Profile Private,Domain | Out-Null
        }

        Write-Host 'Enabling automatic startup and starting SSH...'
        Set-Service -Name sshd -StartupType Automatic
        Start-Service -Name sshd
        $service = Get-Service -Name sshd
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))

        $listeners = @()
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 22 -ErrorAction SilentlyContinue)
            if ($listeners.Count -gt 0) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($listeners.Count -eq 0) {
            throw 'The SSH service started, but TCP port 22 is not listening. Check C:\ProgramData\ssh\sshd_config and the OpenSSH event log; an existing configuration may use a different port.'
        }

        Write-Host ''
        Write-Host 'OpenSSH is running and will start automatically after reboot.' -ForegroundColor Green
        Write-Host ('Windows username: ' + $env:KVM_SSH_ORIGINAL_USER)
        Write-Host 'Use your account password (not the Windows Hello PIN), or an existing SSH key.'

        $profiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue)
        $readyAddresses = 0
        foreach ($profile in $profiles) {
            $addresses = @(Get-NetIPAddress -InterfaceIndex $profile.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                $_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*'
            })
            if ($addresses.Count -eq 0) { continue }
            Write-Host ''
            Write-Host ('Network: {0} ({1}) - {2}' -f $profile.Name, $profile.InterfaceAlias, $profile.NetworkCategory)
            if ([string]$profile.NetworkCategory -eq 'Public') {
                Write-Host 'SSH is not allowed by this setup rule on this Public network.' -ForegroundColor Yellow
                Write-Host 'If this is your trusted home/work LAN, change it to Private with:'
                Write-Host ('  Set-NetConnectionProfile -InterfaceIndex {0} -NetworkCategory Private' -f $profile.InterfaceIndex)
                Write-Host 'Then run this file again to verify the connection details.'
            } else {
                foreach ($address in $addresses) {
                    $readyAddresses++
                    Write-Host 'Run this in Terminal on your Mac:'
                    Write-Host ('  ssh -l "{0}" {1}' -f $env:KVM_SSH_ORIGINAL_USER, $address.IPAddress) -ForegroundColor Cyan
                }
            }
        }
        if ($readyAddresses -eq 0) {
            Write-Host ''
            Write-Host 'No eligible Private/Domain IPv4 address was found. Connect to your trusted LAN and check its network profile.' -ForegroundColor Yellow
        }

        $keygen = Join-Path $env:WINDIR 'System32\OpenSSH\ssh-keygen.exe'
        $publicKey = Join-Path $env:ProgramData 'ssh\ssh_host_ed25519_key.pub'
        if ((Test-Path -LiteralPath $keygen) -and (Test-Path -LiteralPath $publicKey)) {
            Write-Host ''
            Write-Host 'Server fingerprint, for comparison with the first connection prompt on your Mac:'
            & $keygen -lf $publicKey
        }
        Write-Host ''
        Write-Host 'To check later: Get-Service sshd'
        Write-Host 'The Windows computer must remain awake to accept connections.'
    }
} catch {
    $exitCode = 1
    Write-Host ''
    Write-Host ('SETUP FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'If installation failed, check Windows Update access or your organization policy, then rerun this file.'
} finally {
    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
        Write-Host ('Setup log: ' + $logPath)
    }
    if ($env:KVM_SSH_ELEVATED_CHILD -eq '1') {
        Write-Host ''
        Read-Host 'Press Enter to close this administrator window' | Out-Null
    }
}
exit $exitCode
