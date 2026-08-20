using Dodona;
using Xunit;

namespace Dodona.Tests;

/// <summary>
/// The cloud engine's wire contract (docs/VOICE-ENGINE-PLAN.md §2, E3/E4/E6).
///
/// These exist because **the protocol is where every surprise was**. Two of the plan's stated
/// protocol facts turned out to be wrong when measured against the live endpoint on 2026-08-20,
/// and both would have shipped as silent misbehaviour rather than as a crash:
///
/// - the upgrade authenticates NOTHING (101 with no credential at all), so classifying on the
///   HTTP status alone reads a refused credential as a successful start;
/// - the error text is nested under `error.message`, so reading the documented top-level
///   `message` yields an error state with no words in it.
///
/// A protocol living behind a socket is a protocol no check can reach, which is why
/// <see cref="SpeechStream"/> is pure and why these run in ~1 second with no network.
/// </summary>
public class SpeechStreamTests
{
    // ══ the interim-holding turn ══════════════════════════════════════════════════════════

    /// <summary>
    /// THE MOST IMPORTANT PROTOCOL FACT: the server never sends a final transcript. It streams
    /// interims and `TranscriptEndpoint` means "that last interim is now settled".
    ///
    /// Proved RED by break-and-revert (D-V11's method, because `dev prove` refuses the unit
    /// suite): making `Kind.Endpoint` return null instead of promoting gave
    ///   Assert.NotNull() Failure: Value is null
    /// which is the words never reaching the box at all -- dictation that shows a live partial
    /// and then silently discards every finished sentence.
    /// </summary>
    [Fact]
    public void Endpoint_promotes_the_last_interim()
    {
        var turn = new SpeechStream.Turn();

        var a = turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Interim, "run the"), 7);
        Assert.NotNull(a);
        Assert.False(a!.Final);
        Assert.Equal("run the", a.Text);

        // A later interim REPLACES the held one -- an interim rewrites itself, it does not append.
        var b = turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Interim, "run the suites"), 7);
        Assert.NotNull(b);
        Assert.False(b!.Final);

        var final = turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Endpoint, ""), 7);
        Assert.NotNull(final);
        Assert.True(final!.Final);
        Assert.Equal("run the suites", final.Text);
        Assert.Equal(7, final.Epoch);

        // And the held text is CONSUMED: a second endpoint must not re-insert the same sentence.
        Assert.Null(turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Endpoint, ""), 7));
    }

    /// <summary>`TranscriptText` is delivered as NOT final despite its name (plan §2). Treating
    /// it as final is the easiest way to get every sentence inserted twice.</summary>
    [Fact]
    public void TranscriptText_is_not_final()
    {
        var turn = new SpeechStream.Turn();
        var h = turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Text, "collapse lane three"), 1);
        Assert.NotNull(h);
        Assert.False(h!.Final);
    }

    /// <summary>A blank interim must not wipe a held phrase that is about to be promoted, or the
    /// endpoint finalises nothing and the words vanish between the ear and the box.</summary>
    [Fact]
    public void A_blank_interim_does_not_erase_the_held_phrase()
    {
        var turn = new SpeechStream.Turn();
        turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Interim, "publish from the worktree"), 3);
        Assert.Null(turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Interim, "   "), 3));
        var final = turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Endpoint, ""), 3);
        Assert.Equal("publish from the worktree", final!.Text);
    }

    /// <summary>The socket dies mid-utterance: flush the held interim as final rather than losing
    /// the tail silently (§6, and §0.1 -- a silent degrade is a bug).</summary>
    [Fact]
    public void A_dead_socket_flushes_the_held_interim()
    {
        var turn = new SpeechStream.Turn();
        turn.OnMessage(new SpeechStream.Message(SpeechStream.Kind.Interim, "the ff-only merge failed"), 2);
        var flushed = turn.Flush(2);
        Assert.NotNull(flushed);
        Assert.True(flushed!.Final);
        Assert.Equal("the ff-only merge failed", flushed.Text);
        // Nothing held: a second flush must not duplicate it.
        Assert.Null(turn.Flush(2));
    }

    // ══ parsing, including the shape the plan got wrong ═══════════════════════════════════

    [Fact]
    public void The_five_message_types_parse()
    {
        Assert.Equal(SpeechStream.Kind.Interim,
            SpeechStream.Parse("{\"type\":\"TranscriptInterim\",\"data\":\"x\"}").Kind);
        Assert.Equal(SpeechStream.Kind.Text,
            SpeechStream.Parse("{\"type\":\"TranscriptText\",\"data\":\"x\"}").Kind);
        Assert.Equal(SpeechStream.Kind.Endpoint,
            SpeechStream.Parse("{\"type\":\"TranscriptEndpoint\"}").Kind);
        Assert.Equal(SpeechStream.Kind.TranscriptError,
            SpeechStream.Parse("{\"type\":\"TranscriptError\",\"description\":\"boom\"}").Kind);
        Assert.Equal(SpeechStream.Kind.ServerError,
            SpeechStream.Parse("{\"type\":\"error\",\"message\":\"boom\"}").Kind);

        // Never throws: an undocumented endpoint may grow a sixth type (D-E1's accepted risk),
        // and a malformed frame must not stop a read loop.
        Assert.Equal(SpeechStream.Kind.Unknown, SpeechStream.Parse("not json at all").Kind);
        Assert.Equal(SpeechStream.Kind.Unknown, SpeechStream.Parse("[1,2,3]").Kind);
    }

    /// <summary>
    /// THE EXACT FRAME THE LIVE ENDPOINT SENT on 2026-08-20 when the credential was wrong, copied
    /// verbatim from the E1 probe's output. Plan §2 documents a top-level `message`; the real
    /// frame nests it under `error`. Reading only the documented shape gives an error state with
    /// no words in it -- which breaks "on and deaf must never look like on" (§5) exactly as
    /// thoroughly as a wrong colour would.
    ///
    /// Proved RED by break-and-revert: deleting the nested branch from `ErrorText` gave
    ///   Assert.Contains() Failure: ... Not found: Invalid authorization  In value: ""
    /// i.e. the empty reason string that would have reached the operator.
    /// </summary>
    [Fact]
    public void The_live_auth_refusal_frame_yields_words()
    {
        const string frame =
            "{\"type\":\"error\",\"error\":{\"type\":\"permission_error\"," +
            "\"message\":\"Invalid authorization\",\"details\":{\"error_visibility\":\"user_facing\"," +
            "\"error_code\":\"account_session_invalid\"}},\"request_id\":\"req_011CeDaQPYFpuw8PUsK47qb6\"}";

        var m = SpeechStream.Parse(frame);
        Assert.Equal(SpeechStream.Kind.ServerError, m.Kind);
        Assert.Contains("Invalid authorization", m.Text);
        // The code comes along, because "Invalid authorization" alone does not tell the operator
        // whether the problem is the token or the account.
        Assert.Contains("account_session_invalid", m.Text);
    }

    // ══ fatal or transient: spike E1's finding ════════════════════════════════════════════

    /// <summary>
    /// A 4xx must not be retried in a loop against someone's auth endpoint (§6) -- but the
    /// interesting half is that THIS endpoint never 4xxes. It refuses after a successful upgrade,
    /// so the frame text and the 1008 close are the readings that have to work.
    ///
    /// Proved RED by break-and-revert: removing the `closeCode == 1008` arm gave
    ///   Assert.Equal() Failure: Expected Fatal, Actual Transient
    /// which in the running app is a rejected credential treated as a passing network blip.
    /// </summary>
    [Fact]
    public void A_fatal_auth_failure_is_classified_fatal()
    {
        // The handshake route -- still correct, and it is what plan §10's fallback 1 would use.
        Assert.Equal(SpeechStream.Severity.Fatal, SpeechStream.Classify(401, 0, null));
        Assert.Equal(SpeechStream.Severity.Fatal, SpeechStream.Classify(403, 0, null));

        // The route this endpoint actually takes: 101, then an error frame, then 1008.
        Assert.Equal(SpeechStream.Severity.Fatal,
            SpeechStream.Classify(0, 0, "Invalid authorization (account_session_invalid)"));
        Assert.Equal(SpeechStream.Severity.Fatal, SpeechStream.Classify(0, 1008, "Invalid authorization"));
        Assert.Equal(SpeechStream.Severity.Fatal, SpeechStream.Classify(0, 0, "permission_error"));
    }

    /// <summary>
    /// The other half, and it matters just as much: ORDINARY NETWORK DEATH MUST STAY TRANSIENT.
    /// If a dropped wifi connection were classified fatal, dictation would permanently disarm
    /// itself on a train and the only cure would be reading this file.
    /// </summary>
    [Fact]
    public void Ordinary_network_death_stays_transient()
    {
        Assert.Equal(SpeechStream.Severity.Transient, SpeechStream.Classify(0, 1006, "")); // abnormal
        Assert.Equal(SpeechStream.Severity.Transient, SpeechStream.Classify(0, 1000, "")); // normal
        Assert.Equal(SpeechStream.Severity.Transient, SpeechStream.Classify(0, 1011, "server error"));
        Assert.Equal(SpeechStream.Severity.Transient, SpeechStream.Classify(500, 0, "bad gateway"));
        Assert.Equal(SpeechStream.Severity.Transient, SpeechStream.Classify(0, 0, "connection reset"));
    }

    // ══ keyterms, and the cap that truncates in silence ═══════════════════════════════════

    /// <summary>
    /// The extension's normaliser (`lmr`), copied: commas to spaces, whitespace collapsed,
    /// duplicates and empties dropped.
    /// </summary>
    [Fact]
    public void Keyterms_are_normalised_the_way_the_extension_normalises_them()
    {
        var h = SpeechStream.KeytermHeader(new[]
        {
            "  worktree  ", "worktree", "", "   ", "ff,only", "hot   swap", "WORKTREE",
        });
        var terms = h.Split(',');

        // A comma inside a term would silently become two terms, one of them a fragment, because
        // the header itself is comma-joined.
        Assert.Contains("ff only", terms);
        Assert.Contains("hot swap", terms);
        Assert.Contains("worktree", terms);
        // Case-insensitive de-duplication: three spellings of worktree, one term.
        Assert.Single(terms, t => t.Equals("worktree", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("", terms);
    }

    /// <summary>
    /// **THE CAP TRUNCATES SILENTLY, AND THAT IS THE WHOLE HAZARD** (D-E4). Terms past 1024 bytes
    /// are dropped with no error anywhere, so the only thing deciding whether the engine has ever
    /// heard of a word is its POSITION in the list.
    ///
    /// Proved RED by break-and-revert: changing the `break` to `continue` gave
    ///   Assert.True() Failure -- header was 1739 bytes
    /// i.e. an oversized header, which is the request the server rejects outright, turning
    /// dictation off entirely rather than merely dropping the rare words.
    /// </summary>
    [Fact]
    public void Keyterms_stop_at_the_byte_budget_and_keep_the_earliest()
    {
        var many = Enumerable.Range(0, 400).Select(i => "term" + i.ToString("D4")).ToArray();
        var h = SpeechStream.KeytermHeader(many);

        Assert.True(h.Length <= SpeechStream.KeytermBudget, $"header was {h.Length} bytes");
        // Earliest kept, latest dropped -- which is what makes the order significance and not taste.
        Assert.StartsWith("term0000,term0001", h);
        Assert.DoesNotContain("term0399", h);
    }

    /// <summary>Dodona's own list must actually FIT, or the words at the end are decoration. If a
    /// future edit pushes it over, this is the check that says so instead of the engine quietly
    /// never hearing "PowerShell".</summary>
    [Fact]
    public void Dodonas_own_keyterms_all_survive_the_budget()
    {
        var h = SpeechStream.KeytermHeader(SpeechStream.Keyterms);
        Assert.True(h.Length <= SpeechStream.KeytermBudget, $"header was {h.Length} bytes");

        var kept = h.Split(',');
        Assert.Equal(SpeechStream.Keyterms.Length, kept.Length);
        // The two the whole exercise exists for: VOICE-INPUT-PLAN §6 predicted "worktree" would
        // be heard as "work three", and the extension shipping it in its own eighteen is the
        // evidence that biasing is the fix.
        Assert.Contains("worktree", kept);
        Assert.Contains("lane", kept);
    }
}
