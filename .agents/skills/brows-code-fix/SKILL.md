---
name: brows-code-fix
description: Fix specified numbered issues from a code review document, verify the fixes, and preserve resolution history in that document. Use for implementing review findings or addressing review issue numbers, including explicitly requested per-issue Git worktrees. For a new review, use brows-code-review.
---

# Brows code fix

Turn requested review findings into verified, focused fixes that another human or agent can inspect and continue. All review-fix conventions needed for this workflow are included below.

## Establish the exact assignment

Identify the repository, source review document, requested issue numbers, and target checkout or revision. Review files normally live under `{repository-root}/agents/reviews/YYYY-MM-DD_Review-NNN.md`; NNN uses a three-digit minimum. Use the actual specified filename, including legacy names, instead of renaming it to match this convention.

When invoked with a single numeric argument, such as `$brows-code-fix 3`, fix only that issue number in the latest review file under the current repository's `agents/reviews` directory. Select the latest file by the date in its `YYYY-MM-DD_Review-NNN.md` filename, then by the numeric sequence for that date, accepting legacy sequence widths. Do not use modification time: resolving an older review can change its timestamp. Resolve this shorthand to an exact review path and issue number before changing code, and state the selected file and issue in the progress update without asking for confirmation. An explicitly specified review file or directory overrides the default location. If no review file can be identified, the latest file is ambiguous, or the requested issue is absent from it, ask for the missing information; do not silently select an older review containing that number.

Issue numbers are local to each review file: identify an issue by **review file plus issue number**, never the number alone. Read the requested findings and enough surrounding context to understand their contracts and dependencies. If the user explicitly requests all issues, enumerate the applicable issues first. If the file, numbers, or meaning of "all" is materially ambiguous, resolve that ambiguity before changing code. Do not choose an arbitrary subset.

Work only on the requested issues and the minimum changes necessary to correct them. A proposed fix in a review is a hypothesis, not a command to implement blindly. Do not add unrelated refactors, features, public APIs, dependency upgrades, or fixes for other findings. If an unrequested issue must be fixed first, explain the dependency and obtain scope clarification before expanding the assignment. Continue independent requested work when possible.

Read applicable repository instructions. Discover language, build, test, formatting, and contribution conventions from repository evidence. Use relevant installed specialist skills when required or useful, particularly for test authoring and framework-specific test commands; do not assume a particular framework or tool is installed.

## Protect and identify the starting state

Useful read-only commands, run in the intended checkout:

```text
git rev-parse --show-toplevel
git rev-parse HEAD
git status --short --branch
git diff --name-status
git diff --cached --name-status
git ls-files --others --exclude-standard
git worktree list --porcelain
```

Handle a repository without an initial commit explicitly. Record the baseline commit and relevant local changes. Inspect existing edits in affected files before changing them. Preserve user work; do not reset, clean, stash, overwrite, or silently include unrelated edits. Do not switch branches merely to obtain a clean tree. For fixes against a remote review, verify the intended base/head revision rather than assuming the current checkout matches it.

Use Git and provider tools to read required history, PR metadata, and checks as appropriate. A request to fix a local review does not authorize posting comments, approving a PR, pushing, merging, or publishing. Ordinary fixes remain uncommitted unless commits are requested or required by the explicit worktree workflow below.

## Verify each reported defect

For each selected issue:

1. Locate the current implementation, callers, configuration, tests, and relevant contract. Verify paths and line numbers against current code; review references can be stale.
2. Establish the triggering input or state, actual behavior, intended behavior, and impact. Trace the complete relevant execution path and check existing guards before accepting the finding. Verify uncertain API semantics with authoritative documentation matching the dependency version.
3. Classify it as reproducible, supported by a clear static trace, already fixed, disproven, or blocked on missing evidence. Do not change correct code merely to satisfy a mistaken review. Keep consequential unresolved assumptions visible.
4. Choose a correction that addresses the root cause and all affected paths within the requested issue. Preserve intended compatibility, error semantics, resource ownership, security boundaries, and data invariants. Inspect other consumers before changing a shared helper or interface.

