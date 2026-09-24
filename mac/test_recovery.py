from pathlib import Path
import tempfile,subprocess,sys,shlex
root=Path(__file__).resolve().parent
worker=(root/'switch.sh').read_text()
for mode,expected in [('connect',3),('http',1),('timeout',1),('superseded',1)]:
    with tempfile.TemporaryDirectory(dir=root) as tmp:
        d=Path(tmp)
        mock=d/'curl-mock'
        mock.write_text('#!'+sys.executable+'\n'+'''from pathlib import Path
import sys
p=Path(__file__).parent
counter=p/'counter'
n=int(counter.read_text())+1 if counter.exists() else 1
counter.write_text(str(n))
mode=(p/'mode').read_text()
if mode=='superseded':
    (p/'latest-request').write_text('newer-request')
    sys.exit(7)
if mode=='http': sys.exit(22)
if mode=='timeout': sys.exit(28)
if n<3: sys.exit(7)
Path(sys.argv[sys.argv.index('--output')+1]).write_text('{"confirmed":true,"computer":4}')
print('http=200 seconds=0.01',end='')
''')
        mock.chmod(0o700)
        (d/'mode').write_text(mode)
        (d/'server-url.txt').write_text('https://example.invalid:8443')
        test=d/'worker.sh'
        test.write_text(worker.replace('config="$HOME/Library/Application Support/KvmBridge"','config='+shlex.quote(str(d))).replace('/usr/bin/curl',shlex.quote(str(mock))))
        result=subprocess.run(['/bin/sh',str(test),'4'],capture_output=True,text=True,timeout=10)
        attempts=int((d/'counter').read_text())
        assert attempts==expected,(mode,attempts,result.stderr)
        assert not list(d.glob('.reply-*')) and not list(d.glob('.error-*'))
        print(f'PASS {mode}: {attempts} attempt(s)')
