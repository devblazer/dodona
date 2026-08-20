using System.IO;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The microphone, turned into exactly what plan §2 says the endpoint is told to expect:
/// **16 kHz, mono, signed 16-bit little-endian PCM**, in 20 ms frames (docs/VOICE-ENGINE-PLAN.md
/// E2).
///
/// None of the extension's capture code transferred — it ships a per-platform
/// `audio-capture.node` addon with a fallback ladder to `rec` (sox) and `arecord`, and Dodona is
/// .NET/WPF. This half is new work, and it is the half where the plan's loudest trap lives:
///
/// > *The resample to 16 kHz is not optional, and getting it wrong sounds EXACTLY like the
/// > gibberish you are replacing.*
///
/// ══ THE RESAMPLE IS DONE BY A PROVEN LIBRARY, NOT BY US (D-E9) ══
///
/// A first cut of this file hand-rolled a windowed-sinc resampler, so that the rate conversion
/// could be a pure unit check. **That was the wrong trade and it was reverted.** The extension —
/// which is the thing this engine is deliberately mimicking, because that approach is known to
/// work — never resamples in its own code: the native addon captures at 16 kHz, and the `rec` and
/// `arecord` fallbacks are handed `-r 16000` so that a proven implementation does the conversion.
/// Hand-rolled DSP in this exact position is the worst place in the feature to put unproven
/// arithmetic, because a subtle bug there is **not** a crash and **not** a red check — it is
/// confident nonsense, indistinguishable from the SAPI gibberish this whole document exists to
/// replace, and it would be diagnosed as "the cloud engine is bad too".
///
/// So: <see cref="MediaFoundationResampler"/> does rate AND channel conversion in one step,
/// straight to `16 kHz / mono / 16-bit`, which is the wire format. Testability is bought back
/// differently and without inventing DSP — <see cref="Pcm16.DominantHz"/> measures the frequency
/// of what comes OUT of this real path, so a 440 Hz tone that arrives as 1320 Hz is the chipmunk
/// failure caught arithmetically rather than by ear.
///
/// **THERE IS STILL ONE CODE PATH AND IT ALWAYS CONVERTS.** The other tempting shortcut is to ask
/// `WaveInEvent` for 16 kHz directly and let the driver cope — which works on most devices, fails
/// on some, and leaves the fallback as the untested path where the bug then lives. This captures
/// the device's native format and always runs the resampler, so the path a check exercises is the
/// path the operator gets.
///
/// Failure never throws at the caller: <see cref="IRecognizer.Start"/>'s contract is that a
/// missing, muted or already-busy microphone is an ordinary Tuesday that arrives as words.
/// </summary>
sealed class AudioCapture : IDisposable
{
    /// <summary>One 640-byte frame of 16 kHz mono `linear16`, ready to go on the wire as a
    /// BINARY WebSocket frame. **No WAV header** — plan §2: a header gets transcribed as
    /// noise.</summary>
    public event Action<byte[]>? Frame;

    /// <summary>A reason in words, never an exception and never an error code (§7).</summary>
    public event Action<string>? Failed;

    /// <summary>The format the endpoint is promised, in one place: plan §2's
    /// `encoding=linear16, sample_rate=16000, channels=1`.</summary>
    public static WaveFormat WireFormat => new(Pcm16.Rate, 16, 1);

    WasapiCapture? _capture;
    BufferedWaveProvider? _buffer;
    MediaFoundationResampler? _resampler;
    MediaFoundationReader? _fileReader;
    CancellationTokenSource? _pumpCts;
    Task? _pump;

    static bool _mfStarted;
    static readonly object MfLock = new();

    /// <summary>What device was opened, and its native rate — so a rate problem can be diagnosed
    /// from a dump rather than by ear.</summary>
    public string DeviceName { get; private set; } = "";
    public int DeviceRate { get; private set; }

