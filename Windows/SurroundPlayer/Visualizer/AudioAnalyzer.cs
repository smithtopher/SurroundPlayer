// Port of Sources/SurroundPlayer/Visualizer/AudioAnalyzer.swift. Turns the engine's audio
// tap into what presets react to: smoothed waveforms, a spectrum, and MilkDrop-style
// relative band levels (`bass` ≈ 1 is "normal for this song", 2 is a hit) with slower
// `…Att` versions. The FFT is a plain radix-2 one scaled like vDSP's real FFT (2x DFT).
using System;

namespace SurroundPlayer.Visualizer;

public sealed unsafe class AudioAnalyzer
{
    public const int FftSize = 1024;
    private readonly float[] _raw = new float[FftSize * 2];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _re = new float[FftSize], _im = new float[FftSize];
    private readonly int[] _bitrev = new int[FftSize];
    private readonly float[] _cos = new float[FftSize / 2], _sin = new float[FftSize / 2];

    /// 512 points each, roughly -1…1
    public readonly float[] Left = new float[512], Right = new float[512];
    /// 512 magnitude bins (0…24 kHz), log-compressed to roughly 0…1
    public readonly float[] Spectrum = new float[512];

    public float Bass { get; private set; } = 1;
    public float Mid { get; private set; } = 1;
    public float Treb { get; private set; } = 1;
    public float BassAtt { get; private set; } = 1;
    public float MidAtt { get; private set; } = 1;
    public float TrebAtt { get; private set; } = 1;
    public float Volume { get; private set; }
    public bool Beat { get; private set; }
    public bool HasSignal { get; private set; }

    private readonly float[] _shortE = new float[3], _longE = new float[3];
    private double _lastBeat;

    public AudioAnalyzer()
    {
        // vDSP_HANN_NORM: Hann scaled by sqrt(2/3)
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.8165f * 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / FftSize));
        int bits = 10;
        for (int i = 0; i < FftSize; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _bitrev[i] = r;
        }
        for (int i = 0; i < FftSize / 2; i++)
        {
            _cos[i] = MathF.Cos(2 * MathF.PI * i / FftSize);
            _sin[i] = -MathF.Sin(2 * MathF.PI * i / FftSize);
        }
    }

    /// `fetch` fills interleaved L/R floats and returns frames written (0 = nothing playing).
    public delegate int Fetch(float* lr, int frames);

    public void Update(float dt, double time, Fetch fetch)
    {
        const int n = FftSize;
        int got;
        fixed (float* p = _raw) got = fetch(p, n);
        HasSignal = got == n;
        if (!HasSignal)
        {
            // decay gracefully to silence
            for (int i = 0; i < 512; i++) { Left[i] *= 0.85f; Right[i] *= 0.85f; Spectrum[i] *= 0.9f; }
            Bass += (1 - Bass) * 0.1f; Mid += (1 - Mid) * 0.1f; Treb += (1 - Treb) * 0.1f;
            BassAtt = Bass; MidAtt = Mid; TrebAtt = Treb;
            Volume *= 0.9f;
            Beat = false;
            return;
        }
        // waveforms: decimate 1024 -> 512 with a little smoothing
        for (int i = 0; i < 512; i++)
        {
            int a = i * 4;
            Left[i] = Left[i] * 0.35f + 0.65f * (_raw[a] + _raw[a + 2]) * 0.5f;
            Right[i] = Right[i] * 0.35f + 0.65f * (_raw[a + 1] + _raw[a + 3]) * 0.5f;
        }
        // spectrum (vDSP's real FFT returns 2x the DFT, hence the factor below)
        for (int i = 0; i < n; i++)
        {
            _re[_bitrev[i]] = (_raw[i * 2] + _raw[i * 2 + 1]) * 0.5f * _window[i];
            _im[_bitrev[i]] = 0;
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1, step = n / size;
            for (int start = 0; start < n; start += size)
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = _sin[k * step];
                    int i0 = start + k, i1 = i0 + half;
                    float tr = _re[i1] * wr - _im[i1] * wi, ti = _re[i1] * wi + _im[i1] * wr;
                    _re[i1] = _re[i0] - tr; _im[i1] = _im[i0] - ti;
                    _re[i0] += tr; _im[i0] += ti;
                }
        }
        Span<float> energy = stackalloc float[3];
        for (int i = 1; i < 512; i++)
        {
            float mag = 2 * MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
            float m = mag / n;
            float e = m * m;
            // 46.9 Hz bins: bass < ~280 Hz, mid < ~2.3 kHz, treble < ~8 kHz
            if (i <= 6) energy[0] += e; else if (i <= 48) energy[1] += e; else if (i <= 170) energy[2] += e;
            Spectrum[i] = MathF.Min(1, MathF.Log10(1 + m * 400) * 0.5f);
        }
        Spectrum[0] = 0;
        float vol = 0;
        for (int i = 0; i < n; i++) vol += _raw[i * 2] * _raw[i * 2];
        Volume = MathF.Min(2, MathF.Sqrt(vol / n) * 4);

        // relative levels
        float fast = MathF.Min(1, dt * 18), slow = MathF.Min(1, dt * 0.35f), att = MathF.Min(1, dt * 2.5f);
        Span<float> rel = stackalloc float[3];
        for (int b = 0; b < 3; b++)
        {
            _shortE[b] += (energy[b] - _shortE[b]) * fast;
            if (_longE[b] < 1e-9f) _longE[b] = energy[b];   // first signal: start at "normal"
            _longE[b] += (energy[b] - _longE[b]) * slow;
            rel[b] = _longE[b] > 1e-9f ? MathF.Min(4, MathF.Sqrt(_shortE[b] / _longE[b])) : 1;
        }
        Bass = rel[0]; Mid = rel[1]; Treb = rel[2];
        BassAtt += (Bass - BassAtt) * att;
        MidAtt += (Mid - MidAtt) * att;
        TrebAtt += (Treb - TrebAtt) * att;
        Beat = Bass > 1.45f && Bass > BassAtt * 1.25f && time - _lastBeat > 0.25;
        if (Beat) _lastBeat = time;
    }
}
