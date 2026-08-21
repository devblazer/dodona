# SEAM SURVEY — where a test double can go, and where it must not

Read: all of `CLAUDE.md`; `src/Dodona/*.cs` (Daemon, Store, LaneRuntime, LaneSink, Instance,
LaneLiveness, Policy, Briefing, Claims, ProjectLadder, Projects, Repos, Ask, Fence, Git, Trees,
Ver, Program, Concierge, ConciergeStore, Dictation, WorkspaceResolve, Workspaces, Paths,
PaneProgress); `src/DodonaUi/*.cs` (MainWindow.xaml.cs, DaemonClient, Poller, StoreReader,
ConciergeReader, Recognizer, Poses, Shell, Vm, UiPipe, UiSettings, App.xaml.cs);
`src/DodonaShim/Program.cs`; `src/DodonaFakeAgent/Program.cs`; all four `.csproj`;
`tests/Dodona.Tests/*`; `tests/_workspace.ps1` function list.

Everything below is read off the code. The one thing I *ran* is marked MEASURED. Nothing else
is a measurement and I have not invented any.

---

## 0. THE HEADLINE FINDINGS

**F1. The door into the daemon assembly is already open.** `src/Dodona/Dodona.csproj:23-28`
carries `InternalsVisibleTo("Dodona.Tests")`. Every `internal` type in the daemon —
`Store`, `Daemon`, `LaneRuntime`, `Registry`, `ConciergeStore`, `Concierge`, `Instance`,
`Repos`, `Projects`, `Trees`, `Fence`, `Git`, `Ver`, `Paths` — is *already* reachable from the
existing xunit project. No new access plumbing is needed for anything in `src/Dodona` except
`Program.cs` (see F5). The csproj comment states the policy: types stay internal, the test
assembly is granted access, and that is deliberate.

**F2. `Daemon.HandleAsync(string req, StreamWriter w)` (Daemon.cs:981) is the single
highest-value seam in the tree.** It is the *entire* daemon command surface — **45 `case`
labels** (`lane-start`, `say`, `input`, `ticket-create`, `token-request`, `land`, `approve`,
`answer`, `questions`, `swap`, `status`, `focus`, `ack`, `tail`, `repo-init`, …) — and its
only dependencies are a JSON string in and a `StreamWriter` out. The pipe server that calls it
is nine lines (Daemon.cs:957-975): `new NamedPipeServerStream` → `ReadLineAsync` →
`stopping = await HandleAsync(req, w)` → `WriteLine("##end")`. A `StreamWriter` over a
`MemoryStream` substitutes for the pipe with no fake at all. What blocks it today is not the
method — it is the *constructor*: `Daemon(string primary, string wsId, string wsName,
string ctlPipe, Store store)` at Daemon.cs:528 is **private**, and the only public entry
`RunAsync` (Daemon.cs:538) takes a `Global\dodona-<id>` `Mutex`, opens a `Store` itself, and
then never returns until stop. Cost of the seam: **two accessibility keywords** (`internal` on
the ctor and on `HandleAsync`). No refactor, no interface.

**F3. `LaneRuntime.HandleShimLine(string line)` (LaneRuntime.cs:95) is the shim wire parser,
and it is already a pure function over `ILaneSink`.** `ILaneSink` (LaneSink.cs:22) is a
six-method interface that *already exists and already has two production implementers*
(`Store` at Store.cs:12, `ConciergeStore` at ConciergeStore.cs:35). Everything the wire
protocol decides — `system/init` → session id, `system/permission_denied` → `error` row,
`system/thinking_tokens` → presence only and NO row, `assistant` text vs `tool_use` → presence
+ progress row with `seq=null`, `user`/`tool_result` `is_error` → failure row, `result` →
presence idle + `OnResult`, `rate_limit_event` → kv — happens inside that one private method
with no pipe in scope. It is `private void`; making it `internal` and handing it a recording
`ILaneSink` turns *every* wire-content assertion in m0/m1/m2/compression into a ~10 ms test.
The pipe only appears in `ConnectAndPumpAsync` (LaneRuntime.cs:45) which does
`while ((line = await reader.ReadLineAsync()) is not null) HandleShimLine(line);` — i.e. the
pipe's whole job is to produce the strings this method consumes.

