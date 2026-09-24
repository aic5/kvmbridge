"""Package existing Windows/Mac builds with documentation, licenses and checksums on macOS."""
from pathlib import Path
import shutil,subprocess,hashlib,zipfile
repo=Path(__file__).resolve().parents[1]
import xml.etree.ElementTree as ET
version=ET.parse(repo/'src/KvmBridge/KvmBridge.csproj').findtext('.//Version')
assert version is not None
release=repo/'dist/release'
release.mkdir(parents=True, exist_ok=True)
windows=release/f'KvmBridge-Windows-{version}'
mac=release/f'KvmBridge-Mac-{version}'
for dest in (windows,mac):
    dest.mkdir(exist_ok=True)
    for f in ['README.md','LICENSE','NOTICE']:
        shutil.copy2(repo/f,dest/f)
    shutil.copytree(repo/'docs',dest/'docs',dirs_exist_ok=True)
    shutil.copytree(repo/'third-party',dest/'third-party',dirs_exist_ok=True)
    shutil.copytree(repo/'streamdeck',dest/'streamdeck',dirs_exist_ok=True)
shutil.copy2(repo/'dist/windows/KvmBridge.exe',windows/'KvmBridge.exe')
shutil.copy2(repo/'dist/windows/KvmBridge.exe',release/'KvmBridge.exe')
shutil.copytree(repo/'windows',windows/'windows',dirs_exist_ok=True)
shutil.copytree(repo/'dist/KvmBridge-Mac',mac/'Applications',dirs_exist_ok=True)
shutil.copytree(repo/'mac',mac/'mac',ignore=shutil.ignore_patterns('__pycache__'),dirs_exist_ok=True)
for folder in (windows,mac):
    zipfile_name=release/(folder.name+'.zip')
    subprocess.run(['/usr/bin/ditto','-c','-k','--keepParent',str(folder),str(zipfile_name)],check=True)
    with zipfile.ZipFile(zipfile_name) as archive:
        for name in archive.namelist():
            assert not name.endswith(('client.conf','server-url.txt','.pfx','.pem','.log','.key')),name
            assert 'previous-apps' not in name and 'public-integration' not in name
assets=[release/'KvmBridge.exe',release/f'KvmBridge-Windows-{version}.zip',release/f'KvmBridge-Mac-{version}.zip']
(release/'SHA256SUMS.txt').write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in assets))
print((release/'SHA256SUMS.txt').read_text())
