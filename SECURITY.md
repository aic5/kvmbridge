# Security and deployment

Use KvmBridge on a trusted LAN. It has HTTPS and bearer-key authentication, but no user
accounts, per-device authorization, or internet-facing hardening. Do not port-forward it.
Protect the exported API key like any other control credential.

Never commit `client.conf`, `config.json`, server certificates/private keys, SSH keys,
logs, or personal Stream Deck exports. The public source contains no deployment credentials.
Mac launchers read credentials from local Application Support and do not embed them.

Rotate an exposed key immediately; see [Windows maintenance](docs/windows.md).
Do not post credentials or sensitive diagnostic logs in public issues. Report ordinary
bugs through GitHub Issues. For vulnerabilities, use GitHub's private vulnerability
reporting if enabled on this repository; otherwise contact the maintainer through their
GitHub profile before sharing exploit details publicly.
