# Per-issue worktrees

Use this workflow when the user explicitly requests worktrees. The multiple-issue convention is one sibling worktree, branch, and issue-specific commit per requested issue.

## Baseline and names

Resolve the original repository root and its parent. Identify the intended starting commit and whether it contains the reviewed code and source review. A worktree starts from a commit and does not include dirty or untracked work. Do not silently lose required changes or commit unrelated user edits to create a baseline. If the source review is untracked or newer than the baseline, copy only the authorized review artifact into each worktree and include it in the issue commit. If required code exists only as local edits, establish a safe scoped baseline from those edits or clarify when that cannot be done without including unrelated work.

Use the review file's stem (remove only its `.md` extension) in these names, and the issue number without `#`:

```text
Directory: {parent}/_agent-{original-directory-name}-{review-file-stem}-{issue-number}
Branch: agents/{review-file-stem}/{issue-number}/{short-description}
```

Example:

```text
Original root: C:/src/widget
Source review: agents/reviews/2026-11-12_Review-001.md
Issue: #5
Worktree: C:/src/_agent-widget-2026-11-12_Review-001-5
Branch: agents/2026-11-12_Review-001/5/validate-arguments
```

Use a concise hyphenated description. Verify branch syntax with `git check-ref-format --branch`. Check `git worktree list --porcelain`, existing refs, and the resolved sibling directory before creation. Do not overwrite a collision or assume ownership of an existing worktree. A branch collision can use a distinct short description; an occupied prescribed directory needs inspection and clarification if safe reuse cannot be established.

Create from the chosen commit:

```text
git worktree add -b <branch> <sibling-path> <start-commit>
```

Quote arguments appropriately for the active shell (Bash and PowerShell differ) and run commands in the intended checkout using `git -C <path>` rather than `cd`. Never build shell commands from untrusted review prose. Sibling directories sit outside the session's working directory, so Claude Code may prompt for permission to read or edit there; complete permitted preparation before reporting a real permission blocker, and never bypass the sandbox or permission mode to get around one. Do not use the `EnterWorktree` tool for these; it does not follow the prescribed names and locations.

## Keep each issue verifiable

Use a common baseline when the fixes are independent. Keep changes, tests, and the review update for each issue in its own worktree. Do not test one checkout and attribute the result to another. If issues depend on each other, make the dependency and intended merge order explicit; do not claim independent readiness. A fix on a branch does not resolve the issue in the original checkout until integrated.

Before committing, inspect both tracked and new files, run relevant validation and diff checks, stage only intended paths or hunks, and inspect `git diff --cached`. Avoid `git add .` when it could capture unrelated changes. If the review artifact is ignored, check the applicable ignore rules and repository policy rather than silently omitting a required update or broadly changing ignore rules.

Commit each issue on its worktree branch. The first line must start with `Fix issue {issue-number}.`, followed by a blank line and a description of what was broken and how it was fixed:

```text
Fix issue 5. Validate arguments before updating state

Invalid arguments previously left partially updated state. Validate them before
mutation and add a regression case proving state is preserved on rejection.
```

Write the message to a file (in the session scratchpad when available) and pass it with `git commit -F <file>`, or use a quoted Bash heredoc (`git commit -F - <<'EOF'`), to preserve real newlines and avoid shell expansion. Append any attribution trailer the session requires after the description. Honor repository hooks; if they fail, inspect and address relevant failures without bypassing them unless authorized. Revalidate affected behavior after hook-generated edits. If no code patch is needed because an issue is already fixed or disproven, commit only a meaningful authorized review update; do not invent an empty code fix.

After the commit, obtain the full hash with `git rev-parse HEAD` and check working-tree status. Distinguish a successful commit from an attempted one. Report branch, hash, worktree path, resolved issue, checks, and any remaining dirty or ignored artifacts. Notify the requester which branches are ready to merge. Mention review-file merge conflicts when independent branches edited the same file; preserve all issue numbers and per-issue resolution evidence when integration is later requested.

Leave the branches and worktrees available for review. Do not push, merge, rebase, delete branches, or remove worktrees unless requested. If the intended branch could not be completed, report the current state and exact remaining action instead of calling it ready.
