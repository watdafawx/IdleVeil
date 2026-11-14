# IdleVeil

Burn-in protection for OLED screens that stays out of your way.

IdleVeil shows a screensaver-style animation (Matrix rain, Mystify or plain black) when your PC has been idle for a while, and can turn an OLED monitor off for real. Unlike the built-in Windows screensaver, it **doesn't switch to a separate desktop**, so anything running in the background keeps working: remote-control tools, game streaming (Sunshine, Parsec, Moonlight), automation, and AI agents that drive the desktop.

## Features

- **Invisible to screen capture.** Screenshots, recordings and streams see your desktop, not the animation. Only the physical screen shows it.
- **Never steals focus.** Clicks pass straight through, and keyboard focus stays where it was.
- **Only real input counts.** Clicks and keystrokes injected by AI agents and automation tools don't keep the screen awake (this can be turned off for remote-control tools like Parsec or Sunshine).
- **Controllers count as input** (Xbox pads, and other pads mapped through Steam Input or DS4Windows).
- **Turns an OLED off for real.** It can remove one monitor from the desktop after the animation has shown for a while. The monitor sees "no signal" and goes into its own standby, where OLEDs run pixel cleaning, and no app or agent can wake it. Moving the mouse brings it back, with your windows where they were.
- **Respects video playback.** It stays off while a fullscreen app asks Windows to keep the display on.
- **Per-app exceptions.** Never shows while chosen apps (e.g. a game you watch AFK) are focused.
- **Hides the mouse cursor** while the animation is showing (optional).
- **Tray icon** for all settings: animation, idle time, pause, start with Windows.
- **Updates itself** from GitHub releases, with every download checked against the release's SHA-256 checksums.
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
| **Animation** | Random, Matrix rain, Mystify, or Blank (black) |
| **Start after** | Idle time before it appears: 1 to 60 minutes |
| **Hide cursor** | Hide the mouse pointer while the animation is showing |
| **Ignore input from apps** | Only real mouse, keyboard and controller input counts (on by default). Turn off if you use remote-control tools that should count as you being here |
| **Keep screen on for video** | Whether apps asking to keep the display on stop the animation: Fullscreen only (default), Always, or Never |
| **Turn off monitor** | Pick a monitor to switch off (detach) once the animation has shown for 1 to 60 minutes. The other screens keep the animation |
| **Don't show while using** | Apps that stop the animation while they're focused. Focus the app, then open this menu and pick **Add** |
| **Pause for 1 hour / Pause until resumed** | Temporarily stop it from appearing |
| **Start with Windows** | Turn launching at sign-in on or off |
| **Install updates automatically** | Download and install new releases from GitHub (on by default). Checked shortly after start, then every 12 hours; installs only while the animation isn't showing |
| **Check for updates** | Check now, and install if there's a newer version |
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
  --count-injected-input       Count input injected by other apps as activity
  --off-monitor <id>           Monitor to turn off, by its EDID id (e.g. AUS2740)
  --off-after <seconds>        How long the animation shows before that monitor
                               is turned off (default 300)
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
src\IdleVeil.cs       the app (tray, idle logic, animations)
src\PhysicalInput.cs  real vs injected mouse/keyboard input (Raw Input)
src\Controllers.cs    game controller activity (XInput)
src\DisplayDetach.cs  turning a monitor off and back on, restoring windows
src\Updater.cs        self-update from GitHub releases
installer\Setup.cs    the installer and uninstaller
build.ps1             builds the icon, app, installer and zip
```

The source is limited to C# 5, since that's what the built-in compiler supports.

## How it works

- Idle time comes from Raw Input. Injected input (`SendInput`) arrives without a source device, so it's ignored; real devices always have one. With "Ignore input from apps" off, `GetLastInputInfo` is used instead, which sees both. Controllers are polled through XInput.
- A monitor is turned off by removing it from the display layout with `SetDisplayConfig`, never saving that layout to Windows' display database. The previous layout and window placements are restored on return. It waits until no app has injected input for a minute, so an agent clicking on that monitor isn't disturbed mid-task. If IdleVeil is killed while a monitor is off, the next start brings it back.
- The overlay is a topmost, layered, click-through (`WS_EX_TRANSPARENT`), non-activating window.
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` hides it from screen capture (Windows 10 2004 or later).
- Video playback is detected by checking whether any app has set `ES_DISPLAY_REQUIRED` while a fullscreen (not maximized) window has focus.
- The cursor is hidden by temporarily swapping the system cursors for blank ones and restoring the user's cursor scheme afterwards (also on start-up, in case of a crash).

## Releasing

Push a tag like `v1.2.0`. The release workflow builds that version and attaches the setup exe, the portable zip, the bare `IdleVeil.exe` and `SHA256SUMS.txt` to a GitHub release. Running copies pick it up within 12 hours.

## License

[MIT](LICENSE)
