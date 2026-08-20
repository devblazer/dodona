using System.Text;
using System.Text.Json;

namespace Dodona;

/// <summary>
/// The PURE half of the cloud engine (docs/VOICE-ENGINE-PLAN.md §2) — the same split
/// <see cref="Dictation"/>, <see cref="Ask"/> and <see cref="PaneProgress"/> already use, and
/// for the strongest form of the same reason: **the protocol is where the surprises were, and a
/// protocol behind a socket is a protocol no check can reach.**
///
/// No socket, no device, no WPF. Frames in, decisions out — so the whole of the wire contract
/// lands on the ~1 second `dev test unit` loop, including the two facts measured on 2026-08-20
/// that the plan had wrong (see <see cref="Classify"/> and <see cref="Parse"/>).
///
/// **THE MOST IMPORTANT PROTOCOL FACT: THE SERVER NEVER SENDS A FINAL TRANSCRIPT.** It streams
/// interims, and `TranscriptEndpoint` means "that last interim is now settled". So something has
/// to hold the latest interim and re-emit it as final on endpoint, and flush it on close.
/// <see cref="Turn"/> is that something, and it is a pure state machine rather than three fields
/// scattered through a socket loop — which is what makes
/// `unit:Endpoint_promotes_the_last_interim` possible at all.
/// </summary>
public static class SpeechStream
{
    // ══ what the wire says ════════════════════════════════════════════════════════════════

    /// <summary>The five message types of plan §2, plus <see cref="Unknown"/> — an endpoint
    /// nobody documents is an endpoint that may grow a sixth (D-E1's accepted risk), and an
    /// unrecognised frame must be ignored rather than crash a recogniser.</summary>
    public enum Kind { Interim, Text, Endpoint, TranscriptError, ServerError, Unknown }

    /// <summary>One parsed frame. <c>Text</c> carries `data` for the transcript kinds
    /// and the human-readable reason for the error kinds, because both end up in the same place:
    /// either the box or the hint line.</summary>
    public record Message(Kind Kind, string Text);

    /// <summary>
    /// Parse one JSON frame. Never throws: a malformed frame is <see cref="Kind.Unknown"/>, not
    /// an exception into a socket loop that would then stop reading.
    ///
    /// **PLAN §2 IS WRONG ABOUT WHERE THE ERROR TEXT LIVES, measured 2026-08-20.** It documents
    /// `error` carrying a top-level `message`. The frame this endpoint actually sends nests it:
    ///
    ///   {"type":"error","error":{"type":"permission_error","message":"Invalid authorization",
    ///    "details":{"error_visibility":"user_facing","error_code":"account_session_invalid"}}}
    ///
    /// Both spellings are accepted, because reading the documented one only would have produced
    /// an error state with no words in it — and "on and deaf must never look like on" (§5) is
    /// exactly as broken by an empty reason as by a wrong colour.
    /// </summary>
    public static Message Parse(string json)
    {
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            if (e.ValueKind != JsonValueKind.Object) return new Message(Kind.Unknown, "");
            var type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            return type switch
            {
                "TranscriptInterim" => new Message(Kind.Interim, Str(e, "data")),
                // Delivered as NOT final despite the name (plan §2). Treating it as final is the
                // single easiest way to get double-inserted words in the box.
                "TranscriptText" => new Message(Kind.Text, Str(e, "data")),
                "TranscriptEndpoint" => new Message(Kind.Endpoint, ""),
                "TranscriptError" => new Message(Kind.TranscriptError, Str(e, "description")),
                "error" => new Message(Kind.ServerError, ErrorText(e)),
                _ => new Message(Kind.Unknown, ""),
            };
        }
        catch { return new Message(Kind.Unknown, ""); }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The nested shape first, because that is the one the endpoint actually sends;
    /// the documented flat one as a fallback.</summary>
    static string ErrorText(JsonElement e)
    {
        if (e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var m = Str(err, "message");
            var code = err.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
                ? Str(d, "error_code") : "";
            if (m.Length > 0) return code.Length > 0 ? m + " (" + code + ")" : m;
        }
        return Str(e, "message");
    }

