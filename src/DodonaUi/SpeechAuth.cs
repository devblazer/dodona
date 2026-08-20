using System.IO;
using Dodona;

namespace DodonaUi;

/// <summary>
/// Where the bearer token for the speech endpoint comes from (docs/VOICE-ENGINE-PLAN.md §3,
/// spike E1) — and, just as importantly, where it does NOT come from.
///
/// ══ WHAT SPIKE E1 SETTLED AND WHAT IT DID NOT ══
///
/// Settled, by measurement on 2026-08-20: the endpoint is live, the upgrade authenticates nothing
/// (101 with no header at all), and a wrong credential is refused one frame later with
/// `permission_error` / `account_session_invalid` and a 1008 close. So the wire contract is known
/// and the refusal is cleanly detectable.
///
/// **NOT settled: which credential this machine should present.** Plan §3 lists three routes, best
/// first: (1) whatever the `claude` CLI already holds, since Dodona spawns `claude -p` constantly
/// and the machine is therefore already authenticated; (2) the extension's own stored credential;
/// (3) an explicit token the operator sets once.
///
/// **Routes 1 and 2 are not implemented, and that is deliberate rather than unfinished.** Reading
/// the CLI's credential store was refused by the permission classifier in the session that wrote
/// the plan and again in the session that built this, both times correctly — it is
/// credential-handling code. Guessing at that file's shape and shipping an unverified parser for
/// it would be worse than leaving it out: it would be code that cannot be tested, in the one place
/// where a silent wrong answer looks exactly like "the service is down". So route 3 is what is
/// wired, it needs no credential discovery at all, and it is honest about what is happening.
/// <see cref="ClaudeCliRoute"/> is the named, single place route 1 lands when the operator decides
/// how they want it read.
///
/// ══ A SAFETY PROPERTY WORTH STATING, because it is why no suite can spend money ══
///
/// Neither source can exist inside a suite. The env var is not set by `tests/_workspace.ps1`, and
/// the file is resolved under <see cref="Paths.Home"/> — which every suite relocates to an
/// isolated `DODONA_HOME` (§5). So a suite has no credential even if it somehow constructed the
/// real recogniser, which is a second lock behind `DODONA_UI_MIC=off` and the reason D-E5 can be
/// stated as strongly as it is: *no suite ever opens a socket*, and one that did could not
/// authenticate.
/// </summary>
static class SpeechAuth
{
    /// <summary>The env var, for a shell that wants to hand a token to one run.</summary>
    public const string EnvVar = "DODONA_STT_TOKEN";

    /// <summary>The file, for the operator who wants to set it once. Under DODONA_HOME beside
    /// `ui.json` — not in the repo, because §5 is absolute that Dodona's state is never repo
    /// content, and a token in a git worktree is one `git add -A` from being published.</summary>
    public static string TokenFile => Path.Combine(Paths.Home, "stt-token.txt");

    /// <summary>
    /// The token, or null with <paramref name="why"/> set to words the operator can act on.
    ///
    /// Never logs, never echoes, and never puts any part of the token in <paramref name="why"/>.
    /// A reason string reaches the hint line, `ui dump`, and from there a check's output and a
    /// commit message — so a reason carrying four characters of a bearer would be a credential
    /// written into git history, which is a worse outcome than the failure it was describing.
    /// </summary>
    public static string? Token(out string? why)
    {
        why = null;

        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        try
        {
            if (File.Exists(TokenFile))
            {
                // First non-empty, non-comment line, so the operator can leave themselves a note
                // in the file about where the value came from and when it expires.
                foreach (var raw in File.ReadAllLines(TokenFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    return line;
                }
            }
        }
        catch (Exception ex)
        {
            // An unreadable token file is worth saying out loud, unlike a corrupt ui.json: a
            // preference falling back to a default is invisible and fine, a credential falling
            // back to nothing means dictation is off and the operator needs to know which of the
            // two things went wrong.
            why = "could not read the speech token file: " + ex.Message;
            return null;
        }

        why = "no speech credential: set " + EnvVar + ", or put a token in " + TokenFile;
        return null;
    }

    /// <summary>
    /// Route 1 of plan §3, left as a named seam rather than a guess.
    ///
    /// The idea is sound and it is the route that needs no new secret and no new spend: this
    /// machine authenticates to Claude every time Dodona spawns `claude -p`, so the credential is
    /// already here. What is missing is permission to look at where it lives — and an unverified
    /// parser of a credential store is exactly the kind of code that must not be written on a
    /// guess (see the class note).
    ///
    /// When the operator decides, this is the one place it goes, and <see cref="Token"/> gains one
    /// line calling it. Deliberately not called today: a method that quietly probed credential
    /// stores would be doing the thing the classifier refused, one release later.
    /// </summary>
    public static string? ClaudeCliRoute() => null;
}
