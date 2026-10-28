# IdleVeil

Burn-in protection for OLED screens that stays out of your way.

IdleVeil shows a screensaver-style animation (Matrix rain or Mystify) when your PC has been idle for a while. Unlike the built-in Windows screensaver, it **doesn't switch to a separate desktop**, so anything running in the background keeps working: remote-control tools, game streaming (Sunshine, Parsec, Moonlight), automation, and AI agents that drive the desktop.

## Features

- **Invisible to screen capture.** Screenshots, recordings and streams see your desktop, not the animation. Only the physical screen shows it.
- **Never steals focus.** Clicks pass straight through, and keyboard focus stays where it was.
- **Hides on any input,** including synthetic mouse/keyboard input sent by remote tools.
- **Respects video playback.** It stays off while an app asks Windows to keep the display on.
- **Hides the mouse cursor** while the animation is showing (optional).
- **Tray icon** for all settings: animation, idle time, pause, start with Windows.
- **Tiny and dependency-free:** a ~35 KB exe using the .NET Framework that's already part of Windows 10 and 11.

## Install

Download `IdleVeil-Setup-<version>.exe` from the [releases page](https://github.com/watdafawx/IdleVeil/releases/latest) and run it. No admin rights needed.

The installer:

- installs to `%LOCALAPPDATA%\Programs\IdleVeil` (for your user only)
- adds a Start menu shortcut (and a desktop shortcut if you tick the box)
- can set IdleVeil to **start with Windows** (ticked by default)
- adds IdleVeil to **Settings > Apps > Installed apps**, so you can uninstall it from there

Running a newer installer over an existing install updates it in place and keeps your settings.

**Portable option:** download `IdleVeil-<version>-portable.zip`, unzip it anywhere, and run `IdleVeil.exe`. To keep settings next to the exe instead of in `%APPDATA%`, create an empty `IdleVeil.ini` file beside it.

> Windows SmartScreen may warn about an unrecognized app the first time, because the installer isn't code-signed. Click **More info > Run anyway**.

## Using it

IdleVeil lives in the system tray (you may need to click the `^` arrow to see it). Right-click the icon for:

| Menu item | What it does |
| --- | --- |
| **Show now** | Starts the animation right away (double-clicking the icon does the same) |
| **Animation** | Random, Matrix rain, or Mystify |
| **Start after** | Idle time before it appears: 1 to 60 minutes |
| **Hide cursor** | Hide the mouse pointer while the animation is showing |
| **Pause for 1 hour / Pause until resumed** | Temporarily stop it from appearing |
| **Start with Windows** | Turn launching at sign-in on or off |
| **About IdleVeil** | Version, and where settings and the log are stored |
| **Exit** | Quit IdleVeil |

Move the mouse or press any key to dismiss the animation.

### Tip: turn off the Windows screensaver

IdleVeil replaces it. In **Settings > Personalization > Lock screen > Screen saver**, set the screensaver to **(None)**. Otherwise Windows may switch to its own screensaver desktop, which can interrupt remote and streaming tools.

## Command-line options

Settings chosen in the tray menu are saved and take priority over these defaults.

```
IdleVeil.exe [options]

  --idle <seconds>             Idle time before the animation starts (default 300)
  --mode random|matrix|mystify Animation to show (default random)
  --watch <file>               Treat growth of this file as activity, e.g. a log
                               written by a remote-control server
  --fps <n>                    Animation frame rate (default 30)
  --preview                    Show the animation once, then quit on input
  --ignore-display-requests    Show even while an app asks to keep the display on
  --exit                       Tell a running IdleVeil to quit
```

## Uninstall

**Settings > Apps > Installed apps > IdleVeil > Uninstall.** This removes the program, its shortcuts and the start-with-Windows entry. Your settings in `%APPDATA%\IdleVeil` are kept; delete that folder too if you want them gone.

Silent install and uninstall (for scripts):

```
IdleVeil-Setup-1.0.0.exe --silent [--no-autostart]
"%LOCALAPPDATA%\Programs\IdleVeil\Uninstall.exe" --uninstall --silent
```

## Files

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\Programs\IdleVeil` | `IdleVeil.exe`, `Uninstall.exe` |
| `%APPDATA%\IdleVeil\IdleVeil.ini` | Your settings |
| `%APPDATA%\IdleVeil\IdleVeil.log` | Start-up and error log |

## Building from source

Requirements: Windows 10 or 11. That's it. The build uses the C# compiler that ships with the .NET Framework (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so there's no SDK to install.

```powershell
.\build.ps1                 # version 1.0.0
.\build.ps1 -Version 1.1.0  # set the version for a release
```

Output goes to `dist\`:

- `IdleVeil-Setup-<version>.exe`: the installer, with the app embedded
- `IdleVeil-<version>-portable.zip`: the app exe plus this README

Project layout:

```
src\IdleVeil.cs       the app (tray, idle detection, animations)
installer\Setup.cs    the installer and uninstaller
build.ps1             builds the icon, app, installer and zip
```

The source is limited to C# 5, since that's what the built-in compiler supports.

## How it works

- Idle time comes from `GetLastInputInfo`, which also sees input injected by remote tools.
- The overlay is a topmost, layered, click-through (`WS_EX_TRANSPARENT`), non-activating window.
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` hides it from screen capture (Windows 10 2004 or later).
- Video playback is detected by checking whether any app has set `ES_DISPLAY_REQUIRED`.
- The cursor is hidden by temporarily swapping the system cursors for blank ones and restoring the user's cursor scheme afterwards (also on start-up, in case of a crash).

## License

[MIT](LICENSE)
