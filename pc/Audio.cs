using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LeanMic;

/// <summary>
/// Thread-safe adaptive jitter buffer: the network thread writes 16-bit PCM, the audio thread reads floats.
///
/// Latency control:
///  * Target delay starts low (USB 30 ms, Wi-Fi 50 ms). Each underrun (late packets) raises it by 20 ms.
///  * Every 10 s, if the buffer never dipped below a safety margin, the target shrinks by that spare
///    headroom (at most 5 ms per step) — so it only gets lower when measurements show it's safe.
///  * Playback speed is nudged ±1 % (linear interpolation, inaudible for voice) to hold the buffer at the
///    target, which also absorbs clock drift between phone and PC without clicks.
///  * A backlog more than 150 ms over target (e.g. a burst after a Wi-Fi stall) is cut immediately.
/// </summary>
sealed class JitterBuffer : ISampleProvider
{
    const int StepUpMs = 20, StepDownMs = 5, MarginMs = 8, MaxTargetMs = 300, TrimOverMs = 150, BandMs = 8;
    const long WindowMs = 10_000;

    readonly object gate = new();
    readonly float[] ring;
    readonly int rate, msSamples;
    int head, count;
    double frac;                // fractional read position between ring[head] and the next sample
    double avg;                 // smoothed buffer level, in samples
    bool primed;
    float peak;
    int targetMs, floorMs;
    long windowStart;
    int windowMin;              // lowest buffer level (samples) seen at a callback in this window
    int underruns;

    public volatile float Volume = 1f;
    public volatile bool Muted;

    public JitterBuffer(int sampleRate)
    {
        rate = sampleRate;
        msSamples = sampleRate / 1000;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        ring = new float[sampleRate * 2];      // 2 s of headroom
        Reset(usb: false);
    }

    public WaveFormat WaveFormat { get; }

    public int BufferedMs { get { lock (gate) return count / msSamples; } }
    public int TargetMs { get { lock (gate) return targetMs; } }
    public int Underruns { get { lock (gate) return underruns; } }

    /// <summary>Peak level (0..1, after volume, before mute) since the last call.</summary>
    public float TakePeak() { lock (gate) { var p = peak; peak = 0; return p; } }

    /// <summary>Empty the buffer and set latency defaults for a new connection.</summary>
    public void Reset(bool usb)
    {
        lock (gate)
        {
            count = 0; frac = 0; primed = false; underruns = 0;
            floorMs = usb ? 20 : 30;
            targetMs = usb ? 30 : 50;
            avg = targetMs * msSamples;
            StartWindow(Environment.TickCount64);
        }
    }

    void StartWindow(long now) { windowStart = now; windowMin = int.MaxValue; }

    /// <summary>Drop buffered audio (stream ended) without counting it as an underrun.</summary>
    public void Clear() { lock (gate) { count = 0; frac = 0; primed = false; } }

    /// <summary>Appends samples (-1..1).</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (gate)
        {
            int len = ring.Length;
            foreach (float s in samples)
            {
                if (count == len) { head = (head + 1) % len; count--; }
                ring[(head + count) % len] = s;
                count++;
            }
            int target = targetMs * msSamples;
            if (count > target + TrimOverMs * msSamples)
            {
                int drop = count - target;
                head = (head + drop) % len;
                count -= drop;
                avg = target;
            }
        }
    }

    public int Read(float[] buffer, int offset, int n)
    {
        float vol = Volume;
        bool muted = Muted;
        int i = 0;
        lock (gate)
        {
            long now = Environment.TickCount64;
            int target = targetMs * msSamples;
            if (!primed && count >= target) { primed = true; avg = count; StartWindow(now); }
            if (primed)
            {
                if (count < windowMin) windowMin = count;
                avg += (count - avg) * 0.05;   // ~200 ms smoothing at 10 ms callbacks
                double step = avg > target + BandMs * msSamples ? 1.01
                            : avg < target - BandMs * msSamples ? 0.99
                            : 1.0;
                int len = ring.Length;
                float pk = peak;
                for (; i < n && count >= 2; i++)
                {
                    float a = ring[head], b = ring[(head + 1) % len];
                    float v = Math.Clamp((a + (b - a) * (float)frac) * vol, -1f, 1f);
                    float abs = Math.Abs(v);
                    if (abs > pk) pk = abs;
                    buffer[offset + i] = muted ? 0f : v;
                    frac += step;
                    while (frac >= 1.0) { frac -= 1.0; head = (head + 1) % len; count--; }
                }
                peak = pk;

                if (i < n)
                {
                    // Underrun: packets arrived late. Re-prime with a larger safety margin.
                    primed = false;
                    underruns++;
                    targetMs = Math.Min(targetMs + StepUpMs, MaxTargetMs);
                }
                else if (now - windowStart >= WindowMs)
                {
                    int spareMs = windowMin / msSamples - MarginMs;
                    if (spareMs > 0 && targetMs > floorMs)
                        targetMs = Math.Max(targetMs - Math.Min(spareMs, StepDownMs), floorMs);
                    StartWindow(now);
                }
            }
        }
        for (; i < n; i++) buffer[offset + i] = 0f;
        return n;
    }
}

/// <summary>Copies a mono stream onto every output channel.</summary>
sealed class MonoToMulti : ISampleProvider
{
    readonly ISampleProvider src;
    readonly int channels;
    float[] tmp = [];

    public MonoToMulti(ISampleProvider source, int channels)
    {
        src = source;
        this.channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / channels;
        if (tmp.Length < frames) tmp = new float[frames];
        int got = src.Read(tmp, 0, frames);
        for (int f = 0; f < got; f++)
            for (int c = 0; c < channels; c++)
                buffer[offset + f * channels + c] = tmp[f];
        return got * channels;
    }
}

