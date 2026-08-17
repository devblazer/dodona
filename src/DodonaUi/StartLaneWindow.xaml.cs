using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DodonaUi;

/// <summary>
/// How work starts from the UI. Without this the grid could only watch lanes somebody
/// else had created from a terminal, which made a fresh project a dead end — you type
/// into the dispatcher, there is nowhere to route, and the app tells you to go run a
/// command. Everything here is still just a daemon message: this window composes
/// `lane-start`, or `ticket-create` + `ticket-agent`, exactly as the CLI would.
/// </summary>
public partial class StartLaneWindow : Window
{
    readonly string _instanceId;

    /// <summary>Set when a lane was started, so the caller can deliver whatever the user
    /// had already typed into the dispatcher box.</summary>
    public long StartedLane { get; private set; } = -1;

    public StartLaneWindow(string instanceId, string? suggestedName, string? firstMessage)
    {
        _instanceId = instanceId;
        InitializeComponent();
        NameBox.Text = suggestedName ?? "";
        if (firstMessage is { Length: > 0 })
            Sub.Text = $"A lane is a conversation with an agent. It will start with: “{Truncate(firstMessage, 90)}”";
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); DescribeRepos(); };
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Tickets need a repository; say up front which ones exist so the claim
    /// paths can be written correctly the first time (they are workspace-relative).</summary>
    void DescribeRepos()
    {
        try
        {
            var json = DaemonClient.Send(_instanceId, new { cmd = "repo-status" });
            using var d = JsonDocument.Parse(json);
            var isRepo = d.RootElement.GetProperty("isRepo").GetBoolean();
            var nested = d.RootElement.GetProperty("nested").EnumerateArray().Select(x => x.GetString()!).ToList();

            if (isRepo) { RepoNote.Text = "Claim paths are relative to the project root."; return; }
            if (nested.Count > 0)
            {
                RepoNote.Text = $"This workspace holds {nested.Count} repositories ({string.Join(", ", nested)}). " +
                                "Start claim paths with one of those names — a ticket belongs to exactly one repository.";
                return;
            }
            TicketBox.IsEnabled = false;
            TicketBox.ToolTip = "No git repository here yet";
            RepoNote.Text = "";
            Problem.Text = "No git repository in this project, so a ticket has nothing to branch. " +
                           "A plain lane works now; create a repository when you want isolated work.";
            Problem.Visibility = Visibility.Visible;
        }
        catch { /* the daemon will say so plainly when Start is pressed */ }
    }

    void Ticket_Toggled(object sender, RoutedEventArgs e)
    {
        ClaimPanel.Visibility = TicketBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TicketBox.IsChecked == true) ClaimBox.Focus();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Start_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Fail("A lane needs a name — one word, like SETTINGS or WATER."); NameBox.Focus(); return; }
        if (name.Contains(' ')) { Fail("One word, please — the name is the pane's heading and a routing prefix."); NameBox.Focus(); return; }

        var claims = ClaimBox.Text.Split(new[] { '\n', '\r', ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var wantsTicket = TicketBox.IsChecked == true;
        if (wantsTicket && claims.Length == 0) { Fail("A ticket needs at least one claim — what may it touch?"); ClaimBox.Focus(); return; }

        Working(true);
        Task.Run(() =>
        {
            string reply;
            long lane = -1;
            if (wantsTicket)
            {
                var made = DaemonClient.Send(_instanceId, new { cmd = "ticket-create", title = name, mode = "on-approval", repo = (string?)null, claims });
                var m = System.Text.RegularExpressions.Regex.Match(made, @"^ticket (\d+)");
                if (!m.Success) { reply = made; }
                else
                {
                    var agent = DaemonClient.Send(_instanceId, new { cmd = "ticket-agent", ticket = long.Parse(m.Groups[1].Value) });
                    reply = agent;
                    var lm = System.Text.RegularExpressions.Regex.Match(agent, @"^lane (\d+)");
                    if (lm.Success) lane = long.Parse(lm.Groups[1].Value);
                }
            }
            else
            {
                reply = DaemonClient.Send(_instanceId, new { cmd = "lane-start", title = name });
                var lm = System.Text.RegularExpressions.Regex.Match(reply, @"^lane (\d+)");
                if (lm.Success) lane = long.Parse(lm.Groups[1].Value);
            }

            Dispatcher.Invoke(() =>
            {
                Working(false);
                if (lane < 0) { Fail(reply); return; }
                StartedLane = lane;
                DaemonClient.Send(_instanceId, new { cmd = "focus", lane });
                DialogResult = true;
                Close();
            });
        });
    }

    void Working(bool on)
    {
        StartBtn.IsEnabled = !on;
        NameBox.IsEnabled = !on;
        Busy.Text = on ? "starting the agent…" : "";
        if (on) Problem.Visibility = Visibility.Collapsed;
    }

    void Fail(string message)
    {
        Problem.Text = message;
        Problem.Visibility = Visibility.Visible;
    }
}
