# GPU renderer guide

IdleVeil draws its screensaver scenes with Direct3D 11 at native resolution and 60 fps. If the GPU
path can't start, it falls back to the older GDI renderer in `src/Savers.cs`. This guide explains
how the engine works and how to port a scene to it. The last sections are a fully worked port
(Fireflies) and the list of scenes still to port.

Read sections 1 to 5 once. When you port a scene, keep sections 6 (burn-in rules) and 8 (testing)
open next to you.

---

## 1. Architecture

```
src/Gpu/
  D3DInterop.cs     hand-written COM interfaces + P/Invoke (D3D11, DXGI, DirectComposition, D3DCompile)
  GpuDevice.cs      Gpu: device, shader compile + cache, buffers, textures, per-frame state, FrameConstants
  GpuHelpers.cs     Hlsl.Prelude/Sprites/Glyphs, FullscreenPass, SpriteBatch, TextBatch, SceneConstants, GpuTexture
  GpuScene.cs       IGpuScene, GpuScene base class, SceneSetup, FrameContext, RenderView, GpuScenes registry
  SceneHost.cs      screen modes (span / each / mirror / perscreen), scene instances, burn drift, fade-in
  GpuVeil.cs        the live overlay: render thread, one window + swap chain per monitor, frame pacing
  AudioInput.cs     IAudioSource, AudioFrame (smoothing, peaks, beat), SyntheticAudio, LoopbackAudio
  RenderTest.cs     --render-test: off-screen render to PNG
  Scenes/           one file per effect
src/Audio.cs        AudioFeed: WASAPI loopback capture + FFT (shared, ref-counted)
```

**Threads.** One STA render thread owns the D3D device, every overlay window and their message pump.
The tray UI thread only creates and disposes `GpuVeil`. Scenes run only on the render thread, so
they never need locks.

**Windows.** Each monitor gets its own window. Its styles are `WS_EX_LAYERED | WS_EX_TRANSPARENT |
WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW`, plus
`SetLayeredWindowAttributes(alpha 255)`. The window shows a flip-model swap chain through
DirectComposition (`CreateSwapChainForComposition` → DComp target → visual). Its window procedure
returns `HTTRANSPARENT` and `MA_NOACTIVATE`, so mouse clicks pass through to the window underneath
and focus never moves. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` keeps the overlay out of
screenshots. Use `--capturable` to turn that off when you need a screenshot.

**One frame:**

1. Pump messages.
2. Sleep on a high-resolution waitable timer (fps cap).
3. Wait on every swap chain's frame-latency waitable (`SetMaximumFrameLatency(1)`).
4. `SceneHost.Update(dt)`: audio, burn drift, fade, then `scene.Update` once per scene instance.
5. For each screen: clear, bind the constants for this view, `scene.Render(view)`, apply the
   fade-in, then `Present(1, 0)` (vsync).

**Failure.** If device creation, a shader compile, window creation or anything in `Init` throws,
`GpuVeil.TryCreate` returns null. The error is logged (`GPU init failed: ...` in `IdleVeil.log`),
the GPU path is disabled for 10 minutes and the controller uses GDI. A lost device marks the veil
`Broken`, and the controller rebuilds it. Nothing in this path may crash the tray app.

**When the GPU path is used.** `Controller.CreateVeil` tries the GPU path only if *every* screen's
scene id is in `GpuScenes.Ids`. Otherwise the log says `no GPU version of X yet, using GDI`.
`renderer=gdi` in the ini file, or `--gdi`, forces GDI. To port a scene, you register it in
`GpuScenes`. Nothing else in the app changes.

---

## 2. Adding a scene (checklist)

1. Create `src/Gpu/Scenes/<Name>Scene.cs` with a `sealed class <Name>Scene : GpuScene`.
2. Override `OnInit` (create batches and passes, initial state), `Update` (simulate, fill
   batches) and `Render` (draw). Override `Layout` only if the scene is a per-screen composition.
3. Register it in `src/Gpu/GpuScene.cs`: add the id to `GpuScenes.Ids` (tray order, before
   `"blank"`) and a `case` in `GpuScenes.Create`. Use the same id as the GDI saver (`Savers.cs`)
   so that existing settings and the tray menu pick it up.
4. If the scene reads audio, also add the id to `GpuScenes.AudioIds`. The loopback capture only
   runs while such a scene is on screen.
5. Build, run `--render-test`, look at the PNGs, then run the live `--preview --stats` (section 8).

Language rules: C# 5 (.NET Framework 4 `csc.exe`). That means no `$""` strings, no `?.`, no
expression-bodied members and no `nameof`. No NuGet packages. Files use CRLF line endings. HLSL
lives in C# verbatim strings (`@"..."`), so a `"` inside the shader has to be written `""`.

