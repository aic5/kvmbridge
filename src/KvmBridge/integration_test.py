"""macOS/Linux HTTPS + PTY integration test. Usage: python3 integration_test.py APP WORKDIR"""
import concurrent.futures
import json
import os
from pathlib import Path
import pty
import select
import socket
import ssl
import subprocess
import sys
import threading
import time
import tty
import urllib.error
import urllib.request

app = str(Path(sys.argv[1]).resolve())
folder = Path(sys.argv[2]).resolve()
folder.mkdir(parents=True, exist_ok=True)
os.environ['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = str(folder / 'runtime-cache')
data = folder / 'data'
master, slave = pty.openpty()
tty.setraw(slave)
serial_name = os.ttyname(slave)
with socket.socket() as temporary:
    temporary.bind(('127.0.0.1', 0))
    port = temporary.getsockname()[1]
subprocess.run([app, 'init', '--data', str(data), '--port', serial_name,
                '--bind', '127.0.0.1', '--host', '127.0.0.1', '--https-port', str(port)], check=True)
config = json.loads((data / 'config.json').read_text())
config['ResponseTimeoutMs'] = 600
(data / 'config.json').write_text(json.dumps(config))
key = config['ApiKey']
context = ssl.create_default_context(cafile=str(data / 'kvmbridge-ca.pem'))
url = f'https://127.0.0.1:{port}'
writes = []
emit_reply = threading.Event()
emit_reply.set()
stop = threading.Event()

def peer():
    pending = b''
    while not stop.is_set():
        ready, _, _ = select.select([master], [], [], .1)
        if not ready:
            continue
        pending += os.read(master, 1024)
        while len(pending) >= 6:
            frame, pending = pending[:6], pending[6:]
            writes.append(frame)
            if emit_reply.is_set():
                assert frame[:4] == bytes.fromhex('aa bb 03 01') and frame[-1] == 0xee, frame.hex()
                os.write(master, b'Set ch')
                time.sleep(.025)
                os.write(master, f' is {frame[4]-1}\r\r\nQuerry pc LED\r\r\nWrite EEPROM \r\n'.encode())

thread = threading.Thread(target=peer, daemon=True)
thread.start()
stdout = (folder / 'server.log').open('w')
process = subprocess.Popen([app, 'run', '--data', str(data)], stdout=stdout, stderr=subprocess.STDOUT)

def request(path, method='GET', body=None, token=key):
    headers = {'Content-Type': 'application/json'}
    if token is not None:
        headers['Authorization'] = 'Bearer ' + token
    payload = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url + path, data=payload, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, context=context, timeout=10) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw else None

def eventually(condition, duration=10):
    end = time.monotonic() + duration
    last_error = None
    while time.monotonic() < end:
        try:
            if condition():
                return
        except (OSError, urllib.error.URLError) as error:
            last_error = error
        time.sleep(.1)
    raise AssertionError('Timed out waiting for condition: ' + repr(last_error) + '; server log: ' + (folder / 'server.log').read_text())

def check(condition, description):
    assert condition, description
    print('PASS: ' + description, flush=True)

try:
    eventually(lambda: request('/health')[0] == 200)
    check(request('/health', token=None)[0] == 401, 'missing API key rejected')
    check(request('/api/kvm/status', token='bad')[0] == 401, 'incorrect API key rejected')
    try:
        urllib.request.urlopen(url + '/health', timeout=3)
        raise AssertionError('Untrusted certificate accepted')
    except urllib.error.URLError as ex:
        check(isinstance(ex.reason, ssl.SSLCertVerificationError), 'TLS requires the exported CA certificate')
    check(request('/api/kvm/selection', 'PUT', {'computer': 5})[0] == 400, 'invalid channel rejected by API')
    check(request('/api/kvm/selection', 'PUT', {'computer': 1, 'extra': True})[0] == 400, 'unknown JSON fields rejected')
    check(request('/api/kvm/selection', 'GET')[0] == 405, 'wrong HTTP method rejected')
    check(request('/missing')[0] == 404, 'unknown endpoint returns 404')
    check(request('/api/kvm/capabilities')[1]['independentStatusQuery'] is False, 'capabilities disclose missing query support')
    eventually(lambda: request('/api/kvm/status')[1]['serialConnected'])
    check(request('/api/kvm/status')[1]['computer'] is None, 'API starts with unknown selection')
    code, result = request('/api/kvm/selection', 'PUT', {'computer': 4})
    check(code == 200 and result['confirmed'] and result['reply'] == 'Set ch is 3', 'HTTPS request sends exact serial bytes and waits for reply')
    os.write(master, b'QuerrySet ch is 0\r\r\nled_status_update=00 idx=0\r\r\n')
    eventually(lambda: request('/api/kvm/status')[1]['computer'] == 1)
    check(True, 'unsolicited button event reflected by status API')
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        results = list(executor.map(lambda pc: request('/api/kvm/selection', 'PUT', {'computer': pc}), [1, 2, 3, 4]))
    check(all(code == 200 and result['confirmed'] for code, result in results), 'concurrent HTTP callers are serialized')
    count = len(writes)
    emit_reply.clear()
    code, result = request('/api/kvm/selection', 'PUT', {'computer': 2})
    check(code == 504 and not result['confirmed'] and len(writes) == count + 1, 'serial silence returns 504 with no retry')
    check(request('/api/kvm/status')[1]['selectionState'] == 'stale', 'unconfirmed switch marks state stale')
    emit_reply.set()
    clients = folder / 'clients'
    subprocess.run([app, 'client', '--data', str(data), '--output', str(clients)], check=True)
    response = subprocess.run([str(clients / 'select-4.command')], check=True, capture_output=True, text=True)
    check(json.loads(response.stdout)['confirmed'], 'generated Mac curl script works with CA and API-key file')
    status = subprocess.run([str(clients / 'status.command')], check=True, capture_output=True, text=True)
    check(json.loads(status.stdout)['computer'] == 4, 'generated status script returns latest observed selection')
    process.terminate()
    process.wait(timeout=10)
    process = subprocess.Popen([app, 'run', '--data', str(data)], stdout=stdout, stderr=subprocess.STDOUT)
    eventually(lambda: request('/health')[0] == 200)
    check(request('/api/kvm/status')[1]['computer'] is None, 'service restart does not claim cached selection')
    print('ALL HTTPS/SERIAL INTEGRATION TESTS PASSED', flush=True)
finally:
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    stop.set()
    thread.join(timeout=2)
    os.close(master)
    os.close(slave)
    stdout.close()