**F4. The UI's view-model layer is fully testable without a `Window`, and nothing today tests
it.** `MainVm.Apply(Snapshot)` (Vm.cs:551, class at Vm.cs:407) is `public`, takes plain-data
records, and writes `ObservableCollection`s + `Visibility`/`Brush`/`Thickness` values. None of
that needs `InitializeComponent`, a message pump, or `Application.Current` — WPF's
`SolidColorBrush`/`FontFamily`/`Visibility` construct on any thread. `Poses.Get(name)`
(Poses.cs:79) is a *pure `Snapshot` factory* with 16 named fixtures and no store and no clock.
So `Poses.Get("twelve")` → `MainVm.Apply` → assert `GridColumns == 4`, `BandsVisibility`,
`ZeroVisibility`, `AskVisible`, pulse bookkeeping, feed chip brush selection — all in-process,
milliseconds. **Blocker: there is no UI test project.** `DodonaUi.csproj` has no
`InternalsVisibleTo` at all, and a test project would have to target `net8.0-windows` with
`<UseWPF>true</UseWPF>`. `MainVm`, `Snapshot`, `PaneSnap`, `AskSnap`, `PaneView`, `AskView`,
`FeedView`, `BandView` are already `public`; `Poller`, `StoreReader`, `Shell`, `Poses`,
`UiPipe`, `DaemonClient`, `UiSettings`, `IRecognizer` are internal and would need the
attribute.

**F5. `Program.cs` is structurally unreachable from any test assembly, and it holds the write
gate.** It is a top-level-statements file (1838 lines). `GateHook()` (Program.cs:890),
`GateAsk()` (:875), `ParseArgs()` (:1692), `Autostart()` (:1661), `Client()`, `Ui()`,
`Where()`, `Ps()`, `StopAll()`, `Cx()` are all local/private members of the compiler-generated
`Program` class. `InternalsVisibleTo` does **not** grant access to private members, so no
amount of csproj work reaches them. Consequence: every one of `GateHook`'s ~8 `return` paths —
the ones CLAUDE.md §7 and issue #4 spent two documents and a code comment getting wrong — is
provable *only* by running the real `dodona.exe` from PowerShell, which is what
`m1:the_gate_still_checks_the_tree_when_the_ticket_argument_is_unreadable` does. Moving
`GateHook` + `ParseArgs` into a real `static class` in `src/Dodona/` (parameterised on
`TextReader in`, `TextWriter out/err`, and an arg dictionary instead of the closed-over `opts`)
is the single change that converts the largest cluster of "expensive because unreachable"
checks into unit tests. Cost: real, not one keyword — `GateHook` closes over `opts` via
`One(...)` and calls `Client(...)` which opens a named pipe; the daemon call has to become an
injected `Func<object,(int,string)>`.

**F6. `Instance.ConciergeId` (Instance.cs:84) and `Instance.ShellId` (Instance.cs:91) are
`static … { get; } = Scoped(...)` — frozen at first touch from `DODONA_HOME`.** This is the
hard blocker on parallel in-process tests. `Paths.Home` (Paths.cs:19) re-reads the env var on
every call, so *paths* follow a mid-process `DODONA_HOME` change; these two do not. Two
in-process tests wanting two isolated homes will share whichever concierge/shell id was
computed first. Combined with `Instance.AllPipes()` (Instance.cs:171) enumerating the
machine-global `\\.\pipe\` directory and `Registry()` (Workspaces.cs:65) taking **no path
parameter** at all, the honest statement is: **in-process tests that touch identity or liveness
must run sequentially in one process, or `DODONA_HOME` must become a passed value rather than
an ambient one.**

---

## 1. BOUNDARY-BY-BOUNDARY

### 1.1 Shim ↔ Daemon — the wire protocol

**Is it expressible as data? YES, completely.** Read off `src/DodonaShim/Program.cs` and
`LaneRuntime.cs`, it is four line shapes and nothing else:

| direction | line | written at |
|---|---|---|
| shim → daemon | `!hello proto=1 shim=<pid> child=<pid> delivered=<n> buffered=<n>` | DodonaShim/Program.cs:220 |
| shim → daemon | `<seq>\t<raw child stdout line>` | DodonaShim/Program.cs:240 |
| daemon → shim | `{"type":"user","message":{"role":"user","content":[{"type":"text","text":…}]}}` | LaneRuntime.cs:313-318 |
| daemon → shim | `##shutdown` | LaneRuntime.cs:325 / DodonaShim/Program.cs:254 |

1. **Interface?** None. Both sides are hand-written string handling over
   `NamedPipeClientStream` / `NamedPipeServerStream`.
2. **Separable?** The *daemon* half already is (F3). The *shim* half is not: the buffer,
   `delivered` advancing only after a successful write, `FinishIfDrained()`, the lease, and the
   two-server-instance name-holding trick (`MaxServers = 2`, DodonaShim/Program.cs:193-196) are
   all inline in top-level statements with no seam anywhere.