/// <summary>Plays the phone stream into the chosen output device (normally VB-Cable "CABLE Input").</summary>
sealed class AudioEngine : IDisposable
{
    readonly MMDeviceEnumerator enumerator = new();
    WasapiOut? output;
    string? deviceId;
    int rate = 48000;
    bool usb;
    bool dead;                  // output stopped on its own (device reset/unplugged/sleep); needs a rebuild
    long lastHealAttempt;
    float volume = 1f;
    bool muted;
    volatile JitterBuffer? buffer;
    volatile string? error;

    // Network-thread input path (guarded by writeGate).
    readonly object writeGate = new();
    float[] pcm = new float[2048];
    NoiseSuppressor? denoiser;
    JitterBuffer? emitTarget;
    readonly Action<float[], int> emitToBuffer;
    bool noiseSuppression;

    public AudioEngine() => emitToBuffer = (f, n) => emitTarget?.Write(f.AsSpan(0, n));

    public JitterBuffer? Buffer { get => buffer; private set => buffer = value; }
    public string? Error { get => error; private set => error = value; }

    /// <summary>Speech probability of the latest 10 ms frame, or -1 when suppression isn't running.</summary>
    public float VoiceProbability => denoiser?.VoiceProbability ?? -1f;

    /// <summary>Why AI noise suppression can't run, or null.</summary>
    public string? NoiseSuppressionError { get; private set; }

    /// <summary>RNNoise-based cleanup of the phone audio (48 kHz streams only).</summary>
    public bool NoiseSuppression
    {
        get => noiseSuppression;
        set { noiseSuppression = value; ResetDenoiser(); }
    }

    /// <summary>(Re)creates the suppressor with fresh state, or removes it when switched off.</summary>
    void ResetDenoiser()
    {
        lock (writeGate)
        {
            denoiser?.Dispose();
            denoiser = null;
            if (!noiseSuppression || rate != NoiseSuppressor.SampleRate) return;
            try
            {
                denoiser = new NoiseSuppressor();
                NoiseSuppressionError = null;
            }
            catch (Exception e)
            {
                NoiseSuppressionError = "Noise suppression unavailable: " + e.Message;
                Log.Write(NoiseSuppressionError);
            }
        }
    }

    public float Volume { get => volume; set { volume = value; if (Buffer != null) Buffer.Volume = value; } }
    public bool Muted { get => muted; set { muted = value; if (Buffer != null) Buffer.Muted = value; } }

    // All methods below run on the UI thread (NAudio raises PlaybackStopped there too).

    public void SetDevice(string? id) { deviceId = id; Rebuild(); }

    /// <summary>Prepare for a newly connected phone.</summary>
    public void BeginStream(int sampleRate, bool usb)
    {
        this.usb = usb;
        if (sampleRate != rate || output == null || dead)
        {
            rate = sampleRate;
            Rebuild();
        }
        Buffer?.Reset(usb);
        ResetDenoiser();   // fresh noise model per connection (also handles a sample-rate change)
    }

    /// <summary>Called periodically: reopens the output if it died or failed to open (retries every 3 s).</summary>
    public void Heal()
    {
        if (deviceId == null || (output != null && !dead)) return;
        long now = Environment.TickCount64;
        if (now - lastHealAttempt < 3000) return;
        lastHealAttempt = now;
        Rebuild();
    }

    /// <summary>Called on the network thread with 16-bit little-endian PCM.</summary>
    public void Write(byte[] data, int offset, int count)
    {
        lock (writeGate)
        {
            var jb = Buffer;
            if (jb == null) return;
            int n = count / 2;
            if (pcm.Length < n) pcm = new float[n];
            for (int i = 0; i < n; i++)
                pcm[i] = (short)(data[offset + 2 * i] | (data[offset + 2 * i + 1] << 8)) / 32768f;

            if (denoiser != null)
            {
                emitTarget = jb;
                denoiser.Process(pcm.AsSpan(0, n), emitToBuffer);
            }
            else jb.Write(pcm.AsSpan(0, n));
        }
    }

    void Rebuild()
    {
        StopOutput();
        dead = false;
        if (deviceId == null) { Buffer = null; Error = null; return; }
        WasapiOut? o = null;
        try
        {
            // Look the device up again each time: a stored MMDevice goes stale after a device reset.
            var device = enumerator.GetDevice(deviceId);
            var jb = new JitterBuffer(rate) { Volume = volume, Muted = muted };
            jb.Reset(usb);
            var mix = device.AudioClient.MixFormat;
            ISampleProvider chain = jb;
            if (mix.SampleRate != rate) chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);
            if (mix.Channels > 1) chain = new MonoToMulti(chain, mix.Channels);

            o = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
            var self = o;
            o.PlaybackStopped += (_, e) =>
            {
                if (e.Exception == null || output != self) return;
                dead = true;
                Error = "Output stopped (" + e.Exception.Message + "). Reconnecting…";
                Log.Write(Error);
            };
            o.Init(chain);
            o.Play();
            output = o;
            Buffer = jb;
            Error = null;
            Log.Write($"Output: {device.FriendlyName}, mix {mix.SampleRate} Hz x{mix.Channels}, source {rate} Hz");
        }
        catch (Exception e)
        {
            try { o?.Dispose(); } catch { }
            Buffer = null;
            Error = "Can't open output device: " + e.Message;
            Log.Write(Error);
        }
    }

    void StopOutput()
    {
        if (output == null) return;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
        output = null;
    }

    public void Dispose()
    {
        StopOutput();
        lock (writeGate) { denoiser?.Dispose(); denoiser = null; }
        enumerator.Dispose();
    }
}
