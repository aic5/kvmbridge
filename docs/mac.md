# Mac launchers

Build with Python 3 and macOS's `osacompile`, `codesign`, and `curl` tools:

```sh
python3 mac/build.py --client /private/path/to/exported-client --output "$HOME/Applications/KvmBridge"
```

The exported client must contain `client.conf`, `server-url.txt`, and `kvmbridge-ca.pem`.
The builder validates them and stores them in the current user's local
`~/Library/Application Support/KvmBridge` folder with private permissions. Use
`--replace-credentials` when deliberately updating an existing configuration.

If using prebuilt apps from a release:

```sh
python3 mac/build.py --configure-only --client /private/path/to/exported-client
```

Generated apps contain no API key. They may be placed in a commands folder; keep
credentials out of public repositories and cloud-synced folders. The `.app` launchers
are independent of the `.command` files. Shell commands use the adjacent `switch.sh`.
`status.command` prints JSON and can be run in Terminal.

Each app starts one request on initial launch and handles subsequent `reopen` events.
It remains in the background with no Dock icon or Terminal window. Quit it through
Activity Monitor before replacing its files, or sign out. Relaunch it after an update.
Apps are locally signed, not Developer ID signed or notarized.

## Responsiveness and failure handling

Workers run asynchronously. A newer selection cancels an older request that has not
started an attempt or is waiting to retry. Already-transmitted commands cannot be
cancelled; the Windows service serializes them.

Only curl exit code 7 (TCP connection not established) is retried: at most three attempts,
300 ms apart, with a two-second connection timeout and eight-second total request timeout.
HTTP failures and ambiguous timeouts are not replayed. No automatic retry occurs after a
command might have reached the KVM. A disconnected/sleeping Windows host still needs to
be woken; these apps do not implement Wake-on-LAN.

Local logs:

- `computer-N-launch.log`: request ID, attempts, HTTP status, timings and confirmation.
- `computer-N-error.log`: latest curl error.
- `computer-N-launch.log.previous`: previous timing log after a roughly 64 KiB rotation.

A matching serial confirmation does not guarantee the displays have finished negotiating
their video signal. Visual switching can take longer than the API response.

## Troubleshooting

1. Allow each launcher under **System Settings → Privacy & Security → Local Network**
   if macOS asks or reports an immediate connection failure.
2. Run `status.command` and verify `serialConnected` and the server address.
3. Check the local launcher log. If there is no new entry, check the Stream Deck target
   path and that its action is **System → Open**.
4. Check the Windows Private/Domain network profile, firewall rule, and that the host is awake.
5. A certificate failure requires the correct CA and a hostname/IP covered by the server
   certificate. Do not disable verification with `curl -k`.

For multiple Macs, privately provision each Mac's local client directory and map its own
Stream Deck buttons. Keep Stream Deck connected to the controlling Mac if it must remain
usable while the KVM changes USB focus.
