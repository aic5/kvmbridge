#!/usr/bin/env python3
"""Build four silent macOS apps; optionally install a private exported client locally."""
import argparse
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent

def validate_client(source):
    key = (source / 'client.conf').read_text()
    if not re.fullmatch(r'header = "Authorization: Bearer [a-fA-F0-9]{64}"\s*', key):
        raise ValueError('Expected an unmodified client.conf exported by KvmBridge.')
    url = (source / 'server-url.txt').read_text().strip()
    parts = urlsplit(url)
    if (parts.scheme != 'https' or not parts.hostname or parts.username or parts.password
            or parts.path or parts.query or parts.fragment or any(c.isspace() for c in url)):
        raise ValueError('server-url.txt must contain an HTTPS origin, without a path or credentials.')
    _ = parts.port  # Validate the port if supplied.
    pem = (source / 'kvmbridge-ca.pem').read_text()
    if '-----BEGIN CERTIFICATE-----' not in pem or 'PRIVATE KEY' in pem:
        raise ValueError('Expected the exported public CA certificate.')
    return {'client.conf': key, 'server-url.txt': url + '\n', 'kvmbridge-ca.pem': pem}

def install_client(files, destination, replace=False):
    if destination.exists() and not replace:
        raise ValueError('Local client already exists; use --replace-credentials to update it.')
    destination.mkdir(parents=True, exist_ok=True, mode=0o700)
    destination.chmod(0o700)
    for name, contents in files.items():
        target = destination / name
        if target.is_symlink():
            raise ValueError('Refusing to write through a credential symlink.')
        # Create with private permissions before writing any credential bytes.
        import os
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'w') as output:
            os.fchmod(output.fileno(), 0o600)
            output.write(contents)

def build(output):
    output.mkdir(parents=True, exist_ok=True)
    if any(output.glob('KVM Computer *.app')):
        raise ValueError('Output contains existing apps; choose a new build directory.')
    shutil.copy2(ROOT / 'switch.sh', output / 'switch.sh')
    for n in range(1, 5):
        script = f'''on run
    my switchComputer()
end run
on reopen
    my switchComputer()
end reopen
on idle
    return 3600
end idle
on switchComputer()
    set workerPath to (POSIX path of (path to me)) & "Contents/Resources/switch.sh"
    do shell script "/usr/bin/nohup /bin/sh " & quoted form of workerPath & " {n} >/dev/null 2>&1 &"
end switchComputer
'''
        app = output / f'KVM Computer {n}.app'
        subprocess.run(['/usr/bin/osacompile', '-s', '-o', str(app), '-'], input=script, text=True, check=True)
        shutil.copy2(ROOT / 'switch.sh', app / 'Contents/Resources/switch.sh')
        plist = app / 'Contents/Info.plist'
        data = plistlib.loads(plist.read_bytes())
        data.update(NSLocalNetworkUsageDescription='Connect to your Windows KVM service to select a computer.',
                    LSUIElement=True, CFBundleIdentifier=f'local.kvmbridge.computer{n}',
                    CFBundleName=f'KVM Computer {n}', CFBundleDisplayName=f'KVM Computer {n}',
                    CFBundleShortVersionString='1.1.0', CFBundleVersion='3')
        plist.write_bytes(plistlib.dumps(data))
        subprocess.run(['/usr/bin/xattr', '-cr', str(app)], check=True)
        subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', str(app)], check=True)
        subprocess.run(['/usr/bin/codesign', '--verify', str(app)], check=True)
        command = output / f'KVM Computer {n}.command'
        command.write_text('#!/bin/sh\nDIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexec /bin/sh "$DIR/switch.sh" ' + str(n) + '\n')
        command.chmod(0o700)
        print('Built', app.name)
    shutil.copy2(ROOT / 'status.command', output / 'status.command')
    (output / 'status.command').chmod(0o700)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path('dist/KvmBridge-Mac'))
    parser.add_argument('--client', type=Path, help='Private folder exported by KvmBridge.exe client')
    parser.add_argument('--replace-credentials', action='store_true')
    parser.add_argument('--configure-only', action='store_true', help='Install credentials without rebuilding apps')
    args = parser.parse_args()
    files = validate_client(args.client) if args.client else None
    destination = Path.home() / 'Library/Application Support/KvmBridge'
    if files and destination.exists() and not args.replace_credentials:
        parser.error('Local client exists; use --replace-credentials to update it.')
    if args.configure_only and files is None:
        parser.error("--configure-only requires --client")
    if not args.configure_only:
        build(args.output.resolve())
    if files:
        install_client(files, destination, args.replace_credentials)
        print('Credentials installed locally in Application Support; apps contain no credentials.')
    else:
        print('Apps built. Install client.conf, server-url.txt and kvmbridge-ca.pem in ~/Library/Application Support/KvmBridge before use.')

if __name__ == '__main__':
    main()
