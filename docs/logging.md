# Logging

IdleVeil writes three kinds of log files into a `logs` folder next to its settings. The writer
runs on a background thread, so logging never blocks the UI thread or the render thread.

## Where the logs are

| Copy | Folder |
| --- | --- |
| Installed | `%APPDATA%\IdleVeil\logs\` |
| Portable (`IdleVeil.ini` next to the exe) | `<exe folder>\logs\` |
| Built from source with an empty `build\IdleVeil.ini` | `build\logs\` |
| Built from source without an ini | `%APPDATA%\IdleVeilDev\logs\` |

A build from source never writes into the installed copy's `%APPDATA%\IdleVeil`.

**Settings > General > Logs** has these controls:

- **Open logs folder**
- **Detailed logging**, which turns on Debug level (`debug=1` in the ini)
- **Keep logs for**: 3, 7, 14 or 30 days (`logdays=` in the ini)
- **Copy diagnostics**, which puts the version, system info and the last 200 log lines on the
  clipboard, ready for a bug report.

## Files

| File | Contents |
| --- | --- |
| `app-YYYY-MM-DD.log` | Events: start-up and system info, settings changes, veil shown/hidden, tray actions, monitors, updates, audio, errors |
| `perf-YYYY-MM-DD.log` | A performance sample every 10 s while a veil or preview runs, a summary when it closes, and one idle line every 5 min |
| `crash-YYYY-MM-DD-HHMMSS.log` | One per crash: exception chain with stacks, version, uptime, what was showing, screens, and the last 200 app log lines |
| `app-legacy-YYYY-MM-DD.log` | The single `IdleVeil.log` of older versions, moved here once (it ages out like the others) |

A file that grows past 20 MB within a day continues as `app-YYYY-MM-DD.1.log`, `.2.log`, and so
on.

## Retention

Cleanup runs at start-up and every 24 hours after that:

1. App and perf logs older than `logdays` (default 7) are deleted.
2. Crash reports are kept for 30 days.
3. If the folder is still over 200 MB, the oldest files are deleted first until it fits.

## App log lines

```
2026-11-15 03:02:51.459 WARN  [main] gpu: GPU overlay broke, rebuilding overlay
<timestamp, ms>         <lvl> [thread] category: message
```

Each line has these parts:

- **Level**: `DEBUG`, `INFO`, `WARN` or `ERROR`. At `debug=0` the log keeps Info and above, plus
  every perf line. At `debug=1` (or with `--debug`) it also keeps Debug lines, such as the idle
  state every 10 s.
- **Thread**:
  - `main` (UI thread)
  - `IdleVeil render` (GPU loop)
  - `IdleVeil audio` (capture)
  - `IdleVeil thumbnails`
  - `IdleVeil log` (the writer)
  - `tN` for unnamed threads
- **Category**:

  | Category | What it covers |
  | --- | --- |
  | `app` | Start, exit, pause and resume |
  | `system` | Start-up facts |
  | `veil` | Shown and hidden, with the reason and the scene picked |
  | `gpu` | Device, device lost, overlay rebuilt |
  | `audio` | Audio |
  | `music` | Visualizer picks and switches |
  | `display` | Turning screens off and back on (standby and detach), layout changes |
  | `settings` | Window opened and closed, `changed: key old→new` |
  | `tray` | Tray menu clicks |
  | `update` | Updates |
  | `input` | Input |
  | `crash` | Crashes |
  | `log` | Cleanup and migration |

Useful lines:

- `system: ...` at start-up lists the OS build, the .NET release, the CPU and RAM, the GPUs,
  every monitor (name, resolution, position, DPI, EDID id, primary/oled) and the audio device.
- `settings: changed: idle 300→600, mode fire→random` shows only the keys that changed.
- `veil: veil hidden after 41 s: input (mouse ...)` gives the reason the veil closed:
  - `input`, naming the device;
  - `watched file grew`;
  - `fullscreen app keeps display on`.
- `gpu: device lost on screen 2 of 3: Present returned DXGI_ERROR_DEVICE_REMOVED,
  GetDeviceRemovedReason DXGI_ERROR_DEVICE_HUNG ...`, followed by `overlay rebuilt (gpu)`, or
  by `(gdi)` if the GPU could not come back.

## Perf lines

While a veil runs, one line every `--perf-every` seconds (default 10):

```
veil fps=60.5 min=60.4 frame ms p50=16.5 p95=16.7 p99=16.9 max=17.4 work ms p50=2.17 p99=3.22
  late=0 dropped=0 cpu=0.5% ws=76MB priv=89MB handles=830 gdi=4 user=18 threads=119
  audio glitches=0 gpu="NVIDIA GeForce RTX 5070 Ti" screens=3 span fire,fire,fire
