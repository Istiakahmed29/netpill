# NetPill

A single-purpose Windows readout: current download and upload speed, floating just above the taskbar,
plus a per-day data history you can look back through.
One borderless overlay window, one background sampling thread, no installer, no runtime, no network traffic.

```
↓ 12.4 MB/s        ↑ 1.2 MB/s
```

Click it for the history:

```
TODAY  1.69 GB      ↓ 1.53 GB   ↑ 166 MB        56% of your 30-day average
[7 days] [30 days] [12 months]
▁▂▄▁█▂▄▃   hover a bar for that day's exact bytes
YESTERDAY 2.95 GB | THIS MONTH 79.75 GB | LAST MONTH 96.88 GB
AVG/DAY   3.02 GB | ALL TIME   1.35 TB  | DAYS TRACKED  390
```

## Build

No SDK, no Visual Studio, no NuGet. The C# compiler ships inside Windows itself:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /win32manifest:app.manifest /out:NetPill.exe NetPill.cs
```

Or just double-click `build.bat`, or call it by full path from anywhere — it `cd`s to its own folder
first, so the relative source paths always resolve. Output is a single `NetPill.exe`, ~30 KB.

`NetPill.exe` holds a lock on its own file while it runs, so a plain rebuild fails with a sharing
violation. **`rebuild.bat`** does the whole cycle: stop the running instance, compile, start the new
one. Settings and history in `%APPDATA%\NetPill` survive it. If the build fails it leaves NetPill
stopped and pauses so you can read the errors.

Requirements: Windows 10 or Windows 11, x64. .NET Framework 4.8 is already on every such machine.
`/win32manifest:app.manifest` embeds the PerMonitorV2 DPI declaration; drop it and the app still runs and
still gets per-monitor DPI, because `NetPill.cs` also calls `SetProcessDpiAwarenessContext` at startup.

## Run

Double-click `NetPill.exe`. The pill appears above the taskbar in well under a second. No UAC prompt.

From a shell, one line each (`&&` is **cmd only** — Windows PowerShell 5.1 rejects it as a parse error):

```
cd /d "C:\path\to\NetPill" && build.bat && NetPill.exe
```
```powershell
& "C:\path\to\NetPill\build.bat"; & "C:\path\to\NetPill\NetPill.exe"
```

## Measured

Numbers from a real run on Windows 10 22H2 (19045), x64, 125 % DPI, over a 25 s idle window:

| Metric | Measured | Budget |
|---|---|---|
| Idle CPU | 0.125 % of one core | < 0.5 % |
| Working set | 27.4 MB, flat (+0.02 MB / 25 s) | < 30 MB |
| Handle count | 276 → 277 over 25 s | flat |
| Threads | 7 (10 during startup, then settles) | — |
| Second launch | exits silently | single instance |

Two-second usage:

* **Left-drag** to move it. The position persists. It is clamped to the monitor's *work area*,
  so it stops at the taskbar edge and can never be dragged under the taskbar.
* **Left-click** (a drag that never moved) opens the data-usage window; click again to close it.
* **Right-click** for Data usage / Units / Adapters / Click-through / Hide in fullscreen /
  Show today's total / Start with Windows / Opacity / Exit.
* **Hover** for per-adapter speeds and today's running total.
* **Double-click the tray icon** to flip between the pill and the two numbers drawn into the 16x16 icon.

Settings live in `%APPDATA%\NetPill\settings.ini` and are written only when you change something.

## Data usage

The sampler already subtracts the previous `InOctets` / `OutOctets` reading every second to get a rate.
The history feature keeps that raw delta — which is an exact byte count — and adds it to a running total
for the local day.

* Stored in `%APPDATA%\NetPill\usage.tsv`, one plain-text line per day:
  `yyyy-MM-dd <tab> inBytes <tab> outBytes`. About 45 bytes a day, so ~16 KB a year.
  No database, no format version, nothing to migrate: it opens in Notepad or a spreadsheet.
* Rewritten whole and swapped in atomically (`.tmp` then `File.Replace`) every 60 s, at midnight,
  and on exit. Worst case on a hard power cut is the last 60 seconds.
* The day boundary is local midnight.
* Counted across **all** qualifying adapters, regardless of which one you picked for the speed readout —
  choosing a display adapter shouldn't change your data bill.
* Raw deltas are accumulated even when the sample is discarded for the *rate* (a 3 s stall, a resume
  from sleep). A byte the counters saw was really transferred; only the rate readout needs to be picky.

The window opens on top of the pill, drags by its title bar, closes on Esc or the X, and refreshes once
a second while open. **Open folder** in the footer reveals `usage.tsv`.

## How it measures

`GetIfTable2` / `FreeMibTable` from `iphlpapi.dll`, reading `MIB_IF_ROW2.InOctets` and `OutOctets`
(64-bit counters, no admin, no driver, no packet capture). One `GetIfTable2` call per second.

* Only real, active links: `OperStatus == Up`, `HardwareInterface`, not `FilterInterface`,
  `Type` 6 (Ethernet) or 71 (IEEE 802.11), and no virtual/tunnel keyword in the description.
* Rate = delta bytes / real elapsed `Stopwatch` time, never the nominal 1000 ms.
* A sample is discarded (previous value kept) if a counter went backwards, if an adapter just appeared,
  or if more than 3 s elapsed (sleep / resume / hibernation) — so no absurd spikes.
* EMA with alpha 0.35, so digits stay readable but still settle in about 2 s.
* VPN traffic is counted once, at the physical adapter; tunnel adapters are filtered out.
* The same deltas feed the daily history, so it costs no extra syscalls — only a `lock` and two
  additions per adapter per second.

## Known limitations

| Limitation | Why |
|---|---|
| Not embedded *inside* the taskbar | Windows has no public taskbar plugin API. DeskBands and taskbar toolbars are gone in Windows 11; injecting into `explorer.exe` is unsupported and crashes it. A layered overlay positioned against `Shell_TrayWnd` is the only approach that works on both Win10 and Win11. |
| Pill cannot enter the taskbar | Position is clamped to the monitor work area (`rcWork`), not the raw monitor rect, and the clamp runs live during a drag. A saved position from a taller display is healed on load instead of pulling the pill back under the taskbar. |
| Taskbar moves can take up to 5 s to follow | Deliberate: one `GetWindowRect` every 5 s is cheaper than a hook. `DisplaySettingsChanged` / `UserPreferenceChanged` fire immediately for resolution, DPI and monitor changes. |
| Counts all traffic on the adapter | Octets are read at the interface level, so non-TCP traffic and broadcast are included. There is no per-process or per-protocol breakdown — by design. |
| Adapter filtering is heuristic | Virtual/VPN adapters are excluded by interface type plus a description keyword list (`VirtualBox`, `VMware`, `Hyper-V`, `TAP`, `WAN Miniport`, `Loopback`, `Bluetooth`, `VPN`, `Tunnel`, `Wintun`, `Npcap`, `Pseudo`). An adapter renamed to something exotic could slip through; pick the exact adapter in **Adapters** to override. |
| Windows' own numbers can differ by a few percent | Windows reports a different sampling window. NetPill is a 1 s delta with EMA smoothing, so on a sustained transfer it tracks Task Manager within ~3 %, but it is not byte-identical. |
| Tray-number mode is barely legible | 16x16 px holds about four characters per row. It exists because it was asked for; the pill is the real UI. |
| Fullscreen hiding is geometric, and debounced | A window counts as fullscreen when its rect covers the whole monitor. The desktop (`Progman`), `WorkerW`, both taskbar classes and the touch-keyboard host (`Windows.UI.Core.CoreWindow` titled *Microsoft Text Input Application*) are excluded, because they are monitor-sized but are not apps. Hiding needs three consecutive positive one-second checks, so alt-tab and transient shell windows cannot make the pill blink. Turn it off entirely with **Hide in fullscreen**. |
| x64 only | The build targets `Framework64`; there is no 32-bit binary. |
| One extra helper thread | A WinForms app carries ~7 threads: UI, sampler, finalizer, GC, SystemEvents broadcast, plus CLR housekeeping. NetPill itself adds exactly one (the sampler); the rest are the CLR and WinForms floor. |
| The history only counts while NetPill runs | Octets are read live; there is nothing to backfill from. Days the app was off appear as a gap in the chart, not as zero. Turn on **Start with Windows** and the gap is a few seconds a boot. |
| No per-app breakdown | Windows keeps one in the SRUM database (`SRUDB.dat`), but reading it needs admin rights and an ESE parser. That would cost the "no installer, no UAC" property, so the history is total-per-day only. |
| History starts the day this build first ran | No retroactive data. The footer says exactly which day that was. |
| A few bytes per adapter appear/disappear | When an adapter is unplugged and replugged, its counters rebaseline; traffic during that one-second gap is not counted. |

---

## Repository contents

| File | What it is |
|---|---|
| `NetPill.cs` | The whole application: one file, no other source. |
| `app.manifest` | Per-monitor DPI v2 plus `asInvoker`, so there is no UAC prompt. |
| `build.bat` | One-line build with the C# compiler already inside Windows. |
| `rebuild.bat` | Stops the running pill, rebuilds, starts it again. |

No binary is committed on purpose - `NetPill.exe` is a build artifact. Run `build.bat` once and
it is yours.

## License

MIT. See [LICENSE](LICENSE).
