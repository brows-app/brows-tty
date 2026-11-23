---
name: brows-git
description: Apply Brows Git conventions when writing commit or merge messages, staging or committing changes, creating or switching branches or worktrees, or running Git commands that change the repository or contact a remote.
---

# Git conventions

Follow the repository's instructions and the user's current request. These rules apply in every checkout of the
repository, including branches and worktrees that agents create. Follow Codex's execution permissions if the
operation needs to write to Git metadata or a sibling checkout outside the writable scope.

## Remotes

- **Never push to `origin` or any other remote unless the user explicitly tells you to push.** This applies to
  every branch, including branches and worktrees that agents create. A request to commit, merge, or finish work
  is not a request to push, and permission to push once does not extend to later work.
- When a push is requested, push only the named branch to the named remote. Do not force-push, push tags, or
  delete remote branches unless the user asks for that specifically.
- Do not pull, because `git pull` merges or rebases into the working tree. When you need current remote refs,
  fetch them (`git fetch <remote> <ref>`); fetching does not change local branches or files.
- Reading pull requests, issues, and checks through `gh` or connected tools is fine. Do not open, comment on,
  approve, or merge pull requests, or create issues or releases, unless the user asks.
- Do not print or record credentials from remote URLs, Git configuration, or credential helpers.

## Preserve existing work

- Before editing, run `git status --short --branch` and inspect the existing diffs of files you will change.
  Changes you did not make belong to the user; keep your changes separate from them.
- Do not discard or hide work you did not create: no `git reset --hard`, `git checkout -- <path>`,
  `git restore`, `git clean`, or `git stash`, and no `git switch` or `git checkout` of another branch that would
  carry or overwrite local changes. Use `git show <rev>:<path>` to read another revision without changing files.
- Do not rewrite history unless asked: no `git commit --amend`, rebase, reset of committed work, or branch
  deletion. Correct a mistake with a new commit.
- Prefer noninteractive Git commands. Supply messages with `-m` or `-F` to keep Git from opening an editor.
- If a merge, rebase, or cherry-pick stops on conflicts, resolve them only when that is within the request.
  Otherwise report the conflicted paths and leave the operation for the user; do not abort one the user started.

## Commits

- Commit only when the user asks or a workflow the user invoked requires it. Commit on the checked-out branch
  unless the user asks for a new one.
- Stage explicit paths with `git add -- <path>...`. Avoid `git add -A`, `git add .`, and `git commit -a`, which
  can capture unrelated user changes, local settings, and agent worktrees. Inspect the index first and preserve
  unrelated staged changes. If the requested changes cannot be committed separately, report the overlap before
  committing.
- Before committing, review `git diff --cached --stat`, `git diff --cached`, and `git diff --cached --check`,
  and read new files in full; diff checks do not inspect untracked files. Run `git status --short` afterward and
  confirm the commit with `git log -1 --stat`.
- Never commit build and test output (`bin/`, `obj/`, `out/`, `TestResults/`), restored packages and native
  dependency trees (such as `packages/` and `vcpkg_installed/`), IDE state (`.vs/`, `*.user`), local agent
  settings, agent worktrees, or secrets. If Git reports one of these paths as untracked, leave it unstaged
  and mention it. Change `.gitignore` only when asked.
- Commit agent artifacts, such as plans and reviews under `agents/`, only as the skill that produced them directs
  or the user asks.
- Honor hooks. Never use `--no-verify` or disable signing. If a hook fails, fix the cause or report it. Git LFS
  installs `pre-push`, `post-checkout`, `post-commit`, and `post-merge` hooks that fail when `git-lfs` is not on
  the path; report that instead of deleting the hooks.

## Commit messages

- Match the existing history; check it with `git log --format=%s -20`.
- Write the subject as a short, capitalized, imperative sentence that ends with a period, usually under 50
  characters and never over 72: `Add file-system projects.`, `Clean up namespaces.`, `Update .editorconfig.`,
  `Fix a bug that could cause blank panels.`
