namespace Dodona;

/// <summary>
/// Dictation's PURE half (docs/VOICE-INPUT-PLAN.md §3, Phase A) — the same split
/// <see cref="Ask"/> and <see cref="PaneProgress"/> already use, and for the same reason: the
/// half worth testing must not need a device. No microphone, no WPF, no store, so all of it
/// lands on the ~1 second `dev test unit` loop instead of behind a window.
///
/// **THE SAFETY ARGUMENT, and it is a happy accident of the multiline work.** The codebase
/// already separates "put text in the box" (<c>MainWindow.ComposeInput</c>) from "send it"
/// (<c>MainWindow.SubmitInput</c>). Dictation needs the first and must never be able to reach
/// the second — the operator's constraint, verbatim: *"Send will still need an enter."*
///
/// That is not a rule asking future code to behave. It is enforced twice over:
///
/// 1. <see cref="DictationAct"/> HAS NO MEMBER THAT MEANS SEND. There is no value
///    <see cref="Decide"/> could return that a caller could route to a submit, so the
///    decision layer physically cannot ask for one. <c>Dictation_never_submits</c> asserts
///    that by reflection, so adding such a member goes red before anyone wires it up.
/// 2. <c>MainWindow.OnHeard</c> contains no reference to <c>SubmitInput</c>, and a `ui-use`
///    check speaks the word "enter" at a live window and demands the feed did not move.
///
/// The words "enter", "send", "submit" and "go" are therefore ORDINARY TEXT. They are not on
/// a block list that could be forgotten — they simply fall through to
/// <see cref="DictationAct.Insert"/> like every other word, because there is nowhere else for
/// them to go. Deleting the inert-word test does not make them dangerous; it only stops
/// anyone noticing if someone later builds the thing that would.
/// </summary>
public static class Dictation
{
    // ---- the state machine (§3) ------------------------------------------------------

    /// <summary>Where the microphone is. <see cref="Error"/> is a first-class state and not a
    /// flavour of <see cref="Off"/>: "on and deaf" must never render as "on" (§7, and §0.1's
    /// standing directive — a silent degrade is a bug, which is what cost two days on the
    /// routing ladder).</summary>
    public enum ListenState { Off, Starting, Listening, Error }

    /// <summary>One recognition result. <paramref name="Final"/> false is an unsettled
    /// hypothesis that will rewrite itself, and those never reach the box (D-V6).
    /// <paramref name="Epoch"/> is the submit epoch it was recognised under — see
    /// <see cref="ShouldDrop"/>, which is the one bug here a person would find baffling.</summary>
    public record Heard(string Text, bool Final, long Epoch);

    /// <summary>
    /// What a recognition result MAY do. Read the missing member: there is no Submit, no Send,
    /// no Execute. That absence is the feature (see the class note), and it is asserted.
    /// </summary>
    public enum DictationAct
    {
        /// <summary>Nothing at all — an empty utterance, or one that said only silence.</summary>
        None,
        /// <summary>Splice <see cref="Decision.Text"/> into the box at the caret.</summary>
        Insert,
        /// <summary>One newline, through the same <c>InputKey(shift: true)</c> Shift+Enter
        /// uses — not a "\n" written into Text, so the caret scroll behaviour is identical.</summary>
        Newline,
        /// <summary>Two newlines, same path.</summary>
        Paragraph,
        /// <summary>An unsettled hypothesis. Goes to <c>dump.listen.partial</c> and the hint
        /// line; never to <c>InputBox.Text</c> (D-V6).</summary>
        Partial,
        /// <summary>Recognised under a submit epoch that has since passed — the tail of a
        /// sentence already sent. Dropped and logged (<see cref="ShouldDrop"/>).</summary>
        Drop,
    }

    /// <summary>The verdict on one result. <paramref name="Text"/> is meaningful for
    /// <see cref="DictationAct.Insert"/> and <see cref="DictationAct.Partial"/>, and empty
    /// otherwise.</summary>
    public record Decision(DictationAct Act, string Text);

