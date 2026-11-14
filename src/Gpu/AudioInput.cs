// Audio for the music visualizers. Scenes never talk to an audio API: the host reads one
// IAudioSource per frame into an AudioFrame (smoothed bands, falling peaks, waveform, bass, beat)
// and hands it to every scene, on the CPU (FrameContext.Audio) and on the GPU (cbuffer AudioData).
//
// Wiring real audio: the WASAPI loopback capture (AudioFeed, branch feat/music-vis) plugs in at
// AudioInput.Open(). Until then a synthetic source keeps the visualizers moving.
using System;

namespace IdleVeil
{
    interface IAudioSource : IDisposable
    {
        // Copies the newest analysis into raw (64 bands 0..1, 512 wave samples -1..1).
        // Returns true while something is playing.
        bool Read(float[] bands, float[] wave);
    }

    static class AudioInput
    {
        // The one line to change when the real capture lands: return a source wrapping AudioFeed.
        public static IAudioSource Open() { return new SyntheticAudio(); }
    }

    // What scenes see. Bands rise fast and fall slowly; peaks hold for a moment, then drop.
    sealed class AudioFrame
    {
        public const int BandCount = 64, WaveCount = 512;
        public const int CbFloats = BandCount * 2 + WaveCount;   // cbuffer AudioData

        public readonly float[] Bands = new float[BandCount];
        public readonly float[] Peaks = new float[BandCount];
        public readonly float[] Wave = new float[WaveCount];
        public float Bass, Energy, Beat;   // 0..1; Beat jumps to 1 on a kick and decays
        public bool Playing;
        public readonly float[] Cb = new float[CbFloats];

        readonly float[] raw = new float[BandCount];
        readonly float[] peakHold = new float[BandCount];
        float bassAvg, lastBeat = 10f;
        float silentFor;

        public void Update(IAudioSource src, float dt)
        {
            bool playing = false;
            if (src != null)
            {
                try { playing = src.Read(raw, Wave); }
                catch (Exception ex) { Program.Log("audio read failed: " + ex.Message); playing = false; }
            }
            silentFor = playing ? 0f : silentFor + dt;
            Playing = playing || silentFor < 1.5f;   // ride over short gaps between tracks
            if (!playing) { for (int i = 0; i < BandCount; i++) raw[i] = 0f; Array.Clear(Wave, 0, WaveCount); }

            float up = 1f - (float)Math.Exp(-dt * 30f), down = 1f - (float)Math.Exp(-dt * 6f);
            float energy = 0f;
            for (int i = 0; i < BandCount; i++)
            {
                float v = raw[i] < 0f ? 0f : raw[i] > 1f ? 1f : raw[i];
                Bands[i] += (v - Bands[i]) * (v > Bands[i] ? up : down);
                if (Bands[i] >= Peaks[i]) { Peaks[i] = Bands[i]; peakHold[i] = 0.5f; }
                else if ((peakHold[i] -= dt) < 0f) Peaks[i] = Math.Max(Bands[i], Peaks[i] - dt * 0.45f);
                energy += Bands[i];
            }
            Energy = energy / BandCount;
            float bass = (Bands[0] + Bands[1] + Bands[2] + Bands[3] + Bands[4] + Bands[5]) / 6f;
            // A kick: bass well above its running average, at most ~4 per second.
            bassAvg += (bass - bassAvg) * (1f - (float)Math.Exp(-dt * 2f));
            lastBeat += dt;
            if (bass > 0.25f && bass > bassAvg * 1.35f && lastBeat > 0.25f) { Beat = 1f; lastBeat = 0f; }
            else Beat *= (float)Math.Exp(-dt * 5f);
            Bass = bass;
        }

        public void Pack()
        {
            Array.Copy(Bands, 0, Cb, 0, BandCount);
            Array.Copy(Peaks, 0, Cb, BandCount, BandCount);
            Array.Copy(Wave, 0, Cb, BandCount * 2, WaveCount);
        }
    }

    // A made-up track at 118 bpm: kick, off-beat hats, a wandering bass line and pads, so the
    // visualizers can be developed and render-tested without anything playing.
    sealed class SyntheticAudio : IAudioSource
    {
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly FastRandom rng = new FastRandom(0x1234567);
        public double TimeOverride = -1;   // render test: drive by simulated time

        public bool Read(float[] bands, float[] wave)
        {
            double t = TimeOverride >= 0 ? TimeOverride : clock.Elapsed.TotalSeconds;
            double beat = t * 118.0 / 60.0;
            float ph = (float)(beat - Math.Floor(beat));
            float kick = (float)Math.Exp(-ph * 9.0);
            float hat = (float)Math.Exp(-Frac(beat + 0.5) * 22.0);
            float bar = (float)(beat / 4.0);
            float swell = 0.55f + 0.45f * (float)Math.Sin(bar * 0.9);
            for (int i = 0; i < bands.Length; i++)
            {
                float x = i / (float)(bands.Length - 1);
                float low = kick * (float)Math.Exp(-x * 14.0) * 0.95f;
                float bassLine = 0.45f * (float)Math.Exp(-Math.Pow((x - 0.12 - 0.05 * Math.Sin(bar * 2.1)) * 9.0, 2));
                float mids = swell * 0.42f * (0.6f + 0.4f * (float)Math.Sin(x * 17.0 + t * 1.7)) * (float)Math.Exp(-Math.Pow((x - 0.42) * 2.6, 2));
                float highs = hat * 0.5f * (float)Math.Exp(-Math.Pow((x - 0.82) * 4.0, 2));
                float tilt = 1f - 0.55f * x;
                bands[i] = Math.Min(1f, (low + bassLine + mids + highs) * tilt + rng.Next() * 0.06f);
            }
            for (int i = 0; i < wave.Length; i++)
            {
                double s = i / (double)wave.Length;
                wave[i] = (float)(0.5 * kick * Math.Sin(s * 6.283 * 3 + t * 9)
                                + 0.25 * swell * Math.Sin(s * 6.283 * 11 + t * 3)
                                + 0.12 * Math.Sin(s * 6.283 * 37 - t * 5));
            }
            return true;
        }

        static double Frac(double v) { return v - Math.Floor(v); }

        public void Dispose() { }
    }
}