3. **Cost of a seam.** Daemon side: one keyword (F3). Shim side: to test the buffer/lease/
   drain logic in-process you would have to lift ~120 lines out of top-level statements into a
   class taking `(Func<string?> readChildLine, Action<string> writeClient, IClock)` — real work,
   maybe 150 lines moved. There *is* already a cheap partial seam: `DODONA_SHIM` env var
   (Daemon.cs:4079) lets a test substitute a different shim executable.
4. **Never fake:** the *replay-on-reconnect* invariant. `delivered` only advances after
   `writer.WriteLine` succeeds (DodonaShim/Program.cs:240-243) — the m0 guarantee is that
   killing the daemon mid-turn loses no result. That is a property of a real broken pipe
   throwing, and a fake writer that "fails" on demand proves a different thing than a pipe that
   actually breaks. Keep exactly one integration wire for it.

### 1.2 Store / StoreReader — SQLite

1. **Interface?** Partial and already there: `ILaneSink` (LaneSink.cs:22) covers the six
   methods `LaneRuntime` needs. Everything else is concrete `Store` — I counted **~70 public
   members** across Store.cs (constructor, `Backup`, `StampLaneProjects`, `StampRepoPaths`, 6
   question methods, ~10 lane methods, kv, routing, 5 pane methods, ~9 ticket/claim methods, 6
   token/land methods, 4 swap methods, `Tail`, `Feed`-shaped readers, event readers). "Extract
   `IStore`" is a ~70-method interface. **Do not.**
2. **Separable?** The *decisions* are already out: `Claims.Overlap` does the conflict algebra
   (Store.cs:1231 calls it inside `FindConflicts`), `Policy.Resolve` does the model table,
   `PaneProgress.Fold` does the pane fold. What is left inside Store *is* the SQL and the
   transaction boundaries.
3. **`:memory:` — MEASURED.** It is blocked by exactly one line.
   `Store.cs:33` does `Directory.CreateDirectory(Path.GetDirectoryName(path)!)`. I ran it:
   `Path.GetDirectoryName(":memory:")` returns `""` (not null), and
   `Directory.CreateDirectory("")` throws
   `ArgumentException: Path cannot be the empty string or all whitespace.` The same is true for
   the URI form `file:memdb1?mode=memory&cache=shared`. **Fix cost: one guard** —
   `if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);`
   Nothing else in `Store` resists memory: `PRAGMA journal_mode=WAL` is a harmless no-op on an
   in-memory db, `synchronous=FULL` likewise, and `Migrate()` is plain DDL.
   **But there is a second, structural blocker for the UI path:** `StoreReader.Open()`
   (StoreReader.cs:30) does `if (!File.Exists(_path)) return false;` and then opens a *separate*
   `Data Source=…;Mode=ReadOnly` connection. A private `:memory:` database is invisible to a
   second connection, so **the writer/reader pair — which is the whole m3 doctrine — cannot be
   exercised over `:memory:` at all** unless you move to `Mode=Memory;Cache=Shared` with a named
   db *and* drop the `File.Exists` guard. **A temp file is the cheaper answer for anything that
   spans Store and StoreReader**; `:memory:` is worth unblocking only for pure-`Store` tests
   (migrations, token FIFO, claim conflicts), where it removes the disk entirely.
4. **Never fake:** the merge token and the land. `TokenRequest` (Store.cs:1408), `TokenRenew`
   (:1489), `TokenRelease` (:1507) and `LandCommit` (:1525) are correct *because* the read,
   the fence re-check and the write are one `BeginTransaction`. `LandCommit` in particular
   re-checks holder identity and lease expiry inside the transaction that lands the ticket,
   frees the claims, and withdraws the `land` question (Store.cs:1541-1560, one multi-statement
   command). An in-memory fake store reimplements that as sequential field writes and passes —
   while deleting the atomicity that is the entire property. Same for `TicketCreate`
   (Store.cs:1143): `FindConflicts` + `INSERT` in one tx. **Run these against a real SQLite
   file/db, never against a hand-written double.**
5. Also never fake: **the migration ladder.** `Migrate()` (Store.cs:68) walks `user_version` 1→10
   with `ADD COLUMN`s. CLAUDE.md §0.2 records that a migration that throws kills the daemon in
   its constructor before the control pipe exists. A fake schema cannot fail that way.

### 1.3 Registry (`Workspaces.cs`)

1. **Interface?** None. `sealed class Registry : IDisposable`, **parameterless constructor**
   (Workspaces.cs:65) hardcoding `Paths.ConciergeDir` / `Paths.Registry`.
2. **Separable?** No — the load-bearing invariant is the *partial UNIQUE index on
   `members(key) WHERE is_git`* (its own class comment calls it "the real arbiter"). That is a
   SQLite feature, not a C# rule.
3. **Cost of a seam:** add a `Registry(string path)` overload and keep the parameterless one
   delegating to `Paths.Registry`. Two lines. This is the single change that would let two
   isolated registries exist in one process.