    // ══ fatal or transient ════════════════════════════════════════════════════════════════

    /// <summary>Whether to stop for good or try again on the next toggle.</summary>
    public enum Severity { Transient, Fatal }

    /// <summary>
    /// **PLAN §2'S CLASSIFICATION CANNOT WORK, AND THIS IS SPIKE E1'S REAL FINDING.**
    ///
    /// §2 says: regex the upgrade failure for `Unexpected server response: (\d+)` and treat 4xx
    /// as fatal. Measured 2026-08-20 against the live endpoint: **the upgrade returns 101 with no
    /// Authorization header at all, and 101 again for a syntactically valid but wrong bearer.**
    /// The handshake authenticates nothing. Auth is enforced one frame later, as an `error` frame
    /// followed by a close with code **1008 PolicyViolation** and the reason "Invalid
    /// authorization".
    ///
    /// So a recogniser that only classified the HTTP status would have read a rejected credential
    /// as a successful start, sat in `Listening`, and gone deaf in silence — CLAUDE.md §0.3's
    /// believed-a-green-check, one layer down in the protocol. The status is still consulted
    /// (it is the right answer for §10's fallback 1, where auth IS on the handshake), but the
    /// frame and the close code are the readings that matter here.
    ///
    /// <paramref name="httpStatus"/> is 0 when the upgrade succeeded. <paramref name="closeCode"/>
    /// is 0 when the socket did not close.
    /// </summary>
    public static Severity Classify(int httpStatus, int closeCode, string? text)
    {
        // Still correct, still first: a 4xx handshake is unambiguous and is what fallback 1 does.
        if (httpStatus >= 400 && httpStatus < 500) return Severity.Fatal;

        var t = (text ?? "").ToLowerInvariant();

        // What the live endpoint says. `permission_error` / `account_session_invalid` /
        // "invalid authorization" are the observed spellings; the others are the neighbouring
        // words the same class of refusal is written with.
        if (t.Contains("permission_error") || t.Contains("session_invalid")
            || t.Contains("invalid authorization") || t.Contains("unauthorized")
            || t.Contains("authentication_error") || t.Contains("forbidden"))
            return Severity.Fatal;

        // 1008 PolicyViolation is what this endpoint closes an unauthenticated stream with.
        // 1000 (normal) and 1006 (abnormal//dropped) are ordinary network life and must stay
        // transient, or a flaky wifi connection would permanently disarm dictation.
        if (closeCode == 1008) return Severity.Fatal;

        return Severity.Transient;
    }

    /// <summary>The words the operator sees when auth is the problem. English, not a code — the
    /// hint line is read by someone who wants to know whether to keep talking (§7).</summary>
    public const string AuthFailedWords = "speech service refused the credential";

    /// <summary>And when there is no network at all. Named here rather than at the call site so
    /// the check and the code cannot disagree about the sentence.</summary>
    public const string NoNetworkWords = "no network";

    /// <summary>A connect that never answered. The deadline that produces it is
    /// <c>DeepgramRecognizer</c>'s; the words are here for the same reason.</summary>
    public const string NoAnswerWords = "speech service did not answer";

    // ══ holding the interim ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The interim-holding state machine — plan §2's "the extension holds the latest interim in a
    /// variable and re-emits it as final on endpoint, and flushes it on socket close".
    ///
    /// Pure and separate so it can be driven by a fake message sequence in `dev test unit`
    /// (plan §7 asks for exactly that). A socket loop with three mutable fields in it would put
    /// the one genuinely subtle piece of this engine behind a network call.
    /// </summary>
    public sealed class Turn
    {
        string _held = "";

        /// <summary>What the last interim was, for `dump.listen.partial`.</summary>
        public string Held => _held;

