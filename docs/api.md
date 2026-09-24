# HTTPS API

All endpoints require `Authorization: Bearer API_KEY`. Use the generated CA to verify
TLS and the exported `client.conf` to avoid putting the key in process arguments.
Default port: **8443**. The service does not enable CORS or cloud discovery.

| Method | Path | Meaning |
| --- | --- | --- |
| GET | `/health` | Process health and serial-port connection |
| GET | `/api/kvm/status` | Latest observed selection and timestamps |
| GET | `/api/kvm/capabilities` | Supported model, inputs and known limitations |
| PUT | `/api/kvm/selection` | Select both displays: `{"computer":1}` through `4` |

Successful selection:

```json
{
  "confirmed": true,
  "computer": 1,
  "reply": "Set ch is 0",
  "observedAt": "2026-01-01T00:00:00Z",
  "error": null,
  "confirmation": "Matching channel event; firmware supplies no request IDs."
}
```

Status fields include `serialConnected`, `serialPort`, `computer`, `selectionState`,
`lastObservedComputer`, `lastObservedAt`, `lastSerialDataAt`, and `lastError`.
`selectionState` is `unknown`, `observed`, or `stale`. `computer` is null when unknown or
stale; previous observations remain available separately. Timestamps are UTC.

| Status | Meaning |
| --- | --- |
| 200 | Successful response; selection has a matching channel event |
| 400 | Invalid input or malformed request |
| 401 | Missing/incorrect API key |
| 404 / 405 | Unknown route / unsupported method |
| 429 | Command queue full |
| 503 | Serial adapter unavailable or disconnected during a command |
| 504 | Request expired or KVM confirmation timed out |

JSON input has an exact lowercase `computer` field and a maximum size of 1024 bytes.
One serial command is in flight at a time. The queue is bounded to 16; queued requests
expire after eight seconds. Default serial confirmation timeout is four seconds.

A 504 does **not** prove that the switch failed. The command might have executed without
a timely response. The service never automatically retransmits a selection. A command
already sent may execute after the HTTP client disconnects. Firmware supplies no request
IDs, so a matching front-panel event could coincide with a pending selection request.

`/health` is not a KVM heartbeat. A connected USB adapter can remain open while the KVM
is powered off; the last observed channel can consequently be outdated.
