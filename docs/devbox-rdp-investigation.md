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
   (first DNS label, uppercased) via `MachineName.From(...).Canonical`.

2. **Window-to-desktop mapping** (`RdpWindowLocator`): On the **client** machine,
   enumerates all visible `mstsc.exe` windows (matched by PID from
   `Process.GetProcessesByName("mstsc")`), parses the window title
   (`"HOSTNAME - Remote Desktop Connection"`), matches the hostname portion against
   known peer names, and determines which virtual desktop each window lives on via
   `VirtualDesktopProvider.GetDesktopIndexForHwnd`.

The result is the `RdpHostedServers` dictionary (server name → desktop index) which
is broadcast over the mesh so every node can build the correct tree.

## Investigation: DevBox Server Side

Performed on: 2026-05-20 (corrected), inside a Cloud DevBox (`DEVBOX-HOSTNAME`).
Connected from client: `CLIENT-PC`. DevBox friendly name: `my-devbox`.

> **Note:** The previous investigation (2026-05-10) had a flaw: the client hostname
> happened to match the DevBox friendly name by coincidence.
> This re-investigation uses a different client (`CLIENT-PC`) to properly distinguish
> the signals.

### What works

| Check | Result | Notes |
|---|---|---|
| `GetSystemMetrics(SM_REMOTESESSION)` | **1** (REMOTE) | `RdpRoleDetector.IsRemoteSession()` correctly identifies this as a remote session |
| `CLIENTNAME` env var | **`CLIENT-PC`** | Identifies the **client machine name** (NOT the DevBox friendly name) |
| `IsDevBox` env var | **`True`** | Reliably identifies this machine as a Cloud DevBox |
| `HKCU:\Volatile Environment\2` registry | `CLIENTNAME=CLIENT-PC` | Same info available in registry, keyed by session ID |
| Virtual desktop registry | Present and populated | DevBox has its own virtual desktops (2 desktops, current = `<guid>`) |
| RemoteFlattener listening | Port **8765** open | `0.0.0.0:8765` in LISTEN state |
| Port 3389 listening | Yes | `0.0.0.0:3389` and `[::]:3389` are in LISTEN state |
| WTS Client Name (API) | **`CLIENT-PC`** | `WTSQuerySessionInformation(WTSClientName)` returns client hostname |
| WTS Client Directory | `...\Windows365_<version>_x64__...\msrdc\rdclientax.dll` | Confirms client uses Windows 365 app |
| WTS Client Display | **7680×2160, 32-bit** | Full client display canvas resolution |
| DevBox friendly name | **`my-devbox`** | Available from DevBox Agent config (see below) |
| Azure IMDS | Available | VM name: `CPC_<guid>`, region: westus2 |

### What doesn't work

| Check | Result | Notes |
|---|---|---|
| Established TCP on port 3389 | **None** | Zero established connections on port 3389 — root cause of peer discovery failure |
| Azure MSI token | **Identity not found** | Cannot query ARM API from DevBox (no managed identity) |
| Client address (WTS API) | **All zeros** | `WTSClientAddress` returns `AddressFamily=0` with empty bytes (WebRTC transport) |

### DevBox session details

```
Session name:  rdp-sxs<id>#0
Session ID:    2
Session state: Active
Transport:     WebRTC (rdpclipcdv + rdpvchost processes, rdr_sxs infra)
WTS WinStationName: <username>
WTS DomainName: rdp-sxs<id>#0
```

The session type is `rdp-sxs` (not `rdp-tcp`), indicating the connection is tunneled
through the Azure RD Infrastructure rather than arriving as a direct TCP connection.

### DevBox-specific processes

| Process | Path | Role |
|---|---|---|
| `Microsoft.DevCenter.DevBoxUserTaskExecutor` | `C:\Program Files\Microsoft Dev Box Agent\...` | DevBox task execution |
| `rdpclipcdv` | `C:\Program Files\Microsoft RDInfra\rdr_sxs\<version>\` | Clipboard redirection |
| `rdpvchost` | `C:\Program Files\Microsoft RDInfra\rdr_sxs\<version>\` | Virtual channel host |

### DevBox-specific environment variables

| Variable | Value |
|---|---|
| `IsDevBox` | `True` |
| `CLIENTNAME` | `CLIENT-PC` (the actual client machine name) |
| `SESSIONNAME` | `rdp-sxs<id>#0` |
| `COMPUTERNAME` | `DEVBOX-HOSTNAME` |

### DevBox friendly name source

The DevBox friendly name (`my-devbox`) is available from the DevBox Agent configuration:

**File:** `C:\Program Files\Microsoft Dev Box Agent\<version>\<guid>\appsettings.Production.json`

