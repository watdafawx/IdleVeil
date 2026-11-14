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
- `src/Gpu/MusicSupport.cs`: `MusicConfig` (settings, labels, GDI fallback, random picks) and
  `SoundProbe`.
- `src/Settings/`: the settings window's Music page and the live scene previews (`Thumbnails.cs`).

## Design choices

**Burn-in.** All scenes are mostly black. Nothing stays put: visualizers wander with `Burn` and
Lissajous drifts, clocks drift on periods of 3 to 6 minutes (the amplitude is capped so the clock never
leaves the screen), hue turns once per 10 minutes, and brightness has a soft shoulder (`Tone`) so
loud music cannot clip to white blocks. With no music, every visualizer idles on the CPU (swelling
wave, breathing bands) so there is never a frozen frame.

**Span mode.** Every scene except Ripples is `PerScreen`, so in span mode each monitor shows its own
copy. One clock moving across a 6000 px wide layout would sit on a single monitor most of the time
and leave the others black, and it would cross monitor bezels, which makes a time display unreadable.
A clock per monitor reads at a glance from anywhere and still drifts independently on each. Ripples is
`World`: its rings are meant to be continuous across screens (a ring that grows across a bezel is
the point), and it picks a random screen weighted by area for each new ripple.

**Music mode crossfade.** No render target. The visualizer is drawn opaque, a black layer with alpha
`1 - e` dims it, then the clock is added on top with its own `Mix` of `1 - e`
(`e = smoothstep(vizMix)`, easing over 2 s). Any scene can be the visualizer, including future ports.
`AudioFrame.Playing` already has a 2 s hysteresis, so the gap between two tracks does not flip it.

**Random must not pick audio scenes in silence.** `SoundProbe` reads the default output device's
peak meter (`IAudioMeterInformation`, 8 samples over about 160 ms, cached for 1.5 s). It does not open
a capture stream, so it is safe to call at the moment the veil is about to appear. `MusicConfig.Pick`
(Random with the default pool; a custom pool goes through `SceneCatalog.Pick`, same rule) and `MusicConfig.RandomGpu` (per-screen mode) only add audio-reactive ids when
it says something is playing. A random `clock` pick becomes a random clock style.

**GDI fallback.** `--gdi` (or no GPU) shows the plain `DigitalClock` for any clock id and for
`music`. Visualizers have no GDI version and show Mystify there, like Spectrum does.

## Settings

Settings window, **Music** page: "While music plays" (Spectrum, Oscilloscope, Oscilloscope
ring, Radial, Tunnel, Ripples) and "When it's quiet" (the five clocks), with a live meter and a
Test button. Stored in IdleVeil.ini as
`musicviz=<id>` and `musicclock=<id>`; unknown values are ignored.

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
