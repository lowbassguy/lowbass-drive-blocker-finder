# lowbass' Drive Blocker Finder

A Windows desktop utility for the moment when Windows refuses to safely eject your USB drive because *something* is using it — but doesn't tell you what.

`lowbass' Drive Blocker Finder` enumerates the processes holding a drive open, lets you force-kill them in one click, and then performs the canonical three-step Windows eject so the drive comes off cleanly.

---

## Features

- **Drive picker** with type (Removable / Fixed / Network / CDRom), volume label, and capacity
- **Two-pronged blocker detection** that merges and de-duplicates results by PID:
  - Inspects each running process's main module and loaded DLLs to see if any reside on the target drive
  - Queries the Windows **Restart Manager API** to find every process registered against the drive's volume root
- **Force-kill blockers** from the UI — pick a row, click *Kill*, and the offending process is gone
- **Safe eject sequence** using `FSCTL_LOCK_VOLUME` → `FSCTL_DISMOUNT_VOLUME` → `IOCTL_STORAGE_EJECT_MEDIA`
- **Live structured log panel** with optional emoji prefixes and a verbose toggle
- **Per-launch log files** written to `%TEMP%\LowbassDriveBlockerFinder\log-<timestamp>.txt` for after-the-fact debugging
- **DPI-aware UI** (`PerMonitorV2`) so the window stays crisp on 4K displays
- **Graceful shutdown** — in-flight scans cancel, the log file flushes, no orphaned handles

---

## Requirements

- **Windows 10 or later** (the eject ioctls and Restart Manager API are Win32-only)
- **.NET 8 SDK** to build from source (the published binary requires only the .NET 8 desktop runtime)
- **Optional: Administrator rights.** The app ships with `asInvoker` in its manifest and runs without UAC. Right-click *Run as administrator* to get visibility into processes you don't own — useful when a system service is the blocker.

---

## Build & Run

From the project root:

```powershell
dotnet build
dotnet run --project lowbass-drive-blocker-finder.csproj
```

Or open `lowbass-drive-blocker-finder.sln` in **Visual Studio 2022** (17.5+) and press F5.

The built binary lands at:

```
bin\Debug\net8.0-windows\LowbassDriveBlockerFinder.exe
```

For a release build: `dotnet publish -c Release -r win-x64 --self-contained false`.

---

## How to use it

1. Plug in the drive you can't eject.
2. Pick it in the **Drive** dropdown at the top of the window.
3. Click **Scan**. The blocker grid populates with every process holding the drive, along with the reason (`MainModule on drive`, `DLL loaded from drive`, or `Restart Manager`).
4. For each blocker you want gone, click **Kill** on its row. (Save your work in those apps first — this is a hard terminate.)
5. Once the grid is empty, click **Eject**. The status line will tell you whether the lock / dismount / eject sequence succeeded.
6. If eject fails at the *lock* step, that's Windows telling you another handle showed up — click *Scan* again.

---

## Architecture

```
App.xaml(.cs)         WPF Application entry; wires global exception handlers + log flush on exit
MainWindow.xaml(.cs)  Single-window UI: action bar, blocker grid, log panel
Models.cs             DriveItem, ProcessUsage — plain data classes for binding
DriveScanner.cs       Drive enumeration + dual-mode process scanning
EjectService.cs       Lock → dismount → eject via Win32 ioctls
Logger.cs             Thread-safe singleton logger with file + UI sinks
app.manifest          UAC level + DPI awareness declaration
```

### The eject protocol

Windows expects three distinct steps before a removable drive is safe to pull:

1. **`FSCTL_LOCK_VOLUME`** — get exclusive access. If this fails, *something* still holds a handle. That's the signal to scan again.
2. **`FSCTL_DISMOUNT_VOLUME`** — tear down the filesystem mount so cached writes get flushed.
3. **`IOCTL_STORAGE_EJECT_MEDIA`** — tell the device itself to physically eject / power down.

Skipping any of these can corrupt data on the drive or leave the device in a half-mounted state until reboot.

### Scan strategy

The scanner is intentionally layered because no single approach finds everything:

- `Process.MainModule.FileName` catches apps running directly off the drive (think: portable apps, installers staged on USB).
- `Process.Modules` catches apps that loaded a DLL from the drive (think: a plugin host that loaded an extension from there).
- Restart Manager registers the volume root and asks Windows "who is using this?" — catches handle-only holders the first two approaches miss.

Known limitation: raw kernel file handles (the kind `handle.exe` shows) require `NtQuerySystemInformation` + `NtQueryObject`, which can hang on certain handle types and needs careful threading. Not implemented yet.

---

## Logs

- **Location:** `%TEMP%\LowbassDriveBlockerFinder\log-yyyyMMdd-HHmmss.txt`
- **Format:** `[timestamp] {emoji} [LEVEL] [Component] [Action] [Outcome] details`
- **Live mirror:** every line also lands in the log panel inside the app

The verbose toggle and emoji toggle live in the action bar. Emojis sit *before* the structured columns so a parser can strip them without breaking the column layout.

---

## Screenshots

> *Drop screenshots in `docs/screenshots/` and link them here.*

---

## License

TBD.

---

## Author

Joshua "lowbass" Sommerfeldt
