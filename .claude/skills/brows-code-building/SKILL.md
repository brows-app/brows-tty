---
name: brows-code-building
description: Restore, build, and validate .NET solutions and projects, including solutions with native C/C++ projects that need Visual Studio MSBuild and vcpkg, and sweep their NUnit test projects. Use for building, full-solution validation, test-project sweeps, and diagnosing restore or build failures.
argument-hint: "[solution, project, or scope to build]"
---

# Build and validate .NET solutions

Request: `$ARGUMENTS`

## Identify what to build

Read the repository's instructions (`CLAUDE.md`, `AGENTS.md`) and build documentation. Identify the solution file
(`.slnx` or `.sln`), the affected projects, their target frameworks, and the solution's configurations and
platforms before building. Check:

- `global.json` for the SDK version and roll-forward policy. Run commands from a directory where it applies, and
  make sure the operating system, workloads, targeting packs, and runtimes the affected projects need are present.
- `Directory.Build.props` and `Directory.Build.targets` files and the shared property files they import, often
  through `$(SolutionDir)`. They may supply target frameworks, global usings, project references, and the output
  directory, so a nearly empty project file can still depend on them.
- `Directory.Packages.props` for central package management.
- Native `.vcxproj` projects, the `vcpkg.json` manifest, and solution-level build dependencies that order native
  and managed projects.
- The solution platforms. When a solution defines only `x64`, build it with `Platform=x64`.

## Build managed projects

For a solution or project without native projects, use the .NET CLI from the solution or project directory:

```sh
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --no-restore --configuration Release
dotnet pack --no-build --no-restore --configuration Release
```

Pack only projects that produce packages. Pass a platform as an MSBuild property, `-p:Platform=x64`; the .NET CLI
has no `--platform` switch.

When a project imports shared files through `$(SolutionDir)` and is built outside its solution, pass the solution
directory **with a trailing separator**, such as `-p:SolutionDir=C:\repo\sln\`. Without it, `SolutionDir` is
undefined and `$(SolutionDir)/props/...` imports resolve against the drive root.

## Build solutions with native projects

The .NET CLI cannot build C/C++ projects. `dotnet build` of such a solution fails with `MSB4278` because
`Microsoft.Cpp.Default.props` is unavailable, and `dotnet test` of the solution runs the managed tests but exits
nonzero for the same reason. Build the full solution with Visual Studio MSBuild, and target managed test projects
directly with the .NET CLI.

MSBuild needs the developer environment that `VsDevCmd.bat` sets up. Environment changes do not persist between
separate shell invocations, so run the setup and the build in the same `cmd` process. Locate Visual Studio with
`vswhere` instead of hard-coding its path:

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
$devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
$solutionDir = 'C:\repo\sln'
& $env:ComSpec /c "call `"$devCmd`" >nul && cd /d `"$solutionDir`" && msbuild My.slnx /p:Configuration=Release /p:Platform=x64 /restore /verbosity:minimal"
```

Native projects that use vcpkg in manifest mode restore their ports during the build, into the directory the
projects configure (often a repository-local `vcpkg_installed/`). Check how the projects import the vcpkg MSBuild
integration and which vcpkg they use. Many default to the copy bundled with Visual Studio and accept a
`VcpkgRoot` property for another installation. The first build after a manifest or baseline change can compile
ports for many minutes; run it with `run_in_background` and wait for the completion notification.

## Sweep the test projects

To run every test project in a solution that has native projects, restore, build, and test each managed test
project in turn. Stop at the first failure and report which step failed:

```powershell
$solutionDir = 'C:\repo\sln\'
$solutionDirProperty = "-p:SolutionDir=$solutionDir"
Get-ChildItem $solutionDir -Recurse -Filter *.Tests.csproj | ForEach-Object {
    $project = $_.FullName
    dotnet restore $project $solutionDirProperty
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
    dotnet build $project --no-restore --configuration Release $solutionDirProperty
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    dotnet test $project --no-build --no-restore --configuration Release $solutionDirProperty `
        --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
}
```

Adjust the project discovery when the repository names or places its test projects differently. Run only the
affected test projects while iterating, and the full sweep for broader validation. A sweep can take minutes; run
it in the background.

## Choose the validation scope

- For focused work, build or test the affected project and target framework first, then run broader validation.
- For code changes, run the relevant tests and build every affected target framework. Run full-solution
  validation when changing shared build settings, dependencies, or behavior that spans projects. For
  documentation-only changes, review the content and diff.
- Use `--no-build` only when the binaries include the latest code changes for the same configuration and
  framework. Use `--no-restore` only when restore is already current.
- Report the commands and scope, observed results such as test counts, and any check that could not run.
  Distinguish restore, build, test discovery, and test failures.

## Diagnose build failures

- **Concurrent builds.** Do not run the .NET CLI and Visual Studio MSBuild on the same tree at the same time. They
  write the same `obj` and output files and can leave them locked or corrupted.
- **Locked output files.** Copy errors such as `MSB3027` and `MSB3021` usually mean a running instance of the
  application or a test host still holds the output DLLs. Close it and rebuild.
- **Corrupted reference assembly.** If a project that compiled before suddenly reports `CS0122` for an internal
  type despite `InternalsVisibleTo`, together with `CS9103`, the referenced project's reference assembly may be
  corrupt. Delete that project's `obj\ref` and `obj\refint` directories and rebuild; a plain rebuild can reuse
  the corrupt file.
- **Stale binaries.** Incremental builds compare timestamps. A source file restored by copying an older saved
  copy keeps its old timestamp, so the build can keep binaries built from a temporary version. Set the file's
  last-write time to now before rebuilding, or prefer a temporary commit over copying files around.
- **Missing third-party native headers or libraries.** Check that the native project imports the vcpkg MSBuild
  integration, that the manifest restored into the expected installed directory, and that the platform matches
  the triplet.
- **Environment-dependent test failures.** UI rendering, focus, and timing tests can fail when the session is
  locked or the machine is busy. Rerun such a test alone, and compare with the base commit in a separate worktree
  before treating a failure as a regression.
