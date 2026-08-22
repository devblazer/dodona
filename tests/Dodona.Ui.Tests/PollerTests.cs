using System.Text.Json;
using DodonaUi;
using Xunit;

namespace Dodona.Ui.Tests;

/// <summary>
/// The two decisions <see cref="Poller"/> makes about what a tile SAYS, asked directly.
///
/// Moved here by slice S-POLLER (docs/TEST-ARCHITECTURE-PLAN.md W8) out of
/// `ui-grid-acceptance.ps1`, where each of them cost a live WPF window, a daemon, a shim and a
/// real child process taking a fourteen-second turn. Neither is a WIRE: the wire is
/// `ui-grid:the_agent_answered` and `ui-grid:the_newline_survived_to_the_agent`, which stay,
/// and what flows down it is content (plan sec 1.5, COUNT THE WIRES, NOT THE CASES).
/// </summary>
public class PollerLivenessTests
{
    /// <summary>
    /// **Moved from `ui-grid:liveness_shows_a_moving_clock`** (ui-grid-acceptance.ps1:379,
    /// deleted in the same commit). The old check drove a fourteen-second fake-agent turn and
    /// asserted `$mid.presence -match '\d+s'` off a live `ui dump`. That is one predicate over
    /// a pure static that already took `now` as a parameter (plan sec 3.7: no IClock, time is an
    /// argument), so the whole bucket table is asked here and the window is asked nothing.
    ///
    /// Proved by `tests/mutants/s-poller-01.patch`, which widens the 10-second threshold so
    /// the clock never appears. The paired red is recorded in
    /// `tests/ledger/moves/s-poller.tsv`.
    ///
    /// The rows are the rules in docs/LANE-LIFECYCLE.md sec 5, which Poller.cs:57-64 states: the
    /// clock is WITHHELD for ten seconds so a snappy turn does not flicker digits; it is
    /// bucketed to ten seconds so the snapshot JSON only changes on that cadence; long silence
    /// is `quiet Nm`, a neutral word, because a big think is legitimate; and a lane that is not
    /// working gets no clock at all, because a clock on `idle` would be counting nothing.
    /// </summary>
    [Theory]
    // THE OLD CHECK'S OWN CASE, first: mid-turn, past the threshold, so digits appear.
    [InlineData("working", "alive", 14, "working 10s")]
    // Withheld under ten seconds.
    [InlineData("working", "alive", 5, "working")]
    [InlineData("working", "alive", 9, "working")]
    // Bucketed DOWN to ten seconds, never rounded.
    [InlineData("working", "alive", 19, "working 10s")]
    [InlineData("working", "alive", 59, "working 50s")]
    // Past a minute it reads as minutes and seconds, zero-padded.
    [InlineData("working", "alive", 90, "working 1m30s")]
    [InlineData("working", "alive", 65, "working 1m00s")]
    // Five minutes of silence is `quiet`, and it keeps the presence it interrupted.
    [InlineData("working", "alive", 300, "quiet 5m (working)")]
    [InlineData("working", "alive", 3600, "quiet 60m (working)")]
    // Not busy: no clock, whatever the elapsed time says.
    [InlineData("idle", "alive", 900, "idle")]
    [InlineData("landed", "alive", 900, "landed")]
    [InlineData("system", "alive", 900, "system")]
    [InlineData("waiting on you: merge", "alive", 900, "waiting on you: merge")]
    [InlineData("", "alive", 900, "")]
    // The state overrides everything, including a running clock.
    [InlineData("working", "unreachable", 900, "unreachable")]
    [InlineData("working", "dormant", 900, "working")]
    [InlineData("", "dormant", 900, "landed")]
    public void liveness_shows_a_moving_clock(string presence, string state, int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, Poller.Liveness(presence, state, now.AddSeconds(-secondsAgo), now));
    }

    /// <summary>A lane nothing has been heard from has no elapsed time to report, so the
    /// presence stands as written. This is the null arm of the same predicate and it has no
    /// old check behind it -- declared growth, `tests/ledger/added.tsv`.</summary>
    [Fact]
    public void A_lane_with_no_last_seen_gets_no_clock()
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal("working", Poller.Liveness("working", "alive", null, now));
    }
}