        /// <summary>What the window should do with one frame. <c>null</c> means "nothing to
        /// emit" — an endpoint with no interim behind it, an empty frame, an unknown kind.</summary>
        public Dictation.Heard? OnMessage(Message m, long epoch)
        {
            switch (m.Kind)
            {
                case Kind.Interim:
                case Kind.Text:
                    // Blank interims arrive; they must not wipe a held phrase that is about to
                    // be promoted, or the endpoint would finalise nothing and the words would
                    // vanish between the ear and the box.
                    if (m.Text.Trim().Length == 0) return null;
                    _held = m.Text;
                    return new Dictation.Heard(m.Text, Final: false, Epoch: epoch);

                case Kind.Endpoint:
                {
                    if (_held.Trim().Length == 0) return null;
                    var settled = _held;
                    _held = "";
                    return new Dictation.Heard(settled, Final: true, Epoch: epoch);
                }

                default:
                    return null;
            }
        }

        /// <summary>The socket died mid-utterance. Flush the held interim as final rather than
        /// losing the tail silently — plan §6: "losing the tail silently is worse than a visible
        /// reconnect", and a silent degrade is a bug (§0.1).</summary>
        public Dictation.Heard? Flush(long epoch)
        {
            if (_held.Trim().Length == 0) return null;
            var settled = _held;
            _held = "";
            return new Dictation.Heard(settled, Final: true, Epoch: epoch);
        }
    }

    // ══ keyterms ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dodona's vocabulary, **ordered by significance and not by taste** (D-E4). The order is
    /// load-bearing: <see cref="KeytermHeader"/> stops at 1024 bytes and drops the rest
    /// SILENTLY, with no error anywhere, so a word's position is the only thing deciding whether
    /// the engine has ever heard of it.
    ///
    /// The list exists because Claude Code's own extension ships eighteen terms and one of them
    /// is *worktree* — somebody there hit the exact word `VOICE-INPUT-PLAN.md` §6 predicted
    /// ("will hear *work tree* as *work three*") and fixed it the cheap way. These are Dodona's
    /// equivalents: the words that carry an instruction, where a near-miss changes the meaning
    /// rather than merely looking untidy.
    ///
    /// One place, on purpose: a term list nobody can find is a term list that goes stale.
    /// </summary>
    public static readonly string[] Keyterms =
    {
        // the nouns a sentence to Dodona is usually about
        "lane", "worktree", "daemon", "shim", "concierge", "dispatcher", "backstop",
        // the git and delivery vocabulary, where a near-miss is dangerous rather than untidy
        "ff-only", "claim", "ticket", "merge token", "publish", "hot swap", "respawn",
        // the testing vocabulary, which is most of what gets dictated in this repo
        "suite", "gate", "prove", "acceptance", "pane", "store", "workspace",
        // the machinery underneath
        "compressor", "brain", "router", "presence", "quota", "epoch", "splice",
        "WAL", "SQLite", "PowerShell",
    };

    /// <summary>The byte cap on the `x-config-keyterms` header, from the extension's own
    /// normaliser. Named rather than inlined because the silence of the truncation is the whole
    /// hazard.</summary>
    public const int KeytermBudget = 1024;