---

## 3. The scene interface

```csharp
interface IGpuScene : IDisposable
{
    SceneLayout Layout { get; }              // World (default) or PerScreen
    void Init(Gpu gpu, SceneSetup setup);    // once; throw = fall back to GDI
    void Update(FrameContext frame);         // once per frame: simulate, fill batches
    void Render(RenderView view);            // once per screen per frame: draw only
}
```

The `GpuScene` base class stores what you need and calls your `OnInit()`:

| Member  | Meaning |
|---------|---------|
| `Gpu`   | the device wrapper: shaders, buffers, state |
| `Setup` | the `SceneSetup` (below) |
| `World` | `Setup.World`, the whole world rect in world units |
| `U`     | world units per *design unit*. A design unit is one pixel of a 1080p screen, so U = 1 on 1080p and 1.333 on 1440p. Multiply every size and speed by `U`. |
| `Rng`   | `FastRandom` seeded per instance; `Rng.Next()` returns 0..1 |

`SceneSetup`: `World`, `Screens` (the *visible* rects inside the world), `U`, `Seed`, `Mode`, and
`Visible(x, y, margin)`, which is true if the point lies on some screen. A positive margin grows
the screens and a negative margin shrinks them.

`FrameContext` (passed to `Update`): `Time` (seconds since this instance started), `Dt` (clamped
to 0..0.1), `Audio` (`AudioFrame`), and `BurnX`/`BurnY` (the burn-in drift, in world units).

`RenderView` (passed to `Render`): `View` (the world rect this screen shows), `Viewport` (target
pixels) and `Index`. Most scenes ignore it, because the host has already bound everything the
shaders need for the view.

Rules:

- **Simulate in `Update`, draw in `Render`.** `Render` runs once per screen. If you advanced state
  there, a 3-monitor setup would run 3 times too fast.
- Allocate in `OnInit`. In `Update`, reuse arrays and call `SpriteBatch.Clear()` + `Add`.
  Don't allocate per frame.
- GPU objects you create through `Gpu` (passes, batches, constants, textures) are released with
  the device. Override `Dispose` only for other resources.

---

## 4. Coordinates and screen modes

There are three spaces:

- **World units.** Scenes live here. In span mode they are virtual-desktop pixels; in the other
  modes they are the screen's own pixels starting at (0, 0).
- **View.** The rectangle of the world one screen shows.
- **Viewport / pixels.** Where that view lands in the render target. Usually the full target; in
  mirror mode it is letterboxed.

| `screens=` | Scene instances | World | Each screen shows |
|---|---|---|---|
| `span` | 1 | the virtual desktop bounding box | its own rectangle of it, so content runs across monitors |
| `each` (default) | 1 per screen | that screen, origin (0,0) | all of it |
| `mirror` | 1 | the largest screen | all of it, scaled to fit, aspect kept (black bars) |
| `perscreen` | 1 per screen, scene chosen per monitor | that screen | all of it |

The per-monitor scene comes from `scene.<EDID id>=<scene>` in the ini file (for example
`scene.AUS2740=starfield`), or from the tray **Screens** submenu. A detached monitor
(DisplayDetach) gets no window. A display change rebuilds everything.

**Span has holes.** The world is the *bounding box*, so the empty corner under a shorter monitor is
part of the world but not on any screen. Spawn and cull against `Setup.Screens` /
`Setup.Visible`, not `World`. Otherwise objects pile up where nobody can see them.
`Setup.Visible` is exact; on a single screen it is always true inside the world.

**`SceneLayout.PerScreen`.** A scene with a composition per screen (a centred clock, a spectrum
with margins) returns `SceneLayout.PerScreen`. Span mode then falls back to `each` for it, and the
log shows `each`.

**In shaders:**

```hlsl
float2 w  = PixelToWorld(pos.xy);   // SV_Position -> world units (handles span offset and mirror scale)
float2 uv = ViewUV(w);              // 0..1 across this screen's view
float2 g  = WorldUV(w);             // 0..1 across the whole world (continuous in span)
float4 c  = WorldToClip(w);         // vertex shaders: world -> clip space of this viewport
float px  = PixelWorld;             // world units per screen pixel: use it for anti-aliasing widths
```

