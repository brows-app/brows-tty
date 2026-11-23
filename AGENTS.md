# Repository guidance

This file applies to the entire repository. Read the root `README.md` and
nearby implementation and tests before making changes.

## Skills

The general conventions for this repository live in agent skills under
`.agents/skills/` and `.claude/skills/`. Use the skill that matches the task, and
follow it where it differs from older code or other guidance.

| Task | Skill |
| --- | --- |
| Writing or editing C#, `.csproj`, `.props`, `.targets`, or `Directory.Packages.props` | `brows-csharp` |
| Restoring, building, and choosing the validation scope | `brows-code-building` |
| Writing and running NUnit tests | `brows-code-testing` |
| Git status, staging, commits, branches, and worktrees | `brows-git` |
| Planning substantial changes under `agents/plans/` | `brows-code-plan` |
| Reviewing code and writing reports under `agents/reviews/` | `brows-code-review` |
| Fixing numbered review issues, including per-issue worktrees | `brows-code-fix` |
| Performance and SIMD analysis | `analyzing-dotnet-performance`, `vectorization` |

## Solution layout

- `brows-tty.slnx` contains three packages, NUnit integration tests, and a WPF sample.
- `source/Brows.TTY/` contains the UI-independent Ghostty core (`net462`, `net48`, `net8.0`, `net10.0`).
- `source/Brows.TTY.Win32/` contains Windows ConPTY transport without WPF.
- `source/Brows.TTY.Windows/` contains the WPF control and renderer.
- The root namespace is `Brows`; the public control is `Brows.Windows.Controls.TerminalEmulator`.
- `Ghostty/` owns terminal state and the hand-written native binding.
- `Win32/` owns ConPTY sessions; native declarations live in `Win32/PlatformInvoke/`.
- `Windows/Controls/` owns WPF rendering, input, and dispatcher lifecycle.
- `native/` contains a pinned upstream checkout (ignored), our C bridge, and version metadata.
- `scripts/` contains reproducible native build and verification scripts.
- Root build properties and central package versions are inherited by group-level
  `source/`, `tests/`, and `samples/Directory.Build.props` files.
- Generated artifacts live in `out/`; local tool downloads live in `.tools/`.

## Preserve existing work

- When working in a Git repository, inspect its status and existing diffs before
  editing. Preserve existing work, avoid reverting unrelated changes, and keep
  changes scoped to the requested task.
- Do not commit generated build or test output, packages, or IDE state unless the
  task specifically requires them.

## WPF and native integration

- All control access and rendering belong to the WPF dispatcher. Keep blocking
  pipe IO and process waits off the UI thread.
- Serialize access to each Ghostty terminal. Copy dirty rows into snapshots and
  retain WPF row drawings; do not create a UI element per cell.
- Respect UTF-8 boundaries, grapheme clusters, wide cells, terminal input modes,
  synchronized output, resize reflow, and alternate-screen applications.
- Own every native handle explicitly. Dispose streams, process handles, ConPTY,
  callbacks, and Ghostty state on all success, failure, and unload paths.
- Never block the ConPTY reader waiting for the UI or while closing ConPTY.
- Derive ABI declarations from pinned headers; verify layouts on x64 and ARM64.
- Use Windows ConPTY for process transport and Ghostty for VT parsing; do not
  substitute redirected standard IO or a different terminal emulator.
- Native builds must produce x64 and ARM64 binaries from the pinned source.
- Keep public dependency properties, events, XAML mappings, README, and sample aligned.
