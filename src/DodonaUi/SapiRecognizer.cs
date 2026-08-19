using System.Speech.Recognition;
using Dodona;

namespace DodonaUi;

/// <summary>
/// The real engine: SAPI, via the in-box `System.Speech` recogniser (docs/VOICE-INPUT-PLAN.md
/// §6). One PackageReference, no TFM change, no model download, no network, no key — which is
/// the whole reason it is the one wired first.
///
/// **WHAT THIS IS NOT.** It is not the engine the plan chose, because the plan has not chosen
/// one: D-V8 says the engine is settled by Spike 4 — twenty recorded sentences of real operator
/// speech, SAPI against Whisper.net, both biased with Dodona's own vocabulary, scored on word
/// error rate over the technical words and reported with latency. That spike needs a human
/// talking into a microphone and has not been run. §6 predicts SAPI will hear "worktree" as
/// "work three", and Claude Code's own extension shipping an 18-word keyterms list containing
/// *worktree* is direct evidence the prediction is about the right word.
///
/// So this is the seam being real rather than a stub, and nothing more. It compiles and it is
/// wired; **whether it hears anything on this machine is unverified**, because verifying it
/// requires a voice. No suite depends on it: every suite runs with `DODONA_UI_MIC=off`, which
/// makes <see cref="Recognizers.Create"/> refuse to construct this class at all (D-V4).
///
/// THE EPOCH IS STAMPED WHEN THE UTTERANCE STARTS, NOT WHEN IT FINISHES, and that is the whole
/// point of the field. The race (§4) is: the operator finishes speaking, presses Enter, and the
/// engine THEN delivers the tail — so a result stamped at delivery would carry the new epoch and
/// the guard would never fire. An utterance begun before the submit belongs to the message that
/// was sent, so it is stamped at <c>SpeechDetected</c> and dropped downstream.
/// </summary>
sealed class SapiRecognizer : IRecognizer
{
    readonly SpeechRecognitionEngine _engine;
    readonly Func<long> _epochNow;
    long _utteranceEpoch;
    bool _running;

    public event Action<Dictation.Heard>? Heard;
    public event Action<string>? Failed;
    public string Engine => "sapi";

    /// <summary>Throws if no recogniser is installed for this locale — which is why
    /// <see cref="Recognizers.Create"/> is the only caller and wraps it. <paramref name="epochNow"/>
    /// reads the window's current submit epoch; it is a delegate rather than a value because the
    /// window bumps it on every send.</summary>
    public SapiRecognizer(Func<long>? epochNow = null)
    {
        _epochNow = epochNow ?? (() => 0);
        _engine = new SpeechRecognitionEngine();
        // Free-form dictation, not a command grammar. A command grammar is how this becomes a
        // spoken control surface over an orchestrator, which §10 rules out explicitly.
        _engine.LoadGrammar(new DictationGrammar());

        _engine.SpeechDetected += (_, _) => _utteranceEpoch = _epochNow();
        _engine.SpeechHypothesized += (_, e) =>
            Heard?.Invoke(new Dictation.Heard(e.Result.Text, Final: false, Epoch: _utteranceEpoch));
        _engine.SpeechRecognized += (_, e) =>
            Heard?.Invoke(new Dictation.Heard(e.Result.Text, Final: true, Epoch: _utteranceEpoch));

        // A rejected utterance is not a failure — it is a cough, or the operator talking to
        // someone else. Silence is the correct response; announcing it would make the indicator
        // flicker into `error` constantly.
        _engine.RecognizeCompleted += (_, e) =>
        {
            if (e.Error is not null) Failed?.Invoke(Recognizers.Describe(e.Error));
        };
    }

    /// <summary>Never throws (the interface's contract). Both failures that matter live here:
    /// no capture device at all, and a device Windows will not hand over — a call has it, or
    /// speech is switched off in privacy settings.</summary>
    public void Start()
    {
        if (_running) return;
        try
        {
            _engine.SetInputToDefaultAudioDevice();
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            _running = true;
        }
        catch (InvalidOperationException)
        {
            // What SAPI raises when there is no capture endpoint. Said in words a person can
            // act on, never as an error code (§7).
            Failed?.Invoke("no microphone");
        }
        catch (Exception ex)
        {
            Failed?.Invoke(Recognizers.Describe(ex));
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _engine.RecognizeAsyncCancel(); } catch { /* stopping must not be able to fail */ }
    }

    public void Dispose()
    {
        Stop();
        try { _engine.Dispose(); } catch { /* ditto, on the way out */ }
    }
}
