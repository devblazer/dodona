using DodonaUi;
using Xunit;

namespace Dodona.Ui.Tests;

/// <summary>
/// The one trivial fact this project was stood up around (docs/TEST-ARCHITECTURE-PLAN.md W3.0).
/// It is trivial on purpose -- the deliverable is the PROJECT, not coverage -- but it is not
/// empty, and what it asserts was chosen so that the project proves the thing it exists for on
/// the day it lands.
///
/// It touches a real internal type in a net8.0-windows PRODUCTION assembly, which is exactly
/// what tests\Dodona.Tests can never do. So it is the smallest possible demonstration of all
/// three W3.0 claims at once: the framework reaches DodonaUi, InternalsVisibleTo reaches its
/// internals, and `dev prove --with` can redden a check whose subject lives in src\DodonaUi.
///
/// AND IT IS THE ARRIVAL CONTRACT, which is the sentence IRecognizer's own doc comment makes:
/// "exactly one of Ready or Failed arrives, exactly once, and one of them always does". The
/// plan's 3.2 names that contract as the thing that closes behaviour drift for this double,
/// so the first fact in the project is the half of it the fake is responsible for.
/// </summary>
public class RecognizerArrivalTests
{
    [Fact]
    public void The_fake_recogniser_raises_Ready_exactly_once_from_Start()
    {
        var r = new FakeRecognizer();
        var ready = 0;
        var failed = 0;
        r.Ready += () => ready++;
        r.Failed += _ => failed++;

        r.Start();

        // Exactly once, and synchronously. Recognizer.cs:99-111 says why in as many words:
        // ArmMic waits for this event now, so a fake that stayed silent would leave every
        // suite in `starting` and redden all eighteen voice checks. The fake opens nothing,
        // so it has nothing to wait for and nothing to lie about.
        Assert.Equal(1, ready);
        Assert.Equal(0, failed);
    }
}
