# Repository guidance

This file applies to the entire repository. Read the root `README.md` and
nearby implementation and tests before making changes.

## Solution layout

- `brows-tty.slnx` contains three packages, NUnit integration tests, and a WPF sample.
- `source/Brows.TTY/` contains the UI-independent Ghostty core (`net462`, `net48`, `net8.0`, `net10.0`).
- `source/Brows.TTY.Win32/` contains Windows ConPTY transport without WPF.
- `source/Brows.TTY.Windows/` contains the WPF control and renderer.
- The root namespace is `Brows`; the public control is `Brows.Windows.Controls.Terminal`.
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

## Build and test

Build `net462`, `net48`, `net8.0`, and `net10.0` for the core, and `net462`,
`net48`, `net8.0-windows`, and `net10.0-windows` for
the Windows layers, with the SDK pinned in `global.json`. All three source
libraries are packable. Run native builds before managed integration
checks. Tests must exercise the real Ghostty library and real ConPTY where possible.
Verify package contents and a consuming application, not only project references.
Run x64 tests locally; ARM64 execution requires an ARM64 Windows runner.
Never describe cross-compilation alone as ARM64 runtime validation.

For code changes, build all affected target frameworks and run relevant tests.
Use full solution validation for shared build changes. Report checks and limitations.

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
## Code and project conventions

- Follow `.editorconfig`: four spaces for C#; two spaces for
  project XML, XAML, JSON, and Markdown. C# and project/XAML files use UTF-8 with
  BOM; Markdown uses UTF-8 without BOM. Keep C# and XAML lines within the
  configured 120-column guideline.
- Use file-scoped namespaces and opening braces on the same line. Follow the
  configured C# style and nearby code. `.editorconfig` prefers explicit local
  types, while existing implementation and tests frequently use `var`; avoid
  unrelated rewrites to reconcile that difference.
  Use PascalCase for types and members and an `I` prefix for interfaces.
- Never prefix type names with their namespace. Always use `using` statements
  to import namespaced types.
- Method parameters of type `CancellationToken` must always come last in the
  parameter list.
- Use the configured C# language version. APIs must be available on every
  framework targeted by the affected project. Preserve framework-specific
  conditions and compatibility shims; a newer language version does not supply
  newer runtime APIs.
- Check shared build settings and global usings before adding project-specific
  settings or imports. Preserve existing build-property import structures.
- When central package management is configured, manage dependency versions in
  `Directory.Packages.props` and omit versions from project package references.
  Keep dependency changes focused on the task.
- Do not add to the public API unless explicitly instructed to do so. Keep new
  types and members internal or private when the requested change permits it.
- Declare new implementation classes `sealed` or `abstract` where appropriate.
  Preserve WPF partial declarations and the public control's inheritance contract;
  do not seal public controls as incidental cleanup.
- Prefer immutable types. When state mutation is required, keep it private. Prefer
  read-only properties supplied through constructors and init-only properties.
  Pure data objects should be `record` types.
- Put each top-level type in a separate file named after the type. Keep closely
  related helper types nested with their owning type when that matches the
  existing design.
- Namespaces must match the directory structure. The namespace of a type is the
  project's root namespace combined with the folders that contain the type's
  file, relative to the project root. Preserve the test namespace described below
  and assembly-level declarations in `Properties/AssemblyInfo.cs`.
- Update public API documentation, relevant READMEs, and usage examples when
  changing the documented contract. Do not add XML documentation comments to
  private or internal types or members.
- Keep changes scoped. Avoid unrelated formatting, framework, package-version,
  or release-workflow changes. Do not commit generated build/test output,
  packages, or IDE state such as `out/`, `bin/`, `obj/`, and `.vs/`.

Within a C# type, use this member order, omitting items that do not exist:

1. Static constructor.
2. Private constants.
3. Private fields.
4. Private events.
5. Private properties.
6. Private instance constructors.
7. Private methods.
8. Internal constants.
9. Internal events.
10. Internal properties.
11. Internal instance constructors.
12. Internal methods.
13. Protected constants.
14. Protected events.
15. Protected properties.
16. Protected instance constructors.
17. Protected methods.
18. Public constants.
19. Public fields, only where interop layout or framework conventions require them.
20. Public events.
21. Public properties.
22. Public instance constructors.
23. Public methods.
24. Explicit interface implementations, after everything else.

### Properties

Prefer compiler-generated backing fields. C# 14 is enabled; use `field` when
custom property logic can use an automatic backing field, as in `PreviewWorker`.
Keep accessors without custom logic auto-implemented. Validate setter input before
assigning it to `field`. WPF dependency-property wrappers must continue to use
`GetValue` and `SetValue`, including read-only dependency-property keys.

For synchronization fields, use `System.Threading.Lock` when the target
framework is `net9.0` or greater. In multi-targeted code, use
`NET9_0_OR_GREATER` to select `Lock` for those targets and `object` for earlier
targets.

Use a manually declared field when an automatic backing field would make the
code more complicated, such as when the field must be `volatile`.

### C# style

- Follow `.editorconfig` and nearby code: four spaces for C#, opening braces on
  the same line, and CRLF line endings.
