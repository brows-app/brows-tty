---
name: brows-code-building
description: Restore, build, and validate SDK-style .NET projects and solutions, including Windows solutions with Visual C++ projects and vcpkg. Use for builds, NUnit test-project sweeps, and restore or build failures.
---

# Build and validate .NET solutions

## Identify what to build

Read the repository's build instructions and use its build scripts and settings where applicable. Identify the
solution (`.slnx` or `.sln`), affected projects, target frameworks, configurations, and platforms. Check
`Directory.Build.props`, `Directory.Build.targets`, and their imports for settings that the project files inherit.
Check `Directory.Packages.props` if the repository uses central package management.

Run the examples from the directory containing the solution and replace the example file names and configuration
with the repository's values. Verify the selected SDK with `dotnet --version` there when `global.json` pins it.
Check for the operating system, workloads, targeting packs, and runtimes required by the projects.

## Build managed projects

Use the .NET CLI for an SDK-style managed solution or project that can build without traversing `.vcxproj` files.
`dotnet build` restores packages by default. Use an explicit restore followed by `--no-restore` when diagnosing
restore failures or when the workflow already separates those steps.

```powershell
dotnet build My.slnx --configuration Release
```

For a solution that defines only `x64`, pass `-p:Platform=x64`. For standalone projects, use the platform required
by the project. If its imports, references, or output paths depend on `$(SolutionDir)`, supply the absolute solution
directory **with a trailing separator**, as in the test example below. A forward slash works as the trailing
separator for managed projects on Windows and avoids a trailing backslash in a quoted command-line argument.

## Build solutions with native projects

Build a solution containing Visual C++ `.vcxproj` files with Visual Studio MSBuild on Windows. Check that the
installation has MSBuild, the solution's C++ `PlatformToolset`, and its Windows SDK. A missing
`Microsoft.Cpp.Default.props` import (sometimes `MSB4278`) from a .NET CLI invocation is a sign to switch tools.

For an x64 build from PowerShell, locate Visual Studio with `vswhere` and run `VsDevCmd.bat` and MSBuild in the
same `cmd.exe` process. Use an installation that provides the required toolset if the latest match lacks it.

```powershell
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild `
    Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vs) { throw 'Visual Studio with MSBuild and the C++ tools was not found.' }
$devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
$solution = (Resolve-Path -LiteralPath 'My.slnx').Path
$buildCommand = ('call "{0}" -arch=x64 -host_arch=x64 >nul && ' +
    'msbuild "{1}" /restore /p:Configuration=Release /p:Platform=x64 /verbosity:minimal') -f $devCmd, $solution
& $env:ComSpec /d /c $buildCommand
if ($LASTEXITCODE -ne 0) { throw "Solution build failed ($LASTEXITCODE)." }
```

If the projects enable vcpkg manifest restore, the build installs ports from `vcpkg.json`. Check the imported
vcpkg targets, triplet, and installed directory. For projects that import the integration directly, follow the
repository's settings for `VcpkgRoot` (which may default to Visual Studio's copy) and `VcpkgInstalledDir`.
A first build or a baseline change can take several minutes to compile ports.

## Sweep the test projects

In a mixed solution, target the managed test projects directly so the .NET CLI does not evaluate the native
projects in the solution. Standalone test builds omit solution-level build dependencies. If tests load native
DLLs, build or locate the current native outputs for the same configuration and architecture before building the
test projects. Check whether the test projects copy those DLLs at build time or locate them at runtime. Also check
any documented test services or other integration prerequisites.

This PowerShell example sweeps the solution's SDK-style NUnit/VSTest projects with the `.Tests.csproj` naming
convention. Use the repository's test runner and naming when they differ. For focused work, pass only the affected
test project to the same `dotnet test` command.

```powershell
$solutionDirProperty = '-p:SolutionDir=' + (Get-Location).Path + '/'
$projects = dotnet sln My.slnx list
if ($LASTEXITCODE -ne 0) { throw 'Could not list solution projects.' }
$testProjects = $projects | ForEach-Object { $_.Trim() } | Where-Object { $_ -like '*.Tests.csproj' }
if (-not $testProjects) { throw 'No matching test projects were found.' }

$failedProjects = @()
foreach ($project in $testProjects) {
    dotnet test $project --configuration Release $solutionDirProperty `
        --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { $failedProjects += $project }
}
if ($failedProjects) { throw "Validation failed for $($failedProjects -join ', ')." }
```

`dotnet test` restores and builds by default. Check that each expected suite actually discovered and ran tests,
since an exit code alone does not establish that tests ran. If a shared prerequisite blocks the sweep, diagnose it
before retrying. Use the same platform and other build properties as the existing outputs when reusing binaries.

## Choose the validation scope

- For code changes, build affected target frameworks and run relevant tests. Expand to the full solution or test
  sweep when requested or when shared build settings, dependencies, or cross-project behavior warrant it. For
  documentation-only changes, review the content and diff and check changed command examples as applicable.
- Use `--no-build` only when the binaries include the latest code changes for the same configuration and
  framework. Use `--no-restore` only when restore is already current.
- Report the scope, test counts or build result, and any checks that could not run. Distinguish restore, build,
  test discovery, and test failures.

## Diagnose build failures

- **Concurrent builds.** Run independent build invocations sequentially when they share intermediate or output
  directories. This includes overlapping .NET CLI and Visual Studio MSBuild invocations.
- **Locked output files.** Copy errors such as `MSB3027` and `MSB3021` usually mean a running instance of the
  application or a test host still holds the output DLLs. Close it and rebuild.
- **C++ file tracking denied in the sandbox.** If `MSB4018` names `Microsoft.Build.Utilities.FileTracker` and
  reports access denied, retry the validation build with `/p:TrackFileAccess=false /p:ForceRebuild=true`.
  This forces native recompilation without file tracking for that invocation.
- **Stale or corrupt outputs.** If restoring older source files left newer binaries in place, or diagnostics point
  to a corrupt reference assembly, clean and rebuild the affected project with the same build properties. If
  necessary, locate `ref` and `refint` under that project's evaluated `IntermediateOutputPath` and remove the
  affected generated artifacts before rebuilding.
- **Missing third-party native headers or libraries.** Check that the native project imports the vcpkg MSBuild
  integration, that the manifest restored into the expected installed directory, and that the platform matches
  the triplet.
- **Environment-dependent test failures.** For access-denied errors, check the paths used by the fixture and
  whether the current sandbox permits access. UI rendering, focus, and timing tests can fail when the session is
  locked or the machine is busy. Rerun an affected test alone and compare with the base revision if the cause
  remains unclear.
