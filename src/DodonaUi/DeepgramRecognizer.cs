using System.Net.WebSockets;
using System.Text;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The real engine: **Deepgram Nova-3, over Anthropic's speech WebSocket** — the engine Claude
/// Code's own VS Code extension uses (docs/VOICE-ENGINE-PLAN.md, D-E1).
///
/// It replaces `SapiRecognizer`, which is DELETED rather than kept as an offline fallback (D-E6):
/// SAPI shipped, the operator spoke to it, and it produced not wrong words but *gibberish* — and
/// a fallback that emits gibberish is worse than an error saying "no network", because gibberish
/// looks like the feature working badly rather than not working.
///
/// The operator's position on depending on an undocumented internal endpoint, verbatim: *"If the
/// extension can use it, we can use it. They have the exact same risk exposure, etcetera."* The
/// risk is **accepted explicitly by the person who owns the machine**. If it breaks that is the
/// accepted term, not an incident: record it and fall back to plan §10.
///
/// ══ WHAT SPIKE E1 MEASURED, because two of the plan's protocol claims were wrong ══
///
/// Measured against the live endpoint on 2026-08-20:
///
/// 1. **The upgrade authenticates NOTHING.** It returns `101` with no `Authorization` header at
///    all, and `101` again for a syntactically valid but wrong bearer. Plan §2's classification —
///    regex the upgrade for a status and treat 4xx as fatal — therefore cannot work here, because
///    there is no 4xx to see. Auth is refused one frame later, as an `error` frame followed by a
///    close with code **1008** and the reason "Invalid authorization". A recogniser that trusted
///    the handshake would have sat in `Listening` at a rejected credential, deaf and silent —
///    §0.3's believed-a-green-check, one layer down in the protocol.
/// 2. **The error text is NESTED**, under `error.message`, not at the top level as §2 documents.
///    Reading only the documented shape yields an error state with no words in it, which breaks
///    "on and deaf must never look like on" (§5) exactly as thoroughly as a wrong colour would.
///
/// Both corrections live in <see cref="SpeechStream"/>, on the pure side, where a check can reach
/// them without a socket.
///
/// ══ THE EPOCH IS STAMPED AT UTTERANCE START, NOT AT DELIVERY ══
///
/// Inherited verbatim from `SapiRecognizer`, because the reasoning is the engine-independent half
/// and it is the one bug here a person would find baffling. The race (§4): the operator finishes
/// speaking, presses Enter, and the engine THEN delivers the tail — so a result stamped at
/// delivery would carry the NEW epoch, the guard would never fire, and the tail of a sent message
/// would open the next one. An utterance begun before the submit belongs to the message that was
/// sent. The first interim of a phrase is the utterance's start, so that is where it is stamped.
/// </summary>
sealed class DeepgramRecognizer : IRecognizer
{
    const string DefaultEndpoint = "wss://api.anthropic.com/api/ws/speech_to_text/voice_stream";

