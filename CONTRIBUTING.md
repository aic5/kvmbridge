# Contributing

Open an issue describing the KVM model, operating systems, expected behavior and observed
behavior. Redact API keys, private keys, host identifiers and unrelated logs. For protocol
changes, include the exact bytes and response text from a controlled test on your hardware.

Run the relevant tests in [the development guide](docs/development.md). Keep transport,
status freshness and display routing claims separate. Add hardware/model support explicitly
rather than assuming that all TESmart models use the same commands.

Pull requests should describe the user-visible change and how it was verified. Contributions
are accepted under the repository's MIT license.