4. **Never fake.** Repo-exclusivity is the thing that replaced path-hash identity as the
   "one merge token per main" guarantee (Instance.cs class comment; Workspaces.cs:36-58).
   CLAUDE.md §5: "If `tests/workspace-acceptance.ps1`'s exclusivity checks ever go red, that is
   a correctness incident, not a flaky test." A fake registry that enforces uniqueness in a
   `HashSet` is a *different enforcement mechanism* passing a test written about the index.

### 1.4 ConciergeStore

`ConciergeStore(string path)` (ConciergeStore.cs:42) — **already takes a path**, unlike
`Registry`. Implements `ILaneSink`. Directly constructible in-process today. Note the shape
asymmetry worth exploiting: `ConciergeStore` was written *because* `ILaneSink` let the
concierge share `LaneRuntime`'s wire machinery without a workspace store (LaneSink.cs comment).
That is the house's own precedent for "a narrow interface at a real boundary, with two real
implementations".

### 1.5 Daemon internals

- **`Config`** (Daemon.cs:19, `Config.Load(root)` at :109) reads `dodona.json` off disk;
  `Config.For(workspaceRoot, repoPath)` at :106. A record — everything downstream of it
  (`Rules`, `IsPr`, `DeliveryIsPr`, `Allowed`) is pure. `Config.DeliveryIsPr` at :93 is already
  `public static` and unit-testable.
- **`Daemon.ClaudeArgs`** (:2846), **`LaneSystemPrompt`** (:3902), **`TicketSystemPrompt`**
  (:3918), **`LanePrefix`** (:5759), **`ResolveLaneCwd`** (:5792), **`IsObviousGeneric`**
  (:5805), **`Probe`** (:2247) are already `internal static` — i.e. *already unit-reachable
  today*, and `PureLogicTests.cs` exercises some of them.
- **Registry-touching helpers on the instance** — `Members()` (:222), `ProjectPaths()` (:239),
  `ProjectsByRecency()` (:257), `ProjectHandles()` (:275), `TrustedProjects()` (:317) — each
  does `using var reg = new Registry()`. They are the seam candidates if you want the routing/
  reaping logic in-process; each already delegates its *decision* to a pure function
  (`Projects.Of`, `Projects.Live`, `ProjectLadder.Decide`). The I/O half is deliberately thin.
  Daemon.cs:283-291 says so explicitly for `LiveProjectPaths`: "The I/O half … hand them to
  `Projects.Live`, which is where the decision lives and is unit-tested." **This is the house
  pattern; extend it, do not invent a new one.**
- **`LandBegin`/`LandFlow`** (:6214/:6349) are the R3.5 "off the control pipe" pair. `LandRun`
  (:6155) is an in-memory status object. Testable against a real temp git repo; not fakeable
  (see 1.8).

### 1.6 Poller / Shell / StoreReader — the UI read path

**`Poller` is a good seam and a *near*-miss.**
- `Poller(StoreReader reader)` (Poller.cs:48) takes a **concrete** `StoreReader`, not an
  interface. `StoreReader` is `sealed` with non-virtual methods, so it cannot be subclassed.
- `Poller.Build()` (Poller.cs:80) is otherwise clean: eight reader calls
  (`Lanes/Badges/LastActivity/LastInput/TicketRepoByLane/CollapsedLanes/Tail/Feed/Kv`) plus
  two statics — `Paths.NeutralDir` and `Projects.Field` — and produces a `Snapshot`. The
  interesting logic (`Liveness` at :65 — the bucketed elapsed clock, `quiet Nm`, the `busy`
  predicate; `QuotaLine` at :159 — the five-hour window string with its `as of Nm ago` age)
  is *already* `static` / near-pure. **`Liveness` is `private static` and is a textbook unit
  test that does not exist.**
- **Cost of a full seam:** extract an `IStoreView` of the **9 methods** `Poller` actually calls
  (not 70), have `StoreReader` implement it. ~15 lines. Then `Poller.Build()` runs over a
  hand-built row set. **Cheaper still and closer to the house style:** make `Liveness` and
  `QuotaLine` `internal static` and test them directly — that is most of the content and none
  of the plumbing.
- `Shell.Build()` (Shell.cs:157) is where the merged feed, the bands, `OpenAsk` scope ordering
  and boot-to-zero are decided. It depends on `new Registry()` (:88), `Instance.IsLive` (:93,
  :183), `Paths.Store(id)` (:69), `new ConciergeReader()` (:38). All except `Instance.IsLive`
  are `DODONA_HOME`-redirectable. **`Instance.IsLive` is the one un-redirectable dependency in
  the whole read path** — it asks the machine-global pipe namespace.
