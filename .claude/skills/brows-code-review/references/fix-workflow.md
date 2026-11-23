# Fixing numbered review issues

## Scope and evidence

Read the source review and requested issue numbers, then verify each issue against the current code. A review may be stale or mistaken. If already fixed, record the existing correction and verification; if disproven, preserve the issue and explain withdrawal. Do not implement a harmful change to satisfy a mistaken finding.

Fix only requested issues and the minimum dependencies needed to fix them correctly. Keep unrelated findings and refactors out of the patch. Surface a dependency on an unrequested issue before expanding scope. Ordinary fixes stay in the existing checkout unless worktrees are requested or an isolated checkout is needed for safe execution.

Prefer red/green testing: add a focused behavioral regression case, run it against the unfixed code, confirm failure for the reported reason, apply the fix, then rerun it and appropriate related checks. Use applicable testing skills when available and required by the environment. A compilation/setup failure is not evidence that the regression was reproduced. For issues unsuitable for automated tests, document a deterministic reproduction or justified inspection and the limits of validation. Never invent a red run after the fix.

Update only the requested issues in the review file. Preserve their numbers and original evidence; mark each heading `[Resolved]` only once the correction and suitable verification establish resolution. Record commands, results, and remaining limitations.

## Explicitly requested worktrees

For multiple issues with worktrees explicitly requested, create one worktree and branch per requested issue. Obtain the original repository root and its parent, and identify a common starting commit that contains the reviewed code and source review. Do not silently omit uncommitted fixes or an untracked review from that baseline. If needed, copy just the authorized review artifact into each worktree; do not commit unrelated user changes to prepare a baseline.

Use these names, removing only the `.md` extension from the source review filename:

```text
Worktree: {parent}/_agent-{original-directory-name}-{review-file-stem}-{issue-number}
Branch:   agents/{review-file-stem}/{issue-number}/{short-description}
```

Example for issue #5:

```text
Worktree: C:/src/_agent-widget-2026-11-12_Review-001-5
Branch:   agents/2026-11-12_Review-001/5/validate-arguments
```

Use a short hyphenated description. Verify branch syntax with `git check-ref-format --branch`, check existing branches/worktrees and target directories, and use `git worktree add -b <branch> <sibling-path> <start-commit>`. Do not overwrite a collision or reuse someone else's worktree. Resolve name collisions with a distinct short description; if the prescribed directory already exists, inspect it and ask when safe ownership or reuse cannot be established. Sibling paths may require execution permission in the current environment; do permitted preparatory work before reporting a real permission blocker.

In each worktree, fix, test, and update that issue in its own copy of the source review file. Stage only the intended fix, regression tests, and review update. Inspect the staged diff and commit each issue on its branch using this exact first-line prefix:

```text
Fix issue 5. Validate arguments before updating state

Describe what was broken, how it was fixed, and relevant verification.
```

Replace `5` with the actual issue number, without `#`. Use a commit-message file with `git commit -F <file>` (in the session scratchpad when available) or a quoted Bash heredoc to preserve newlines and avoid shell expansion. Never put `#` before an issue number in the message. Append any attribution trailer the session requires after the description. Create worktrees with `git worktree add`, not the `EnterWorktree` tool, since names and locations are prescribed. Honor repository hooks and report failures accurately; do not bypass them without authorization.

Record the commit hash using `git rev-parse HEAD`, branch name, worktree path, and validation results. Notify the requester that each worktree branch is ready to merge. Leave worktrees and branches available for review; do not merge, push, or remove them unless requested. If fixes depend on each other or review-file edits conflict when combined, explain the dependency and merge order rather than claiming independent merge readiness. Do not mark the original checkout's review resolved merely because an unmerged branch contains a fix.