**Field:** `DevBoxAgent.metadata.devBoxDataplaneId`

**Value:** `<tenantId>:<devCenterName>:<projectName>:<poolId>:my-devbox`

**Format:** `<tenantId>:<devCenterName>:<projectName>:<poolId>:<friendlyName>`

The friendly name is the **last colon-separated segment** of `devBoxDataplaneId`.

Other useful fields in the same metadata block:
- `cloudPcId`: `<guid>` 
- `cloudPcDeviceId`: `<guid>` (matches AAD DeviceId)
- `poolName`: `<pool-name>`
- `projectName`: `<project-name>`

### Network connectivity (DevBox → Client)

| Check | Result | Notes |
|---|---|---|
| DNS resolve `CLIENTNAME` | ✅ `<client-ipv4>` (IPv4), `<client-ipv6>` (IPv6) | Corp DNS resolves client hostname |
| TCP to `<client-ipv4>:8765` | ✅ **SUCCESS** | DevBox can reach client's RemoteFlattener mesh |
| TCP to `CLIENT-PC:8765` (IPv4-forced) | ✅ **SUCCESS** | Works when forcing IPv4 |
| TCP to `CLIENT-PC:8765` (default) | ❌ **TIMEOUT** | Fails because OS tries IPv6 first |
| TCP to IPv6 address:8765 | ❌ **TIMEOUT** | IPv6 path not routable |

**Route:** DevBox (`<devbox-ip>` on Ethernet) → `MSFTVPN-Manual` VPN → client (`<client-ipv4>`)

The DevBox **can initiate a mesh connection to the client**, but must force IPv4
(e.g., resolve hostname and use the IPv4 address directly, or use
`AddressFamily.InterNetwork`).

### Network connections (non-loopback established)

No connections on port 3389. All established connections go to Azure infrastructure
IPs (168.63.129.16, various 10.x.x.x, GitHub IPs, etc.) — none to the client machine
directly.

## Root Cause

`RdpConnectionDetector.ExtractRdpAddresses` scans TCP connections for port 3389.
Cloud DevBox uses **WebRTC** transport via the `rdp-sxs` session infrastructure — the
RDP protocol is tunneled through Azure's Remote Desktop Infrastructure, so **no direct
TCP connection on port 3389 exists** between the client and DevBox.

This means `GetRdpPeers()` returns an empty list, and the DevBox is never discovered
as an RDP peer by the client (or vice versa).

## Available Signals for a Fix

On the **server (DevBox) side**, the following are available:

1. **`CLIENTNAME` environment variable** — set to the client's short machine name.
   This is the same format `MachineName.From(...).Canonical` produces.
2. **`IsDevBox` environment variable** — set to `True`, reliably identifies Cloud DevBox.
3. **DevBox friendly name** — extractable from `appsettings.Production.json` in the
   DevBox Agent directory. The friendly name is the last `:` segment of
   `DevBoxAgent.metadata.devBoxDataplaneId`.
4. **Registry** at `HKCU:\Volatile Environment\<SessionId>` — contains `CLIENTNAME`
   keyed by the active session ID.
5. **WTS API** — `WTSQuerySessionInformation(WTSClientName)` returns client hostname.
6. **Network reach** — DevBox can connect to client on port 8765 via corp VPN (IPv4).
   DNS resolves `CLIENTNAME` correctly. Must force IPv4 (IPv6 unreachable).

## Investigation: Client Side

Performed on: 2026-05-10 (original), client machine was `CLIENT-PC-OLD`.
Corrected 2026-05-20: client is now `CLIENT-PC` connecting to DevBox `DEVBOX-HOSTNAME`
(friendly name `my-devbox`). The window-level findings below remain valid as the
Windows 365 app behavior is the same.

### Client identity

| Check | Result |
|---|---|
| `Environment.MachineName` | `CLIENT-PC` |
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

`msrdc.exe` creates windows with the **same window class** as `mstsc.exe` but a
**different title format**:

| Window Class | Title | Visible | Notes |
|---|---|---|---|
| `TscShellContainerClass` | `my-devbox` | Yes | **Main RDP viewport** — same class as mstsc! |
| `#32770` | `Remote Desktop` | Yes | Dialog window |
| `BBarWindowClass` | `BBar` | Yes | Connection bar |

**Key differences from mstsc:**

| Aspect | mstsc | msrdc (DevBox) |
|---|---|---|
| Window class | `TscShellContainerClass` | `TscShellContainerClass` (same!) |
| Title format | `HOSTNAME - Remote Desktop Connection` | `my-devbox` (friendly name only) |
| Title content | Server's actual hostname | DevBox display name from Windows App |
| Title separator | ` - ` present | No separator |