- For a change that needs explanation, add a blank line and a body of prose paragraphs wrapped at 72 columns.
  Describe what changed and why, with one paragraph for each distinct change. Do not list files.
- Do not put `#` before a number unless it refers to a hosting-service issue or pull request; GitHub links
  `#<number>` automatically. Write `Fix issue 2.` for a numbered finding in a local review.
- Put trailers, such as a `Co-Authored-By:` line the session requires, last, after a blank line.
- Write merge subjects in the same sentence style instead of Git's default `Merge branch '...'`, such as
  `Merge FTP link navigation and opening.`, `Merge origin/dev into dev.`, or `Squash merge themes into dev.`
  Merge or squash only when asked, and use the strategy the user names.
- For a multiline message, use a structured commit tool or pass a UTF-8 message file to `git commit -F <file>`.
  Put the file in a writable scratch directory outside the checkout. In PowerShell, a single-quoted here-string
  (`@'` ... `'@`, with the closing `'@` at column 0) preserves the text when writing the file.

```text
Fix panel focus and splitters.

Keep panel splitters visible when panels move. Splitters are drawn above
panels, so closing a panel other than the last no longer hides the
splitters to its right.

Co-Authored-By: Example Agent <agent@example.com>
```

## Line endings, encodings, and LFS

- `.gitattributes` decides line endings. Text is stored normalized and checked out with each type's `eol`
  setting, typically CRLF for source, project, configuration, and Markdown files and LF for shell scripts and
  SVG. Do not change `.gitattributes`, `core.autocrlf`, or a file's line endings to silence Git. A warning such
  as `LF will be replaced by CRLF` for a newly written file is expected; the commit is normalized either way.
- Git does not add or remove a byte-order mark. A diff that changes the first line with no visible difference
  usually means a BOM was added or removed. Keep each file's configured encoding (see `.editorconfig`) unless the
  change is intended. In PowerShell, check for a BOM with `Format-Hex -LiteralPath <path> | Select-Object -First 1`.
  A UTF-8 BOM starts with `EF BB BF`.
- Binary types such as images, icons, and DLL, EXE, PDB, and LIB files are routed through Git LFS by
  `.gitattributes`. Confirm a binary you add with `git check-attr filter -- <path>` (it reports `lfs`) and,
  after staging, `git lfs status`. If a binary type has no LFS rule, do not commit it as a regular blob; ask
  whether to add a rule with `git lfs track`. Never commit built binaries from build output.

## Branches and worktrees

- Find the default branch with `git symbolic-ref --short refs/remotes/origin/HEAD`; do not assume `main`.
- Create branches only when the user asks or a workflow skill requires one. Name an agent branch
  `agents/<short-hyphenated-description>` unless a workflow prescribes a name, and check it with
  `git check-ref-format --branch <name>`.
- When the user asks for a worktree, create it in a sibling directory outside the checkout, such as
  `git worktree add -b <branch> ../_agent-<repository-directory>-<description> <start-commit>`, after checking
  `git worktree list --porcelain` and that the directory does not exist. A workflow skill that prescribes
  worktree names and locations takes precedence. A worktree inside the checkout appears as untracked in the
  main checkout unless it is ignored. Keep worktree directories out of the index.
- A new worktree contains only committed files. It has none of the original checkout's uncommitted changes or
  ignored files, such as restored packages, native dependencies, and build output, so its first build is a full
  build; follow the repository's build instructions. LFS content is checked out by the LFS hooks.
- Run commands in another checkout with `git -C <path>` rather than changing directory. Do not remove worktrees,
  delete branches, or merge worktree branches unless asked; report each branch, its commit, and its path.

## Shell notes

- Put `--` before path arguments, and quote paths that contain spaces.
- In PowerShell, quote revisions that contain braces, such as `'@{u}'` and `'HEAD@{1}'`; unquoted, `@{...}` is
  parsed as a hashtable.
- Use `git --no-pager` or a limit such as `-20` when output could be long, and pass `--no-ext-diff
  --no-textconv` when diffing an untrusted repository.
