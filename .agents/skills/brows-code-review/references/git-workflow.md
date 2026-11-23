# Git and review tools

## Establish the repository state

Run commands in the intended repository, using `git -C <root>` where helpful. Useful initial probes:

```text
git rev-parse --show-toplevel
git status --short --branch
git diff --name-status
git diff --cached --name-status
git ls-files --others --exclude-standard
git log -5 --oneline --decorate
git remote -v
git worktree list --porcelain
```

Inspect applicable `AGENTS.md` and repository contribution/build instructions. Preserve pre-existing work and note which
changes belong to the review. Never reset, clean, stash, checkout over user edits, or rewrite history to make reviewing
easier. Do not expose remote credentials in artifacts or summaries.

## Select the correct comparison

- **Bare `$brows-code-review`:** review the current branch at `HEAD` against the repository's default branch. Prefer
  repository configuration or provider metadata identifying the default branch, including the relevant remote's symbolic
  `HEAD` (for example, `git symbolic-ref refs/remotes/origin/HEAD`). If no default is identified, prefer an existing
  `dev` branch/ref; otherwise infer from available `main` or `master` refs and repository evidence. Prefer the relevant
  remote-tracking ref when available, and disclose stale or inaccessible remote information. If conflicting evidence
  leaves the default branch unclear, ask only for the missing baseline. Do not use the current branch's upstream as the
  baseline merely because it is configured; it may track the feature branch itself. Exclude local uncommitted changes
  unless the user requests them.
- **PR/branch changes:** determine the actual base and target refs from PR metadata, the user, or the default above.
  Resolve both to commit IDs, compute `git merge-base <base-id> <target-id>`, and compare that ancestor to the target.
  Explain if reviewing endpoints instead. A missing merge base or shallow history must be resolved or disclosed, never
  silently substituted.
- **Explicit revisions:** `git diff <old-id> <new-id>` compares the requested endpoints. Do not replace this with a
  merge-base comparison without cause.
- **Single commit:** inspect commit metadata and parents, then compare the intended parent with that commit. Merge
  commits require a deliberate parent choice; root commits have no parent.
- **Local work:** `git diff` covers unstaged tracked edits; `git diff --cached` covers staged edits; `git diff HEAD`
  covers their combined net effect for an existing HEAD. None includes untracked files. Inspect relevant untracked files
  separately, and handle repositories without an initial commit accordingly.

Use `--stat`, `--name-status`, and `--find-renames` for inventory, then inspect actual patches and full context. Use
`--` before path arguments. Quote paths and ref inputs appropriately for the shell; never interpolate untrusted PR text
into commands. Disable external diff/text conversion helpers when reviewing an untrusted repository, for example
`git --no-pager diff --no-ext-diff --no-textconv <old-id> <new-id> -- <path>`.

Use `git show <commit>:<path>` to inspect committed content without switching the working tree. Confirm line numbers
against the revision cited. Avoid attributing dirty-checkout code to a clean committed target. For executable validation
of another revision, use an isolated checkout/worktree when necessary and permitted, and record the exact code tested.

## Remote reviews

Prefer available connected provider tools, or an authenticated CLI such as `gh`, for PR metadata, base/head commit IDs,
changed files, checks, and discussion. Check tool availability and authentication rather than assuming them. Reading a
PR is not authorization to post comments, approve/request changes, push, or merge. Write the local review artifact
unless the user explicitly requests external publication.

If required refs are unavailable, fetch only the needed refs when permitted; fetching is distinct from merging or
pulling into the working tree. Record stale or inaccessible remote information as a limitation. Recheck the PR head
before declaring a moving PR reviewed; reassess if it changed.

## Finish accurately

Review tracked diffs and relevant new/untracked files, run `git diff --check` for working-tree changes and the
corresponding cached check for staged changes, and inspect `git status --short`. Account for ignored files with explicit
path checks when necessary. Diff checks do not inspect untracked file contents. Do not stage the entire repository
indiscriminately. Review artifacts may be ignored or untracked; still verify their content and disclose their state when
relevant.
