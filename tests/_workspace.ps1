# Shared test plumbing for workspaces (docs/WORKSPACES-CONCIERGE.md §1).
#
# Two things every suite now needs, and both exist for reasons the suites already care
# about (§17: tests collide with nothing, including the instance the operator is using
# right now):
#
#   1. AN ISOLATED REGISTRY. Workspace identity lives in a machine-global registry under
#      %LOCALAPPDATA%\Dodona. A suite that created workspaces there would litter the
#      operator's own workspace list, and a suite that tested the repo-exclusivity REFUSAL
#      could refuse one of their real repos. DODONA_HOME redirects the whole tree —
#      registry, workspace stores, shim-info files, the neutral cwd — into a temp folder
#      the suite owns and deletes.
#
#   2. WHERE THE STORE IS. It is no longer `<root>\.dodona\store.db`: a workspace is named
#      rather than located, so its store lives under the workspace id. Suites ask the
#      binary instead of reconstructing the path — `dodona where --json` exists for exactly
#      this, and it means a future relocation breaks nothing here.

function Use-IsolatedDodonaHome([string]$tag) {
    $dir = Join-Path $env:TEMP ("dodona-home-$tag-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $dir | Out-Null
    $env:DODONA_HOME = $dir
    $dir
}

# Resolve (creating or migrating if need be) the workspace that owns $root, and report
# where its state lives. Returns @{ Id; Name; Dir; Store; CtlPipe }.
function Get-WorkspacePaths([string]$dodona, [string]$root) {
    # Native stderr under $ErrorActionPreference='Stop' throws NativeCommandError even on a
    # clean exit (CLAUDE.md §0.2), and `where` deliberately narrates a first-time workspace
    # creation on stderr. Continue here, and read stdout only.
    $ErrorActionPreference = 'Continue'
    $json = (& $dodona where --root $root --json) | Out-String
    if (-not $json.Trim()) { throw "dodona where --root $root --json produced nothing" }
    $w = $json | ConvertFrom-Json
    [pscustomobject]@{
        Id      = $w.id
        Name    = $w.name
        Dir     = $w.dir
        Store   = $w.store
        CtlPipe = $w.ctlPipe
    }
}

# CLAUDE.md §4: never kill by process NAME — that murdered the operator's live session
# once. Resolve pids from THIS workspace's own shim-info files, which now live in the
# workspace directory rather than under the project root.
function Stop-WorkspaceShims([string]$wsDir) {
    Get-ChildItem "$wsDir\shim-lane*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        $si = Get-Content $_.FullName | ConvertFrom-Json
        foreach ($p in @($si.shimPid, $si.childPid)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
    }
}
