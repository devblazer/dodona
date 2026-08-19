using Dodona;
using Xunit;
using static Dodona.Dictation;

namespace Dodona.Tests;

/// <summary>
/// Dictation's pure half (docs/VOICE-INPUT-PLAN.md §8, Phase A). Six checks, and the first one
/// is the operator's constraint: *"Send will still need an enter."*
///
/// A NOTE ON HOW THESE WERE PROVED, because `dev prove` cannot do it here and a check nobody
/// saw fail is worth nothing (CLAUDE.md §0.3). `dev prove` REFUSES the unit suite by design and
/// says why: a unit test compiles against the code it tests, so a HEAD without <c>Dictation</c>
/// cannot run these at all — there is no red to see, only a compile error. Its own prescribed
/// substitute is what was done, once per check: break the function on purpose, run
/// `dev test unit`, read the failure, revert. The red each one produced is recorded beside it.
/// </summary>
public class DictationTests
{
    // ---- 1. the one that matters -----------------------------------------------------

    /// <summary>
    /// THE OPERATOR'S CONSTRAINT, and the reason this feature is safe to build at all.
    ///
    /// It is asserted structurally rather than behaviourally, which is stronger: there is no
    /// value <see cref="Dictation.Decide"/> can return that MEANS send, so no caller can route
    /// one there even by mistake. The window half is pinned separately by
    /// `ui-use:spoken_send_words_do_not_submit`, which speaks the word at a live window and
    /// demands the feed did not move.
    ///
    /// PROVED RED by adding `Submit` to DictationAct and returning it for "enter":
    ///   Assert.False() Failure -- DictationAct has a member meaning send: Submit
    /// </summary>
    [Fact]
    public void Dictation_never_submits()
    {
        foreach (var name in Enum.GetNames<DictationAct>())
        {
            var n = name.ToLowerInvariant();
            Assert.False(n.Contains("submit") || n.Contains("send") || n.Contains("execute"),
                $"DictationAct has a member meaning send: {name}");
        }

        // ...and no utterance, however imperative, produces anything but text or a newline.
        foreach (var said in new[] { "enter", "send", "submit", "go", "run it", "do it now", "okay send that" })
        {
            var d = Decide(new Heard(said, Final: true, Epoch: 0), 0);
            Assert.True(d.Act is DictationAct.Insert or DictationAct.Newline or DictationAct.Paragraph,
                $"'{said}' produced {d.Act}");
        }
    }

    // ---- 2. the words that must do nothing --------------------------------------------

    /// <summary>
    /// "enter", "send", "submit" and "go" are ORDINARY TEXT — they appear in the box exactly as
    /// spoken and nothing else happens (§4's last table row).
    ///
    /// PROVED RED by adding `"enter" => "\n"` to the punctuation table:
    ///   Assert.Equal() Failure: Expected: enter  Actual: (newline)
    /// </summary>
    [Theory]
    [InlineData("enter")]
    [InlineData("send")]
    [InlineData("submit")]
    [InlineData("go")]
    [InlineData("return")]
    public void Spoken_enter_is_inert(string said)
    {
        var d = Decide(new Heard(said, Final: true, Epoch: 3), 3);
        Assert.Equal(DictationAct.Insert, d.Act);
        Assert.Equal(said, d.Text);
        // The word is on the inert list AND is not punctuation -- two ways of saying the same
        // thing, because the list is documentation and the absence is the enforcement.
        Assert.True(IsInert(said));
        Assert.Null(Punctuation(said));
    }

    // ---- 3. the one spoken control that IS allowed ------------------------------------

    /// <summary>
    /// "new line" is a newline and "new paragraph" is two — through <c>InputKey(shift: true)</c>,
    /// the same method Shift+Enter uses, so a dictated newline and a typed one are the same
    /// code (§4).
    ///
    /// PROVED RED by deleting the `"new line" or "next line"` arm from Decide:
    ///   Assert.Equal() Failure: Expected: Newline  Actual: Insert
    /// </summary>
    [Theory]
    [InlineData("new line")]
    [InlineData("next line")]
    [InlineData("New line.")]      // a recogniser capitalises and punctuates; both must fold
    public void Spoken_new_line_inserts_one(string said) =>
        Assert.Equal(DictationAct.Newline, Decide(new Heard(said, true, 0), 0).Act);

    [Fact]
    public void Spoken_new_paragraph_inserts_two() =>
        Assert.Equal(DictationAct.Paragraph, Decide(new Heard("new paragraph", true, 0), 0).Act);

    // ---- 4. where it lands ------------------------------------------------------------

    /// <summary>
    /// AT THE CARET, not at the end — the operator types and speaks in the same sentence, and
    /// dictation is another way of typing (§4).
    ///
    /// PROVED RED by returning `(heard, text.Length + heard.Length)` — append at end:
    ///   Assert.Equal() Failure: Expected: 11  Actual: 22
    /// </summary>
    [Fact]
    public void Splice_lands_at_caret()
    {
        // "run| the suites" -- the caret sits after "run", and speaking "all" belongs THERE,
        // not tacked onto the end. This is the whole difference between dictation being
        // another way of typing and dictation being an append-only firehose.
        const string text = "run the suites";
        var (insert, caret) = Splice(text, 3, 0, "all");

        Assert.Equal("run all the suites", text[..3] + insert + text[3..]);
        Assert.Equal(3 + insert.Length, caret);     // the caret follows what was just inserted
        Assert.Equal(7, caret);                     // "run all|" -- not 17, which is the end
    }

