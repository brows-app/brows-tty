---
name: brows-code-testing
description: Write and run NUnit tests in .NET repositories that use matching .Tests projects and SubjectTest fixtures. Use for new tests, regression coverage, test fixes, and focused NUnit runs.
argument-hint: "[type, member, behavior, or test project]"
---

# Write and run NUnit tests

Request: `$ARGUMENTS`

## Find the test project

Read the target repository's instructions (`CLAUDE.md`, `AGENTS.md`), build and test documentation, and nearby
tests. Identify the source project, the matching test project, and its target frameworks. Inspect the test
project's references and shared build settings for NUnit, the test runner or adapter, analyzers, and global
usings. Reuse existing test helpers and test dependencies.

- Name the test project after the source project with `.Tests` appended, for example `My.Foo.Bar.Tests` for
  `My.Foo.Bar`. Confirm the test project references the source project. For internal types, check the project's
  existing `InternalsVisibleTo` setup before changing production visibility.
- Name each fixture after the tested type with `Test` appended, for example `OrderIdTest` for `OrderId`. Put
  `OrderIdTest.cs` in the folder that mirrors the source type's folder and use the same namespace as the type.
- Declare `[TestFixture]` classes as `internal sealed`. Name test methods `Member_Behavior`. Use `[Test]` for a
  single scenario and `[TestCase]` for inputs that exercise the same behavior.

```csharp
using NUnit.Framework;

namespace My.Foo.Bar.Orders;

[TestFixture]
internal sealed class OrderIdTest {
    [TestCase("1", 1)]
    [TestCase("42", 42)]
    public void Parse_ValidText_ReturnsOrderId(string text, int expected) {
        var result = OrderId.Parse(text);
        Assert.That(result.Value, Is.EqualTo(expected));
    }
}
```

## Write behavior-focused tests

- Cover observable results, relevant side effects, and error or boundary cases. Use NUnit constraint assertions
  such as `Assert.That(actual, Is.EqualTo(expected))`. For exceptions, isolate the action expected to throw and
  check the exception type and any details the caller relies on.
- When making multiple related assertions in a row, group them in a `using (Assert.EnterMultipleScope())`
  block so NUnit reports their failures together. Keep prerequisite checks outside the block when later
  assertions depend on them passing. `Assert.EnterMultipleScope()` requires NUnit 4.2 or later.

  ```csharp
  using (Assert.EnterMultipleScope()) {
      Assert.That(actual.Id, Is.EqualTo(expectedId));
      Assert.That(actual.Name, Is.EqualTo(expectedName));
  }
  ```

- Follow the affected project's NUnit and Moq style. Use Moq where the project already uses it and a dependency
  needs a substitute. Verify calls only when the interaction is part of the behavior under test.
- Prefer a focused integration test when mocking a dependency becomes complicated or unwieldy. For example,
  use the real file system in a unique temporary directory per test when the behavior needs real files or
  directories. Reuse the project's test helpers for setup and cleanup when available.
- Keep tests independent of order and machine state. On test exit, clean up all test-created artifacts and
  restore any shared state, including after an assertion failure, exception, or cancellation. Use a disposable
  helper or `try`/`finally`. Close any open streams and other file handles before removing the test-owned files
  and directories.
- Return `Task` from async tests and await the operation. Prefer explicit task signals or events with bounded
  waits over timing-dependent sleeps. Use APIs available on every applicable target framework for tests and
  shared helpers.
- Add regression coverage for behavior changes. When practical, run the regression against the unfixed code and
  confirm it fails for the reported behavior. After the fix, rerun it and the relevant existing tests. Preserve
  existing work when checking a baseline: inspect `git status` and diffs first, and do not use `git stash`,
  `git checkout`, or `git reset` on files that hold uncommitted changes you did not make.

## Run the relevant tests

1. Follow the repository's documented command and configuration. Check `global.json`, the project and imported
   properties, and CI for the SDK, test runner, settings, platform, and any required MSBuild properties. Run from
   a directory where the intended `global.json` applies. For solutions with native projects, use the documented
   toolchain or target the managed test project directly if supported. Supply any required `SolutionDir` override
   with its trailing separator.
2. Start with the requested method, fixture, or affected project. For SDK-style VSTest projects, a fixture filter
   looks like the command below. For a method, include its name in the fully qualified name filter. Add
   `--framework` when selecting a target from a multi-targeted project. For Microsoft Testing Platform or a
   classic project, use the repository's configured runner and its filter syntax.

   ```sh
   dotnet test path/to/My.Foo.Bar.Tests.csproj --filter 'FullyQualifiedName~My.Foo.Bar.Orders.OrderIdTest.'
   ```

   Quote the filter for the shell in use. Run long sweeps with `run_in_background` and wait for the completion
   notification instead of polling.
3. Use `--no-build` only when the output matches the latest source and tests, configuration, and framework. Use
   `--no-restore` only when restore is already current. Confirm the expected cases were discovered and executed.
   If none ran, check the project and target framework, test adapter or runner, and filter before reporting a result.
4. For changes spanning projects or frameworks, run the broader validation required by the repository. Report the
   command and scope, observed counts, and any failures or blocked checks. Distinguish restore, build, discovery,
   and test failures. A regression check needs a failure from the targeted behavior itself.