    /// <summary>
    /// Build the `x-config-keyterms` header value, copying the extension's normaliser (`lmr`)
    /// exactly:
    ///
    /// - each term: commas become spaces, non-ASCII-printable stripped, whitespace collapsed, trimmed
    /// - duplicates dropped, empties dropped
    /// - accumulate `term.length + 1` per term and **break the moment the total would exceed 1024**
    ///
    /// **KEYTERMS GO IN A HEADER, NOT AS REPEATED QUERY PARAMETERS.** `VOICE-INPUT-PLAN.md` §6.2
    /// says query parameters and is wrong; VOICE-ENGINE-PLAN §2 corrects it.
    ///
    /// Commas become spaces because the header is comma-joined: a term containing a comma would
    /// silently become two terms, one of them a fragment.
    /// </summary>
    public static string KeytermHeader(IEnumerable<string> terms, int budget = KeytermBudget)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        var total = 0;
        foreach (var raw in terms)
        {
            var term = NormalizeTerm(raw);
            if (term.Length == 0 || !seen.Add(term)) continue;
            // The extension's arithmetic, including the +1 for the joining comma that the last
            // term does not actually need. Copied rather than corrected: matching its budget
            // exactly is worth more than reclaiming one byte.
            if (total + term.Length + 1 > budget) break;
            total += term.Length + 1;
            kept.Add(term);
        }
        return string.Join(",", kept);
    }

    /// <summary>
    /// The words the engine reliably mishears, repaired on SETTLED text only (D-E23).
    ///
    /// **THIS EXISTS BECAUSE KEYTERMS DO NOT WORK, and that was measured three ways.** The header
    /// (`x-config-keyterms`), repeated `keyterm=` query parameters, and turning the conversation
    /// engine off all produced identical transcripts, and the four words the keyterm list exists
    /// for failed in every one of them: *worktree → "work tree", daemon → "demon", WAL → "wall",
    /// SQLite → "s q light"*. Whatever the proxy does with vocabulary hints, it is not boosting
    /// these.
    ///
    /// ══ WHY THIS IS NARROW, AND WHY "wall" IS DELIBERATELY NOT IN IT ══
    ///
    /// D-V9 already settled the principle at stake: **a dictation box that edits your words where
    /// you did not ask is worse than one that types the wrong thing, because the second is visible
    /// and the first is not.** That argument does not evaporate because the fix is convenient here,
    /// so the bar is: a repair is allowed ONLY when the mistaken form is not plausible English that
    /// the operator might actually have said.
    ///
    /// - `work tree` → `worktree` — "work tree" is not a phrase anybody says. Safe.
    /// - `s q light` / `sq light` → `SQLite` — likewise, not English.
    /// - `f f only` / `ff only` → `ff-only` — restores the hyphen the engine drops.
    /// - `demon` → `daemon` — a real word, but in a box whose entire purpose is orchestrating
    ///   daemons it is overwhelmingly the wrong one. Judged worth it; the reversible half is that
    ///   deleting this one line restores the literal text.
    /// - **`wall` → `WAL` is NOT here, on purpose.** "Wall" is ordinary English and this repair
    ///   would silently corrupt any sentence containing it. So WAL stays mistranscribed, visibly,
    ///   which is the honest failure. A word that cannot be repaired safely does not get repaired.
    ///
    /// Applied to final text only — never to an interim, which rewrites itself anyway — and it is a
    /// pure function, so `dev test unit` covers every case in about a millisecond.
    /// </summary>
    public static string Vocabulary(string settled)
    {
        if (string.IsNullOrEmpty(settled)) return settled;
        var s = settled;
        foreach (var (heard, meant) in Repairs)
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"\b" + System.Text.RegularExpressions.Regex.Escape(heard) + @"\b", meant,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>Longest first, so "s q light" is repaired before any shorter overlap could
    /// consume part of it.</summary>
    static readonly (string Heard, string Meant)[] Repairs =
    {
        ("s q light", "SQLite"),
        ("sq light", "SQLite"),
        ("work tree", "worktree"),
        ("f f only", "ff-only"),
        ("ff only", "ff-only"),
        ("demon", "daemon"),
    };

    static string NormalizeTerm(string? raw)
    {
        if (raw is null) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c == ',') { sb.Append(' '); continue; }
            // Printable ASCII only. A smart quote or an em dash in a term list is a header the
            // server may reject outright, and §0.2 has a whole section on non-ASCII literals.
            if (c >= 0x20 && c <= 0x7E) sb.Append(c);
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
        }
        var collapsed = new StringBuilder(sb.Length);
        var space = false;
        foreach (var c in sb.ToString())
        {
            if (c == ' ') { space = true; continue; }
            if (space && collapsed.Length > 0) collapsed.Append(' ');
            space = false;
            collapsed.Append(c);
        }
        return collapsed.ToString();
    }
}