- Use block-bodied methods and constructors with opening and closing braces. Do
  not use expression-bodied methods (`=>`). Expression-bodied properties and
  accessors are fine when they keep the property clear.
- Always use braces around statement bodies, including `if`/`else`, `do`, `for`,
  `foreach`, `while`, `using`, and `lock`, even when the body is one statement.
- Keep simple conditions inline. For compound or complex `if` or `while` conditions,
  calculate a descriptively named `bool` variable beforehand; update or recompute it
  explicitly inside loops.

  > Examples of acceptable and unacceptable conditions:

  ```csharp
  /*
   * These simple conditions are acceptable inline.
   */
  if (arg is null) { }
  if (num >= 0) { }
  if (string.IsNullOrEmpty(str)) { }

  /*
   * Calculate compound conditions beforehand and give them a descriptive name.
   */
  var inputIsInvalid = input is null || number < 0 || string.IsNullOrEmpty(text);
  if (inputIsInvalid) { }
  ```

- Prefer early `return` over `else` blocks.
- Check reference values for null with `is null` and `is not null`. Prefer these
  patterns over `== null` and `!= null`.
- Prefer `/* ... */` block comments, with ` * ` at the start of each interior
  line. Use `//` only for a single-line clarification; align a continuation line
  with the comment when needed.
- In XML documentation, put each element's opening tag, content, and closing tag
  on separate lines. Document public APIs; do not add XML documentation comments
  to private or internal members.
- Use PascalCase for types and members, and prefix interfaces with `I`. Follow
  the member ordering above and local declaration style. Prefer private
  `readonly` fields where possible, keep fields non-public, and put each type in
  a separate file named after the type.

## Tests

The single test project references the source project, whose
`Properties/AssemblyInfo.cs` grants it access to internal members. NUnit is
referenced through `tests/Directory.Build.props`, which imports its global using.
Moq has a centrally managed version available if a test actually needs mocking.

Fixtures use the `Tests` suffix and namespace `Brows.TTY.Tests`.
Use NUnit constraint assertions and Moq only when a real integration is impractical.
Mark WPF fixtures `[Apartment(ApartmentState.STA)]` and pump the dispatcher for
asynchronous behavior. Prefer bounded task signals over arbitrary sleeps.
Tests must cover observable terminal behavior, process lifecycle, and native
packaging. Verify both managed targets and distinguish build from runtime evidence.
## Code reviews

When asked to review code, inspect the entire requested scope thoroughly and
identify all bugs and issues you can find. Write the findings to `Review.md` in
the directory `_agents` in the repository root. Number each issue so it can be
referenced later. For every issue, describe the problem and a way to resolve it.
Cite specific file names and line numbers where applicable. Review existing
findings in `Review.md` before editing it so relevant issues are retained or
updated without duplication. Keep review issue numbers stable: never renumber
existing issues or reuse their numbers, including those marked resolved. However,
do not search the Git history for a `Review.md` if one does not exist: in that
case, simply create a new `Review.md` and start with issue #1. Assign each new
issue a number greater than the highest issue number already used in `Review.md`.

> The full path to `Review.md` should be `{root}/_agents/Review.md`.

When asked to fix issues from `Review.md`, work only on the issue numbers
specified in the request. Prefer red/green testing: first add a test that fails
because of each issue, then fix the code, then rerun the previously failing test
to confirm it passes. Once an issue is resolved, keep it in `Review.md` and mark
it as resolved in that issue's heading; do not remove it.

If worktrees are explicitly requested for fixing multiple issues, create a
separate Git worktree for each requested issue number. Place each worktree
directory directly in the parent directory of the original repository root,
as a sibling of that repository. Name it
`_agent-{original-directory-name}-{issue-number}`, where
`original-directory-name` is the name of the original repository's root directory.
Name each worktree branch
`agent/{issue-number}/{short-description}`. Fix, test, and update `Review.md`
for that issue in its own worktree. Commit each issue's changes on
its worktree branch. The first line of the commit message must start with
`Fix #{issue-number}.`, followed by a blank line and a description of what was
broken and how it was fixed. Once the commit is ready, notify the requester
that the worktree branch is ready to be merged.

> The `{short-description}` in branch names should be hyphenated, for example,
> `agent/5/validate-arguments`.

Before reporting completion, review the final diff for unintended changes,
whitespace problems, and accidental public API additions. Check new and untracked
files as well as tracked changes, and confirm that the changes match the request.

Report completion consistently: summarize what changed, list resolved issue
numbers when applicable, and state which tests or checks ran and their results.
Identify any checks that could not run. For worktree fixes, include the branch
name and commit hash that are ready to be merged.

## Planning

When asked to plan code changes, write your plan to `Plan-{short-description}.md`
in the directory `_agents` in the repository root. The `{short-description}` should
be PascalCase, with no spaces, dashes, or underscores in the string.

> For example, the full path to a plan for updating security code would be
> `{root}/_agents/Plan-SecurityUpdate.md`

Your plan should include enough information for another agent with lesser capability
to follow it. Include in the plan the means of accomplishing tasks: you will start
subagents for any tasks that can be logically separated from others, and you will
review their work as it completes and merge their changes into your main worktree
branch for the updates.
