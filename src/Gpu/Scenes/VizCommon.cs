// Shared base for the music visualizers (oscilloscope, ring, radial, tunnel, ripples).
//
// The host hands scenes the raw AudioFrame. A visualizer wants a little more than that: a
// waveform that holds still instead of jittering (software trigger), an echo of it, an "idle"
// signal so nothing freezes when the music stops, and everything eased between the two. This
// base class does all of that on the CPU once per frame and gives the shader one constant
// buffer (register b2) with the result, so each scene's HLSL only draws.
//
//   SP  x hue, y play (0 idle .. 1 music), z bass, w beat
//   SQ  x energy, y phase (advances faster when it is loud), z, w spare
//   VB  64 bands, VP 64 falling peaks, VW 512 triggered waveform samples, VE the same, slower
//   EX  16 float4 of per-scene extras (ripples keep their state here)
//
// Rules from docs/renderer.md that this keeps for every visualizer: mostly dark, nothing static
// (centres wander with Burn, the palette turns a full hue circle in 10 minutes), and a gentle
// idle motion when Audio.Playing is false.
using System;

namespace IdleVeil
{
    static class VizHlsl
    {
        public const string Common = @"
cbuffer Scene : register(b2)
{
    float4 SP;        // x hue, y play, z bass, w beat
    float4 SQ;        // x energy, y phase
    float4 VB[16];    // 64 bands
    float4 VP[16];    // 64 peaks
    float4 VW[128];   // 512 waveform samples (triggered, smoothed)
    float4 VE[128];   // the same, slower: an echo
    float4 EX[16];    // scene extras
};

float BandN(int i) { i = clamp(i, 0, 63); return VB[i >> 2][i & 3]; }
float PeakN(int i) { i = clamp(i, 0, 63); return VP[i >> 2][i & 3]; }
float WaveN(int i) { i = clamp(i, 0, 511); return VW[i >> 2][i & 3]; }
float EchoN(int i) { i = clamp(i, 0, 511); return VE[i >> 2][i & 3]; }
float BandF(float t) { float x = saturate(t) * 63.0; int i = (int)x; return lerp(BandN(i), BandN(i + 1), x - i); }
float PeakF(float t) { float x = saturate(t) * 63.0; int i = (int)x; return lerp(PeakN(i), PeakN(i + 1), x - i); }
float WaveF(float t) { float x = saturate(t) * 511.0; int i = (int)x; return lerp(WaveN(i), WaveN(i + 1), x - i); }
float EchoF(float t) { float x = saturate(t) * 511.0; int i = (int)x; return lerp(EchoN(i), EchoN(i + 1), x - i); }

// Palette: t 0..1 walks a third of the hue circle from the drifting base hue.
float3 Pal(float t) { return hsv(SP.x + t * 0.30, 0.68, 1.0); }
// Towards white: the hot core of a beam.
float3 Hot(float3 c, float k) { return lerp(c, float3(1.0, 1.0, 1.0), k); }
// Soft shoulder, so overlapping glows never clip hard.
float3 Tone(float3 c) { return 1.0 - exp(-c * 1.5); }
";
    }

    abstract class VizScene : GpuScene
    {
        public const int Floats = 1224;
        const int OffSP = 0, OffVB = 8, OffVP = 72, OffVW = 136, OffVE = 648, OffEX = 1160;

        FullscreenPass pass;
        readonly float[] lowpass = new float[AudioFrame.WaveCount];

        protected SceneConstants K;
        protected readonly float[] Bands = new float[64];
        protected readonly float[] Peaks = new float[64];
        protected readonly float[] Wave = new float[512];
        protected readonly float[] Echo = new float[512];
        protected float Play;      // 0 idle .. 1 music, eased
        protected float Bass, Beat, Energy, Phase, Hue;

        public override SceneLayout Layout { get { return SceneLayout.PerScreen; } }

        // The fragment shader body; VizHlsl.Common and the prelude are put in front of it.
        protected abstract string Shader { get; }
        protected virtual void OnInitViz() { }
        protected virtual void OnUpdateViz(FrameContext f) { }

        protected override void OnInit()
        {
            pass = new FullscreenPass(Gpu, VizHlsl.Common + Shader);
            K = new SceneConstants(Gpu, Floats);
            Hue = Rng.Next();
            Phase = Rng.Next() * 50f;
            OnInitViz();
        }