Rules of thumb:

- Things that should continue across monitors (sky, particles, noise fields) use world x/y.
- Things that should sit at the same place on every screen (a horizon line, a curtain hem) use
  `w.y - View.y` (the distance from the top of this screen). Aurora mixes the two: world x and
  per-screen y.
- Use `U` for sizes in design units. Use `PixelWorld` (not `U`) for "one pixel": anti-aliasing,
  minimum line width.

---

## 5. Constant buffers and the HLSL prelude

Every shader gets `Hlsl.Prelude` prepended. Slots:

| Register | What | Set by |
|---|---|---|
| `b0` | `Frame` (below) | host, per view |
| `b1` | `AudioData` | host, per frame |
| `b2` | `Scene` (your own) | `SceneConstants.Bind()` |
| `t0` | sprite / glyph instance buffer | `SpriteBatch` / `TextBatch` |
| `t1`+ | your textures | `GpuTexture.Bind(slot)` |
| `s0` / `s1` / `s2` | `SampLinear` (clamp), `SampPoint` (clamp), `SampWrap` (linear wrap) | host |

`cbuffer Frame : register(b0)`:

| Field | Meaning |
|---|---|
| `Time`, `Dt` | seconds since the scene started, seconds since the last frame |
| `U` | world units per design unit |
| `Seed` | 0..1, fixed per instance: offset noise with it so instances differ |
| `World` | x, y, w, h of the whole world |
| `View` | x, y, w, h of the world part this viewport shows |
| `Viewport` | x, y, w, h in target pixels |
| `Audio` | x bass, y energy, z playing (0/1), w beat pulse (1 on a kick, decays) |
| `Burn` | the burn-in drift in world units (section 6) |
| `Fade` | 0..1 fade-in. The host applies it; read it only if you need it |
| `PixelWorld` | world units per screen pixel |
| `ViewIndex` | screen index 0..n-1 |

`cbuffer AudioData : register(b1)`: `Bands4[16]` (64 bands, 0..1, low → high, smoothed),
`Peaks4[16]` (falling peak per band), `Wave4[128]` (512 waveform samples, -1..1). Read them with
`BandAt(i)`, `PeakAt(i)`, `WaveAt(i)` or the interpolated `Band(t)`, `Peak(t)`, `Wave(t)` for
t in 0..1.

Prelude functions:

- **Colour.** `hsv(h, s, v)`. `Out(c, pos.xy)` is the final pixel of a full-screen pass: it clamps,
  applies a ±1 LSB triangular dither (smooth dark gradients on 8-bit) and returns opaque.
- **Noise.** `hash11`, `hash12`, `hash22`, `noise(float2)` (value noise, 0..1),
  `fbm(float2, octaves)`.
- **Shapes.** `sdSegment(p, a, b)`, `sdRoundBox(p, halfSize, r)`, and `Coverage(d, PixelWorld)`
  (a 1-pixel anti-aliased edge from a signed distance).
- **Constants.** `PI`, `TAU`. `VSFull` (the full-screen triangle vertex shader).

Your own constants: declare `cbuffer Scene : register(b2) { float A; float B; float2 Pad; };` and
create `new SceneConstants(Gpu, 4)`. Set `k[0] = ...` in `Update`, then call `k.Bind()` in `Render`
before drawing. Values are packed in declaration order in float4 rows, so keep the HLSL and C#
indices in sync and pad to a multiple of 4.

---

## 6. Helpers

### FullscreenPass: one pixel shader over the viewport

```csharp
pass = new FullscreenPass(Gpu, Shader);   // Shader defines float4 PS(float4 pos : SV_Position) : SV_Target
...
public override void Render(RenderView v) { pass.Draw(); }          // Opaque by default
```

Use it for fields: aurora, plasma, gradients, and anything per pixel. You can stack several passes
with `BlendMode.Add` or `BlendMode.Alpha`. Reference scenes: `AuroraScene` (pure shader, no
per-frame upload) and `SpectrumScene` (shader + `SceneConstants` + audio).

### SpriteBatch: instanced soft capsules in world units

```csharp
sprites = new SpriteBatch(Gpu, capacity);   // grows automatically
// Update:
sprites.Clear();
sprites.Add(ax, ay, bx, by, radius, glow, glowAmp, tail, r, g, b, a);  // segment A->B (streak, line)
sprites.AddDot(x, y, radius, glow, glowAmp, r, g, b, a);              // A == B
// Render:
sprites.Draw(BlendMode.Add);   // or Alpha
```

