using Dodona;

namespace DodonaUi;

/// <summary>
/// The DEVICE half of dictation, behind a seam (docs/VOICE-INPUT-PLAN.md §3).
///
/// Two implementations and ONE landing site: <c>MainWindow.OnHeard</c>. The real engine and the
/// fake raise the same event into the same method, which is the `ui type` reasoning applied one
/// layer down — a fake that fed a parallel path would prove nothing about the real one, exactly
/// as <c>DodonaFakeAgent</c> stands in for `claude` without inventing a second lane runtime.
///
/// <see cref="Start"/> NEVER THROWS. A microphone that is missing, muted, in use by a call, or
/// switched off in Windows privacy settings is an ordinary Tuesday, not an exception for the
/// caller to handle — it arrives as <see cref="Failed"/> with a reason in words, the indicator
/// goes loud, and the box keeps typing normally. A dead recogniser may make the box deaf; it
/// must never be able to make the box unusable, because the box is the one thing you would use
/// to report the problem (the same argument <see cref="UiSettings"/> already makes about a
/// corrupt ui.json).
/// </summary>
interface IRecognizer : IDisposable
{
    /// <summary>A result, partial or final, stamped with the submit epoch it was recognised
    /// under. Raised on the recogniser's own thread — the window marshals.</summary>
    event Action<Dictation.Heard>? Heard;

    /// <summary>One reason string, never an exception to the caller. The words go in the hint
    /// line, so they must read as English to someone who is not a programmer: "no microphone",
    /// not "COMException 0x80045005".</summary>
    event Action<string>? Failed;

    /// <summary>
    /// The engine is genuinely hearing now — leave <c>Starting</c>.
    ///
    /// **THIS EXISTS BECAUSE THE ENGINE STOPPED BEING SYNCHRONOUS** (docs/VOICE-ENGINE-PLAN.md
    /// §6, and spike E1's finding). SAPI's `Start()` returned only once it had either failed or
    /// begun, so <c>ArmMic</c> could promote <c>Starting</c> to <c>Listening</c> on the next line
    /// and the state could not be sat in. A socket connect is not like that, and worse: this
    /// endpoint's UPGRADE SUCCEEDS EVEN WITH NO CREDENTIAL, refusing one frame later. So
    /// "Start() returned" and "we are hearing" are now two different facts, and promoting on the
    /// first would leave the indicator reading `listening` at a rejected credential — deaf,
    /// silent, and looking healthy, which is the exact failure §5 exists to prevent.
    ///
    /// The contract <c>ArmMic</c> relies on: **exactly one of <see cref="Ready"/> or
    /// <see cref="Failed"/> arrives, exactly once, and one of them always does** — because the
    /// connect has a deadline (§0.1: every wait names the thing that un-sticks it, and it is never
    /// a person). That is what lets the window hold `Starting` without a timer of its own.
    /// </summary>
    event Action? Ready;

    void Start();
    void Stop();

    /// <summary>What `ui dump` reports as `listen.engine`, so a check (and the operator) can
    /// tell which half is wired without reading the build.</summary>
    string Engine { get; }
}

/// <summary>
/// The recogniser that opens nothing (D-V4).
///
/// It is not a stub standing in for missing work — it is the implementation the SUITES use, and
/// the one that must exist for `ui-use` to be able to test dictation at all. No suite may open a
/// real microphone: a check that grabs the mic while the operator is in a call is CLAUDE.md §4's
/// incident in a new costume, and §0.1's "quota is the scarce resource" has a device-shaped
/// sibling here.
///
/// It never raises <see cref="Heard"/> by itself. Utterances arrive through `dodona ui heard`,
/// which lands directly in <c>MainWindow.OnHeard</c> — the same place this class's event would
/// land, and the same reason `ui type` calls <c>SubmitInput</c> rather than synthesising a
/// keystroke.
/// </summary>
sealed class FakeRecognizer : IRecognizer
{
    public event Action<Dictation.Heard>? Heard;
    public event Action<string>? Failed;
    public event Action? Ready;
    public string Engine => _engine;

    readonly bool _hang;
    readonly string _engine;

    /// <summary>
    /// <paramref name="hang"/> is <c>DODONA_UI_MIC=hang</c>: never becomes ready, so the connect
    /// deadline is reachable without a network (see <see cref="Recognizers.Create"/>).
    ///
    /// <paramref name="engine"/> exists for the operator's no-silent-fallback rule (D-E11). When
    /// this class stands in because the REAL engine could not be constructed, it reports
    /// <c>none</c> rather than <c>fake</c> — because "fake" would read, to anyone looking at
    /// `ui dump`, as though an engine were installed and working. The suites keep <c>fake</c>,
    /// which is the truth there: they asked for no engine and got none.
    /// </summary>
    public FakeRecognizer(bool hang = false, string engine = "fake")
    {
        _hang = hang;
        _engine = engine;
    }

