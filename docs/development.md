# Development and packaging

Requirements: .NET 10 SDK; Python 3 for integration tests and Mac packaging. No Python
packages are required. Mac app compilation requires macOS. Windows builds target x64.

```sh
dotnet restore src/KvmBridge/KvmBridge.csproj --locked-mode
dotnet publish src/KvmBridge/KvmBridge.csproj -c Release -r win-x64 \
  --self-contained true --no-restore -o dist/windows
```

The self-contained executable embeds C# source, its project/lock files, service guide,
and licensing notices. `KvmBridge.exe source --output DIRECTORY` extracts those resources.
Native runtime libraries are unpacked at execution time into a protected service-writable
cache. The distributor should include the MIT license and third-party notices with builds.

## Tests

```sh
# Choose the runtime matching the development machine:
dotnet publish src/KvmBridge/KvmBridge.csproj -c Release -r osx-arm64 \
  --self-contained true -o dist/test-host
./dist/test-host/KvmBridge self-test
python3 src/KvmBridge/integration_test.py ./dist/test-host/KvmBridge artifacts/integration
python3 mac/test_recovery.py
python3 mac/test_config.py
```

The integration test uses a pseudo-terminal peer and generated local HTTPS certificates,
not physical hardware. It verifies authentication, malformed inputs, selection, concurrent
requests, front-panel events, timeout/recovery, and exported curl scripts. Use a new/empty
integration directory each time. Runtime-specific package assets may need restore when
switching target runtimes.

`mac/test_recovery.py` uses an isolated mock curl to verify bounded connect retries,
no retries after HTTP errors or timeouts, cancellation before retry, and temporary-file
cleanup. `mac/test_config.py` validates private client imports and permissions.

## Mac distribution

```sh
python3 mac/build.py --output dist/KvmBridge-Mac
```

This builds credential-free apps without touching an existing installation. It does not
contact the KVM. The `--client` option separately provisions private local credentials.
Do not include generated client exports, certificates, logs, SSH keys, or private profiles
in source archives. `.gitignore` excludes these; review the staged files before publishing.

The CI workflow builds/tests on Windows and macOS and uploads credential-free build
artifacts. Build output and live configuration are never committed. Physical confirmation
and startup behavior should be checked on a destination host when changing serial,
certificate, service, or firewall code. Do not run integration tests against a live serial port.

## Architecture and scope

`Program.cs` owns the API, configuration and logging. `Controller.cs` owns serial discovery,
the port, parsing and a bounded request queue. `Setup.cs` owns certificates, installation,
firewall configuration and client export. `SelfTests.cs` exercises the controller with a
fake connection. The Mac worker uses the exported API configuration and keeps a small
local request/timing history. Apps embed that worker and handle both initial and repeat opens.

Avoid adding automatic retries after ambiguous serial writes. Protocol observations are
not proof of video routing, and the hardware has no transaction IDs or verified heartbeat.
