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

## Investigation: Client Side

Performed on: 2026-05-10, on the client machine (`DAVRIS-10`).

This machine has one Cloud DevBox connection (to `CPC-davri-XXS9M`) and two
traditional mstsc RDP connections (to `DAVRIS-0` and `DAVRIS-4`).

### Client identity

| Check | Result |
|---|---|
| `Environment.MachineName` | `DAVRIS-10` |
| `SM_REMOTESESSION` | **0** (LOCAL) — this is the physical machine |
| `IsDevBox` env var | Not set |
| `CLIENTNAME` env var | Not set |

### DevBox client process

The DevBox connection is hosted by `msrdc.exe` (not `mstsc.exe`), launched as a child
of `Windows365.exe` (the "Windows App"):

| Property | Value |
|---|---|
| Process | `msrdc.exe` (PID 31324) |
| Path | `C:\Program Files\WindowsApps\MicrosoftCorporationII.Windows365_...\msrdc\msrdc.exe` |
| Parent | `Windows365.exe` (PID 46976, "Windows App") |
| Description | "Remote Desktop" |
| Company | Microsoft Corporation |

### Window properties

`msrdc.exe` creates windows with the **same window class** as `mstsc.exe` but a
**different title format**:

| Window Class | Title | Visible | Notes |
|---|---|---|---|
| `TscShellContainerClass` | `davris-10` | Yes | **Main RDP viewport** — same class as mstsc! |
| `#32770` | `Remote Desktop` | Yes | Dialog window |
| `BBarWindowClass` | `BBar` | Yes | Connection bar |

**Key differences from mstsc:**

| Aspect | mstsc | msrdc (DevBox) |
|---|---|---|
| Window class | `TscShellContainerClass` | `TscShellContainerClass` (same!) |
| Title format | `HOSTNAME - Remote Desktop Connection` | `davris-10` (friendly name only) |
| Title content | Server's actual hostname | DevBox display name from Windows App |
| Title separator | ` - ` present | No separator |

The title `davris-10` comes from the `WorkspaceDisplayName` field in the JSON launch
config, which is the user-assigned DevBox name — **NOT** the machine hostname
(`CPC-davri-XXS9M`).

### Virtual Desktop API

✅ **`GetWindowDesktopId` works on msrdc windows!**

| API Call | Result |
|---|---|
| `IsWindowOnCurrentVirtualDesktop` | `hr=0x00000000` — succeeds |
| `GetWindowDesktopId` | `hr=0x00000000, id=c13c4511-33cd-4572-8de0-f0e3de48107e` |
| Desktop index | **Desktop 4** (out of 5) |

The `TscShellContainerClass` window is correctly tracked by the Virtual Desktop API.
`VirtualDesktopProvider.GetDesktopIndexForHwnd` will return the correct desktop index.

### Network connectivity

| Check | Result |
|---|---|
| TCP port 3389 connections | **None** — no direct TCP to DevBox |
| msrdc TCP connections | To Azure endpoints (`20.42.x:443`, `40.64.x:443`) only |
| msrdc UDP | `192.168.4.103:57211` — WebRTC data channel |
| DNS resolve `CPC-davri-XXS9M` | **FAILS** — DevBox hostname not in DNS |
| RemoteFlattener mesh (port 8765) | Connected to `DAVRIS-0` and `DAVRIS-4` only, **NOT** to DevBox |

The DevBox is completely behind Azure's RD gateway. There is no direct IP path between
the client and DevBox — all traffic goes through Azure's WebRTC infrastructure.

### Launch configuration