- **Never fake `StoreReader` itself away from a real store.** Its two `pragma_table_info`
  capability probes (`HasCompressed` :39, `HasLaneCwd` :59) exist because the reader is
  read-only, cannot migrate, and is pointed at *older* stores by `--attach`. Faking them
  deletes the only reason they exist. Test those against a real store rolled back to an older
  `user_version` — which is what `_workspace.ps1` already does.

### 1.7 MainWindow / the `ui` verb surface

**"Each `ui` verb lands in the same method a click lands in" — I checked it, and it is
literally true.** `UiPipe` (UiPipe.cs:470) marshals to `win.HandleVerb(e)`;
`HandleVerb` (MainWindow.xaml.cs:121) dispatches:

| verb | lands in | the click that lands there |
|---|---|---|
| `type` | `InputBox.Text = …; SubmitInput()` (:1092) | Enter via `Input_PreviewKeyDown` (:623) → `InputKey(false)` → `SubmitInput` |
| `compose` | `ComposeInput` (:652) | typing characters |
| `key enter\|shift+enter` | `InputKey(bool)` (:636) | `Input_PreviewKeyDown` (:623) — same method |
| `key escape` | `EscapePressed()` (:1149) | `Window_KeyDown` (:1139) — same method |
| `lane <5 actions>` | `LaneAction` (:545) | `Pane_Click`/`Pane_Close`/`Pane_Collapse`/`Collapsed_Click`/`Pane_Wake` (:561-621) all call `LaneAction` |
| `answer` | `AnswerAsk` (:1174) | `AskChoice_Click` (:1209) → `AnswerAsk` |
| `workspace` | `FocusWorkspace` (:1274) | `Band_Click` (:1264) → `FocusWorkspace` |
| `input-resize` | `ResizeInput` (:1055) | `InputGrip_DragDelta` (:1039) / `InputGrip_Reset` (:1046) |
| `listen` | `SetListening` (:807) | `Mic_Click` (:1007) |
| `heard` | `OnHeard` (:688) | `_mic.Heard += … OnHeard(h)` (:852) — the real engine's landing site |
| `dump` / `screenshot` / `pose` / `overlay` / `update` / `close` | window-only verbs | — |

**Is it a usable seam for tests? Yes — but only for out-of-process ones as things stand.**
Every one of those methods is on a live `MainWindow`, and most touch `InputBox` (a XAML-generated
field). `HandleVerb` itself is `public`, so if a UI test project ever creates a real window on
an STA thread it could call it directly and skip the pipe — but the WPF `Application` is a
per-process singleton (`App.xaml.cs:8` `OnStartup`, `Application.Current` used at :213, :409,
:1223), which caps you at **one window per test process** and rules out parallelism.

**The write side is a hard static.** `DaemonClient` is `static class` (DaemonClient.cs:13) with
`public static string Send(string instanceId, object request)` (:160). `MainWindow.Send`
(:1242) and `MainWindow.SendConcierge` (:1251) and `AnswerAsk` (:1201) call it directly.
There is **no interface and no injection point** — so a `MainWindow` test cannot avoid a real
named pipe. **Cost of the seam: one static field.** Give `MainWindow` (or `DaemonClient`) a
`internal static Func<string, object, string> Transport = DaemonClient.Send;` and route the
three call sites through it; every `LaneAction`/`AnswerAsk`/`FocusWorkspace`/`SubmitInput`
*routing* assertion (which pipe, which command shape, which id) becomes a recording-delegate
test, and only the "the pipe actually carries it" wire needs a daemon. Note `AnswerAsk` also
calls `DaemonClient.Ensure`/`EnsureConcierge` (:1199-1200), so the seam has to cover those too
or the test will try to `Process.Start` a daemon.

**`Dump()` (:223) is ~90 % a pure function of `MainVm`.** It reads exactly four things off the
`Window` — `Width`, `Height`, `Title`, `IsActive` — and everything else off `_vm` (`PoseName`,
`OverlayPane`, `Ask`, `AskVisible`, `AskDismissed`, bands, feed, slots, listen state). Splitting
it into `MainVm.DumpObject()` + a four-field window record would make **most `ui dump`
assertions in ui-grid / ui-shell / ui-ask checkable in milliseconds with no window at all**.
This is probably the largest single check-count win available in the UI.

**`ApplyPose` (:421) is the existing headless driver and it is already close to pure.**
It is `Poses.Get(name)` → `_vm.Apply(snap)` → set `_vm.OverlayPane`/`Toasts`/`Status`. The only
window state it touches is the dictation indicator (`_listen`, `_partial`, `ShowPending`,
`UpdateListenUi`) for the `listening` pose, which the code comment at :440 explains is window
state rather than snapshot state. **`Poses.Get` → `MainVm.Apply` → assert is a real,
window-free path today** — it needs only a UI test project to exist.