- `radius`: the solid core half-width (world units, so multiply by `U`). A sprite smaller than a
  pixel dims instead of shrinking, so tiny stars don't shimmer.
- `glow`: the gaussian sigma of the halo. `glowAmp`: halo brightness; 0 means no halo.
- `tail`: 0 means even brightness; 1 fades towards A (motion streaks: put A behind the object).
- The colour is linear 0..1, and `a` scales everything (with Add blending it acts as brightness).
- Cull invisible sprites (`Setup.Visible`) before `Add`. Every visible sprite is drawn on each
  screen; the GPU clips the off-screen ones, but they still cost vertex work.

Reference scenes: `StarfieldScene` (streaks, culling, span), `FirefliesScene` (section 7).

### TextBatch: seven-segment digits as distance fields

```csharp
text = new TextBatch(Gpu, 64);
text.Clear();
float w = TextBatch.Measure("12:34", h);                // width for centring
text.Add("12:34", x, y, h, 0.12f, 6f * U, 0.25f, r, g, b, a);   // top-left x,y; height h; stroke; glow
text.Draw();                                            // additive
```

It draws `0-9 : . -` and space, crisp at any size. Use it for clock styles and counters. For
other glyph shapes, extend `GlyphDist` in `Hlsl.Glyphs` (and `Code()` in `TextBatch`).

### SceneConstants, GpuTexture

- `SceneConstants(gpu, floats)`: your `b2` buffer, uploaded lazily on `Bind()`.
- `GpuTexture(gpu, w, h)`: a CPU-written BGRA texture (`Pixels[i] = 0xAARRGGBB`). Call
  `Upload()` after writing and `Bind(slot)` before drawing. In HLSL: `Texture2D Grid :
  register(t1); Grid.Sample(SampPoint, uv)`. Use it for cellular grids (Life) and heat buffers
  (Fire). Keep the grid small and draw the detail in the shader (for example rounded cells from
  `SampPoint` + `sdRoundBox` on `frac(uv * size)`), so it stays crisp at 1440p.

### Blend modes

`BlendMode.Opaque` (full-screen base), `Add` (light: glows, sparks, text) and `Alpha`
(occluding shapes). The target is cleared to black before `Render`.

---

## 7. OLED / burn-in rules (mandatory)

The app exists to protect OLED panels. Every scene must follow these rules:

1. **Mostly dark.** The average picture level stays low. The background is black or near-black
   (≤ 0.02).
2. **No large bright areas.** Peaks are fine on small things (stars, fireflies, bar tips). Broad
   fields (aurora, plasma) should stay under about 0.5 linear. Use a soft shoulder such as
   `c / (1 + c * k)` rather than clipping.
3. **Nothing static.** Any element that would otherwise sit still (a clock, a bar baseline, a
   horizon, margins) must move. Add `Burn` (shader) or `f.BurnX/f.BurnY` (CPU) to its position.
   Burn is a ±14U Lissajous with periods of a few minutes, different for each instance.
4. **Slow drift of everything else.** Vanishing points wander, palettes rotate (for example a full
   hue turn every 10 minutes), and layouts breathe.
