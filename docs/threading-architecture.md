# Threading Architecture

## Overview

RemoteFlattener is a WPF desktop app that uses four categories of threads.
Every UI mutation happens on the WPF Dispatcher thread; background work is
marshalled back via `Dispatcher.Invoke` / `InvokeAsync` / `BeginInvoke`.

---

## Thread Categories

### 1. UI Thread (WPF Dispatcher)

The main STA thread. Owns all WPF controls, the `ObservableCollection<MachineInfo>`,
and `BitmapImage` encoding (a `DispatcherObject`).

**Responsibilities:**
- Button click handlers, settings load/save
- `UpsertMachineInfo`, `GetOrAdd`, `RefreshStatusLabel`
- Building and encoding wallpaper thumbnails (`EncodeWallpaperThumbnail`)
- Opening/closing the `TreeWindow` overlay
- Tray icon interactions

### 2. Network Threads (Task.Run, managed by NetworkManager)

`NetworkManager.Start()` spawns several `Task.Run` fire-and-forget tasks:

| Task | Count | Lifetime |
|------|-------|----------|
| `ListenLoopAsync` | 1 | Until `Stop()` or cancellation |
| `HandleConnectionAsync` | 1 per active peer | Until peer disconnects |
| `OutgoingLoopAsync` | 1 per configured peer | Retries every 5 s until `Stop()` |

**Thread-safety mechanisms inside NetworkManager:**
- `ConcurrentDictionary<string, PeerConnection>` — peer registry
- `SemaphoreSlim` per peer (`WriteLock`) — serializes JSON line writes
- `lock (_seenLock)` — guards the message-ID dedup `HashSet`
- `CancellationTokenSource` — cooperative shutdown

**Events fired on background threads:**
- `MessageReceived` — from `HandleConnectionAsync` read loop
- `PeerConnected` — from `HandleConnectionAsync` after auth
- `PeerDisconnected` — from `HandleConnectionAsync` finally block, or `Stop()`

### 3. Timer Thread (System.Threading.Timer, thread pool)

`_stateTimer` fires `BroadcastOurState()` on a thread-pool thread.

- **With VirtualDesktop API:** fires once after 1 s (initial state broadcast), then
  only on `DesktopChanged` events.
- **Without VirtualDesktop API:** fires every 5 s (polling fallback).

`BroadcastOurState()` immediately posts to the Dispatcher via `InvokeAsync` because
`BitmapImage` encoding requires the UI thread.

### 3. Keyboard Hook Thread

`HotkeyManager` installs a `WH_KEYBOARD_LL` hook from the UI thread. Callbacks
(`HookCallback`) run on whichever thread pumps the message loop (typically the UI
thread, but `ScheduleEdgeCheck` continuations run on the thread pool).

**Events fired:**
- `WinTabPressed` — raised synchronously in `HookCallback`
- `SwitchDesktopLeft` / `SwitchDesktopRight` — raised from `Task.Delay(300).ContinueWith`
  (thread pool) after the edge-check timer

---

## Thread Boundary Crossings

| From | To | Mechanism | Example |
|------|----|-----------|---------|
| Network → UI | `Dispatcher.Invoke()` | `OnMessageReceived`, `OnPeerConnected`, `OnPeerDisconnected` |
| Timer → UI | `Dispatcher.InvokeAsync()` | `BroadcastOurState` (thumbnail encoding) |
| Hotkey → UI | `Dispatcher.BeginInvoke()` | `OnWinTabPressed` (overlay toggle) |
| Logger → UI | `Dispatcher.InvokeAsync()` | `OnLogWritten` (log box append) |
| Hotkey → Network | Direct call | `OnSendSwitchLeft/Right` → `BroadcastAsync` (safe: NetworkManager is thread-safe) |
| VirtualDesktop COM → Timer/UI | `DesktopChanged` event | `OnDesktopChangedEvent` → `BroadcastOurState` |

---

## Current Issues & Improvement Opportunities

### Issue 1: `Dispatcher.Invoke` (synchronous) blocks network threads