The title `my-devbox` comes from the `WorkspaceDisplayName` field in the JSON launch
config, which is the user-assigned DevBox name — **NOT** the machine hostname
(`DEVBOX-HOSTNAME`).

### Virtual Desktop API

✅ **`GetWindowDesktopId` works on msrdc windows!**

| API Call | Result |
|---|---|
| `IsWindowOnCurrentVirtualDesktop` | `hr=0x00000000` — succeeds |
| `GetWindowDesktopId` | `hr=0x00000000, id=<guid>` |
| Desktop index | **Desktop 4** (out of 5) |

The `TscShellContainerClass` window is correctly tracked by the Virtual Desktop API.
`VirtualDesktopProvider.GetDesktopIndexForHwnd` will return the correct desktop index.

### Network connectivity

| Check | Result |
|---|---|
| TCP port 3389 connections | **None** — no direct TCP to DevBox |
| msrdc TCP connections | To Azure endpoints (various `*.*.x:443`) only |
| msrdc UDP | `<local-ip>:<port>` — WebRTC data channel |
| DNS resolve `DEVBOX-HOSTNAME` | **FAILS** — DevBox hostname not in DNS |
| RemoteFlattener mesh (port 8765) | Connected to other peers only, **NOT** to DevBox |

The DevBox is completely behind Azure's RD gateway. There is no direct IP path between
the client and DevBox — all traffic goes through Azure's WebRTC infrastructure.

### Launch configuration

