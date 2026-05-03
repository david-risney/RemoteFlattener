# RemoteFlattener

RemoteFlattener keeps your Windows virtual desktops in sync across machines connected via Remote Desktop (RDP). When you switch virtual desktops on the RDP host, all connected client machines switch in parallel — so your physical monitors and your remote session always show the same logical desktop.

## How it works

Run RemoteFlattener on every machine in the setup (the RDP server you connect *to*, and the local machine you connect *from*). The instances find each other over TCP port **8765** using a shared password.

- **RDP server role** – detected automatically when the app is running inside an RDP session. Intercepts **Win+Tab**, **Win+Left**, and **Win+Right** globally.
  - **Win+Tab** — opens an overlay window showing the full RDP topology (all connected machines, their roles, and current virtual desktop index).
  - **Ctrl+Win+Left / Ctrl+Win+Right** — switch the virtual desktop on the server *and* broadcast the command to all connected clients so they switch simultaneously.
- **RDP client role** – receives switch commands and executes **Ctrl+Win+Left/Right** locally to mirror the desktop change.

Each machine broadcasts its desktop state (current desktop index, total desktop count, RDP role) to all peers every 5 seconds so the overlay stays current.

## Prerequisites

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (build) or the .NET 8 Desktop Runtime (run only)
- TCP port **8765** open between all participating machines

## Build

```powershell
dotnet build RemoteFlattener.sln
```

For a self-contained release build:

```powershell
dotnet publish RemoteFlattener/RemoteFlattener.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o publish/
```

This bundles the .NET 8 runtime alongside the app, so the target machine does **not** need .NET installed. When the command finishes, copy the entire `publish/` folder to the other machine and run `RemoteFlattener.exe` from it. No installer or additional files are required — everything needed is inside that folder.

To reduce the folder size you can add `-p:PublishSingleFile=true`, which merges everything into a single `RemoteFlattener.exe`:

```powershell
dotnet publish RemoteFlattener/RemoteFlattener.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o publish/
```

Copy just `RemoteFlattener.exe` to the other machine and run it directly.

## Run

```powershell
dotnet run --project RemoteFlattener/RemoteFlattener.csproj
```

Or launch the compiled executable directly:

```powershell
.\RemoteFlattener\bin\Debug\net8.0-windows\RemoteFlattener.exe
```

### First-time setup

1. Open RemoteFlattener on every machine in the session.
2. Click **Generate Password** (or type your own) and use the **same password** on all machines.
3. In the **Machines** box enter the hostnames or IP addresses of the other machines (one per line), **or** click **Detect** to auto-populate the list from active RDP connections.
   - **Detect** inspects TCP port 3389 for established connections and performs a reverse-DNS lookup on each remote address. It merges any discovered hostnames with whatever you have already typed.
   - Run it on both machines — the RDP server sees the client's address, and the client sees the server's address.
4. Click **Start**. The status label changes to *Running – RDP Server* or *Running – RDP Client* depending on whether the app detects an active RDP session.

## Hotkeys (RDP server only)

| Hotkey | Action |
|--------|--------|
| Win+Tab | Toggle the machine/desktop overview overlay |
| Ctrl+Win+Left | Switch to the previous virtual desktop on all machines |
| Ctrl+Win+Right | Switch to the next virtual desktop on all machines |

## Authentication

Peers authenticate with an **HMAC-SHA256** challenge derived from the machine name and the shared password. Connections that fail authentication are dropped immediately. The password is never transmitted in plaintext.

## License

See [LICENSE](LICENSE).
