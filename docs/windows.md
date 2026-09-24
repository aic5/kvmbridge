# Windows installation and maintenance

Use a Windows x64 host with the FTDI VCP driver installed. The release executable
includes .NET; a separate runtime is not required. Device Manager should show the
converter under Ports. Connect it directly to the host rather than a switched KVM USB port.

In Administrator PowerShell:

```powershell
.\KvmBridge.exe ports
.\KvmBridge.exe install --port COM5
# Alternatively, identify the FTDI adapter so COM renumbering is handled:
.\KvmBridge.exe install --serial YOUR_FTDI_SERIAL
.\KvmBridge.exe client --output .\KvmBridge-Mac
```

Choose one `install` form. `--serial` uses the FTDI registry and live Plug and Play
state, so an unplugged adapter's old registry entry cannot select an unrelated device.
`--port` pins an explicit port; check it again if Windows reassigns device numbers.

Installation creates:

- `C:\Program Files\KvmBridge\KvmBridge.exe`
- `C:\ProgramData\KvmBridge\config.json`, `server.pfx`, `kvmbridge-ca.pem`
- `C:\ProgramData\KvmBridge\logs` and `runtime`
- The automatic **KvmBridge** service, running as **LocalService**, with restart recovery.
- **KvmBridge-HTTPS** firewall rule: TCP 8443, LocalSubnet, Private/Domain profiles.

The service waits for the adapter if it is absent and never switches automatically at
startup. LocalService can read configuration and write only the logs/runtime directories
within the application data directory. Windows manages the temporary Schannel key container.

Optional flags: `--host HOST_OR_IP`, `--https-port 8443`, `--bind 0.0.0.0`.
Use a DHCP reservation or stable address. The certificate includes the requested host,
Windows hostnames, localhost, and IPv4 addresses present at initial installation.
An existing certificate cannot acquire a new address through `client --host`.

If LAN access times out, check the Wi-Fi/Ethernet profile under Windows network settings.
Mark only a trusted network Private. Installation does not change the profile or open
Public-network access. Windows must stay awake for this service to respond.

## Export and update

`client` exports a private API-key file, public CA, server URL, and curl scripts. The
server private key stays on Windows. Transfer the folder privately. Do not publish it.

To update, run the new executable's `install` command as administrator. Existing config,
API key, and certificates are preserved. The installer stops the service, waits for the
executable to be released, updates it, and checks its local HTTPS health endpoint.

```powershell
Get-Service KvmBridge
Restart-Service KvmBridge
.\KvmBridge.exe status
.\KvmBridge.exe source --output .\KvmBridge-source
.\KvmBridge.exe uninstall
```

Uninstall removes the service and its firewall rule, retaining files and credentials.
Logs rotate at roughly 5 MiB with three backups.

## Credentials and certificate renewal

Stop the service before editing protected configuration. To rotate the API key, replace
`ApiKey` with a cryptographically random 64-character hexadecimal value, restart, and
export clients again. Do not share `config.json` or `server.pfx`.

Certificates last five years. To renew or cover a new address, stop the service, privately
back up and remove `config.json`, `server.pfx`, and `kvmbridge-ca.pem` from ProgramData,
then reinstall with the desired port/serial and host. This generates a new API key and CA.
Redistribute the new client files. The original CA signing key is not retained.

## Optional SSH administration

SSH is not needed by the KVM API. `windows/Enable-Windows-SSH.bat` installs Windows
OpenSSH Server, enables automatic startup, and restricts its firewall rule to the local
subnet on Private/Domain profiles. It prompts for elevation and may need Windows Update
or a restart. `Check-Windows-SSH.bat` collects read-only diagnostics if installation stalls.
Review diagnostic reports before sharing: they can contain machine names and local paths.

Generate your own key on the administration computer. Copy only its `.pub` file to
Windows and, for an administrator account, run:

```powershell
.\Authorize-SSH-Key.ps1 -PublicKeyPath C:\path\to\your-key.pub
```

This uses the default Windows OpenSSH shared `administrators_authorized_keys` file,
so the key authorizes administrator accounts under the standard configuration. `restrict`
disables forwarding and PTY allocation; command execution and file transfer remain available.
Non-administrator accounts and custom SSH configurations require their own key setup.
No deployment SSH key is distributed with this project.
