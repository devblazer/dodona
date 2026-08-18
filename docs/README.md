# docs

| File | What it is |
|---|---|
| `ORCHESTRATOR-DESIGN.md` | **The authority.** Every `§n` reference in this codebase points here. |
| `ORCHESTRATOR-REVIEW.md` | The review and milestone plan: rationale, measurements, what is carried. |
| `LANE-LIFECYCLE.md` | Lane closing and the attention model — decisions taken, **and ideas already rejected**. |
| `WORKSPACES-CONCIERGE.md` | Workspaces and the concierge: named identity, the group-scope ladder, the fence. |
| `M5-DELIVERY-PLAN.md` | **The plan for M5:** foreign git processes, multi-repo ticket groups, and ships that outlive an agent. Authority for that work; supersedes design §7.1 on two points. |

## Why these are copies

The design doc originally lived deliberately *outside* this repo, on the reasoning that it
governs a system which rewrites itself and no branch, merge or reset should be able to
touch it. That still holds for the master copy — the originals remain at
`..\MassWorks\ORCHESTRATOR-DESIGN.md` and `..\MassWorks\ORCHESTRATOR-REVIEW.md`.

These are copies because **agents could not read the originals.** A lane works inside its
ticket worktree at `<root>\.dodona\wt\t<N>`, where `..\MassWorks\` resolves to nothing at
all — three directories short. Every section reference in the code pointed at a document
the reader was structurally unable to open, which is the same class of failure as a GUI
telling you to run a CLI command.

## Keeping them honest

The originals are the master. When the design changes there, refresh here:

```powershell
Copy-Item ..\MassWorks\ORCHESTRATOR-DESIGN.md docs\ -Force
Copy-Item ..\MassWorks\ORCHESTRATOR-REVIEW.md docs\ -Force
```

If you are an agent and a design question turns on wording that looks stale, say so rather
than deciding from a copy — a drifted copy is worse than an absent one.
