# Optional: authorize a supplied PUBLIC key for Windows administrator SSH login.
# Run from an elevated PowerShell window. Existing authorized keys are preserved.
param([Parameter(Mandatory=$true)][string]$PublicKeyPath)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator.'
}
$key = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PublicKeyPath).Path).Trim()
if ($key -notmatch '^ssh-ed25519 [A-Za-z0-9+/]+={0,2}( [^\r\n]*)?$') {
    throw 'Supply a single-line Ed25519 public key (.pub), never a private key.'
}
$keygen = Join-Path $env:WINDIR 'System32\OpenSSH\ssh-keygen.exe'
& $keygen -lf $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw 'ssh-keygen could not validate the public key.' }
$target = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
if (-not (Test-Path (Split-Path $target))) { throw 'Install OpenSSH Server first.' }
$existing = if (Test-Path $target) { [IO.File]::ReadAllText($target) } else { '' }
if (-not $existing.Contains(($key -split ' ')[1])) {
    [IO.File]::AppendAllText($target, "`r`nrestrict " + $key + "`r`n", (New-Object Text.UTF8Encoding($false)))
}
$acl = New-Object Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($text in @('S-1-5-32-544', 'S-1-5-18')) {
    $sid = New-Object Security.Principal.SecurityIdentifier($text)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow')))
}
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
Set-Acl -LiteralPath $target -AclObject $acl
Write-Host 'Public key authorized for administrator accounts using the standard OpenSSH configuration.'