```

(This is one line in the file.)

| Field | Meaning | Healthy |
| --- | --- | --- |
| `fps` | Frames in the sample / its length | ≈ the cap (60 GPU, 30 GDI) |
| `min` | Slowest one-second bucket in the sample | Within a few fps of `fps` |
| `frame ms p50/p95/p99/max` | Time between frame starts (what you see) | p99 < 20 ms at 60 fps |
| `work ms p50/p99` | CPU time to update and submit a frame | Well under the frame time |
| `late` | Frames that came more than 1.5 × the frame period after the previous one | 0 or rare |
| `dropped` | Frames missed: each late frame adds round(gap / period) − 1 | 0 or rare |
| `cpu` | Process CPU over the sample, as % of all cores | < 1 % |
| `ws` / `priv` | Working set / private bytes | Steady (no growth across samples) |
| `handles`, `gdi`, `user`, `threads` | Kernel, GDI and USER object counts, threads | Steady; growth means a leak |
| `audio glitches` | Capture packets that Windows flagged as a gap (`DATA_DISCONTINUITY`) | 0 (one when a device opens is normal) |
| tail | GPU adapter, screen count, screen mode and the scene on each screen (or `gdi WxH mode`) | |

When the veil closes, it writes one summary line:

```
veil closed after 512 s: frames=30710 fps=60.0 worst min fps=58.9 worst p99 ms=17.9
  work ms avg=1.62 late=4 dropped=4 cpu=0.4% ... gpu="..." screens=3 span fire,fire,fire
```

`worst min fps` and `worst p99 ms` are the worst sample of the session. The process numbers
cover the whole session.

While nothing shows, one line every 5 minutes:

```
idle cpu=0.1% ws=41MB priv=29MB handles=402 gdi=30 user=21 threads=17
```

### How to read it

- **Stutter.** Look for high `p99`/`max` together with `late`/`dropped`. If `work ms` stays low
  at the same time, the stalls come from outside the app: the compositor, the driver, or another
  full-screen app. If `work ms p99` is high too, a scene is too heavy.
- **Leaks.** Compare `priv`, `handles`, `gdi` and `user` across the idle lines over hours. They
  should stay flat.
- **Overhead.** The stats cost nothing measurable. The render loop stores two floats per frame
  in fixed buffers and formats one line per sample. On a 3-screen span of fire, the test showed
  60.5 fps at 0.3–0.5 % CPU, the same as before the logging work.

## Crash reports

A crash report is written from these hooks:

| Hook | What happens next |
| --- | --- |
| `Application.ThreadException` (UI thread) | The app keeps running. There is no WinForms error dialog, so nothing sits over the veil. |
| `AppDomain.UnhandledException` | The process ends. The cursor is restored and screens turned off by the veil are brought back first. |
| An exception in the GPU render loop | The overlay is rebuilt, just as after a lost device. |

After every crash, the system cursor is restored. At most 5 reports are written per run, so a
fault that repeats cannot fill the disk.

## Command line

```
--log-console     Mirror the log (and perf lines, prefixed "perf") to the terminal or a redirected stdout
--perf-every <s>  Perf sample period in seconds (default 10; --stats is the same as --perf-every 5)
--debug           Debug level for this run, whatever the ini says
--crash-test ui|render|thread
                  Throw on purpose after about 2 s, to test crash reports (development only)
--exit --preview  Close a running --preview cleanly (so its perf summary gets written)
```

Example:

```powershell
.\build\IdleVeil.exe --preview --mode fire --screens span --perf-every 2 --log-console | Out-Host
.\build\IdleVeil.exe --exit --preview
```
