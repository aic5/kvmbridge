# Changelog

## 1.1.0

First public release of the working KvmBridge deployment.

- Configurable serial port/FTDI identity and server address; no deployment-specific defaults.
- Self-contained Windows service with automatic startup, authenticated HTTPS, serial
  confirmation and timestamped event tracking.
- Windows Schannel-compatible certificate loading and service-update file-lock handling.
- Four silent Mac apps with repeat-open handling, bounded connection recovery and logs.
- Private local credential provisioning, original Stream Deck icons, setup guides and CI.

Earlier private iterations were tested with the documented TESmart/Waveshare hardware.
The public source is a generalized package; see README for verification boundaries.
