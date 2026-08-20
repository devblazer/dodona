namespace Dodona;

/// <summary>
/// The audio arithmetic for dictation that is worth having outside the device half
/// (docs/VOICE-ENGINE-PLAN.md E2) — no device, no NAudio, no WPF, so it lands on the ~1 second
/// `dev test unit` loop.
///
/// **WHAT THIS IS FOR, AND WHAT IT DELIBERATELY IS NOT.** The plan's loudest trap: *"The resample
/// to 16 kHz is not optional, and getting it wrong sounds EXACTLY like the gibberish you are
/// replacing. If the first end-to-end test sounds wrong, suspect the sample rate before the
/// engine."* A wrong rate is not a crash and not a red check — it is confident nonsense that
/// looks like a bad speech engine.
///
/// The obvious response is to write the resampler here, pure, and unit-test it. **That was tried
/// and reverted** (D-E9): the engine is deliberately mimicking Claude Code's extension because
/// that approach is known to work, and the extension never resamples in its own code — its native
/// addon captures at 16 kHz and its `rec`/`arecord` fallbacks are handed `-r 16000`, so a proven
/// implementation always does the conversion. Hand-rolled DSP in that exact position is unproven
/// arithmetic in the one place where a bug is indistinguishable from the failure being fixed.
/// <see cref="AudioCapture"/> uses `MediaFoundationResampler` instead.
///
/// So this file does not convert rates. It **measures** them: <see cref="DominantHz"/> reads the
/// frequency of whatever the real capture path produced, so a 440 Hz tone that arrives as 1320 Hz
/// is the chipmunk failure caught arithmetically — with no microphone, no voice and no ear. That
/// is the difference between a trap written down and a trap enforced (§0.3: a written warning is
/// not a fix).
/// </summary>
public static class Pcm16
{
    /// <summary>What the endpoint is told, so it is what must be sent: plan §2's
    /// `sample_rate=16000`, `channels=1`, `encoding=linear16`.</summary>
    public const int Rate = 16000;

    /// <summary>20 ms at 16 kHz mono 16-bit = 320 samples = 640 bytes. The extension streams
    /// continuously and ~20-100 ms per frame is sane; 20 ms keeps the latency floor well under
    /// the ~300 ms budget `endpointing_ms=300` sets.</summary>
    public const int FrameSamples = Rate / 50;
    public const int FrameBytes = FrameSamples * 2;

    /// <summary>
    /// Float samples in [-1,1] to signed 16-bit little-endian — `linear16`. Used to build the
    /// synthetic tone the rate check pushes through the real capture path.
    ///
    /// **CLAMPED, not wrapped.** An unclamped cast turns a sample just past full scale into a
    /// large NEGATIVE number, so a loud syllable becomes a burst of noise exactly where the
    /// speech is. A headset with its gain up reaches this on ordinary speech, not only on a shout.
    /// </summary>
    public static byte[] ToBytes(ReadOnlySpan<float> samples)
    {
        var outp = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i];
            if (float.IsNaN(v)) v = 0f;
            var s = (int)MathF.Round(v * 32767f);
            if (s > short.MaxValue) s = short.MaxValue;
            if (s < short.MinValue) s = short.MinValue;
            outp[i * 2] = (byte)(s & 0xFF);
            outp[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return outp;
    }

    /// <summary>Signed 16-bit little-endian back to floats — what lets a check read a frame the
    /// capture path produced.</summary>
    public static float[] ToFloats(ReadOnlySpan<byte> pcm)
    {
        var outp = new float[pcm.Length / 2];
        for (var i = 0; i < outp.Length; i++)
        {
            // §0.2's `-shl on a byte` trap has a C# sibling: forgetting the cast to short turns
            // every negative sample positive, which is full-scale distortion that still has the
            // right RMS.
            var s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            outp[i] = s / 32768f;
        }
        return outp;
    }

    /// <summary>Root-mean-square level, 0 for silence and ~1 for full scale. The one honest thing
    /// a session with no ears can say about a captured buffer: whether anything was in it.</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>A tone of a known frequency, for the rate check. Not test-only code hiding in
    /// production: it is the generator half of the measurement <see cref="DominantHz"/> makes, and
    /// splitting the pair across a project boundary is how one of them goes stale.</summary>
    public static float[] Tone(double hz, int rate, double seconds, double amplitude = 0.5)
    {
        var n = (int)(rate * seconds);
        var outp = new float[n];
        for (var i = 0; i < n; i++)
            outp[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return outp;
    }

    /// <summary>
    /// The strongest frequency present, by the Goertzel algorithm over a candidate sweep.
    ///
    /// Goertzel rather than a full FFT because the question is narrow — "is the tone still at the
    /// frequency I put in?" — and a single-bin evaluator has nowhere to hide a bug where a
    /// hand-rolled FFT has several. Accurate to the sweep's step, which is all this needs: the
    /// failure it looks for moves 440 Hz to 1320 Hz.
    /// </summary>
    public static double DominantHz(ReadOnlySpan<float> samples, int rate, double lowHz = 50,
                                    double highHz = 4000, double stepHz = 5)
    {
        var best = 0.0;
        var bestPower = -1.0;
        for (var hz = lowHz; hz <= highHz; hz += stepHz)
        {
            var power = Goertzel(samples, rate, hz);
            if (power > bestPower) { bestPower = power; best = hz; }
        }
        return best;
    }

    static double Goertzel(ReadOnlySpan<float> samples, int rate, double hz)
    {
        var w = 2 * Math.Cos(2 * Math.PI * hz / rate);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + w * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - w * s1 * s2;
    }
}
