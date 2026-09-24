from pathlib import Path
import tempfile
import build

with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    source = root / 'export'
    source.mkdir()
    # Explicitly synthetic fixture, never a deployment credential.
    (source/'client.conf').write_text('header = "Authorization: Bearer ' + 'a' * 64 + '"\n')
    (source/'server-url.txt').write_text('https://example.invalid:8443\n')
    (source/'kvmbridge-ca.pem').write_text('-----BEGIN CERTIFICATE-----\nfixture\n-----END CERTIFICATE-----\n')
    files = build.validate_client(source)
    dest = root / 'local'
    build.install_client(files, dest)
    assert dest.stat().st_mode & 0o777 == 0o700
    assert (dest/'client.conf').stat().st_mode & 0o777 == 0o600
    try:
        build.install_client(files, dest)
        raise AssertionError('Existing client should need explicit replacement')
    except ValueError:
        pass
    for invalid in ('http://example.invalid', 'https://user:pass@example.invalid', 'https://example.invalid/path', 'https://example.invalid?key=value'):
        (source/'server-url.txt').write_text(invalid)
        try:
            build.validate_client(source)
            raise AssertionError('Invalid origin accepted')
        except ValueError:
            pass
print('PASS client import validation, overwrite protection and private permissions')