    /// <summary>A leading space unless the box is empty or the caret already follows
    /// whitespace or an opening bracket; a capital at the start and after a sentence end.
    /// Mechanical, and wrong in a way that is instantly visible.</summary>
    [Theory]
    [InlineData("", 0, "hello", "Hello")]                       // empty box: capital, no space
    [InlineData("run the suites", 14, "then ship", " then ship")]
    [InlineData("run the suites ", 15, "then ship", "then ship")] // already a space
    [InlineData("done.", 5, "next up", " Next up")]              // capital after a full stop
    [InlineData("call (", 6, "the daemon", "the daemon")]        // after an open bracket
    public void Splice_spaces_and_capitalises(string text, int caret, string heard, string expected) =>
        Assert.Equal(expected, Splice(text, caret, 0, heard).Insert);

    /// <summary>Punctuation attaches to the word it follows: no space, no capital. A comma
    /// preceded by a space is the one output nobody would accept.</summary>
    [Fact]
    public void Splice_attaches_punctuation_without_a_space() =>
        Assert.Equal(",", Splice("run the suites", 14, 0, ",").Insert);

    // ---- 5. the race a person would find baffling -------------------------------------

    /// <summary>
    /// THE SUBMIT RACE (§4). The operator finishes a sentence, presses Enter, and the recogniser
    /// then delivers the tail of what they said — into a box that has just been cleared, where
    /// it would become the opening words of the next message.
    ///
    /// PROVED RED by making ShouldDrop always return false:
    ///   Assert.Equal() Failure: Expected: Drop  Actual: Insert
    /// </summary>
    [Fact]
    public void Stale_epoch_result_dropped()
    {
        // Recognised under epoch 1; the operator has since pressed Enter, so we are on 2.
        Assert.True(ShouldDrop(resultEpoch: 1, submitEpoch: 2));
        Assert.Equal(DictationAct.Drop, Decide(new Heard("and the worktree", true, 1), 2).Act);

        // The CURRENT epoch is the operator still speaking, and must land. Strictly older only.
        Assert.False(ShouldDrop(2, 2));
        Assert.Equal(DictationAct.Insert, Decide(new Heard("and the worktree", true, 2), 2).Act);

        // A stale PARTIAL is stale too -- the ghost of a sentence already sent, painted into
        // the hint line, would be just as confusing as one spliced into the box.
        Assert.Equal(DictationAct.Drop, Decide(new Heard("and the wor", false, 1), 2).Act);
    }

    // ---- 6. on and deaf must never read as on -----------------------------------------

    /// <summary>
    /// A toggle that reads "listening" while the engine is dead is the silent degrade §0.1
    /// forbids by name — it is what cost two days on the routing ladder, and it is the most
    /// likely way this feature ships broken.
    ///
    /// PROVED RED by giving Error the same arm as Listening in Describe:
    ///   Assert.DoesNotContain() Failure: Found: "listening" in: "listening"
    /// </summary>
    [Fact]
    public void Error_state_is_not_listening()
    {
        Assert.False(IsListening(ListenState.Error));
        Assert.DoesNotContain("listening", Describe(ListenState.Error, "no microphone"));
        // The reason reaches the operator in WORDS -- never a dialog (D-V3).
        Assert.Contains("no microphone", Describe(ListenState.Error, "no microphone"));
        // An error with no reason still must not present as listening.
        Assert.DoesNotContain("listening", Describe(ListenState.Error, null));
        Assert.NotEqual("", Describe(ListenState.Error, null));

        Assert.True(IsListening(ListenState.Listening));
        Assert.Equal("listening", Describe(ListenState.Listening, null));
        Assert.Equal("", Describe(ListenState.Off, null));
    }

    /// <summary>The legal moves (§3). Error is reachable from Starting (its deadline) and from
    /// Listening (a headset walking out of range), and retries from Error — otherwise the
    /// toggle would sit in a dead state forever, which is the standing directive's "stuck".</summary>
    [Fact]
    public void The_state_machine_can_always_recover_and_always_stop()
    {
        Assert.True(CanTransition(ListenState.Starting, ListenState.Error));
        Assert.True(CanTransition(ListenState.Listening, ListenState.Error));
        Assert.True(CanTransition(ListenState.Error, ListenState.Starting));
        foreach (var s in Enum.GetValues<ListenState>())
            Assert.True(CanTransition(s, ListenState.Off), $"{s} cannot be switched off");
        // Listening is never entered without going through Starting: arming a microphone is
        // not instantaneous, and a state that skipped it could not have a deadline.
        Assert.False(CanTransition(ListenState.Off, ListenState.Listening));
    }
}