### 1.8 Git

`Git.Run(workDir, params string[] args)` (Git.cs:9) is `static`, `Process.Start`, no interface.
Every git operation in the product goes through it — `Sha` (:28), `ShaOrEmpty` (:49), `IsRepo`
(:62), `HasCommit` (:72), `FindRepos` (:77), `TempWorktree` (:113), plus the land's
`MainMergeOnBranch` (Daemon.cs:6093) and `SilentDrops` (:6129).

**NEVER FAKE THIS.** The land's whole value is that git's real behaviour is what decides:
ff-only refusal, a merge that drops a hunk silently, a branch already checked out in another
worktree, `.git` being a *file* in a worktree (CLAUDE.md §5.2). A fake `Git.Run` returning
canned stdout tests the parser and proves nothing about the operation. `SilentDrops`
(Daemon.cs:6129) is the exact case: it exists to detect a merge that *succeeded* and lost work.
What *is* safely unit-testable is the **parsing** of git output — split `MainMergeOnBranch` and
`SilentDrops` into `(runGit) → decision` shape with the string-parsing half separated, the way
`Trees.Locate` already is (below).

### 1.9 Filesystem

**The house already has the pattern, and it is good.** `Trees.Locate(fullPath, projects,
Func<string,bool> dirExists, Func<string,bool> fileExists)` (Trees.cs:44) with a convenience
overload binding the real `Directory.Exists`/`File.Exists` (Trees.cs:77). `TreesTests.cs`
(11 facts) uses it. **This is the model.** The same shape would fit:
- `Repos.Under` / `Repos.Discover` (Repos.cs:81/:61) — `Directory.GetDirectories` at :93
- `Fence.Enumerate` (Fence.cs:67) — `Directory.GetDirectories` at :76
- `Fence.Roots` (Fence.cs:41) — `Directory.Exists` at :51
- `Git.FindRepos` (Git.cs:77)
- `LaneLiveness.Records(dir)` (LaneLiveness.cs:50) — `Directory.EnumerateFiles` + `File.ReadAllText`

Cost per site: one extra parameter with a defaulted overload. ~5 lines each.

### 1.10 Recognizer — the exemplar to copy

`IRecognizer` (Recognizer.cs:21) is the best-designed seam in the tree and its doc comment
states the anti-drift rule explicitly: *"Two implementations and ONE landing site:
`MainWindow.OnHeard`. The real engine and the fake raise the same event into the same method,
which is the `ui type` reasoning applied one layer down — a fake that fed a parallel path would
prove nothing about the real one."* `FakeRecognizer` (:73) is selected by `DODONA_UI_MIC`
(`off`/`fail`/`hang`), and `tests/_workspace.ps1` sets it for every suite. Note the honesty
detail at :87-90: when the fake stands in *because the real engine failed*, it reports
`Engine = "none"`, not `"fake"` — so a dump can never make a missing engine look installed.
**Any new fake in this plan should be held to that standard: same landing site, and it must
report itself as what it is.**

### 1.11 DodonaFakeAgent — the existing fake, and the one with real drift risk

`src/DodonaFakeAgent/Program.cs` (545 lines) is a **process** that hand-writes `claude`'s
stream-json shape: `{type:system,subtype:init,session_id}`, `{type:assistant,message.content[]}`
with `text`/`tool_use`, `{type:user,…tool_result,is_error}`, `{type:result,result}`,
`{type:system,subtype:thinking_tokens}`. It is driven by directives in the incoming text
(`sleep:N`, `say`, `tool:Name:arg`, `bash:`, `toolfail:`, `think:N`, `env:NAME`, `cwd`,
`brief`) and by `DODONA_LANE_ROLE` (`compressor`, `brain`, `brain-hi`, `router`, `concierge-lo`,
`concierge-hi`).

**Its virtues:** it goes through the *real* shim, the *real* pipe, the *real* `LaneRuntime`,
so it is not a parallel path. Its defaults are deliberately un-helpful (router defaults to
`unclear`, concierge tiers default to `none/low`) so a directive-less fake can never cause a
silent wrong delivery — that is good fake design and worth preserving verbatim.

**Its drift risk, named plainly:** *nothing keeps its wire shape in sync with what `claude`
actually emits.* There is a `tests/assets/recordings` directory (used by the voice work), but I
found no mechanism that replays a recorded real agent transcript through `HandleShimLine` and
demands the parse still holds. This is exactly the routing-ladder shape one layer over: the
suite stands up the wire it then parses. **If the plan introduces more fakes, this is the one
place a drift mechanism is owed** — e.g. a checked-in corpus of real `claude` stream-json lines
that `HandleShimLine` must still classify correctly, which becomes cheap the moment F3 lands.