5. **No hard edges that stay put.** Even moving content shouldn't keep a sharp edge in one spot
   for minutes (the spectrum's baseline and margins drift for this reason).
6. **Idle audio state.** A visualizer with no music playing must still move gently (the spectrum
   bars breathe when `Audio.z == 0`). It must not freeze into a static frame.

The host already provides the 1.2 s fade-in and the dither. Scenes don't need to do either.

---

## 8. Testing

Build (no warnings allowed):

```powershell
pwsh -NoProfile -File .\build.ps1 -Version 1.2.0-dev
```

Put an empty `build\IdleVeil.ini` next to the exe (it is gitignored). That makes test runs
portable, so the log goes to `build\IdleVeil.log` instead of `%APPDATA%\IdleVeil`.

**Off-screen PNGs.** Renders at the real monitor resolutions and needs no visible window:

```powershell
.\build\IdleVeil.exe --render-test <dir> [--mode fireflies] [--screens span] [--frames 3]
```

- It writes `<scene>_<mode>_s<screen>_f<n>.png` for each screen and
  `<scene>_<mode>_layout_f<n>.png`, a half-size picture of the whole desktop with grey where
  there is no screen. Use the layout image to check span continuity across monitor seams.
- Timing per screen goes to `<dir>\render-test.txt`.
- The simulation runs at a fixed 60 fps from t = 0, so results are repeatable. Audio is synthetic.
- **Look at the PNGs yourself.** Check that they are dark overall, that the detail is crisp at
  1440p, that nothing is clumped in a span hole, and that edges continue across the seams.

**Live preview with stats** (keep each run ≤ 8 s if someone is at the desk):

```powershell
.\build\IdleVeil.exe --preview --mode fireflies --screens span --stats
# after ~5 s IdleVeil.log shows:
# gpu stats: fps=60.7 frame ms avg=0.94 max=1.63 longest gap ms=17.0 cpu=0.2% screens=3 span ...
Stop-Process -Name IdleVeil   # then restore cursors:
Add-Type -Namespace W -Name C -MemberDefinition '[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, System.IntPtr c, uint d);'; [W.C]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0)
```

- The target is fps ≈ 60 and `longest gap` < 20 ms. `frame ms` is CPU time per frame (update +
  submit).
- Killing the process leaves cursors hidden. The app restores them at startup, but restore them
  right away after a kill anyway.
- Other flags: `--fake-audio` (synthetic music, for visualizers in a quiet room), `--gdi` (force
  the old renderer), `--capturable` (allow screenshots of the overlay), `--fps N`.
- Check all four `--screens` modes. Span and perscreen catch most bugs.

**Shader errors.** A compile error throws
`shader PS (ps_5_0) failed to compile: <compiler output>`. It shows up in `render-test.txt` and
`IdleVeil.log` (`render test failed` / `GPU init failed`). The live app falls back to GDI, so a
broken shader shows the GDI version instead of crashing. Always check the log.

---

## 9. Worked example: porting Fireflies

The GDI original (`Fireflies` in `src/Savers.cs`):

- ~50 fireflies per 1080p screen. Each has x, y, heading, pulse phase and pulse speed.
- They random-walk the heading at 55 u/s and steer back to the centre at the edges.
- They pulse with a cubed sine.
- They are drawn with 4×4 pre-baked glow bitmaps (size and brightness levels), blitted at whole
  pixels.

### Mapping GDI → GPU

| GDI | GPU |
|---|---|
| `Size size`, `W`, `H` | `World` / `Setup.Screens` (world units) |
| `U` from the screen height | `U` from `GpuScene` (same meaning) |
| `Step()` / `Tick()` / `Dt` | `Update(FrameContext f)` / `f.Dt` |
| `Draw(hdc)` + `PatBlt(BLACKNESS)` | `Render(view)`; the target is already black |
| pre-baked glow bitmaps (`MiniDib.MakeGlow`) | `SpriteBatch.AddDot(radius, glow, glowAmp, ...)`, continuous size and brightness, sub-pixel position |
| clamp to `0..W`, `0..H` | `Setup.Visible` + the nearest screen (span has holes) |
| `FastRandom rng` with a fixed seed | `Rng` (seeded per instance, so the screens differ) |

### The code (`src/Gpu/Scenes/FirefliesScene.cs`)

```csharp
sealed class FirefliesScene : GpuScene
{
    SpriteBatch sprites;
    float[] x, y, heading, phase, rate;

    protected override void OnInit()
    {
        // About 50 per 1080p screen of visible area (not of the world: span worlds have holes).
        float area = 0f;
        foreach (RectF r in Setup.Screens) area += r.W * r.H;
        int n = Math.Max(40, Math.Min(400, (int)(50f * area / (1920f * 1080f))));
        x = new float[n]; y = new float[n]; heading = new float[n]; phase = new float[n]; rate = new float[n];
        for (int i = 0; i < n; i++)
        {
            RectF s = Setup.Screens[i % Setup.Screens.Length];   // start on a real screen
            x[i] = s.X + Rng.Next() * s.W;
            y[i] = s.Y + Rng.Next() * s.H;
            heading[i] = Rng.Next() * 6.2831853f;
            phase[i] = Rng.Next() * 6.2831853f;
            rate[i] = 0.6f + Rng.Next() * 1.2f;
        }
        sprites = new SpriteBatch(Gpu, n);
    }

    public override void Update(FrameContext f)
    {
        float dt = f.Dt, speed = 55f * U, margin = 4f * U;      // design units * U
        sprites.Clear();
        for (int i = 0; i < x.Length; i++)
        {
            heading[i] += (Rng.Next() - 0.5f) * 3f * dt;
            x[i] += (float)Math.Cos(heading[i]) * speed * dt;
            y[i] += (float)Math.Sin(heading[i]) * speed * dt;
            if (!Setup.Visible(x[i], y[i], -margin))
            {
                // Off every screen: turn towards the middle of the nearest one and stay on it.
                RectF home = Nearest(x[i], y[i]);
                float want = (float)Math.Atan2(home.CenterY - y[i], home.CenterX - x[i]);
                heading[i] += (float)Math.Sin(want - heading[i]) * 2f * dt;
                x[i] = Math.Min(Math.Max(x[i], home.X), home.Right);
                y[i] = Math.Min(Math.Max(y[i], home.Y), home.Bottom);
            }
            phase[i] += dt * rate[i];

            float pulse = 0.5f + 0.5f * (float)Math.Sin(phase[i]);
            pulse = pulse * pulse * pulse;                       // mostly dark, short flash
            if (pulse <= 0.03f) continue;                        // don't upload invisible sprites
            float radius = U * (1.4f + 1.6f * pulse);
            float glow = U * (6f + 7f * pulse);
            sprites.AddDot(x[i], y[i], radius, glow, 0.6f, 0.75f, 0.95f, 0.25f, pulse);
        }
    }

    RectF Nearest(float px, float py) { /* screen with the smallest distance to the point */ }

    public override void Render(RenderView view) { sprites.Draw(BlendMode.Add); }
}
```

Then register it in `GpuScene.cs`:

```csharp
public static readonly string[] Ids = { "starfield", "aurora", "fireflies", "spectrum", "blank" };
...
case "fireflies": return new FirefliesScene();
```

### Why each choice

- **Count from `Setup.Screens` area, not `World`.** In span mode on this test rig, the world is
  4480×2160 but only 3 screens are lit. Counting by world area would put ~20% of the fireflies
  in the hole.
- **Steer only when off *every* screen.** Fireflies drift across monitor seams in span mode, and
  in each/mirror mode this behaves exactly like the GDI edge steering.
- **No burn offset.** Every firefly moves all the time, so nothing is static (rule 3). A scene
  with fixed anchors would add `f.BurnX/BurnY`.
- **Brightness through `a`.** With additive blending, `a` multiplies the light, so the pulse
  dims the core and halo together and the 3 to 6 % of fireflies that are "off" are skipped.
- **Sizes × `U`.** On the 1440p screen everything is 1.33× larger, so the scene looks identical
  to 1080p, only sharper.

### Verifying it

```powershell
pwsh -NoProfile -File .\build.ps1 -Version 1.2.0-dev
.\build\IdleVeil.exe --render-test $env:TEMP\ff --mode fireflies     # look at the PNGs
.\build\IdleVeil.exe --preview --mode fireflies --screens span --stats   # ≤ 8 s, read fps, kill, restore cursors
```

---

## 10. Scenes left to port

| Scene (id) | GDI class | Suggested pattern |
|---|---|---|
| `plasma` | `Plasma` | One `FullscreenPass`. Keep the colours dark with a slow hue drift. The easiest port. |
| `snow` | `Snow` | `SpriteBatch` dots (like Fireflies) with wind drift. In span mode, spawn above each screen's top edge (`Setup.Screens`). Optional: a ground pile in a `GpuTexture`. |
| `fire` | `Fire` | Keep the heat grid on the CPU, upload it to a `GpuTexture` each frame, palette lookup + smoothing in a `FullscreenPass` (`SampLinear`). Per-screen y (flames rise from each screen's bottom). |
| `life` | `Life` | CPU grid → `GpuTexture` (`SampPoint`). Draw rounded cells with `sdRoundBox(frac(uv*size))`, and fade between generations in the shader with two textures. |
| `clock` + styles | `DigitalClock` | **Ported** (clock, clock-led, clock-neon, clock-analog, clock-music, music). See [music-scenes.md](music-scenes.md). |
| `mystify` | `Mystify` (IdleVeil.cs) | `SpriteBatch.Add` segments (glow, Add blend) for the polygons and trails. Bounce inside the visible screens. |
| `matrix` | `MatrixRain` (IdleVeil.cs) | Glyph columns: either a glyph atlas `GpuTexture` drawn per cell in a `FullscreenPass`, or a new `Hlsl.Glyphs`-style SDF set. Per-column state on the CPU, uploaded as a small texture. |
| music visualizers (deckviz) | deckviz source (`saver.rs`, `hd.wgsl`) | **Ported** (scope, scopering, radial, tunnel, ripples). See [music-scenes.md](music-scenes.md). |

Once all of these are ported, the GDI path is only used as the fallback.