    /// <summary>
    /// An audio FILE instead of the microphone (D-E14) — the only way anyone without a voice can
    /// answer the question this whole engine exists for: **does it hear?**
    ///
    /// Everything else about dictation is checkable headlessly. Recognition quality is not: it
    /// needs speech, and a session building this unattended has none. That is why plan §7 says
    /// *"what no suite can verify is whether it hears"* and why no word-error-rate number was ever
    /// reported. A recording closes that gap — the operator speaks twenty sentences once, and from
    /// then on it is a **repeatable measurement** rather than a ceremony, which also means the next
    /// engine change can be regression-tested against the same audio instead of re-recorded.
    ///
    /// It earns its place in production code for the reason §5 gives about `ui heard` and D-V15
    /// gives about `DODONA_UI_MIC=fail`: it feeds the REAL path — same resampler, same frame size,
    /// same socket, same keyterms — so what it measures is what the operator gets. A test-only
    /// harness that opened its own socket would be measuring a rehearsal.
    ///
    /// Any format Media Foundation reads (wav, mp3, m4a) and any rate: it is converted the same way
    /// the microphone is. Frames are paced in REAL TIME rather than dumped, because the endpoint's
    /// `endpointing_ms=300` and `utterance_end_ms=1000` are about gaps in speech, and a file
    /// delivered at once is a file with no gaps in it.
    /// </summary>
    static string? WavSource
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("DODONA_STT_WAV");
            return string.IsNullOrWhiteSpace(p) ? null : p.Trim();
        }
    }

    /// <summary>Never throws. Both failures that matter live here: no capture endpoint at all,
    /// and one Windows will not hand over (a call has it, or speech is off in privacy
    /// settings).</summary>
    public bool Start()
    {
        var wav = WavSource;
        if (wav is not null) return StartFromFile(wav);

        try
        {
            // Media Foundation has to be started once per process before a resampler exists.
            // Idempotent and locked: two ArmMic calls racing would otherwise both initialise it.
            lock (MfLock)
            {
                if (!_mfStarted) { MediaFoundationApi.Startup(); _mfStarted = true; }
            }

            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                Failed?.Invoke("no microphone");
                return false;
            }
            // Role.Communications rather than Console: it is the endpoint Windows nominates for
            // speech, which is the headset rather than the desk array when both exist.
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            DeviceName = device.FriendlyName;

            _capture = new WasapiCapture(device);
            DeviceRate = _capture.WaveFormat.SampleRate;

            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                // A second of slack. DiscardOnBufferOverflow rather than a throw: if the pump
                // stalls, dropping the oldest audio is right — a recogniser fed stale audio is
                // transcribing what was said a second ago, into a box the operator has moved on
                // from, which is the submit race with extra steps.
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,

                // ReadFully DEFAULTS TO TRUE AND THAT WOULD HAVE BEEN A REAL BUG. With it on,
                // Read() never returns 0 — it MANUFACTURES SILENCE to fill whatever was asked
                // for. So the pump below would never find the buffer dry, would never sleep, and
                // would spin at CPU speed posting fabricated silent frames to the socket: a hot
                // loop, a wasted core, and an endpoint being told the operator is in a very quiet
                // room forever. Caught by the E2 probe overflowing a List<byte> rather than by
                // reading the docs, which is the only reason it is not in the shipped build.
                ReadFully = false,
            };

            _resampler = new MediaFoundationResampler(_buffer, WireFormat) { ResamplerQuality = 60 };

            _capture.DataAvailable += (_, e) => _buffer!.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _capture.RecordingStopped += (_, e) =>
            {
                // A device that vanishes mid-utterance (the Bluetooth headset walking out of
                // range, §7's first failure) surfaces here and nowhere else. Silence would leave
                // the indicator reading "listening" at a dead microphone.
                if (e.Exception is not null) Failed?.Invoke(Describe(e.Exception));
            };

            _capture.StartRecording();

            _pumpCts = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(Describe(ex));
            return false;
        }
    }

    /// <summary>
    /// The resampler is pull-based and the device is push-based, so something has to do the
    /// pulling. One 20 ms frame at a time, which is what keeps the latency floor well under the
    /// ~300 ms budget `endpointing_ms=300` sets.
    ///
    /// A short sleep when the buffer is dry rather than a spin: this thread exists for as long as
    /// the microphone is armed, and a spinning one would show up as a permanently warm core on a
    /// machine whose whole point is running other people's builds.
    /// </summary>
    async Task PumpAsync(CancellationToken ct)
    {
        var scratch = new byte[Pcm16.FrameBytes * 4];

        // PERSISTENT across reads, and that is not tidiness. A resampler returns whatever it has,
        // which is rarely a whole number of 640-byte frames; a leftover discarded at the end of
        // one iteration and re-read at the start of the next would both lose audio AND shift every
        // subsequent sample by an odd number of bytes — which swaps the low and high halves of
        // every 16-bit sample. That is not a dropout, it is full-scale noise with the right RMS:
        // gibberish, arriving from the correct engine, at the correct rate.
        var carry = new List<byte>(Pcm16.FrameBytes * 8);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // With ReadFully off, 0 means the device has not produced anything yet.
                var n = _resampler!.Read(scratch, 0, scratch.Length);
                if (n > 0)
                {
                    carry.AddRange(new ReadOnlySpan<byte>(scratch, 0, n));
                    while (carry.Count >= Pcm16.FrameBytes)
                    {
                        var frame = carry.GetRange(0, Pcm16.FrameBytes).ToArray();
                        carry.RemoveRange(0, Pcm16.FrameBytes);
                        Frame?.Invoke(frame);
                    }
                }
                else
                {
                    // Dry: let the device fill the buffer. Ten milliseconds is half a frame, so
                    // this never becomes the thing limiting latency against the ~300 ms budget.
                    await Task.Delay(10, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* Stop(); the ordinary way out */ }
        catch (Exception ex)
        {
            // A throw on this thread would otherwise be swallowed and the mic would simply go
            // quiet. Reported, because on-and-deaf must never look like on (§5).
            Failed?.Invoke(Describe(ex));
        }
    }

    /// <summary>The file source. Same resampler, same 20 ms frames, same everything downstream —
    /// only the origin of the samples differs.</summary>
    bool StartFromFile(string path)
    {
        try
        {
            lock (MfLock)
            {
                if (!_mfStarted) { MediaFoundationApi.Startup(); _mfStarted = true; }
            }
            if (!File.Exists(path))
            {
                Failed?.Invoke("the audio file named by DODONA_STT_WAV does not exist");
                return false;
            }

            var reader = new MediaFoundationReader(path);
            _fileReader = reader;
            DeviceName = "file: " + Path.GetFileName(path);
            DeviceRate = reader.WaveFormat.SampleRate;
            _resampler = new MediaFoundationResampler(reader, WireFormat) { ResamplerQuality = 60 };

            _pumpCts = new CancellationTokenSource();
            _pump = Task.Run(() => PumpFileAsync(_pumpCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(Describe(ex));
            return false;
        }
    }

    async Task PumpFileAsync(CancellationToken ct)
    {
        var frame = new byte[Pcm16.FrameBytes];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var got = 0;
                while (got < frame.Length)
                {
                    var n = _resampler!.Read(frame, got, frame.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got == 0) break;                       // end of file
                if (got < frame.Length) Array.Clear(frame, got, frame.Length - got);
                Frame?.Invoke((byte[])frame.Clone());

                // Real time, one frame at a time. Faster would collapse the gaps between
                // sentences, and the gaps are what the server's endpointing reads.
                await Task.Delay(20, ct);
            }

            // ══ THE FILE MUST KEEP BEHAVING LIKE A MICROPHONE AFTER IT RUNS OUT ══
            //
            // Measured on the first real recording: eight sentences went in, four came out. The
            // server had transcribed all eight — the interim stream proved it — but only four
            // `TranscriptEndpoint` frames ever arrived, so the last four never settled and never
            // reached the box. Fourteen seconds of waiting changed nothing.
            //
            // The cause is that ENDPOINTING IS DRIVEN BY AUDIO, not by the clock: the server
            // decides an utterance ended when it hears the configured silence. Simply ceasing to
            // send frames is not silence, it is absence — so the pending text sat there forever.
            // A real microphone never does this, because it keeps streaming quiet room tone for as
            // long as it is armed, which is exactly why this only shows up on the file path.
            //
            // So the file's tail is padded with real silence rather than with nothing. Two seconds
            // is well past `utterance_end_ms=1000`.
            var quiet = new byte[Pcm16.FrameBytes];
            for (var i = 0; i < 100 && !ct.IsCancellationRequested; i++)
            {
                Frame?.Invoke((byte[])quiet.Clone());
                await Task.Delay(20, ct);
            }
            // Deliberately quiet at the end otherwise: the file running out is not a failure, and
            // putting the indicator in `error` here would look like the engine breaking at exactly
            // the moment it finished working.
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failed?.Invoke(Describe(ex)); }
    }

    public void Stop()
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _capture?.StopRecording(); } catch { /* stopping must not be able to fail */ }
        try { _buffer?.ClearBuffer(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _pump?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _pumpCts?.Dispose(); } catch { }
        // The resampler owns nothing of the capture, but it must go before the buffer it reads.
        try { _resampler?.Dispose(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _fileReader?.Dispose(); } catch { }
        _fileReader = null;
        _resampler = null; _buffer = null; _capture = null; _pumpCts = null; _pump = null;
    }

    /// <summary>Words a person can act on, never an HRESULT. Anything unrecognised keeps its own
    /// message rather than being flattened to "an error occurred" — a reason nobody can act on is
    /// the same silent degrade as no reason at all.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        // What WASAPI raises when there is no capture endpoint, or the operator has denied
        // microphone access to desktop apps in privacy settings.
        System.Runtime.InteropServices.COMException => "no microphone, or speech is switched off in Windows settings",
        UnauthorizedAccessException => "Windows denied access to the microphone",
        _ => ex.Message,
    };
}
