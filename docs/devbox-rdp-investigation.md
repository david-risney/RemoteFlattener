# Cloud DevBox RDP Detection Investigation

## Problem

RemoteFlattener does not detect the RDP connection between a Microsoft Cloud DevBox
and its client machine. The DevBox's remote desktop session is not matched to a
virtual desktop on the parent (client) machine.

## Background: How RemoteFlattener Matches Today

The matching pipeline has two steps, both designed for traditional `mstsc.exe` RDP:

1. **Peer discovery** (`RdpConnectionDetector`): Scans established TCP connections on
   **port 3389**. On a server, the client's IP appears as the remote end of an inbound
   connection. On a client, the server's IP appears as the remote end of an outbound
   connection. Each IP is reverse-DNS resolved and normalized to a short hostname
   (first DNS label, uppercased) via `MachineInfo.NormalizeHostname`.

2. **Window-to-desktop mapping** (`RdpWindowLocator`): On the **client** machine,
   enumerates all visible `mstsc.exe` windows (matched by PID from
   `Process.GetProcessesByName("mstsc")`), parses the window title
   (`"HOSTNAME - Remote Desktop Connection"`), matches the hostname portion against
   known peer names, and determines which virtual desktop each window lives on via
   `VirtualDesktopProvider.GetDesktopIndexForHwnd`.

The result is the `RdpHostedServers` dictionary (server name → desktop index) which
is broadcast over the mesh so every node can build the correct tree.

## Investigation: DevBox Server Side

Performed on: 2026-05-10, inside a Cloud DevBox (`CPC-davri-XXS9M`).

### What works

| Check | Result | Notes |
|---|---|---|
| `GetSystemMetrics(SM_REMOTESESSION)` | **1** (REMOTE) | `RdpRoleDetector.IsRemoteSession()` correctly identifies this as a remote session |
| `CLIENTNAME` env var | **`DAVRIS-10`** | Identifies the client machine name reliably |
| `HKCU:\Volatile Environment\2` registry | `CLIENTNAME=DAVRIS-10` | Same info available in registry, keyed by session ID |
| Virtual desktop registry | Present and populated | `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops` has `CurrentVirtualDesktop` and `VirtualDesktopIDs` |
| RemoteFlattener listening | Port **8765** open | `0.0.0.0:8765` in LISTEN state |
| Port 3389 listening | Yes | `0.0.0.0:3389` and `[::]:3389` are in LISTEN state |

### What doesn't work

| Check | Result | Notes |
|---|---|---|
| Established TCP on port 3389 | **None** | Zero established connections on port 3389 — this is the root cause |

### DevBox session details

```
Session name:  rdp-sxs260209600#0
Session ID:    2
Session state: Active
Transport:     WebRTC (MsRdcWebRTCSvc process)
```

The session type is `rdp-sxs` (not `rdp-tcp`), indicating the connection is tunneled
through the Azure RD Infrastructure rather than arriving as a direct TCP connection.

### DevBox-specific processes

| Process | Path | Role |
|---|---|---|
| `Microsoft.DevCenter.DevBoxAgent` | — | DevBox lifecycle management |
| `MsRdcWebRTCSvc` | — | WebRTC transport for the RDP session |
| `rdpclipcdv` | `C:\Program Files\Microsoft RDInfra\rdr_sxs\...` | Clipboard redirection |
| `rdpinputcdv` | — | Input redirection |
| `rdpvchost` | `C:\Program Files\Microsoft RDInfra\rdr_sxs\...` | Virtual channel host |

### DevBox-specific environment variables

| Variable | Value |
|---|---|
| `IsDevBox` | `True` |
| `CLIENTNAME` | `DAVRIS-10` |
| `SESSIONNAME` | `rdp-sxs260209600#0` |

### Network connections (non-loopback established)

No connections on port 3389. All established connections go to Azure infrastructure
IPs (168.63.129.16, various 10.x.x.x, GitHub IPs, etc.) — none to the client machine
directly.

## Root Cause

`RdpConnectionDetector.ExtractRdpAddresses` scans TCP connections for port 3389.
Cloud DevBox uses **WebRTC** transport (`MsRdcWebRTCSvc`) via the `rdp-sxs` session
infrastructure — the RDP protocol is tunneled through Azure's Remote Desktop
Infrastructure, so **no direct TCP connection on port 3389 exists** between the client
and DevBox.

This means `GetRdpPeers()` returns an empty list, and the DevBox is never discovered
as an RDP peer by the client (or vice versa).

## Available Signals for a Fix

On the **server (DevBox) side**, the client machine name is available through:

1. **`CLIENTNAME` environment variable** — set to `DAVRIS-10`, the short machine name
   of the client. This is the same format `MachineInfo.NormalizeHostname` produces.
2. **Registry** at `HKCU:\Volatile Environment\<SessionId>` — contains `CLIENTNAME`
   keyed by the active session ID.
3. **`qwinsta` / WTS API** — the session list shows the active `rdp-sxs` session with
   the username, which could be cross-referenced.

## TODO: Client-Side Investigation

The following still needs to be investigated on the client machine (`DAVRIS-10`):

- What process hosts the DevBox connection (likely `msrdc.exe` or Windows App, not
  `mstsc.exe`)?
- What does the window title look like?
- Is there a TCP connection on port 3389 from the client side, or is it also
  WebRTC-only?
- Can `RdpWindowLocator` be extended to find the DevBox client window and map it to a
  virtual desktop?