        // Writes one float4 of scene extras (EX[slot]).
        protected void SetEx(int slot, float x, float y, float z, float w)
        {
            float[] v = K.Values;
            int o = OffEX + slot * 4;
            v[o] = x; v[o + 1] = y; v[o + 2] = z; v[o + 3] = w;
        }

        public override void Update(FrameContext f)
        {
            AudioFrame a = f.Audio;
            float dt = f.Dt, t = f.Time;
            Play += ((a.Playing ? 1f : 0f) - Play) * (1f - (float)Math.Exp(-dt * 1.5f));

            // Bands: real ones blended with a slow, breathing idle pattern.
            float energy = 0f;
            for (int i = 0; i < 64; i++)
            {
                float x = i / 63f;
                float bump = (x - (0.5f + 0.42f * (float)Math.Sin(t * 0.11f))) * 5f;
                float idle = 0.05f + 0.035f * (float)Math.Sin(t * 0.8f + i * 0.37f) + 0.02f * (float)Math.Sin(t * 1.9f - i * 0.11f)
                           + 0.16f * (float)Math.Exp(-bump * bump) * (0.6f + 0.4f * (float)Math.Sin(t * 0.5f + i * 0.2f));
                float b = idle + (a.Bands[i] - idle) * Play;
                Bands[i] = b;
                if (b >= Peaks[i]) Peaks[i] = b;
                else Peaks[i] = Math.Max(b, Peaks[i] - dt * 0.35f);
                energy += b;
            }
            Energy = energy / 64f;
            float bassIdle = 0.10f + 0.06f * (float)Math.Sin(t * 0.7f);
            Bass = bassIdle + (a.Bass - bassIdle) * Play;
            Beat = a.Beat * Play;
            Phase += dt * (0.18f + 1.5f * Energy);
            Hue = (Hue + dt / 600f) % 1f;   // a full turn of the palette every 10 minutes

            BuildWave(a, t, dt);
            OnUpdateViz(f);

            float[] v = K.Values;
            v[OffSP] = Hue; v[OffSP + 1] = Play; v[OffSP + 2] = Bass; v[OffSP + 3] = Beat;
            v[OffSP + 4] = Energy; v[OffSP + 5] = Phase;
            Array.Copy(Bands, 0, v, OffVB, 64);
            Array.Copy(Peaks, 0, v, OffVP, 64);
            Array.Copy(Wave, 0, v, OffVW, 512);
            Array.Copy(Echo, 0, v, OffVE, 512);
            K[0] = Hue;   // marks the buffer dirty
        }

        // A steady waveform: start the window at a rising zero crossing of a low-passed copy so
        // periodic sound stands still, stretch it over 512 samples, then ease it a little.
        void BuildWave(AudioFrame a, float t, float dt)
        {
            float[] src = a.Wave;
            // 8-sample running mean, only used to find the trigger point.
            float run = 0f;
            for (int i = 0; i < src.Length; i++)
            {
                run += src[i];
                if (i >= 8) run -= src[i - 8];
                lowpass[i] = run * 0.125f;
            }
            int trigger = 0;
            for (int i = 9; i < 110; i++)
                if (lowpass[i] <= 0f && lowpass[i + 1] > 0f) { trigger = i - 4; break; }
            if (trigger < 0) trigger = 0;
            const int window = 400;
            float kMain = 1f - (float)Math.Exp(-dt * 40f), kEcho = 1f - (float)Math.Exp(-dt * 7f);
            for (int j = 0; j < 512; j++)
            {
                float s = trigger + j * (window - 1) / 511f;
                int i0 = (int)s;
                float fr = s - i0;
                float real = (src[i0] * (1f - fr) + src[i0 + 1] * fr) * 0.9f;
                float u = j / 511f;
                float idle = 0.12f * (float)Math.Sin(u * 6.2831853f * 3f + t * 1.1f)
                           + 0.07f * (float)Math.Sin(u * 6.2831853f * 7f - t * 1.6f)
                           + 0.035f * (float)Math.Sin(u * 6.2831853f * 17f + t * 2.3f);
                float w = idle + (real - idle) * Play;
                Wave[j] += (w - Wave[j]) * kMain;
                Echo[j] += (w - Echo[j]) * kEcho;
            }
        }

        public override void Render(RenderView view)
        {
            K.Bind();
            pass.Draw();
        }
    }
}
