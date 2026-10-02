# Remote Mesh requests and the future `MeshRequestGate`

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

## Remote Mesh requests and the future `MeshRequestGate`

Several remote operations follow:

```text
remote command ->
<- MSG_SENT

... Mesh network ...

<- final push response
```

Examples include login, status, telemetry, binary request, and path discovery.

Important current-firmware constraint: these operations share firmware pending-request state. Starting another such request can call/behave like `clearPendingReqs()` and overwrite correlation state for the previous one.

Therefore MeshCoreSharp should eventually add one shared `MeshRequestGate` spanning the whole remote operation, not merely the immediate `MSG_SENT` phase.

Immediate `CommandGate` and remote `MeshRequestGate` serve different purposes.

### Login

Pattern:

```text
SEND_LOGIN -> MSG_SENT
later -> LOGIN_SUCCESS or LOGIN_FAIL
```

Both success and failure are terminal completions and must finish the high-level login operation. Do not wait only for success and let failure become a timeout.

### Legacy status request

Status correlation in baseline firmware is effectively single-flight and has legacy matching behavior. The final `STATUS_RESPONSE` does not provide a universal client-visible request ID suitable for concurrent status requests.

### Telemetry

Remote telemetry may use internal firmware tagging, but final client-visible response formats do not make arbitrary parallel client requests safe. Keep it under the shared remote-request gate unless a targeted firmware revision is proven to support more.

### Binary request

Binary request is better designed for correlation: the final `BINARY_RESPONSE` includes a tag that can be matched to the tag from `MSG_SENT`.

Even so, examined current firmware still keeps only one shared pending remote request, so do not infer that multiple simultaneous binary requests are safe merely because the packet contains a tag.

### Path discovery

Immediate phase can contain a tag, but the final path-discovery response does not expose a universal request tag suitable for arbitrary parallel requests. Treat it as single-flight under the shared remote gate.

### Trace

Trace operations are distinct because application-provided tags can be present in returned trace data. They may support a different concurrency tracker. Verify the exact targeted firmware behavior before enabling concurrency.