    /// <summary>The legal moves. Off is reachable from anywhere (the operator may switch the
    /// toggle off while it is failing), and Error is reachable from Starting and Listening —
    /// a headset walks out of range mid-utterance, which is §7's first failure.</summary>
    public static bool CanTransition(ListenState from, ListenState to) => (from, to) switch
    {
        _ when from == to => true,
        (_, ListenState.Off) => true,                       // the toggle always wins
        (ListenState.Off, ListenState.Starting) => true,
        (ListenState.Starting, ListenState.Listening) => true,
        (ListenState.Starting, ListenState.Error) => true,  // Starting has a deadline (§7)
        (ListenState.Listening, ListenState.Error) => true, // the headset walked away
        (ListenState.Error, ListenState.Starting) => true,  // retry on device arrival
        _ => false,
    };

    /// <summary>Is the microphone actually hearing anything? Error is NOT listening, and that
    /// is the whole point of the state existing (§5: a toggle that is on and deaf must never
    /// look like a toggle that is on).</summary>
    public static bool IsListening(ListenState s) => s == ListenState.Listening;

    // ---- what a result means (§4) ----------------------------------------------------

    /// <summary>
    /// Spoken punctuation, whole-utterance only. "comma" said by itself is a comma; "the grace
    /// period" is three words of text.
    ///
    /// WHOLE-UTTERANCE AND NOT WORD-LEVEL, deliberately (D-V9). Substituting inside a phrase
    /// reads better in a demo — "run the suites full stop" ending in a full stop — and it
    /// silently mangles ordinary English the moment someone says "the grace period" or "a comma
    /// separated list". A dictation box that edits your words where you did not ask is worse
    /// than one that types "period", because the second is visible and the first is not. This
    /// is the reversible half: word-level substitution can be added later behind its own
    /// checks, and nothing here has to be undone first.
    /// </summary>
    public static string? Punctuation(string spoken) => Normalize(spoken) switch
    {
        "comma" => ",",
        "full stop" or "period" => ".",
        "question mark" => "?",
        "exclamation mark" or "exclamation point" => "!",
        "colon" => ":",
        "semicolon" or "semi colon" => ";",
        _ => null,
    };

    /// <summary>
    /// The words that must do NOTHING but appear as themselves (§4's last table row, and the
    /// operator's constraint in code).
    ///
    /// Nothing consults this function to decide safety — that is the point. It exists so a test
    /// can name the words and so a reader can find them, but the words are inert because
    /// <see cref="DictationAct"/> has no send, not because they were listed. If this function
    /// were deleted the behaviour would not change; if it grew a return path into a submit,
    /// there would be nothing for it to return.
    /// </summary>
    public static bool IsInert(string spoken) => Normalize(spoken) switch
    {
        "enter" or "return" or "send" or "submit" or "go" => true,
        _ => false,
    };

    /// <summary>
    /// The submit race, which is real and easy to miss (§4). Recognition is asynchronous: the
    /// operator finishes a sentence, presses Enter, and the recogniser THEN delivers the tail of
    /// what they said — into a box that has just been cleared, where it becomes the opening
    /// words of the next message. So every result carries the epoch it was recognised under,
    /// <c>SubmitInput</c> bumps the epoch, and anything older is dropped.
    ///
    /// Strictly older. A result stamped with the CURRENT epoch is the operator still speaking
    /// and must land.
    /// </summary>
    public static bool ShouldDrop(long resultEpoch, long submitEpoch) => resultEpoch < submitEpoch;

    /// <summary>The whole decision for one result, in one pure function so the window's
    /// <c>OnHeard</c> is fifteen lines of plumbing with no judgement in it.</summary>
    public static Decision Decide(Heard h, long submitEpoch)
    {
        // The epoch first: a stale result is stale whether or not it was final, and a stale
        // PARTIAL painting the hint line would be the ghost of a sentence already sent.
        if (ShouldDrop(h.Epoch, submitEpoch)) return new Decision(DictationAct.Drop, "");

        var text = (h.Text ?? "").Trim();
        if (text.Length == 0) return new Decision(DictationAct.None, "");

        // Partials never enter InputBox.Text (D-V6): they would make `ui dump`'s `input.text`
        // non-deterministic and turn every existing input check intermittent.
        if (!h.Final) return new Decision(DictationAct.Partial, text);

        var n = Normalize(text);
        if (n is "new line" or "next line" or "newline") return new Decision(DictationAct.Newline, "");
        if (n is "new paragraph") return new Decision(DictationAct.Paragraph, "");
        if (Punctuation(text) is string p) return new Decision(DictationAct.Insert, p);

        // Everything else, INCLUDING "enter" / "send" / "submit" / "go", is text. There is no
        // branch above this one that could have taken them anywhere else.
        //
        // The vocabulary repair runs on SETTLED text only, and here rather than in the engine so
        // that `ui heard` reaches it — a repair only the socket could exercise would be a repair no
        // acceptance check could see (§3: drive the affordance, not a rehearsal of it). It is
        // deliberately narrow: see SpeechStream.Vocabulary on why "wall" is not repaired and
        // "work tree" is.
        return new Decision(DictationAct.Insert, SpeechStream.Vocabulary(text));
    }

