param(
  [ValidateSet('win-x64', 'win-arm64')]
  [string[]] $Runtime = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$versions = Get-Content (Join-Path $root 'native/versions.json') -Raw | ConvertFrom-Json
$toolRoot = Join-Path $root '.tools'
$zig = Join-Path $toolRoot "zig-x86_64-windows-$($versions.zigVersion)/zig.exe"

function Invoke-Checked([string] $File, [string[]] $Arguments) {
  & $File @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

if (!(Test-Path $zig)) {
  New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
  $archive = Join-Path $toolRoot 'zig.zip'
  Invoke-WebRequest $versions.zigWindowsX64Url -OutFile $archive -UseBasicParsing
  if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $versions.zigWindowsX64Sha256) {
    throw 'Zig archive checksum mismatch.'
  }
  Expand-Archive $archive $toolRoot -Force
}

# Use the same redistributable console host as Windows Terminal. Inbox ConPTY
# versions can consume OSC color queries without returning or forwarding them.
$conptyRoot = Join-Path $toolRoot "conpty-$($versions.conptyVersion)"
$conptyArchive = Join-Path $conptyRoot 'conpty.zip'
New-Item -ItemType Directory -Path $conptyRoot -Force | Out-Null
if (!(Test-Path $conptyArchive)) {
  Invoke-WebRequest $versions.conptyPackageUrl -OutFile $conptyArchive -UseBasicParsing
}
if ((Get-FileHash $conptyArchive -Algorithm SHA256).Hash -ne $versions.conptyPackageSha256) {
  throw 'ConPTY package checksum mismatch.'
}
Expand-Archive $conptyArchive $conptyRoot -Force

$source = Join-Path $root 'native/ghostty'
if (!(Test-Path (Join-Path $source '.git'))) {
  Invoke-Checked git @('clone', '--no-checkout', '--filter=blob:none', $versions.ghosttyRepository, $source)
  Invoke-Checked git @('-C', $source, 'checkout', '--detach', $versions.ghosttyCommit)
}
$revision = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne $versions.ghosttyCommit) {
  throw 'Ghostty checkout does not match native/versions.json. Preserve local edits and check out the pinned commit.'
}
Invoke-Checked git @('-C', $source, 'diff', '--exit-code', 'HEAD')

$previousCache = $env:ZIG_GLOBAL_CACHE_DIR
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $toolRoot 'zig-cache'
try {
  foreach ($rid in $Runtime) {
    # Zig's bundled Windows runtime avoids a separate Visual C++ redistributable prerequisite.
    $target = switch ($rid) {
      'win-x64' { 'x86_64-windows-gnu' }
      'win-arm64' { 'aarch64-windows-gnu' }
    }
    $destination = Join-Path $root "out/native/$rid"
    Push-Location $source
    try {
      Invoke-Checked $zig @('build', '-Demit-lib-vt', '-Doptimize=ReleaseFast', "-Dtarget=$target", '--prefix', $destination)
    }
    finally { Pop-Location }
    Invoke-Checked $zig @('cc', '-target', $target, '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror',
      '-DBROWS_GHOSTTY_EXPORTS', '-shared', (Join-Path $root 'native/bridge/brows_ghostty.c'),
      '-I', (Join-Path $destination 'include'), (Join-Path $destination 'lib/ghostty-vt.lib'),
      "-Wl,--out-implib,$(Join-Path $destination 'bin/brows-ghostty.lib')",
      '-o', (Join-Path $destination 'bin/brows-ghostty.dll'))
    Invoke-Checked $zig @('cc', '-target', $target, '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror',
      '-shared', (Join-Path $root 'native/bridge/brows_session.c'), '-I', (Join-Path $destination 'include'),
      '-I', (Join-Path $conptyRoot 'inc'),
      (Join-Path $conptyRoot "runtimes/$rid/lib/uap10.0/conpty.lib"),
      (Join-Path $destination 'bin/brows-ghostty.lib'), '-o', (Join-Path $destination 'bin/brows-tty-win32.dll'))
    $architecture = if ($rid -eq 'win-x64') { 'x64' } else { 'arm64' }
    Copy-Item (Join-Path $conptyRoot "runtimes/$rid/native/conpty.dll") (Join-Path $destination 'bin') -Force
    Copy-Item (Join-Path $conptyRoot "build/native/runtimes/$architecture/OpenConsole.exe") `
      (Join-Path $destination 'bin') -Force
    $testDestination = Join-Path $root "out/native-tests/$rid"
    New-Item -ItemType Directory -Path $testDestination -Force | Out-Null
    Invoke-Checked $zig @('cc', '-target', $target, '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror',
      (Join-Path $root 'tests/native/ColorQuery.c'), '-o', (Join-Path $testDestination 'color-query.exe'))
    Invoke-Checked $zig @('cc', '-target', $target, '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror',
      (Join-Path $root 'tests/native/KeyEcho.c'), '-o', (Join-Path $testDestination 'key-echo.exe'))
    Get-FileHash (Join-Path $destination 'bin/*.dll') -Algorithm SHA256
  }
}
finally { $env:ZIG_GLOBAL_CACHE_DIR = $previousCache }
