@echo off
setlocal DisableDelayedExpansion
title Windows SSH Setup Diagnostics
set "KVM_SSH_DIAGNOSTIC_FILE=%~f0"
set "KVM_SSH_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "KVM_SSH_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%KVM_SSH_PS%" -NoProfile -ExecutionPolicy Bypass -Command "$raw=[IO.File]::ReadAllText($env:KVM_SSH_DIAGNOSTIC_FILE); $parts=$raw -split '(?m)^# POWERSHELL_PAYLOAD\r?$',2; if($parts.Count -ne 2){throw 'Diagnostic payload missing.'}; & ([scriptblock]::Create($parts[1]))"
set "KVM_SSH_RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %KVM_SSH_RESULT%

# POWERSHELL_PAYLOAD
# Read-only inspection: does not start another DISM operation, stop services,
# cancel installation, change network profiles, or change Windows configuration.
$ErrorActionPreference = 'Stop'
try {
    $report = Join-Path $env:TEMP ('Windows-SSH-Diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
    Write-Host 'Collecting service information and recent installer logs...'
    $content = & {
        'WINDOWS SSH SETUP DIAGNOSTICS'
        'Collected: ' + (Get-Date -Format o)
        'Read-only report. This does not cancel or restart installation.'
        ''
        '--- WINDOWS VERSION ---'
        try {
            Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' |
                Select-Object ProductName, DisplayVersion, CurrentBuild, UBR |
                Format-List | Out-String
        } catch { 'Version information unavailable: ' + $_.Exception.Message }
        ''
        '--- SERVICES ---'
        foreach ($serviceName in @('sshd', 'wuauserv', 'BITS', 'TrustedInstaller')) {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($null -eq $service) {
                $serviceName + ': not present'
            } else {
                $service | Select-Object Name, Status, StartType | Format-Table -AutoSize | Out-String
            }
        }
        'Windows Update/BITS/TrustedInstaller can be started on demand; Stopped alone is not an error.'
        ''
        '--- INSTALLER PROCESSES (CUMULATIVE CPU SECONDS) ---'
        $processes = @(Get-Process -Name dism, DismHost, TiWorker, TrustedInstaller -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) { 'No matching installer processes were found.' }
        else { $processes | Select-Object ProcessName, Id, CPU | Format-Table -AutoSize | Out-String }
        ''
        '--- PORT 22 LISTENERS ---'
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 22 -ErrorAction SilentlyContinue)
        if ($listeners.Count -eq 0) { 'No TCP port 22 listener found (or access unavailable).' }
        else { $listeners | Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize | Out-String }
        ''
        '--- SETUP TRANSCRIPT (MOST RECENT IN THIS ACCOUNT TEMP FOLDER) ---'
        $transcript = Get-ChildItem -LiteralPath $env:TEMP -Filter 'Windows-SSH-Setup-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -ne $transcript) {
            'File: ' + $transcript.FullName
            try { Get-Content -LiteralPath $transcript.FullName -Tail 35 } catch { $_.Exception.Message }
        } else { 'No setup transcript found in this account TEMP folder.' }
        ''
        foreach ($relativeLog in @('Logs\DISM\dism.log', 'Logs\CBS\CBS.log')) {
            $logFile = Join-Path $env:WINDIR $relativeLog
            '--- ' + $relativeLog + ' (LAST 60 LINES) ---'
            try {
                $logInfo = Get-Item -LiteralPath $logFile
                'Last written: ' + $logInfo.LastWriteTime.ToString('o')
                'Size in bytes: ' + $logInfo.Length
                Get-Content -LiteralPath $logFile -Tail 60
            } catch {
                'Cannot read log: ' + $_.Exception.Message
                'If access was denied, right-click this batch file and choose Run as administrator.'
            }
            ''
        }
        '--- END ---'
        'Log activity may include other Windows servicing operations; silence alone does not prove a hang.'
    }
    $content | Out-String -Width 240 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Host ('Report saved to: ' + $report) -ForegroundColor Cyan
    Write-Host 'Opening the report in Notepad. Share the report file or paste its contents into the conversation.'
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\notepad.exe') -ArgumentList @('"' + $report + '"')
    exit 0
} catch {
    Write-Host ('Diagnostic collection failed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