/// <summary>
/// The five-hour quota line, rendered from the bytes the CLI pushed unasked.
///
/// **NOT faked at the reader.** `Poller.QuotaLine(string? raw, DateTime now)` takes the kv
/// value itself, which is what `LaneRuntime` wrote at LaneRuntime.cs:217; the instance
/// overload binds `_reader.Kv("rate_limit")` to it, so production keeps exactly one path
/// (Trees.cs:44 + :77). Plan sec 3.5 forbids faking `StoreReader` away from a real store, and
/// this seam means nobody has to.
/// </summary>
public class PollerQuotaTests
{
    /// <summary>The shape `LaneRuntime` writes: its own `observedTs` and `lane`, wrapping the
    /// CLI's `rate_limit_info` object verbatim (LaneRuntime.cs:216-222). The field names here
    /// are the CLI's own, observed live 2026-08-17 and reproduced by `DodonaFakeAgent`
    /// (Program.cs:494-501) -- which is the shape the deleted acceptance check actually put on
    /// the wire.</summary>
    static string Kv(double utilization, DateTime observedTs, string type = "five_hour",
                     bool overage = false, long? resetsAt = null) =>
        JsonSerializer.Serialize(new
        {
            observedTs = observedTs.ToString("o"),
            lane = 3,
            info = new
            {
                status = "allowed_warning",
                resetsAt = resetsAt ?? DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
                rateLimitType = type,
                utilization,
                isUsingOverage = overage,
            },
        });

    /// <summary>
    /// **Moved from `ui-grid:quota_line_from_wire`** (ui-grid-acceptance.ps1:390, deleted in
    /// the same commit). The old check told a fake agent `ratelimit:0.42`, waited twenty
    /// seconds for a live `ui dump` and asserted `$d.quota -match '5h window 42%'`.
    ///
    /// The CROSSING half of that -- a `rate_limit_event` on the agent's wire reaching kv -- is
    /// not lost with it: `Dodona.Tests.WireCorpusTests` asserts it at line 171 against REAL
    /// RECORDED BYTES from `tests/assets/wire/real/wire.jsonl`, which is stronger evidence than
    /// a fake agent's re-emission of the same shape. What the acceptance check uniquely held
    /// was the rendering, and that is here.
    ///
    /// Proved by `tests/mutants/s-poller-02.patch`.
    /// </summary>
    [Fact]
    public void quota_line_from_wire()
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        var line = Poller.QuotaLine(Kv(0.42, now), now);
        Assert.NotNull(line);
        Assert.StartsWith("5h window 42%", line);
    }

    /// <summary>The number is the CLI's own and is never estimated, so it is printed to the
    /// unit and not rounded into a bucket. Declared growth.</summary>
    [Theory]
    [InlineData(0.0, "5h window 0%")]
    [InlineData(0.426, "5h window 43%")]
    [InlineData(0.97, "5h window 97%")]
    [InlineData(1.0, "5h window 100%")]
    public void The_percentage_is_the_wire_value_not_a_bucket(double utilization, string expected)
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        Assert.StartsWith(expected, Poller.QuotaLine(Kv(utilization, now), now));
    }

    /// <summary>"It only updates when a lane takes a turn, so it always carries its age -- a
    /// stale number presented as live is how a fleet dies at 4pm with no warning" (Poller.cs's
    /// own comment, sec 2.6). Under two minutes there is nothing worth saying; past it, the age
    /// is stated. Declared growth.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(45, true)]
    public void A_reading_older_than_two_minutes_says_how_old_it_is(int minutesAgo, bool expectAge)
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        var line = Poller.QuotaLine(Kv(0.42, now.AddMinutes(-minutesAgo)), now);
        Assert.NotNull(line);
        if (expectAge) Assert.Contains($"as of {minutesAgo}m ago", line);
        else Assert.DoesNotContain("as of", line);
    }

    /// <summary>Overage is called by its name, in capitals, because it is the one state that
    /// costs money. Declared growth.</summary>
    [Fact]
    public void Overage_is_named()
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        Assert.Contains("OVERAGE", Poller.QuotaLine(Kv(0.42, now, overage: true), now));
    }

    /// <summary>A reset time is offered when the wire carried one, and the line survives
    /// without it. The clock is local -- the operator reads it -- so the assertion is the
    /// SHAPE, never a particular hour, which would make this test a passenger of whatever
    /// timezone the machine is in. Declared growth.</summary>
    [Fact]
    public void A_reset_time_is_offered_when_the_wire_carried_one()
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        var at = new DateTimeOffset(now.AddHours(2)).ToUnixTimeSeconds();
        Assert.Matches(@"resets \d\d:\d\d", Poller.QuotaLine(Kv(0.42, now, resetsAt: at), now));
    }

    /// <summary>Silence rather than a guess, in every case where the reading is not one this
    /// column can honestly show. A wrong quota number is worse than none: the whole reason it
    /// is carried at all is that it is the CLI's own, not an estimate. Declared growth.</summary>
    [Theory]
    [InlineData(null)]                              // nothing has ever arrived
    [InlineData("")]                                // arrived empty
    [InlineData("not json at all")]
    [InlineData("{}")]                              // no info object
    [InlineData("{\"info\":{}}")]                   // no utilization
    [InlineData("{\"info\":{\"rateLimitType\":\"seven_day\",\"utilization\":0.42}}")]
    [InlineData("{\"info\":{\"utilization\":-1}}")]
    public void An_unreadable_reading_renders_nothing(string? raw)
    {
        var now = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);
        Assert.Null(Poller.QuotaLine(raw, now));
    }
}