## Reproduce, fix, and validate

Prefer red/green testing for each defect:

1. Add the smallest useful behavioral regression case using established repository patterns. Assert externally meaningful results; avoid tests that merely match implementation details or the wording of the review.
2. Run it against the unfixed implementation. Confirm that it fails for the reported defect. Compilation, missing tools, network errors, or unrelated setup failures do not demonstrate the bug.
3. Apply the focused correction, rerun that case, and confirm the expected behavior. Check relevant boundaries, error paths, and nearby consumers so the fix does not exchange one defect for another.
4. Run appropriate related tests and required repository checks. Broaden checks when shared behavior or results justify it. Avoid repeated broad runs after sufficient checks pass unless further edits or new concerns require them.

Never claim a red run that was not performed. If the patch already exists, use an isolated baseline reproduction when practical without disturbing user changes. If automated regression testing is unsuitable, use a deterministic manual reproduction or precise static reasoning, explain why, and record the limits of the evidence. Do not mark an issue resolved while material uncertainty about its correction remains. A passing test suite alone does not prove a finding is fixed.

Inspect unfamiliar scripts before running them and avoid production systems or destructive data operations for reproduction. Keep scratch artifacts isolated. Record the commands, working directory, configuration, revision, and results needed to distinguish the tested code from other checkouts. Do not hide failing checks or bypass repository hooks just to report success.

## Update the source review

Update the existing issue in its original review file. Do not create a replacement review, remove findings, renumber issues, reuse numbers, or duplicate findings. Preserve the original problem, evidence, and issue identity. Change only requested findings and directly related summary counts or status text needed to keep the document accurate.

When the correction and suitable verification establish resolution, append `[Resolved]` to the issue's heading and update any existing status field. Keep the original heading style and severity. For example:

```markdown
### #5 [P2] Validate arguments before updating state [Resolved]

Original finding and evidence retained here.

Resolution: Reject invalid input before mutating state in `src/example.ext`.
Verification: Exact focused check and observed result; related checks and limitations.
```

Record actual changed locations, behavioral correction, regression evidence, and relevant check results. Use repository-relative paths and verified current line numbers when useful. If already fixed, identify the existing correction and verify it without inventing a new patch. If disproven, mark the heading `[Withdrawn]` with the supporting reason, preserving the issue. If blocked or partially addressed, leave it unresolved and document the missing evidence or remaining action.

For worktree fixes, update the issue in that worktree's copy of the review. Do not mark the original checkout's issue resolved on the strength of an unmerged branch.

## Explicit worktrees and commits

Whenever commits are requested or required by this workflow, never put `#` before a review issue number anywhere in the commit message, including its subject and body. Write `Fix issue 2.`, not `Fix #2.`; refer to additional findings as `issue 3`, not `#3`. GitHub can automatically link `#{issue-number}` strings to GitHub issues or pull requests, but review issue numbers are local to the review document. Check the complete commit message for this before committing.

When the user explicitly requests worktrees for multiple issues, create a separate sibling worktree and branch for each requested issue, fix and test each there, update its review entry there, and commit its changes there. Read [references/worktrees.md](references/worktrees.md) for the exact directory, branch, and commit conventions. Apply the same conventions to a single-issue worktree if requested. Do not assume that worktrees authorize parallel subagents.

## Final inspection and handoff

Inspect the final tracked diff, staged diff if any, and new and untracked files. Run `git diff --check` and, when staged changes exist, `git diff --cached --check`; these do not inspect untracked file contents. Read those files explicitly, including ignored review artifacts when applicable. Check for unintended changes, whitespace problems, accidental public API additions, debugging code, and generated artifacts. Confirm that the patch and review updates match the requested issues and tested revision.

Report a compact outcome per requested issue: resolved, already fixed and verified, withdrawn, partial, or blocked. Include the review-file link, what changed, tests/checks and results, and checks that could not run with reasons. Distinguish actual verification from inference. For worktree fixes, include worktree path, branch name, and commit hash ready to merge, plus any dependency or merge caveat. Leave a precise next action for anything unfinished; do not claim complete success if a requested issue remains unresolved.