`OnMessageReceived`, `OnPeerConnected`, and `OnPeerDisconnected` use synchronous
`Dispatcher.Invoke()`. This blocks the network read loop until the UI thread
processes the callback. If the UI thread is busy (e.g., encoding thumbnails), this
stalls message processing for all peers sharing that handler.

**Recommendation:** Change to `Dispatcher.InvokeAsync()` (already used by
`BroadcastOurState` and `OnLogWritten`). Network threads should never block on the
UI thread.

### Issue 2: Fire-and-forget `BroadcastAsync` hides exceptions

Several places call `_ = _networkManager.BroadcastAsync(msg)` or
`_ = _networkManager?.SendToPeerAsync(...)` without awaiting. Exceptions are
silently swallowed.

**Recommendation:** Add a top-level exception handler or use a helper like
`FireAndForget(task)` that logs unobserved exceptions via `AppLogger`.

### Issue 3: `OnSendSwitchLeft/Right` calls `BroadcastAsync` without marshalling

These hotkey handlers call `BroadcastAsync` directly from whatever thread invokes
the event. While `NetworkManager` is thread-safe, the `_networkManager` field itself
could be set to `null` by `StopAll()` on the UI thread between the null-check
(`_networkManager?.`) and the actual call. The `?.` operator prevents a
`NullReferenceException`, but the discarded task from a disposed manager could cause
subtle issues.

**Recommendation:** Capture `_networkManager` in a local variable before use:
```csharp
var nm = _networkManager;
if (nm == null) return;
nm.BroadcastAsync(new NetworkMessage { ... });
```

### Issue 4: `_isRunning` and `_isRdpServer` lack synchronization

These `bool` fields are written on the UI thread (`StartNetwork`, `StopAll`) and
read from network callback threads (`OnMessageReceived`). While torn reads of
`bool` are unlikely on x86, this is technically a data race.

**Recommendation:** Either always read these on the UI thread (inside `Dispatcher`
callbacks — already the case for `_isRdpServer` in `OnMessageReceived` since it's
inside `Dispatcher.Invoke`) or mark them `volatile`.

### Issue 5: `_stateTimer` callback can race with `StopAll`

The timer callback checks `_networkManager == null` but `StopAll()` sets
`_stateTimer = null` and `_networkManager = null` without synchronization against
an in-flight timer callback.

**Recommendation:** The `Dispatcher.InvokeAsync` inside `BroadcastOurState` already
rechecks `_networkManager == null` on the UI thread, which is the actual guard.
This is safe in practice but could be clearer with a comment.

### Issue 6: `VirtualDesktopProvider.DesktopChanged` fires on a COM background thread

The `OnCurrentChanged` handler raises `DesktopChanged` on whatever thread the COM
event arrives on. `OnDesktopChangedEvent` calls `BroadcastOurState`, which correctly
uses `Dispatcher.InvokeAsync` — so this is safe, but the threading isn't obvious.

**Recommendation:** Document in `VirtualDesktopProvider` that `DesktopChanged`
fires on a background thread and callers must marshal to the UI thread.

### Issue 7: `ShowPeersStatus` timer races with Dispatcher

`_peersStatusTimer` is a `System.Threading.Timer` that calls `Dispatcher.Invoke` to
hide the status text. If `ShowPeersStatus` is called rapidly, the old timer is
disposed but its callback may already be queued on the thread pool, leading to a
potential `ObjectDisposedException`.

**Recommendation:** Use `DispatcherTimer` instead (fires on the UI thread natively)
or suppress the exception in the callback.

---

## Summary

The threading model is straightforward and mostly correct:
- All UI mutations go through the Dispatcher ✓
- NetworkManager is internally thread-safe ✓
- Logger is thread-safe with lock ✓

The main improvements are:
1. **Replace `Dispatcher.Invoke` with `InvokeAsync`** in network callbacks (avoid blocking network threads)
2. **Log fire-and-forget exceptions** instead of discarding them
3. **Capture `_networkManager` in locals** before cross-thread use
4. **Minor documentation** of thread-safety contracts on events
