# Glossary — the words this project uses, and the ones it stopped using

Settled with the operator 2026-08-19. **These are the words to use in code, comments, commit
messages, docs and anything the operator reads.** Where the code currently uses a different
word, that is noted as a rename to make, not an alternative that is also fine.

The reason this file exists: the operator says *location* and *project* and *manager*; the code
says *member* and *repo* and *brain*; and they do not map one-to-one. Three separate design
conversations were lost to that mismatch.

---

## The nouns

| word | means | notes |
|---|---|---|
| **Workspace** | The operator's named grouping of projects. Has an id, a display name, a store, and N projects. | **Created by the operator only.** Dodona auto-creating one from a folder it happened to be run in is a defect (see the plan's Phase 0c). |
| **Project** | **A location — one folder.** It may *be* a git repo, *contain* one, contain several, or contain none. | The operator's primary unit. In the code today this is a `members` row; **rename `member` → `project` in user-facing text.** |
| **Repo** | A git repository found at or under a project. | **Not** a synonym for project. One project may hold several repos. Merging serialises per repo, because each has its own `main`. |
| **Lane** | One conversation with one agent. Durable; the agent inside it is disposable. | |
| **Manager** | The per-project **scope**: that project's coordination rows (claims, tickets, merge tokens) plus its brain. | **Not a process.** See D-L1 in the plan. |
| **Brain** | The judgement agent: naming, is-this-a-new-task, is-this-ticket-worthy. | Runs in a **neutral** folder, never inside a project — a manager that reads a project's `CLAUDE.md` and skills can end up running `/ship`. |
| **Router** | Decides where a typed sentence goes. One per workspace. | |
| **Concierge** | Machine-wide, one per machine. Resolves *which workspace*, owns the registry. | Holds no lanes, no claims, no merge tokens. |
| **Daemon** | The process that runs one workspace. | |
| **Shim** | The wrapper process that owns an agent process and outlives its daemon. | |
| **Registry** | `registry.db`. Workspaces, projects, aliases. Machine-wide. | The concierge is its only writer. |
| **Ticket** | Isolated work: a branch, a worktree, claims, and a place in a merge queue. | **Needs a repo.** Lanes do not. |
| **Claim** | What a ticket is allowed to touch. | |
| **Merge token** | The right to land in one repo. Per repo, not per project. | |

## The verbs

| word | means |
|---|---|
| **attach** | add a project to a workspace |
| **detach** | remove a project from a workspace |
| **land** | fast-forward a ticket's branch onto its repo's main |
| **wake** | start a sleeping workspace's daemon |
| **publish** | build and hot-swap the running binaries |

---

## Words to stop using

| stop saying | say instead | why |
|---|---|---|
| **member** | **project** | operator-facing word; `member` stays only as the column name until renamed |
| **location** | **project** | they mean the same thing — one word, and the operator picked this one |
| **primary** / `_primary` | **the workspace's first project** | `_primary` is an implementation fallback, not a concept. It should not appear in anything the operator reads |
| **instance** | **workspace** | leftover from before workspaces were named |
| **root** | **project path** | `--root` survives as a CLI flag; the word does not |
| **dispatcher** | **router** | two words for one thing; `dispatcher` also names a lane role that is only a UI row |

---

## The two distinctions that keep being lost

**1. Project ≠ repo.** A project is a folder. A repo is a git repository. A project may contain
zero, one or several repos. Anything about *branches, merging, claims or tokens* is per **repo**.
Anything about *where a lane opens, what the operator names, or which brain answers* is per
**project**.

**2. Workspace ≠ project.** A workspace is a named group the operator made. A project is one
folder in it. The operator switches *workspaces*; work happens in *projects*.