The msrdc command line references an `.rdp` file and a `.json` settings file in:
`C:\Users\<username>\AppData\Local\Packages\MicrosoftCorporationII.Windows365_...\LocalCache\LaunchFiles\`

**RDP file** key fields:
```
full address:s:rdgateway-r1.wvd.microsoft.com     (Azure gateway, not DevBox IP)
remotedesktopname:s:Microsoft Dev Box AMD 16vCPU/64GB/2048GB
remoteapplicationmode:i:0
```

**JSON settings file** key fields:
```json
{
  "WorkspaceDisplayName": "my-devbox",
  "LaunchPartnerId": "Windows365NativeClient",
  "PeerActivityId": "cpc=<guid>;session=<guid>",
  "TaskbarAppId": "MicrosoftCorporationII.Windows365_...:<guid>"
}
```

Neither file contains the DevBox's actual hostname (`DEVBOX-HOSTNAME`).

## Consolidated Root Cause

There are **three** broken links in the detection chain for Cloud DevBox:

1. **Peer discovery fails** — `RdpConnectionDetector` scans TCP port 3389, but DevBox
   uses WebRTC through Azure. No direct TCP connection exists.

2. **Window scanning is incomplete** — `RdpWindowLocator` only looks for `mstsc.exe`
   processes. DevBox connections use `msrdc.exe`. However, `msrdc.exe` uses the
   **same window class** (`TscShellContainerClass`), and the VirtualDesktop API works.

3. **Hostname matching fails** — Even if we found the msrdc window, its title is
   `my-devbox` (the DevBox friendly name), not `DEVBOX-HOSTNAME` (the actual
   hostname). The `" - "` separator used by mstsc for title parsing is absent.

## Available Signals for a Fix

### Server side (DevBox)

| Signal | Value | How to access |
|---|---|---|
| Client machine name | `CLIENT-PC` | `%CLIENTNAME%` env var |
| Client machine name | `CLIENT-PC` | `HKCU:\Volatile Environment\<SessionId>` registry |
| Is a DevBox | `True` | `%IsDevBox%` env var |
| Session type | `rdp-sxs` | `%SESSIONNAME%` env var (prefix `rdp-sxs`) |
| DevBox friendly name | `my-devbox` | DevBox Agent `appsettings.Production.json` → `devBoxDataplaneId` last segment |
| DevBox hostname | `DEVBOX-HOSTNAME` | `Environment.MachineName` |
| Can reach client | TCP to `CLIENTNAME:8765` (IPv4) | DNS resolves, VPN route works |

### Client side

| Signal | Value | How to access |
|---|---|---|
| DevBox friendly name | `my-devbox` | msrdc `TscShellContainerClass` window title |
| DevBox display name | `my-devbox` | JSON `WorkspaceDisplayName` field |
| Window desktop index | Desktop 4 | `IVirtualDesktopManager.GetWindowDesktopId` — works! |
| Process identity | `msrdc.exe` | `Process.GetProcessesByName("msrdc")` |
| Window class | `TscShellContainerClass` | Same as mstsc — shared code path possible |

### The name mapping problem

The fundamental challenge is mapping the **DevBox friendly name** (visible on the
client as the window title, e.g. `my-devbox`) to the **DevBox hostname** (reported
by `Environment.MachineName` on the DevBox, e.g. `DEVBOX-HOSTNAME`). These are
completely different strings with no algorithmic relationship.

**Resolution:** The DevBox CAN discover its own friendly name from:
`C:\Program Files\Microsoft Dev Box Agent\<version>\<guid>\appsettings.Production.json`
→ `DevBoxAgent.metadata.devBoxDataplaneId` → last colon-separated segment.

This means the DevBox can report its friendly name over the mesh, and the client
can match it directly to the `msrdc.exe` window title.

### Suggested approach: CLIENTNAME + friendly name matching

The most robust approach combines signals from both sides:

1. **DevBox (server) side**: Read `%CLIENTNAME%` and the friendly name from
   `appsettings.Production.json`. Report both in the mesh `HELLO` message
   (new fields: `rdpClientName` and `devBoxFriendlyName`).
2. **Client side**: When processing a peer's `HELLO` that includes `rdpClientName`
   matching the local `Environment.MachineName`, mark that peer as "connected to me
   via RDP" — equivalent to what port-3389 scanning does for mstsc.
3. **Client side**: Extend `RdpWindowLocator` to also scan `msrdc.exe` windows.
   Match by comparing the DevBox's reported `devBoxFriendlyName` to the msrdc window
   title (case-insensitive). This resolves the hostname↔friendly-name mismatch.
4. **Client side**: Use `VirtualDesktopProvider.GetDesktopIndexForHwnd` on the msrdc
   `TscShellContainerClass` window to determine the desktop index — this already works.

## Server → Client Network Connectivity

For the mesh to work, the DevBox needs a TCP connection to the client (or vice versa)
on port 8765. Since the client can't discover or reach the DevBox (no DNS, no direct
IP), the connection must be **initiated by the DevBox → client**.

### Client reachability (tested 2026-05-20)

| Property | Value |
|---|---|
| Client listening on port 8765 | ✅ Yes |
| Client IP (from DevBox DNS lookup) | `<client-ipv4>` (corp VPN) |
| Client IPv6 | `<client-ipv6>` (unreachable from DevBox) |
| DevBox IP | `<devbox-ip>` (Ethernet), `<devbox-vpn-ip>` (MSFTVPN-Manual) |

### Connectivity test results

| Test | Result |
|---|---|
| TCP to `<client-ipv4>:8765` (direct IPv4) | ✅ **SUCCESS** |
| TCP to `CLIENT-PC:8765` (IPv4-forced) | ✅ **SUCCESS** |
| TCP to `CLIENT-PC:8765` (default OS resolution) | ❌ TIMEOUT (tries IPv6 first) |
| TCP to IPv6 address:8765 | ❌ TIMEOUT |

### Network topology

- DevBox subnet: `10.0.x.0/x` (Azure vnet)
- DevBox VPN IP: `100.64.x.x` (MSFTVPN-Manual, CGNAT range)
- Client VPN IP: `10.x.x.x` (corp network)
- Route: DevBox → `MSFTVPN-Manual` VPN handles `10.0.0.0/8` → reaches client

The existing mesh peers on other VMs already connect to clients via similar VPN
routing. The DevBox uses the same mechanism.

### Connection initiation strategy

The DevBox can resolve `CLIENTNAME` via DNS and connect to the client:

1. DevBox reads `%CLIENTNAME%` → gets `CLIENT-PC`
2. DevBox resolves `CLIENT-PC` → gets `<client-ipv4>` (must use IPv4 / `AddressFamily.InterNetwork`)
3. DevBox connects to `<client-ipv4>:8765` (RemoteFlattener mesh)
4. Normal mesh handshake proceeds — DevBox identifies itself by hostname
   (`DEVBOX-HOSTNAME`) and also reports `rdpClientName=CLIENT-PC` and
   `devBoxFriendlyName=my-devbox`
5. Client receives HELLO, sees `rdpClientName` matches its own hostname
6. Client scans `msrdc.exe` windows, finds one with title `my-devbox` matching
   the peer's `devBoxFriendlyName`
7. Client reads the virtual desktop index from that window via `GetWindowDesktopId`

**IPv4 requirement:** The DevBox must force IPv4 when connecting to the client.
The default OS behavior tries IPv6 first (which is unreachable from the DevBox's
network). Use `AddressFamily.InterNetwork` or resolve and filter for IPv4 addresses.

If DNS resolution fails, the DevBox could fall back to broadcasting its
`%CLIENTNAME%` over existing mesh connections, and rely on peers to relay
the connection request to the named client.
