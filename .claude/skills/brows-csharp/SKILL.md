---
name: brows-csharp
description: Write and edit C# and related .NET project files using the repository's conventions for formatting, type and member layout, properties, public APIs, and shared build settings. Use whenever creating or changing .cs files or .NET project files such as .csproj, .props, .targets, and Directory.Packages.props.
---

# C# and .NET project conventions

Read the target repository's instructions, applicable `.editorconfig`, nearby code, and shared build settings.
Follow the configured formatting and language version.

The explicit rules in this skill take precedence over conflicting `.editorconfig` preferences, conflicting
solution-specific guidance, and nearby code. For example, always use braces even where `.editorconfig` allows
braceless single-line statements. Much existing code predates these rules. Do not copy its older patterns into
new or changed code: braceless `if` bodies, `== null` and `false ==` checks, `_Name` fields, and
expression-bodied methods. Do not restyle code the task does not otherwise touch.

## Formatting and C# style

- Indent C# with four spaces. Use two spaces for project files, XML, XAML, JSON, and Markdown. Use UTF-8 with
  BOM for C# (`.cs`), MSBuild project files (`.csproj`, `.props`, `.targets`, `.slnx`), and XAML. Use UTF-8
  without BOM for `.xml`, `.json`, and Markdown. Preserve configured line endings and the 120-column guideline
  for C# and XAML.
- Use file-scoped namespaces where supported. Put opening braces on the same line. Always use braces around
  statement bodies, including single statements in `if`/`else`, `do`, `for`, `foreach`, `while`, `using`, and
  `lock` blocks.
- Use block bodies for methods, constructors, local functions, and operators. Expression-bodied properties and
  accessors are fine when clear.
- Prefer `var` for local variables. Use PascalCase for types and members, and prefix interface names with `I`.
  Private fields are PascalCase too, with no underscore or other prefix, such as `Locker` or `Count`.
- Import namespaces with `using` directives and refer to types by their unqualified names. Check existing
  global usings before adding imports.
- Prefer early returns over `else` blocks. For reference null checks, use `is null` and `is not null`.
- Keep simple conditions inline. Assign compound or complex `if` and `while` conditions to a descriptively
  named Boolean variable. Recompute the condition explicitly inside a loop as its inputs change.

  ```csharp
  if (text is null) {
      return;
  }

  var inputIsInvalid = number < 0 || string.IsNullOrEmpty(text);
  if (inputIsInvalid) {
      return;
  }
  ```

- Prefer `/* ... */` comments, with ` * ` at the start of each interior line. Use `//` for a single-line
  clarification and align any continuation with the first comment.

  ```csharp
  /*
   * Explain the reason for the next block here,
   * continuing on as many lines as needed.
   */
  lock (Locker) {
      Count++; // Clarify one line, and align a continuation
               // with the start of the comment above.
  }
  ```

## Types and APIs

- Declare classes `sealed` or `abstract`. Keep new types and members `internal` or `private` where practical.
  Add to the public API only when explicitly requested.
- Prefer immutable types, constructor-supplied read-only properties, and `init`-only properties. Keep required
  mutation private. Use `record` types for pure data objects.
- Put each top-level type in its own file named after the type. Nest closely related helper types when they
  belong to their owning type. Form the namespace from the project's root namespace and the folders between
  the project root and the file.
- Keep fields private. Prefer `readonly` where possible. Public fields are reserved for interop layout or
  framework conventions that require them.
- Prefer explicit interface implementations when possible.
- Declare class constructors explicitly instead of using primary constructors. Positional `record` types are
  fine for pure data.
- Put `CancellationToken` parameters last, and pass the token on to every call that accepts one.
- Use APIs available on every target framework. Preserve framework-specific conditions and compatibility
  shims. Language version controls syntax and target frameworks determine API availability.
- Document public APIs with XML documentation. Put each element's opening tag, content, and closing tag
  on separate lines. Reserve XML documentation comments for public APIs. When changing a documented contract,
  update its API documentation and any affected READMEs and usage examples.

## Member order

Omit groups that are absent. Keep each group of static members immediately above its instance counterpart
within the same accessibility and member kind.

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
19. Public fields, where required.
20. Public events.
21. Public properties.
22. Public instance constructors.
23. Public methods.
24. Explicit interface implementations.
25. Nested types.

## Properties and synchronization

- Prefer auto-properties and compiler-generated backing fields. In C# 14 or later, use `field` in accessors
  that need custom logic, such as lazy initialization or setter validation. Keep accessors without custom
  logic auto-implemented. Validate incoming values before assigning to `field`.
- Declare a backing field when it makes the code simpler or supports a requirement such as `volatile`.
- Use `Lock` from `System.Threading` for synchronization fields on .NET 9 or later. When a project targets
  earlier .NET versions as well, use `NET9_0_OR_GREATER` to select `Lock` and use `object` on earlier targets.
  Use C# 13 or later for the compiler's `Lock` support.

`ILabelSource.cs` illustrates a documented public contract.

```csharp
namespace Example.Settings;

/// <summary>
/// Provides a label for display.
/// </summary>
public interface ILabelSource {
    /// <summary>
    /// Gets the label to display.
    /// </summary>
    string Label { get; }
}
```

`LabelSettings.cs` uses a read-only constructor-supplied property, lazy initialization, a validated setter,
and an explicit implementation of that contract.

```csharp
using System;

namespace Example.Settings;

internal sealed class LabelSettings : ILabelSource {
    private const int DefaultMaximumLength = 80;

    internal string Label => field ??= InitialLabel.Trim();

    internal int MaximumLength {
        get;
        private set {
            if (value <= 0) {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Use a positive length.");
            }
            field = value;
        }
    } = DefaultMaximumLength;

    internal string InitialLabel { get; }

    internal LabelSettings(string initialLabel) {
        InitialLabel = initialLabel ?? throw new ArgumentNullException(nameof(initialLabel));
    }

    internal void SetMaximumLength(int maximumLength) {
        MaximumLength = maximumLength;
    }

    string ILabelSource.Label => Label;
}
```

`Counter.cs` shows a synchronization field in a project that targets only .NET 9 or later.

```csharp
using System.Threading;

namespace Example.Settings;

internal sealed class Counter {
    private readonly Lock Locker = new();

    private int Count;

    internal int Increment() {
        lock (Locker) {
            return ++Count;
        }
    }
}
```

When the project also targets frameworks earlier than .NET 9, declare the field this way instead.

```csharp
    private readonly
#if NET9_0_OR_GREATER
        Lock
#else
        object
#endif
        Locker = new();
```

## New files

A file-writing tool may create a file as UTF-8 without BOM and with LF line endings, which differs from the
configured encoding of C#, MSBuild project, and XAML files. Edits to an existing file keep its encoding and line
endings. After creating or fully rewriting one of these files, convert it to UTF-8 with BOM and CRLF line
endings, unless the configuration for that file type says otherwise. For example, in PowerShell:

```powershell
$path = 'path\to\NewType.cs'
$text = [System.IO.File]::ReadAllText($path) -replace "(?<!`r)`n", "`r`n"
[System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($true))
```

## Project changes

- Check shared build properties, targets, and imports before adding project-specific settings. Preserve the
  existing import structure.
- If the repository uses central package management, put package versions in `Directory.Packages.props`
  and use versionless `PackageReference` entries in project files.
- Keep edits scoped to the task. Avoid unrelated formatting, framework, package-version, and release-workflow
  changes. Keep generated build and test output, packages, and IDE state such as `out/`, `bin/`, `obj/`, and
  `.vs/` out of commits.