    /// <summary>
    /// Where to connect. Overridable for two unrelated and both good reasons:
    ///
    /// - **a suite can test a DEAD NETWORK without touching one.** Pointed at `ws://127.0.0.1:1/`
    ///   the connect is refused instantly by the loopback stack, which exercises the real socket
    ///   failure path with no egress, no credential and no cost — so D-E5's "no suite ever opens a
    ///   socket to the operator's endpoint" survives intact while the failure it cares about is
    ///   still genuinely reachable. `voice:a_dead_network_reads_as_error_not_listening` is that.
    /// - **plan §10's fallback 1** is Deepgram directly with the operator's own key, where
    ///   "everything in §2 stays true except the URL and the auth header". This is the URL half,
    ///   already in place if that day comes.
    /// </summary>
    static string Endpoint
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("DODONA_STT_ENDPOINT");
            return string.IsNullOrWhiteSpace(over) ? DefaultEndpoint : over.Trim();
        }
    }

    /// <summary>Plan §2 (`imr`): on open, and then every 8000 ms.</summary>
    const int KeepAliveMs = 8000;

    /// <summary>How long the socket must survive unobjected-to before it counts as authenticated
    /// (see the note at the grace timer). E1 measured the refusal arriving as one frame, well
    /// inside a second; this is generous against that.</summary>
    const int AuthGraceMs = 1500;

    public event Action<Dictation.Heard>? Heard;
    public event Action<string>? Failed;

    /// <summary>Raised exactly once if the socket opened AND was not refused — see
    /// <see cref="IRecognizer.Ready"/>, which exists because of finding 1 above.</summary>
    public event Action? Ready;

    public string Engine => "deepgram";

    readonly Func<long> _epochNow;
    readonly AudioCapture _capture = new();
    readonly SpeechStream.Turn _turn = new();

    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    Task? _pump;
    Task? _keepalive;

    long _utteranceEpoch;
    bool _inUtterance;
    bool _settled;      // Ready or Failed has been raised; exactly one, exactly once
    readonly object _lock = new();

    /// <summary>How long a connect may take before it is a failure. §6: *"`Starting` must not be
    /// sittable-in. SAPI's `Start()` was synchronous so `Starting` could not linger; a socket
    /// connect is not."* §0.1's standing directive is literal about this — every wait names the
    /// thing that un-sticks it, and it is never a person. Overridable so a check can force the
    /// deadline without waiting out a real one.</summary>
    public static int ConnectTimeoutMs =>
        int.TryParse(Environment.GetEnvironmentVariable("DODONA_STT_CONNECT_MS"), out var ms) && ms > 0
            ? ms : 8000;

    public DeepgramRecognizer(Func<long>? epochNow = null)
    {
        _epochNow = epochNow ?? (() => 0);
        _capture.Failed += r => Fail(r);
        _capture.Frame += SendAudio;
    }

    /// <summary>Never throws (the interface's contract). Returns immediately: the connect runs on
    /// its own task and reports through <see cref="Ready"/> or <see cref="Failed"/>, exactly one
    /// of which is guaranteed to arrive because the connect has a deadline.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
        }
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    async Task RunAsync(CancellationToken ct)
    {
        var token = SpeechAuth.Token(out var authWhy);
        if (token is null)
        {
            // No credential at all is a FATAL, stated in words, and it is not retried: there is
            // nothing about waiting that would make a token appear.
            Fail(authWhy ?? "no speech credential");
            return;
        }

        var ws = new ClientWebSocket();
        // .NET 7+: populates HttpStatusCode even when the upgrade FAILS. Kept even though this
        // endpoint never 4xxes the handshake, because plan §10's fallback 1 (Deepgram direct)
        // does authenticate there, and that is the reading which would matter then.
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.SetRequestHeader("x-app", "vscode");
        ws.Options.SetRequestHeader("anthropic-client-platform", "claude_code_vscode");

        // KEYTERMS GO IN A HEADER, not as repeated query parameters (plan §2 corrects
        // VOICE-INPUT-PLAN §6.2). The 1024-byte cap truncates SILENTLY, which is why the list is
        // ordered by significance (D-E4).
        // DODONA_STT_NO_KEYTERMS turns the list OFF, so the same recording can be run both ways
        // and the difference measured (T1). Without that A/B, "keyterms help" is an assumption
        // dressed as a decision — and D-E4 spent real care on the ORDER of a list whose effect
        // nobody had observed.
        var keyterms = Environment.GetEnvironmentVariable("DODONA_STT_NO_KEYTERMS") is null or ""
            ? SpeechStream.KeytermHeader(SpeechStream.Keyterms)
            : "";
        if (keyterms.Length > 0) ws.Options.SetRequestHeader("x-config-keyterms", keyterms);
        Trace("keyterms " + (keyterms.Length > 0 ? keyterms.Length + " bytes: " + keyterms : "OFF"));

        _ws = ws;

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeoutMs);
            await ws.ConnectAsync(new Uri(Endpoint + "?" + Query()), connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail(SpeechStream.NoAnswerWords);
            return;
        }
        catch (OperationCanceledException) { return; }      // Stop() was called; not a failure
        catch (Exception ex)
        {
            var status = ws.HttpStatusCode != 0 ? (int)ws.HttpStatusCode : 0;
            Fail(SpeechStream.Classify(status, 0, ex.Message) == SpeechStream.Severity.Fatal
                ? SpeechStream.AuthFailedWords
                : SpeechStream.NoNetworkWords);
            return;
        }

        // The socket is OPEN, which by finding 1 above says nothing about auth yet.
        await Send(ws, "{\"type\":\"KeepAlive\"}", ct);
        if (!_capture.Start()) { await CloseQuietly(ws); return; }

        // ══ SURVIVING THE GRACE PERIOD IS WHAT "AUTHENTICATED" MEANS HERE ══
        //
        // Ready cannot wait for the first transcript frame, and getting this wrong made dictation
        // unusable in a way no check would have caught. A correctly authenticated microphone that
        // nobody is talking into receives NOTHING — so waiting for a transcript would hold
        // `Starting` through the whole connect deadline and land in `error`, meaning dictation only
        // ever worked if you started speaking within eight seconds of toggling it on. Silence is
        // the normal state of a microphone, not a failure of one.
        //
        // What actually distinguishes authenticated from refused is TIME: the refusal is one frame
        // and it arrives immediately (measured in E1: a single error frame, then a 1008 close, well
        // inside a second). So the reading is "the socket is still open and nothing has objected".
        // If a slow network delivers the refusal later, the indicator flips from listening to error
        // and still says why — late and correct beats early and wrong.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(AuthGraceMs, ct); } catch { return; }
            // Settle is idempotent and Fail claims the same flag, so whichever happened first
            // wins: a rejection already reported leaves this a no-op.
            if (ws.State == WebSocketState.Open) Settle();
        }, ct);

        _keepalive = Task.Run(() => KeepAliveAsync(ws, ct), ct);
        _pump = ReadAsync(ws, ct);
        await _pump;
    }

    static string Query()
    {
        var parts = new List<string>
        {
            "encoding=linear16",
            "sample_rate=" + Pcm16.Rate,
            "channels=1",
            "endpointing_ms=300",
            "utterance_end_ms=1000",
            "language=en",
            // Overridable purely to test whether IT is what swallows the keyterms and lazies the
            // endpointing (D-E22). The extension sends true, so true stays the default.
            "use_conversation_engine=" +
                (Environment.GetEnvironmentVariable("DODONA_STT_NO_CONVERSATION") is null or "" ? "true" : "false"),
            "stt_provider=deepgram-nova3",
            // forward_interims is sent ONLY when typed-interims is on, and it is not (plan §2).
        };

        // ══ KEYTERMS AS REPEATED QUERY PARAMETERS, AS WELL AS THE HEADER (D-E21) ══
        //
        // Measured: the header alone does NOTHING. The same recording with `x-config-keyterms` on
        // and off produced byte-for-byte identical transcripts, while the four words the list
        // exists for — worktree, daemon, WAL, SQLite — all came back wrong (D-E18).
        //
        // `VOICE-INPUT-PLAN.md` §6.2 read the bundle as sending keyterms as **repeated query
        // parameters**; VOICE-ENGINE-PLAN §2 "corrected" that to a header only. That correction is
        // the prime suspect, and Deepgram's own documented interface for Nova-3 keyterm prompting
        // is a repeated `keyterm` query parameter — which is what this endpoint is a proxy for.
        //
        // BOTH are sent now rather than swapping one for the other: the header is what the bundle
        // definitely does, the query parameters are what the engine underneath definitely reads,
        // and sending both costs one URL. `DODONA_STT_NO_KEYTERMS` still turns the whole lot off so
        // the A/B stays runnable.
        if (Environment.GetEnvironmentVariable("DODONA_STT_NO_KEYTERMS") is null or "")
            foreach (var term in SpeechStream.Keyterms)
                parts.Add("keyterm=" + Uri.EscapeDataString(term));

        return string.Join("&", parts);
    }

    /// <summary>
    /// The read pump: the five message types of plan §2, and the close frame that carries the
    /// auth verdict.
    ///
    /// **THE SERVER NEVER SENDS A FINAL TRANSCRIPT.** It streams interims and
    /// `TranscriptEndpoint` means "that last interim is now settled". <see cref="SpeechStream.Turn"/>
    /// holds the latest and promotes it, which is why that logic is pure and unit-checked rather
    /// than three mutable fields in this loop.
    /// </summary>
    async Task ReadAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, ct);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    var code = r.CloseStatus is not null ? (int)r.CloseStatus : 0;
                    var why = r.CloseStatusDescription ?? "";
                    var fatal = SpeechStream.Classify(0, code, why) == SpeechStream.Severity.Fatal;

                    // The tail, not lost silently: flush the held interim as final before saying
                    // anything about the failure (§6 — losing the tail silently is worse than a
                    // visible reconnect). A fatal auth close has nothing held, so this is a no-op
                    // there and matters exactly when the network dropped mid-sentence.
                    Emit(_turn.Flush(_utteranceEpoch));

                    Fail(fatal ? SpeechStream.AuthFailedWords
                               : why.Length > 0 ? why : SpeechStream.NoNetworkWords);
                    return;
                }

                if (r.MessageType != WebSocketMessageType.Text) continue;
                var m = SpeechStream.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
                Trace($"{m.Kind,-16} len={m.Text.Length,4}  {m.Text}");

                switch (m.Kind)
                {
                    case SpeechStream.Kind.ServerError:
                    case SpeechStream.Kind.TranscriptError:
                    {
                        var fatal = SpeechStream.Classify(0, 0, m.Text) == SpeechStream.Severity.Fatal;
                        Emit(_turn.Flush(_utteranceEpoch));
                        // A FATAL IS NOT RETRIED, EVER, IN A LOOP. Plan §6: that is how you get a
                        // hot loop against someone's auth endpoint. It re-arms on the next
                        // deliberate toggle-on and not before.
                        Fail(fatal ? SpeechStream.AuthFailedWords
                                   : m.Text.Length > 0 ? m.Text : "speech recognition failed");
                        return;
                    }

                    case SpeechStream.Kind.Interim:
                    case SpeechStream.Kind.Text:
                        // The stream is alive AND was not refused: this is the earliest honest
                        // moment to say Listening, and it is why Ready exists.
                        Settle();
                        if (!_inUtterance)
                        {
                            _inUtterance = true;
                            _utteranceEpoch = _epochNow();
                        }
                        Emit(_turn.OnMessage(m, _utteranceEpoch));
                        break;

                    case SpeechStream.Kind.Endpoint:
                        Settle();
                        Emit(_turn.OnMessage(m, _utteranceEpoch));
                        _inUtterance = false;      // the next interim starts a new utterance
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ══ THE ORDINARY WAY OUT STILL HAS TO HAND BACK WHAT IT WAS HOLDING ══
            //
            // This used to be an empty handler, and that was a silent data loss the suites could
            // never have caught. Measured on the first real recording: the server sends
            // `TranscriptEndpoint` far more lazily than `endpointing_ms=300` suggests — three
            // endpoints in twenty seconds, then twenty-three seconds of continuous transcription
            // with none at all. So at any given moment there is usually a settled-but-unpromoted
            // phrase being held.
            //
            // Switching the microphone off cancels this token. With nothing here, everything spoken
            // since the last endpoint was DISCARDED — you finish a sentence, click the mic off, and
            // the sentence is gone. That is §6's "losing the tail silently is worse than a visible
            // reconnect", and §0.1's silent degrade, arriving by the one route nobody instrumented:
            // the HAPPY path.
            Emit(_turn.Flush(_utteranceEpoch));
        }
        catch (Exception ex)
        {
            Emit(_turn.Flush(_utteranceEpoch));
            Fail(AudioCapture.Describe(ex));
        }
    }

    /// <summary>
    /// Plan §2: every 8000 ms. Without it the endpoint drops a stream that is merely quiet, so
    /// the operator who stops talking for ten seconds mid-thought comes back to a dead
    /// microphone — a silent degrade, which is a bug (§0.1).
    /// </summary>
    async Task KeepAliveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(KeepAliveMs, ct);
                if (ws.State == WebSocketState.Open) await Send(ws, "{\"type\":\"KeepAlive\"}", ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* the read pump is what reports a dead socket; two voices would double-report */ }
    }

    void SendAudio(byte[] frame)
    {
        var ws = _ws;
        var cts = _cts;
        if (ws is null || cts is null || ws.State != WebSocketState.Open) return;
        try
        {
            // Raw PCM as a BINARY frame. linear16 means signed 16-bit LE, 16 kHz, mono, and NO
            // WAV HEADER — a header would be transcribed as noise (plan §2).
            _ = ws.SendAsync(frame, WebSocketMessageType.Binary, true, cts.Token);
        }
        catch { /* the read pump reports the dead socket; a send race must not throw into NAudio */ }
    }

    static Task Send(ClientWebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    void Emit(Dictation.Heard? h) { if (h is not null) Heard?.Invoke(h); }

    /// <summary>
    /// A frame-level trace, to a file named by `DODONA_STT_TRACE`, off unless that is set.
    ///
    /// It exists because the first real recording produced a question no dump could answer: the box
    /// received four sentences while the interim stream had reached the eighth, so **endpoints went
    /// sparse partway through** and there was no way to see whether the server had stopped sending
    /// them or this code had stopped acting on them. Guessing between those two is exactly the kind
    /// of thing §0.3 says to instrument rather than reason about.
    ///
    /// Off by default and never on in a suite: a per-frame log on the operator's machine is a
    /// transcript of everything they say, written to disk.
    /// </summary>
    static void Trace(string line)
    {
        var path = Environment.GetEnvironmentVariable("DODONA_STT_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            System.IO.File.AppendAllText(path,
                DateTime.UtcNow.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
        }
        catch { /* a trace that cannot write must not break dictation */ }
    }

    /// <summary>Exactly one of Ready/Failed, exactly once — the contract <c>ArmMic</c> relies on
    /// to leave `Starting` without a timer of its own.</summary>
    void Settle()
    {
        lock (_lock) { if (_settled) return; _settled = true; }
        Ready?.Invoke();
    }

    void Fail(string why)
    {
        lock (_lock) { if (_settled) { /* already Listening: report, do not re-settle */ } else _settled = true; }
        Failed?.Invoke(why);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        ClientWebSocket? ws;
        lock (_lock) { cts = _cts; ws = _ws; _cts = null; _ws = null; }

        _capture.Stop();
        try { cts?.Cancel(); } catch { }
        if (ws is not null)
        {
            // Plan §2 (`amr`): tell the server the stream is over rather than dropping the socket,
            // so it finalises rather than timing out.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await Send(ws, "{\"type\":\"CloseStream\"}", CancellationToken.None);
                }
                catch { }
                await CloseQuietly(ws);
            });
        }
        try { cts?.Dispose(); } catch { }
    }

    static async Task CloseQuietly(ClientWebSocket ws)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", t.Token);
        }
        catch { /* stopping must not be able to fail */ }
        finally { try { ws.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        Stop();
        try { _capture.Dispose(); } catch { /* on the way out */ }
    }
}