    // ---- where it lands (§4) ---------------------------------------------------------

    /// <summary>
    /// The exact <c>(insert, newCaret)</c> a recognised phrase produces.
    ///
    /// AT THE CARET, not at the end: the operator may type and speak in the same sentence, and
    /// dictation is another way of typing — so it lands where typing lands and replaces a
    /// selection the way a typed character does, which is what <c>ComposeInput</c> already
    /// implements. <paramref name="caret"/> is the selection START, so the text before it is
    /// the context whether or not anything is selected.
    ///
    /// Spacing and capitalisation are mechanical and wrong in a way that is instantly visible:
    /// a leading space unless the box is empty or the caret already follows whitespace or an
    /// opening bracket, and a capital after a sentence end. Punctuation gets neither — a comma
    /// preceded by a space is the one output nobody would accept.
    /// </summary>
    public static (string Insert, int NewCaret) Splice(string text, int caret, int selectionLength, string heard)
    {
        text ??= "";
        heard ??= "";
        caret = Math.Max(0, Math.Min(caret, text.Length));
        var before = text[..caret];

        // Punctuation attaches to the word it follows. No space, no capital, nothing to decide.
        if (heard.Length > 0 && heard.Length <= 2 && heard.All(c => ",.?!:;".Contains(c)))
            return (heard, caret + heard.Length);

        var needSpace = before.Length > 0
                        && !char.IsWhiteSpace(before[^1])
                        && !"([{<\"'".Contains(before[^1]);

        // Capitalise at the start of the box or after a sentence end. `TrimEnd` because the
        // operator may have left a trailing space before speaking.
        var tail = before.TrimEnd();
        var capitalise = tail.Length == 0 || ".?!".Contains(tail[^1]);
        if (capitalise && heard.Length > 0 && char.IsLower(heard[0]))
            heard = char.ToUpperInvariant(heard[0]) + heard[1..];

        var insert = (needSpace ? " " : "") + heard;
        return (insert, caret + insert.Length);
    }

    // ---- what it says it is doing (§3) -----------------------------------------------

    /// <summary>
    /// The one sentence the indicator and `ui dump` both show, so the screen and the dump
    /// cannot disagree about what is happening — the same rule <see cref="PaneProgress"/>
    /// follows about the pane and the fold.
    ///
    /// An <see cref="ListenState.Error"/> NEVER contains the word "listening". That is the
    /// check <c>error_state_is_not_listening</c> and it is not pedantry: the failure this
    /// feature is most likely to ship is a toggle that reads "listening" while the engine is
    /// dead, which is precisely the silent degrade §0.1 forbids by name.
    /// </summary>
    public static string Describe(ListenState state, string? reason) => state switch
    {
        ListenState.Off => "",
        ListenState.Starting => "starting the microphone",
        ListenState.Listening => "listening",
        // The reason in WORDS, in the hint line — never a dialog (D-V3), and never a link to a
        // control panel that may not exist on that build (§7).
        ListenState.Error => string.IsNullOrWhiteSpace(reason) ? "microphone unavailable" : reason!,
        _ => "",
    };

    /// <summary>Lower-case, trimmed, inner whitespace collapsed, and trailing sentence
    /// punctuation removed — a recogniser hands back "New line." as readily as "new line",
    /// and the two must mean the same thing.</summary>
    static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var parts = s.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).TrimEnd('.', ',', '!', '?');
    }
}