The msrdc command line references an `.rdp` file and a `.json` settings file in:
`C:\Users\davris\AppData\Local\Packages\MicrosoftCorporationII.Windows365_...\LocalCache\LaunchFiles\`

**RDP file** key fields:
```
full address:s:rdgateway-r1.wvd.microsoft.com     (Azure gateway, not DevBox IP)
remotedesktopname:s:Microsoft Dev Box AMD 16vCPU/64GB/2048GB
remoteapplicationmode:i:0
```

**JSON settings file** key fields:
```json
{
  "WorkspaceDisplayName": "davris-10",
  "LaunchPartnerId": "Windows365NativeClient",
  "PeerActivityId": "cpc=f45ef37f-...;session=06240285-...",
  "TaskbarAppId": "MicrosoftCorporationII.Windows365_...:f45ef37f-..."
}
```

Neither file contains the DevBox's actual hostname (`CPC-davri-XXS9M`).

## Consolidated Root Cause

There are **three** broken links in the detection chain for Cloud DevBox:

1. **Peer discovery fails** — `RdpConnectionDetector` scans TCP port 3389, but DevBox
   uses WebRTC through Azure. No direct TCP connection exists.

2. **Window scanning is incomplete** — `RdpWindowLocator` only looks for `mstsc.exe`
   processes. DevBox connections use `msrdc.exe`. However, `msrdc.exe` uses the
   **same window class** (`TscShellContainerClass`), and the VirtualDesktop API works.

3. **Hostname matching fails** — Even if we found the msrdc window, its title is
   `davris-10` (the DevBox friendly name), not `CPC-davri-XXS9M` (the actual
   hostname). The `" - "` separator used by mstsc for title parsing is absent.

## Available Signals for a Fix

### Server side (DevBox)

| Signal | Value | How to access |
|---|---|---|
| Client machine name | `DAVRIS-10` | `%CLIENTNAME%` env var |
| Client machine name | `DAVRIS-10` | `HKCU:\Volatile Environment\<SessionId>` registry |
| Is a DevBox | `True` | `%IsDevBox%` env var |
| Session type | `rdp-sxs` | `%SESSIONNAME%` env var (prefix `rdp-sxs`) |

### Client side

| Signal | Value | How to access |
|---|---|---|
| DevBox friendly name | `davris-10` | msrdc `TscShellContainerClass` window title |
| DevBox display name | `davris-10` | JSON `WorkspaceDisplayName` field |
| Window desktop index | Desktop 4 | `IVirtualDesktopManager.GetWindowDesktopId` — works! |
| Process identity | `msrdc.exe` | `Process.GetProcessesByName("msrdc")` |
| Window class | `TscShellContainerClass` | Same as mstsc — shared code path possible |

### The name mapping problem

The fundamental challenge is mapping the **DevBox friendly name** (visible on the
client as the window title, e.g. `davris-10`) to the **DevBox hostname** (reported
by `Environment.MachineName` on the DevBox, e.g. `CPC-davri-XXS9M`). These are
completely different strings with no algorithmic relationship.

Possible solutions:

1. **Network mesh handshake** — When the DevBox connects to the RemoteFlattener mesh,
   it can report both its hostname (`CPC-davri-XXS9M`) and its client's name
   (`DAVRIS-10` from `%CLIENTNAME%`). The client can then cross-reference: "I'm
   `DAVRIS-10`, and a peer says its CLIENTNAME is `DAVRIS-10` — that peer must be
   connected to me via RDP." This doesn't require window title matching at all.

2. **DevBox self-identification** — The DevBox can include its friendly name in mesh
   messages (read from the Windows App configuration or Azure metadata). The client
   matches the friendly name to the msrdc window title.

3. **Hybrid approach** — Use the mesh for hostname discovery (solution 1) and then
   fall back to process scanning for desktop placement. The client already knows which
   desktop the msrdc window is on via the VirtualDesktop API.

### Suggested approach: CLIENTNAME-based matching

The most robust approach combines signals from both sides:

1. **DevBox (server) side**: Read `%CLIENTNAME%` and report it in the mesh `HELLO`
   message (new field, e.g. `rdpClientName`).
2. **Client side**: When processing a peer's `HELLO` that includes `rdpClientName`
   matching the local `Environment.MachineName`, mark that peer as "connected to me
   via RDP" — equivalent to what port-3389 scanning does for mstsc.
3. **Client side**: Extend `RdpWindowLocator` to also scan `msrdc.exe` windows.
   Since the title format differs (no `" - "` separator), match by checking if the
   DevBox hostname appears anywhere in the title, or use the `CLIENTNAME`-based
   mesh pairing to skip window-title matching entirely.
4. **Client side**: Use `VirtualDesktopProvider.GetDesktopIndexForHwnd` on the msrdc
   `TscShellContainerClass` window to determine the desktop index — this already works.

## Server → Client Network Connectivity

For the mesh to work, the DevBox needs a TCP connection to the client (or vice versa)
on port 8765. Since the client can't discover or reach the DevBox (no DNS, no direct
IP), the connection must be **initiated by the DevBox → client**.

### Client reachability

| Property | Value |
|---|---|
| Client listening on port 8765 | ✅ Yes (`0.0.0.0:8765`) |
| Client Wi-Fi IP | `192.168.4.103` (local LAN only) |
| Client VPN IP | `100.64.224.18` (`MSFT-AzVPN-Manual` interface) |
| `DAVRIS-10` resolves (from client) | ✅ → `192.168.4.103`, `100.64.224.18` |

### Network topology

Both the DevBox and client route through the **same Azure VPN** (`MSFT-AzVPN-Manual`):

- Client's VPN IP: `100.64.224.18` (CGNAT range)
- DevBox subnet: `10.0.x.x`
- Existing mesh peers: `10.91.x.x` → connect to client at `100.64.224.18:8765` ✅
- VPN route on client: `10.0.0.0/8` → `MSFT-AzVPN-Manual` (same VPN carries all)

The existing mesh peers (`DAVRIS-0` at `10.91.110.73`, `DAVRIS-4` at `10.91.111.26`)
already connect to the client's VPN IP `100.64.224.18` on port 8765 successfully.
This suggests the DevBox (`10.0.x.x`) could do the same, since all `10.x.x.x` traffic
routes through the same VPN.

### What needs testing from the DevBox side

These tests must be run from the DevBox to confirm connectivity:

1. `nslookup DAVRIS-10` — Can the DevBox resolve the client hostname?
2. `Test-NetConnection DAVRIS-10 -Port 8765` — Can the DevBox reach the client?
3. `Test-NetConnection 100.64.224.18 -Port 8765` — Can it reach the VPN IP directly?

### Connection initiation strategy

If the DevBox can resolve `DAVRIS-10` (from `%CLIENTNAME%`) and reach it on port 8765:

1. DevBox reads `%CLIENTNAME%` → gets `DAVRIS-10`
2. DevBox resolves `DAVRIS-10` → gets client IP
3. DevBox connects to client IP on port 8765 (RemoteFlattener mesh)
4. Normal mesh handshake proceeds — DevBox identifies itself by hostname
   (`CPC-davri-XXS9M`), client now knows the DevBox's real hostname
5. Client matches the DevBox to an msrdc `TscShellContainerClass` window via
   the friendly name reported by the DevBox (or via the `CLIENTNAME` pairing)
6. Client reads the virtual desktop index from the msrdc window

If DNS resolution fails, the DevBox could fall back to broadcasting its
`%CLIENTNAME%` over existing mesh connections, and rely on peers to relay
the connection request to the named client.
