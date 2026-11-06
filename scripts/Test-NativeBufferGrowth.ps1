$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$versions = Get-Content (Join-Path $root 'native/versions.json') -Raw | ConvertFrom-Json
$zig = Join-Path $root ".tools/zig-x86_64-windows-$($versions.zigVersion)/zig.exe"
$nativeRoot = Join-Path $root 'out/native/win-x64'
$nativeInclude = Join-Path $nativeRoot 'include'
$nativeLibrary = Join-Path $nativeRoot 'lib/ghostty-vt.lib'
$nativeBin = Join-Path $nativeRoot 'bin'
$ghosttyRuntime = Join-Path $nativeBin 'ghostty-vt.dll'
$testRoot = Join-Path $root 'out/native-tests/win-x64'
$testExecutable = Join-Path $testRoot 'buffer-growth.exe'

if (!(Test-Path $zig)) { throw 'Zig is missing. Run scripts/Build-Native.ps1 first.' }
if (!(Test-Path $nativeInclude) -or !(Test-Path $nativeLibrary) -or !(Test-Path $ghosttyRuntime)) {
    throw 'The x64 Ghostty native build is missing. Run scripts/Build-Native.ps1 -Runtime win-x64 first.'
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

$previousCache = $env:ZIG_GLOBAL_CACHE_DIR
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $root '.tools/zig-cache'
try {
    $arguments = @(
        'cc', '-target', 'x86_64-windows-gnu', '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror',
        '-DBROWS_GHOSTTY_EXPORTS', (Join-Path $root 'tests/native/BufferGrowth.c'),
        '-I', $nativeInclude, $nativeLibrary, '-o', $testExecutable
    )
    & $zig @arguments
    if ($LASTEXITCODE -ne 0) { throw "BufferGrowth.c compilation failed with exit code $LASTEXITCODE." }

    $previousPath = $env:PATH
    try {
        $env:PATH = "$nativeBin;$previousPath"
        & $testExecutable
        if ($LASTEXITCODE -ne 0) { throw "BufferGrowth.exe failed with exit code $LASTEXITCODE." }
    }
    finally { $env:PATH = $previousPath }
}
finally { $env:ZIG_GLOBAL_CACHE_DIR = $previousCache }