    public void Start()
    {
        // Deliberately silent about Heard and Failed: a fake that announced a failure would put
        // the indicator in `error` for every suite run, and a fake that announced success would
        // be claiming a device it never touched.
        _ = Heard; _ = Failed;

        // Ready IS raised, synchronously, and that is what keeps the 268 existing checks byte-for
        // byte unchanged. Before the seam went asynchronous, `ArmMic` promoted Starting to
        // Listening on the line after Start(); now it waits for this event, so a fake that stayed
        // silent would leave every suite in `starting` and redden all eighteen voice checks. The
        // fake opens nothing, so it has nothing to wait for and nothing to lie about.
        if (!_hang) Ready?.Invoke();
    }

    public void Stop() { }
    public void Dispose() { }
}

/// <summary>
/// Which recogniser this window gets, and the one place that decision is made.
/// </summary>
static class Recognizers
{
    /// <summary>The hard, machine-level override (D-V2). `DODONA_UI_MIC=off` refuses to
    /// construct a real recogniser at all — not "starts it stopped", refuses to build it — and
    /// `tests/_workspace.ps1` sets it for every suite. A toggle remembered in ui.json is a
    /// preference; this is the escape hatch that outranks it.</summary>
    public static bool MicDisabled => Mode == "off";

    /// <summary>
    /// `fail` constructs no device and reports a failure, which is the ONLY way to reach the
    /// error state headlessly — the alternative is a check that unplugs a microphone.
    ///
    /// It earns its place in production code rather than behind a test flag for the reason
    /// §5 gives about `ui heard`: the state it produces must be the state a real failure
    /// produces, or the check proves nothing about the real one. `voice:no_modal_when_the_mic_fails`
    /// is what it exists for, and D-V3 is what that check defends — a modal a test window
    /// cannot produce would be a permanent blind spot, which is why PickerWindow and
    /// StartLaneWindow have no coverage at all.
    /// </summary>
    static string Mode => (Environment.GetEnvironmentVariable("DODONA_UI_MIC") ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Never throws, and never returns null. If the real engine cannot be constructed the fake
    /// comes back with <paramref name="why"/> set, which the window renders as `error` — loud,
    /// in words, and never a dialog (D-V3).
    /// </summary>
    public static IRecognizer Create(out string? why) => Create(null, out why);

    /// <summary>
    /// <paramref name="epochNow"/> reads the window's current submit epoch. It is a delegate
    /// rather than a value because the window bumps it on every send, and the engine stamps an
    /// utterance at its START — see <c>DeepgramRecognizer</c>'s note on the race.
    /// </summary>
    public static IRecognizer Create(Func<long>? epochNow, out string? why)
    {
        why = null;

        // FIRST, BEFORE ANYTHING ELSE, AND THE ORDER IS THE POINT (D-E5).
        //
        // With a cloud engine this is no longer merely "do not touch a device". Constructing the
        // real recogniser opens a WebSocket to Anthropic's API on the OPERATOR'S CREDENTIALS from
        // inside a test run — CLAUDE.md §4's incident with a bill attached. `voice:mic_off_opens_
        // no_socket` pins the ordering specifically, because "connect then do not listen" would
        // pass any check that only looked at the state.
        if (MicDisabled) return new FakeRecognizer();

        if (Mode == "fail")
        {
            // The same words a missing capture endpoint produces, so the check pins the real
            // sentence rather than a test-only one.
            why = "no microphone";
            return new FakeRecognizer();
        }

        // A connect that never answers, reachable without a network (D-E10). The error state was
        // already unreachable without unplugging a microphone, which is what `fail` is for; a
        // HUNG START is a third thing, new with this engine, and §6 names it as the genuinely new
        // state to get wrong. It earns production code for the reason §5 gives about `ui heard`:
        // a test-only path would prove nothing about the real one.
        if (Mode == "hang") return new FakeRecognizer(hang: true);

        try
        {
            return new DeepgramRecognizer(epochNow);
        }
        catch (Exception ex)
        {
            // ══ NO SILENT FALLBACK TO A WORSE ENGINE, EVER (D-E11) ══
            //
            // The operator's rule, and it is the reason SapiRecognizer was deleted rather than
            // kept (D-E6): *"I also don't want this thing falling back to lighter inferior
            // versions. If something is not available the way we need it to be to run the proper
            // thing, then it's better to simply tell the user"* — because an inferior engine
            // mangles their words while LOOKING like the feature working badly, and that is
            // strictly worse than a feature that says it is unavailable.
            //
            // So this path returns something that hears NOTHING and says so. It reports
            // `engine=none` rather than `engine=fake`: "fake" is the truth in a suite that asked
            // for no engine, but on the operator's machine it would read as though an engine were
            // installed and merely quiet. The reason travels in `why` to the indicator, in amber,
            // in words.
            why = Describe(ex);
            return new FakeRecognizer(engine: "none");
        }
    }

    /// <summary>Turn an engine exception into words a person can act on. Anything unrecognised
    /// keeps its own message rather than being flattened to "an error occurred" — a reason
    /// nobody can act on is the same silent degrade as no reason at all.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        PlatformNotSupportedException => "speech recognition is not available on this Windows build",
        // Media Foundation absent (a stripped Windows install, an N edition with no media feature
        // pack) is the one construction failure the cloud engine still has.
        NotSupportedException => "audio conversion is not available on this Windows build",
        _ => AudioCapture.Describe(ex),
    };
}
