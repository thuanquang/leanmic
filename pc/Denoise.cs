using System.Runtime.InteropServices;

namespace LeanMic;

/// <summary>
/// Voice cleanup for 48 kHz mono audio, in three stages:
///  1. 90 Hz low-cut filter: removes hum, fan rumble and desk thumps (RNNoise barely touches these).
///  2. RNNoise (https://github.com/xiph/rnnoise) neural noise suppression.
///  3. Voice gate driven by RNNoise's speech probability: between words, remaining noise is turned down
///     a further 20 dB. Opens immediately on speech, holds 300 ms, then fades, so word endings survive.
/// Takes samples (-1..1) in any chunk size and emits cleaned 10 ms frames. Adds ~10 ms of latency.
/// Not thread-safe; the caller serialises access.
/// </summary>
sealed partial class NoiseSuppressor : IDisposable
{
    public const int SampleRate = 48000;
    const int Frame = 480;              // 10 ms at 48 kHz, fixed by RNNoise
    const float Scale = 32768f;         // RNNoise expects 16-bit sample range

    // Voice gate tuning.
    const float VoiceThreshold = 0.5f;  // RNNoise speech probability that opens the gate
    const int HoldFrames = 30;          // keep open 300 ms after the last speech frame
    const float FloorGain = 0.1f;       // -20 dB when closed
    const float ReleasePerFrame = 0.85f;// fade toward the floor over ~150 ms

    readonly float[] input = new float[Frame];
    readonly float[] output = new float[Frame];
    readonly Biquad lowCut = Biquad.HighPass(90, SampleRate);
    IntPtr state;
    int fill;
    int hold;
    float gate = 1f;

    [LibraryImport("rnnoise")] private static partial IntPtr rnnoise_create(IntPtr model);
    [LibraryImport("rnnoise")] private static partial void rnnoise_destroy(IntPtr st);
    [LibraryImport("rnnoise")] private static unsafe partial float rnnoise_process_frame(IntPtr st, float* outFrame, float* inFrame);

    /// <summary>Throws (DllNotFoundException etc.) if the native library can't be loaded.</summary>
    public NoiseSuppressor()
    {
        state = rnnoise_create(IntPtr.Zero);   // built-in model
        if (state == IntPtr.Zero) throw new InvalidOperationException("rnnoise_create failed");
    }

    /// <summary>Speech probability (0..1) of the most recent frame.</summary>
    public float VoiceProbability { get; private set; }

    /// <summary>Feeds samples (-1..1). Each completed 10 ms frame is passed to <paramref name="emit"/>.</summary>
    public unsafe void Process(ReadOnlySpan<float> samples, Action<float[], int> emit)
    {
        ObjectDisposedException.ThrowIf(state == IntPtr.Zero, this);
        foreach (float s in samples)
        {
            input[fill++] = lowCut.Process(s) * Scale;
            if (fill < Frame) continue;
            fill = 0;

            float vad;
            fixed (float* i = input, o = output)
                vad = rnnoise_process_frame(state, o, i);
            VoiceProbability = vad;

            // Gate: ramp the gain smoothly across the frame from the previous value to the new one.
            float from = gate;
            if (vad >= VoiceThreshold) { hold = HoldFrames; gate = 1f; }
            else if (hold > 0) hold--;
            else gate = Math.Max(FloorGain, gate * ReleasePerFrame);
            float step = (gate - from) / Frame;

            for (int k = 0; k < Frame; k++)
                output[k] = output[k] / Scale * (from + step * k);
            emit(output, Frame);
        }
    }

    public void Dispose()
    {
        if (state == IntPtr.Zero) return;
        rnnoise_destroy(state);
        state = IntPtr.Zero;
    }

    /// <summary>The low-cut stage on its own (for tests and comparisons).</summary>
    public static float[] LowCutOnly(ReadOnlySpan<float> x)
    {
        var f = Biquad.HighPass(90, SampleRate);
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = f.Process(x[i]);
        return y;
    }

    /// <summary>Second-order (12 dB/octave) Butterworth filter, direct form I.</summary>
    sealed class Biquad
    {
        readonly float b0, b1, b2, a1, a2;
        float x1, x2, y1, y2;

        Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            this.b0 = (float)(b0 / a0); this.b1 = (float)(b1 / a0); this.b2 = (float)(b2 / a0);
            this.a1 = (float)(a1 / a0); this.a2 = (float)(a2 / a0);
        }

        public static Biquad HighPass(double cutoffHz, double rate)
        {
            double w = 2 * Math.PI * cutoffHz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public float Process(float x)
        {
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }
}