---

## 2. PROCESS-GLOBAL / STATIC STATE — what forbids parallel in-process tests

| what | where | why it blocks |
|---|---|---|
| `Instance.ConciergeId`, `Instance.ShellId` | Instance.cs:84, :91 | `static … { get; } = Scoped(...)`, computed **once** from `DODONA_HOME`. Two homes in one process is impossible. |
| `Instance.AllPipes()` | Instance.cs:171 | enumerates `\\.\pipe\` — machine-global, shared with the operator's live session |
| `Registry()` | Workspaces.cs:65 | no path parameter; always `Paths.Registry` |
| `Global\dodona-<wsId>` mutex | Daemon.cs:558, Concierge.cs:124 | OS-global; one daemon per workspace id per machine |
| `Ver.Build` | Ver.cs:41 | `static … { get; } = Compute()` reading `typeof(Ver).Assembly` — one answer per process |
| `MainWindow.TestWindow` | MainWindow.xaml.cs:35 | `public static bool`, deliberately static so it survives a hot swap |
| `Application.Current` | App.xaml.cs, MainWindow:213/:409/:1223 | WPF: one `Application` per process ⇒ at most one UI test window per process |
| `AudioCapture._mfStarted` + `MfLock` | AudioCapture.cs:69-70 | MediaFoundation init, once per process |
| `Console.SetOut` / `Environment.SetEnvironmentVariable("DODONA_NO_AUTOSTART")` | Program.cs:881-882, :887 | `GateAsk` mutates process-global console + env to capture the gate verdict |
| `DODONA_HOME`, `DODONA_UI_MIC`, `DODONA_NO_AUTOSTART`, `DODONA_SHIM`, `DODONA_EXE`, `DODONA_LANE_ROLE`, `DODONA_SHIM_LEASE_SEC`, `DODONA_BIN_ROOT`, `DODONA_WORKSPACE`, `DODONA_TEST_CONCURRENCY` | throughout | every isolation lever in the system is an **environment variable**, i.e. process-global |

**Practical consequence for the plan:** the existing suites get isolation by being *separate
processes* with separate env. Any move to in-process integration tests either (a) accepts
`xunit` collection-level serialisation for anything touching identity/liveness, or (b) pays to
turn `DODONA_HOME` into a passed value (`Registry(path)`, `Paths` instance, `Instance` ids
derived from a passed home). (b) is a genuine refactor across `Paths`, `Instance`, `Registry`,
`Daemon.RunAsync`, `Concierge.RunAsync`, `DaemonClient`, `UiSettings`, `Shell` — I would not
propose it as part of a test refactor.

---

## 3. WHAT MUST NEVER BE FAKED (the routing-ladder test, applied)

The routing-ladder rule: *a fake is forbidden where the fake's existence makes the lookup
unable to miss.* Applied honestly, these are the boundaries that fail it:

1. **The merge token and the land transaction** — Store.cs:1408/:1489/:1507/:1525. The property
   *is* the transaction. A double reimplements it and proves itself.
2. **Repo-exclusivity** — Workspaces.cs, the partial unique index. The enforcement is the index;
   a fake enforces something else.
3. **Git, for anything that mutates a ref** — Git.cs:9 and Daemon.cs:6093/:6129. ff-only,
   silent drops, branch locks. A canned stdout proves the parser, not the operation.
4. **`GateHook`'s deny paths** — Program.cs:890. CLAUDE.md §7 and issue #4: this property has
   twice been *asserted in prose and been false*. It must be exercised as a real process with
   real stdin (including a BOM). Even after F5 makes the function unit-testable, keep **one**
   integration wire that pipes real bytes into the real exe.
5. **The shim's exactly-once replay** — DodonaShim/Program.cs:240-243 + `UNIQUE(lane_id, seq)`.
   Needs a real broken pipe and a real reconnect.
6. **Lane liveness** — `Instance.LiveLanes` (Instance.cs:132) + `LaneLiveness.Live`
   (LaneLiveness.cs:79). CLAUDE.md §0.2 measured 8 of 192 reads seeing no pipe while the shim
   was alive. A fake pipe namespace has no blink, so it cannot reproduce the bug the union
   exists to prevent. Keep one real-OS wire.
7. **Start-on-demand** — `DaemonClient.Send` (DaemonClient.cs:160). The 2026-08-19 incident was
   that a *call site* forgot to ensure. Faking the transport removes the daemon whose absence
   is the whole test. Keep one wire where the daemon is genuinely dead.
8. **`AttachShimAsync`'s spawn-site invariants** — Daemon.cs:3998. `Projects.PromptDirMismatch`
   (:4013) compares the system prompt's stated directory against the real
   `ProcessStartInfo.WorkingDirectory`; `DeployGate` (:6581) writes the settings file. The
   comment at :4064 says outright that gating only real-claude lanes would make the deployment
   invisible to every suite. This is where "the way the operator runs it" lives.
9. **WPF's own behaviour** — `Input_PreviewKeyDown` (:623). CLAUDE.md §0.2: with
   `AcceptsReturn`, the TextBox class handler eats Enter before instance `KeyDown`. Only a real
   TextBox in a real visual tree proves the handler is the right one. Keep one live-window wire
   for "Enter sends, Shift+Enter does not".

---

## 4. SEAM LEDGER (cost-ordered)

| # | seam | change | unlocks |
|---|---|---|---|
| S1 | `LaneRuntime.HandleShimLine` `private`→`internal` | 1 keyword | every wire-content assertion (kinds, presence derivation, progress rows, `seq=null`, rate-limit kv, failed tool_result) as ~10 ms tests over a recording `ILaneSink` |
| S2 | `Store` `:memory:` | 1 guard at Store.cs:33 | migration ladder, token FIFO, claim conflicts, question upsert — no disk. **Not** the Store↔StoreReader pair (see 1.2). |
| S3 | `Daemon` ctor + `HandleAsync` `private`→`internal` | 2 keywords | 45 command shapes driven in-process against a real temp `Store`, no pipe, no mutex, no `RunAsync` |
| S4 | `Poller.Liveness` + `QuotaLine` → `internal static` | 2 keywords | the elapsed-clock buckets, `quiet Nm`, the 5-hour line and its `as of` age |
| S5 | `Registry(string path)` overload | 2 lines | two isolated registries in one process |
| S6 | new `Dodona.Ui.Tests` project (`net8.0-windows`, `UseWPF`) + `InternalsVisibleTo` on DodonaUi.csproj | 1 csproj + 1 attribute | `Poses.Get` → `MainVm.Apply` → assert: grid columns, visibilities, band palette, feed chip brushes, pulse, ask dismissal — **no window** |
| S7 | `IStoreView` = the 9 methods `Poller` calls; `StoreReader` implements | ~15 lines | `Poller.Build()` over hand-built rows |
| S8 | `MainWindow`/`DaemonClient` transport delegate | 1 static field + 5 call sites (:1201, :1244, :1256, and the two `Ensure*`) | every "which pipe, which command" assertion for `LaneAction`, `AnswerAsk`, `FocusWorkspace`, `SubmitInput`'s concierge-vs-daemon branch |
| S9 | `Dump()` split → `MainVm.DumpObject()` + 4 window fields | ~40 lines moved | most of `ui dump`'s surface, window-free — likely the biggest UI check-count win |
| S10 | injected `dirExists`/`fileExists` on `Repos.Under`, `Fence.Enumerate`, `Fence.Roots`, `Git.FindRepos`, `LaneLiveness.Records` | ~5 lines each | repo discovery, the fence's cap/skip list, shim-record reading |
| S11 | `GateHook` + `ParseArgs` out of `Program.cs` into a real class | real refactor; `opts` closure and `Client()` must become parameters | the CLI's arg ladder + **every gate deny path** as unit tests (keep one integration wire, item 4 above) |
| S12 | shim buffer/lease/drain lifted out of top-level statements | ~150 lines moved | `FinishIfDrained`, lease arithmetic, `delivered` advance — without spawning processes |

---

## 5. THINGS THE PLAN SHOULD NOT DO

- **Do not extract `IStore`.** ~70 members, and the properties worth testing are the transaction
  boundaries an interface erases. Use the real `Store` over a temp file (or `:memory:` once S2
  lands).
- **Do not fake `Registry`, git, the pipe namespace, or `GateHook`'s transport.** §3.
- **Do not assume `:memory:` gives you the UI read path.** `StoreReader.Open()` needs a file
  (StoreReader.cs:30) and a second connection. Measured constraint, not a preference.
- **Do not plan for parallel in-process tests over multiple `DODONA_HOME`s** without first
  fixing `Instance.ConciergeId`/`ShellId` (Instance.cs:84/:91). Today they freeze on first touch.
- **Do not plan more than one WPF window per test process.** `Application.Current` is a
  singleton and `MainWindow.TestWindow` is `static`.
- **Do not invent a new fake style.** The tree has three good precedents and the plan should
  name them: `ILaneSink` (narrow interface, two *production* implementers, so it cannot rot),
  `IRecognizer` (two implementations, ONE landing site, and the stand-in reports itself as
  `none` rather than `fake`), and `Trees.Locate`'s injected predicates (defaulted overload binds
  the real thing, so production has exactly one path).
