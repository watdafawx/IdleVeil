# Music visualizers, clocks and music mode

The GPU renderer's audio-reactive scenes and clocks. Read `docs/renderer.md` first; this file only
covers what is specific to these scenes.

| Id | Name in Settings | What it is |
|---|---|---|
| `scope` | Oscilloscope | Triggered waveform line with an echo, white-hot core and a coloured halo |
| `scopering` | Oscilloscope ring | The waveform bent into a ring; kicks send a shock ring outward |
| `radial` | Radial | 96 rounded spokes, mirrored spectrum, bass at the bottom, peak dots |
| `tunnel` | Tunnel | Rings flying towards you, 28 spectrum-lit rails, fog at the vanishing point |
| `ripples` | Ripples | Rings spawned by kicks and hi-hat transients on a random screen |
| `waterfall` | Waterfall | Spectrogram flowing down the whole world, bass in the middle, about 10 s of history |
| `ridges` | Ridges | Stacked spectrum lines receding upward (painter's order, filled so near ridges hide far ones) |
| `skyline` | Skyline | A night city on every floor; windows lit by the band above them, towers blink on kicks |
| `burst` | Beat burst | Spark fireworks on kicks, small sprays on hi-hats, a soft bloom every few seconds when quiet |
| `lissajous` | Lissajous | XY scope of the wave against a delayed copy, auto gain, slowly turning, with an echo trace |
| `clock` | Digital clock | Large 7-segment time, ghost segments, small seconds |
| `clock-led` | LED clock | 25 x 9 amber dot matrix with a seconds bar |
| `clock-neon` | Neon clock | Thin pink tubes, cyan colon, power-on flicker |
| `clock-analog` | Analog clock | Ring, 60 ticks, tapered hands, smooth orange second hand with a comet |
| `clock-music` | Music clock | 7-segment clock that swells with bass, echoes on kicks, spectrum row below |
| `music` | Music (visualizer + clock) | A visualizer while sound plays, a clock when it is quiet, 2 s crossfade |

## Files

- `src/Gpu/Scenes/VizCommon.cs`: `VizScene` base (idle audio, peak tracking, triggered/echo wave,
  10-minute hue turn) and the shared HLSL (`VizHlsl.Common`: `BandN/PeakN/WaveN/EchoN`, `Pal`, `Tone`).
  Each visualizer is a short file with one shader.
- `src/Gpu/Scenes/ClockCommon.cs`: `ClockScene` (time, digit-change transition, drift, `Mix`) and
  `SegClockScene` (a `TextBatch` 7-segment clock with style hooks). `clock`, `clock-neon` and
  `clock-music` are small subclasses of it; `clock-led` and `clock-analog` are their own shaders.
- `src/Gpu/Scenes/MusicScene.cs`: the `music` crossfade.
- `src/AudioDevices.cs`: device list (friendly names), peak meters, `AudioConfig` (`audiodevice=`),
  the auto picker and the resolver `src/Audio.cs` asks every 2 s which device to capture.
- `src/Gpu/MusicSupport.cs`: `MusicConfig` (settings, labels, GDI fallback, random picks) and
  `SoundProbe`.
- `src/Settings/`: the settings window's Music page and the live scene previews (`Thumbnails.cs`).

## Design choices

**Burn-in.** All scenes are mostly black. Nothing stays put: visualizers wander with `Burn` and
Lissajous drifts, clocks drift on periods of 3 to 6 minutes (the amplitude is capped so the clock never
leaves the screen), hue turns once per 10 minutes, and brightness has a soft shoulder (`Tone`) so
loud music cannot clip to white blocks. With no music, every visualizer idles on the CPU (swelling
wave, breathing bands) so there is never a frozen frame.

**Span mode.** Every scene is `World`, so span is one composition over all monitors:

- Spectrum is one wide spectrum along every floor (`SceneSetup.Floors`), low to high from left to
  right, no bar in a hole. Skyline stands on the same floors.
- Scope and Lissajous run across the screens; Scope ring, Radial, Tunnel and Lissajous are centred
  on `SceneSetup.Focus` (the largest hole-free rect), so a hole never cuts them.
- Waterfall and Ridges map the bands across the world; the waterfall falls through stacked screens.
- Ripples and Beat burst pick a random screen (weighted by area) for each new ring or burst, and
  the rings and sparks cross bezels.
- A clock is never cut by a bezel: it sits on one screen (the clock screen, by default the largest
  non-OLED one; Settings > Screens > Clock screen, ini `clockscreen=<EDID key>`), and a dim ambient
  layer (drifting motes, a seconds sweep crossing the world once a minute) moves on all screens.
  Each clock style tints the ambient layer; the music clock makes it swell with the bass.

`music` follows the screen mode like any scene: in span the visualizer spans and the clock sits on
the clock screen; in each/mirror every screen shows the same pick; in per-screen mode every monitor
can have its own visualizer.

**Music mode crossfade.** No render target. The visualizer is drawn opaque, a black layer with alpha
`1 - e` dims it, then the clock is added on top with its own `Mix` of `1 - e`
(`e = smoothstep(vizMix)`, easing over 2 s). Any scene can be the visualizer, including future ports.
`AudioFrame.Playing` already has a 2 s hysteresis, so the gap between two tracks does not flip it.

**Random must not pick audio scenes in silence.** `SoundProbe` reads the peak meter
(`IAudioMeterInformation`) of the audio source (`audiodevice=`; with `auto`, of every playback
device): 8 samples over about 160 ms, cached for 1.5 s. It does not open
a capture stream, so it is safe to call at the moment the veil is about to appear. `MusicConfig.Pick`
(Random with the default pool; a custom pool goes through `SceneCatalog.Pick`, same rule) and `MusicConfig.RandomGpu` (per-screen mode) only add audio-reactive ids when
it says something is playing. A random `clock` pick becomes a random clock style.

**GDI fallback.** `--gdi` (or no GPU) shows the plain `DigitalClock` for any clock id and for
`music`. Visualizers have no GDI version and show Mystify there, like Spectrum does.

## Settings

Settings window, **Music** page:

1. "While music plays": **One visualizer** (click a live card) or **Random** (tick the cards that
   form the pool; a new pick each time the music starts again after the clock was back).
2. In per-screen mode, one drop-down per monitor: "Same as above", "Random from pool" or a
   visualizer.
3. "When it's quiet": the five clock cards.
4. "Audio source": Default output, Auto (the loudest playback device), or any playback device
   (captured as loopback) or recording device (captured directly, e.g. Voicemeeter Out B1), with
   the level of the device being captured, the loudest devices right now (click one to use it),
   and a Refresh button (the list also updates by itself every 3 s).
5. A live "music detected" meter on top, and under "Try it" a **Test** button that shows the real veil in music mode.

Stored in IdleVeil.ini as `musicviz=<id>|random`, `musicpool=<id>,<id>,...`,
`musicviz.<EDID key>=<id>|random` (per-screen), `musicclock=<id>` and
`audiodevice=default|auto|<endpoint id>`; unknown values are ignored. A chosen device that is
missing falls back to the default output (logged) and is picked up again when it returns; a
capture that fails (`AUDCLNT_E_DEVICE_INVALIDATED`, unplugged) is reopened after 3 s.
`--audio-test` lists all devices with their levels; `--audio-device <id|name part>` overrides the
setting for one run.

## Testing

```
build\IdleVeil.exe --render-test <dir> --mode <id> --screens span|each|mirror|perscreen --frames 1
```

`--fake-audio` (or the render test itself) gives synthetic music. Extra hooks, read only at scene init:

- `IDLEVEIL_TEST_TIME=HH:mm:ss`: pin the clock's start time (the clock then runs from there), for
  checking digits and the minute change.
- `IDLEVEIL_MUSIC_STATE=clock|music|0..1`: pin the music-mode mix, for stills of the quiet state, the
  playing state and the middle of the crossfade.

## Adding a clock style

Subclass `SegClockScene` and override the hooks (`OnStyleInit`, `OnStyle`, `SlotColor`,
`SlotLevel`, `OnDrawExtras`), or subclass `ClockScene` for a custom shader. Draw with `Mix` as the
overall level so `music` can fade it, and keep drawing in the `Add` blend. Then add the id to
`GpuScenes.Ids`/`Create` and to `MusicConfig.Clocks`/`Label`.
