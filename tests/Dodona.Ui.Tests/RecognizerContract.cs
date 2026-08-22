using DodonaUi;
using Xunit;

namespace Dodona.Ui.Tests;

/// <summary>
/// ══ ONE BODY, TWO SUBJECTS: the arrival contract, run against the FAKE and against the REAL
/// ENGINE ══
///
/// This is what closes behaviour drift (D2) for `FakeRecognizer`, and it is the answer to the
/// red the double ledger produces on day one against code nobody wrote for it
/// (docs/TEST-ARCHITECTURE-PLAN.md 3.2).
///
/// The sentence is `IRecognizer`'s own doc comment, verbatim: **"exactly one of Ready or Failed
/// arrives, exactly once, and one of them always does"**. `ArmMic` relies on it to hold
/// `Starting` without a timer of its own, so a recogniser that raised neither would leave the
/// mic glyph in `starting` for ever, and one that raised both would flip a live window from
/// listening to error for no reason.
///
/// ══ THE REAL SUBJECT REALLY RUNS, AND IT COSTS NOTHING ══
///
/// `DeepgramRecognizer` is pointed at **a closed loopback port**. That is deterministic,
/// in-process, and needs no network, no microphone, no credential and no quota:
///
/// - `DODONA_STT_ENDPOINT=ws://127.0.0.1:1/` -- the loopback stack refuses the connect
///   instantly, which exercises the real socket-failure path with no egress. The same trick is
///   already load-bearing in the `voice` suite (`a_dead_network_reads_as_error_not_listening`),
///   and `DeepgramRecognizer`'s own comment on `Endpoint` says it is there for exactly this.
/// - `DODONA_STT_TOKEN` is set to a dummy, which is NOT decoration. Without it `SpeechAuth`
///   walks to the operator's real `~\.claude\.credentials.json`, and a test that reads a live
///   credential to prove an arrival count is a test doing something nobody asked it to.
/// - `AudioCapture` is never started: `RunAsync` only reaches `_capture.Start()` after the
///   socket is OPEN, and it never opens. **No suite may open the operator's microphone**
///   (CLAUDE.md 4's incident in a new costume), and this one cannot, structurally.
///
/// ══ WHAT IT DOES NOT CLOSE, SAID PLAINLY ══
///
/// The TIMING half. `FakeRecognizer.Start()` raises `Ready` SYNCHRONOUSLY, inside `Start()`;
/// `DeepgramRecognizer` raises it only after a socket has answered and survived a grace period.
/// No in-process subject makes a socket answer late on purpose without becoming a third fake,
/// and a third fake would need its own anchor. So that half is a permanent, counted
/// `KnownDivergence` with an issue on the `[Double]` itself -- not a debt to be worked off, and
/// labelling it as one would be the same dishonesty in a smaller font.
/// </summary>
public abstract class RecognizerContract
{
    /// <summary>The subject. `internal` because `IRecognizer` is internal to DodonaUi and reached
    /// here through `InternalsVisibleTo`: a public member exposing it would not compile.</summary>
    internal abstract IRecognizer Create();

    /// <summary>How long the subject may take to settle. The fake settles synchronously and the
    /// refused socket settles in microseconds; this is a deadline so that a subject which never
    /// settles fails as itself rather than hanging the suite (CLAUDE.md 0.1: every wait names the
    /// thing that un-sticks it, and it is never a person).</summary>
    protected virtual int SettleMs => 5000;

    /// <summary>After the first arrival, how long a SECOND one still counts. Absence needs a
    /// window; this is the only real duration in the file and it is small on purpose.</summary>
    protected virtual int QuietMs => 250;

    [Fact]
    public void Exactly_one_of_Ready_or_Failed_arrives_and_it_arrives_exactly_once()
    {
        using var r = Create();
        var ready = 0;
        var failed = 0;
        using var settled = new ManualResetEventSlim(false);

        r.Ready += () => { Interlocked.Increment(ref ready); settled.Set(); };
        r.Failed += _ => { Interlocked.Increment(ref failed); settled.Set(); };

        r.Start();

        Assert.True(settled.Wait(SettleMs),
            GetType().Name + ": neither Ready nor Failed arrived within " + SettleMs + " ms. " +
            "ArmMic holds `Starting` waiting for one of them, so a recogniser that never settles " +
            "leaves the mic glyph in `starting` for ever -- on and deaf, looking like on.");

        // The second half of the sentence: EXACTLY once. An absence is only observable inside a
        // window, so this is a real duration and says so.
        Thread.Sleep(QuietMs);

        var total = Volatile.Read(ref ready) + Volatile.Read(ref failed);
        Assert.True(total == 1,
            GetType().Name + ": Ready fired " + Volatile.Read(ref ready) + " time(s) and Failed fired " +
            Volatile.Read(ref failed) + " time(s). The contract is exactly one of them, exactly once.");
    }
}

/// <summary>
/// The double. Subject 1.
///
/// NAMED FOR THE CONTRACT AND NOT FOR ITS SUBJECT, and that is rung 1 correcting this file rather
/// than a style choice. It was `FakeRecognizerContract` first, and `dev lint` refused it:
///
///   tests\Dodona.Ui.Tests\RecognizerContract.cs:91 class 'FakeRecognizerContract' is a test
///   double by its NAME and carries no [Double(...)]
///
/// Which is right. `Fake*` is this repo's word for a thing that stands in for something real, and
/// a class named that, carrying no anchor, is exactly the ambiguity rung-1 assertion 1 exists to
/// remove. This class stands in for nothing -- it SUPPLIES a double to a contract body. So it
/// says so.
/// </summary>
public sealed class RecognizerContractOverTheFake : RecognizerContract
{
    internal override IRecognizer Create() => new FakeRecognizer();
}

/// <summary>
/// The real engine, at a closed loopback port. Subject 2, and the reason this is a CONTRACT
/// rather than another test of the fake.
///
/// The environment is set per-instance and restored, and xunit's default is one test class per
/// collection running in parallel -- so `Dodona.Ui.Tests` disables parallelisation in
/// AssemblyInfo.cs rather than leaving two classes racing over three process-wide variables.
/// </summary>
public sealed class RecognizerContractOverDeepgramAtAClosedPort : RecognizerContract, IDisposable
{
    readonly string?[] _saved = new string?[3];
    static readonly string[] Vars = { "DODONA_STT_ENDPOINT", "DODONA_STT_TOKEN", "DODONA_STT_CONNECT_MS" };

    public RecognizerContractOverDeepgramAtAClosedPort()
    {
        for (var i = 0; i < Vars.Length; i++) _saved[i] = Environment.GetEnvironmentVariable(Vars[i]);
        // Port 1 is never listening. The connect is refused by the loopback stack, so this is the
        // real ClientWebSocket, the real classification and the real Fail() path -- with no
        // network reached and nothing of the operator's touched.
        Environment.SetEnvironmentVariable("DODONA_STT_ENDPOINT", "ws://127.0.0.1:1/");
        Environment.SetEnvironmentVariable("DODONA_STT_TOKEN", "not-a-real-token-and-never-sent-anywhere");
        Environment.SetEnvironmentVariable("DODONA_STT_CONNECT_MS", "2000");
    }

    internal override IRecognizer Create() => new DeepgramRecognizer();

    public void Dispose()
    {
        for (var i = 0; i < Vars.Length; i++) Environment.SetEnvironmentVariable(Vars[i], _saved[i]);
    }
}
