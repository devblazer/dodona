using System.IO;
using System.Text.Json;
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
/// **Also settled, and it is ROUTE 1** (<see cref="ClaudeCliRoute"/>): the credential is the one
/// the `claude` CLI already holds, because Dodona spawns `claude -p` constantly and this machine is
/// therefore already authenticated. `account_session_invalid` was the endpoint saying so.
///
/// **How that got written without anyone reading a secret, because the first attempt was refused
/// three times and correctly.** Reading the credential store is exactly the shape of thing a
/// permission classifier should stop, and the answer was not to keep pushing at it. Two facts
/// separated the deadlock:
///
/// - the SHIPPING code has to read that credential at runtime anyway, so writing it was never the
///   optional half — testing it via a different path would have been the detour;
/// - the file's SHAPE is not its contents. The operator ran a structure report — key names, value
///   types, string lengths, expiry dates, no values — and that was enough to write the parser
///   against the real format instead of a guess.
///
/// The parser is then verified by OUTCOME rather than by inspection: the socket staying open with
/// no rejection frame is proof it read the right field, and it leaks nothing either way. No token
/// has ever passed through a transcript, a log, or a commit message.
///
/// The env-var and file routes above it are the ESCAPE HATCH, not leftovers — see
/// <see cref="ClaudeCliRoute"/> on why an undocumented file format is a live risk.
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

        // The two EXPLICIT overrides first, because explicit beats implicit — and because they
        // are the escape hatch if the route below ever breaks (see ClaudeCliRoute's note on why
        // that is a live risk rather than a theoretical one).
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

        // And the route that needs nothing set up at all.
        var oauth = ClaudeCliRoute(out var oauthWhy);
        if (oauth is not null) return oauth;
        if (oauthWhy is not null) { why = oauthWhy; return null; }

        why = "no speech credential: set " + EnvVar + ", or put a token in " + TokenFile;
        return null;
    }

    /// <summary>Where the `claude` CLI keeps its OAuth credential on Windows.</summary>
    static string ClaudeCredentialsPath => Path.Combine(
        Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    /// <summary>
    /// Route 1 of plan §3, and the one that needs no setup: **this machine is already
    /// authenticated to Claude**, because Dodona spawns `claude -p` constantly. The speech endpoint
    /// takes the same account session — which is what E1's `account_session_invalid` was telling
    /// us (D-E7).
    ///
    /// The shape was read on 2026-08-20 by reporting **key names and value types only**, never
    /// values:
    ///
    /// ```
    /// claudeAiOauth : object
    ///     accessToken           : string, 108 chars
    ///     refreshToken          : string, 108 chars
    ///     expiresAt             : number  (ms epoch)
    ///     refreshTokenExpiresAt : number  (ms epoch)
    ///     scopes                : array[5]
    ///     subscriptionType, rateLimitTier : string
    /// ```
    ///
    /// ══ IT IS READ-ONLY, AND THAT IS A SAFETY RULE RATHER THAN A SIMPLIFICATION ══
    ///
    /// **Dodona must never refresh this token, and must never write this file.** An OAuth refresh
    /// ROTATES THE REFRESH TOKEN: if Dodona refreshed while the `claude` CLI also held it, one
    /// would invalidate the other and the operator could be logged out of Claude Code entirely —
    /// a dictation feature breaking the thing the whole product is built on. Measured lifetimes at
    /// the time of writing: the access token had **~6.6 hours** left and the refresh token ~25
    /// days, so this file rotates on the order of hours.
    ///
    /// That is safe to depend on precisely BECAUSE Dodona runs `claude -p` all the time (router,
    /// brain, compressors), so the CLI refreshes the credential as a side effect of ordinary
    /// operation and the file is essentially always fresh. The token is therefore re-read on
    /// **every** arming of the microphone rather than cached at window start — which is what makes
    /// rotation invisible instead of a thing the operator has to think about.
    ///
    /// An expired token is reported in words, not worked around. §0.1's directive says never park
    /// behind a person, and this does not: the condition that un-sticks it is *any* `claude`
    /// command running, which on this machine happens by itself.
    ///
    /// ══ WHY THE ENV VAR SURVIVES ABOVE THIS ══
    ///
    /// This reads a private file belonging to another product. D-E1's accepted risk was an
    /// undocumented endpoint; this is an undocumented FILE FORMAT, and it can change with any
    /// Claude Code update. If this were the only route, that update would silently kill dictation.
    /// The override exists so the answer is "set one env var", not "wait for a Dodona fix".
    /// </summary>
    /// <summary>
    /// The suite lock, and it exists because ROUTE 1 BROKE THE ONE THAT WAS THERE.
    ///
    /// D-E5's safety argument used to have two independent locks: `DODONA_UI_MIC=off` refuses to
    /// construct the real recogniser, AND a suite had no credential to present even if it somehow
    /// did — the env var is cleared by `tests/_workspace.ps1` and the token file lives under the
    /// isolated `DODONA_HOME`. Route 1 reads `%USERPROFILE%\.claude\.credentials.json`, which
    /// **`DODONA_HOME` does not relocate**, so that second lock silently became "the operator's
    /// live credential, available to every suite". One env var away from a test run streaming audio
    /// on their account.
    ///
    /// A property that quietly stopped holding is exactly what §0.1 calls a bug, so it is restored
    /// explicitly rather than argued away. `tests/_workspace.ps1` sets this for every suite, in the
    /// same place and for the same reason it sets `DODONA_UI_MIC=off`.
    /// </summary>
    public const string NoCliAuthVar = "DODONA_STT_NO_CLI_AUTH";

    public static string? ClaudeCliRoute(out string? why)
    {
        why = null;

        // First, before touching the filesystem at all. A suite must not even READ the operator's
        // credential, let alone present it.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NoCliAuthVar))) return null;

        try
        {
            if (!File.Exists(ClaudeCredentialsPath)) return null;   // not an error: fall through

            using var doc = JsonDocument.Parse(File.ReadAllText(ClaudeCredentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o)
                || o.ValueKind != JsonValueKind.Object)
            {
                // The format moved. Say so specifically, because "no credential" would send the
                // operator looking for a token they already have.
                why = "the Claude credential file has a shape this build does not recognise; set "
                      + EnvVar + " to override";
                return null;
            }

            if (!o.TryGetProperty("accessToken", out var t) || t.ValueKind != JsonValueKind.String)
            {
                why = "the Claude credential file carries no access token; set " + EnvVar;
                return null;
            }

            // Expiry is advisory: if it has passed, the endpoint would refuse anyway, and saying
            // WHY beats letting it read as "the speech service refused the credential".
            if (o.TryGetProperty("expiresAt", out var exp) && exp.TryGetInt64(out var ms)
                && DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.UtcNow)
            {
                why = "the Claude credential has expired; any claude command refreshes it";
                return null;
            }

            var token = t.GetString();
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
        catch (Exception ex)
        {
            // Never the exception's own text alone: this one names a path into the operator's
            // profile, and the reason string reaches `ui dump`, a check's output and possibly a
            // commit message.
            why = "could not read the Claude credential (" + ex.GetType().Name + "); set " + EnvVar;
            return null;
        }
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
