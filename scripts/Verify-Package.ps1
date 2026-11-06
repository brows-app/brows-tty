param([string] $Version = '0.1.0-alpha')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$packageFolder = Join-Path $root 'out/package/release'
foreach ($packageId in @('Brows.TTY', 'Brows.TTY.Win32', 'Brows.TTY.Windows')) {
  $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $packageFolder "$packageId.$Version.nupkg"))
  try {
    $packageFrameworks = if ($packageId -eq 'Brows.TTY') { @('net462', 'net48', 'net8.0', 'net10.0') }
    else { @('net462', 'net48', 'net8.0-windows7.0', 'net10.0-windows7.0') }
    $required = @($packageFrameworks | ForEach-Object { "lib/$_/$packageId.dll" }) +
      @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
    $libraries = switch ($packageId) {
      'Brows.TTY' { @('ghostty-vt.dll', 'brows-ghostty.dll') }
      'Brows.TTY.Win32' { @('brows-tty-win32.dll', 'conpty.dll', 'OpenConsole.exe') }
      'Brows.TTY.Windows' { @() }
    }
    foreach ($name in $required) {
      if (!$archive.GetEntry($name)) { throw "$packageId is missing $name" }
    }
    $nativeEntries = @($archive.Entries | Where-Object FullName -like 'runtimes/*')
    if ($nativeEntries.Count -ne $libraries.Count * 2) { throw "Unexpected native assets in $packageId" }
    foreach ($rid in @('win-x64', 'win-arm64')) {
      foreach ($library in $libraries) {
        $name = "runtimes/$rid/native/$library"
        $entry = $archive.GetEntry($name)
        if (!$entry) { throw "$packageId is missing $name" }
        $stream = $entry.Open()
        $memory = [IO.MemoryStream]::new()
        try {
          $stream.CopyTo($memory)
          $bytes = $memory.ToArray()
          $offset = [BitConverter]::ToInt32($bytes, 0x3c)
          $expected = if ($rid -eq 'win-x64') { 0x8664 } else { 0xaa64 }
          if ([BitConverter]::ToUInt16($bytes, $offset + 4) -ne $expected) { throw "Wrong machine in $name" }
        }
        finally { $stream.Dispose(); $memory.Dispose() }
      }
    }
    $nuspec = $archive.GetEntry("$packageId.nuspec").Open()
    $reader = [IO.StreamReader]::new($nuspec)
    try { [xml] $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $dependencies = @($metadata.SelectNodes('//*[local-name()="dependency"]') | ForEach-Object { $_.id } |
      Sort-Object -Unique)
    $expectedDependency = switch ($packageId) {
      'Brows.TTY' { '' }
      'Brows.TTY.Win32' { 'Brows.TTY' }
      'Brows.TTY.Windows' { 'Brows.TTY.Win32' }
    }
    if (($dependencies -join ',') -ne $expectedDependency) { throw "Wrong dependency direction in $packageId" }
  }
  finally { $archive.Dispose() }
}

# A fresh cache prevents reusing an older locally packed package with the same version.
$consumer = Join-Path $root ('out/consumer/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $consumer -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'PackageConsumer/*') $consumer
$project = Join-Path $consumer 'PackageConsumer.csproj'
$configPath = Join-Path $consumer 'NuGet.Config'
$config = [xml] '<configuration><packageSources><clear/><add key="local" value=""/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="local"><package pattern="Brows.TTY*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>'
$localSource = $config.SelectSingleNode('/configuration/packageSources/add[@key="local"]')
if ($null -eq $localSource) { throw 'The local package source is missing from NuGet.Config.' }
$localSource.SetAttribute('value', [string] $packageFolder)
$config.Save($configPath)
& dotnet restore $project --packages (Join-Path $consumer 'packages') --configfile $configPath `
  "-p:TerminalPackageVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Package consumer restore failed.' }
& dotnet build $project -c Release --no-restore "-p:TerminalPackageVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Package consumer build failed.' }
foreach ($framework in @('net462', 'net48', 'net8.0-windows', 'net10.0-windows')) {
  if ($framework -like 'net4*') {
    & (Join-Path $consumer "bin/Release/$framework/PackageConsumer.exe")
  }
  else {
    & dotnet (Join-Path $consumer "bin/Release/$framework/PackageConsumer.dll")
  }
  if ($LASTEXITCODE -ne 0) { throw "Package consumer failed on $framework." }
}
# Consume each lower layer without the WPF package or a WindowsDesktop framework reference.
foreach ($packageId in @('Brows.TTY', 'Brows.TTY.Win32')) {
  $lower = Join-Path $consumer $packageId
  New-Item -ItemType Directory -Path $lower -Force | Out-Null
  Copy-Item (Join-Path $PSScriptRoot 'CorePackageConsumer/*') $lower
  $lowerProject = Join-Path $lower 'CorePackageConsumer.csproj'
  $frameworks = if ($packageId -eq 'Brows.TTY') { 'net462;net48;net8.0;net10.0' } else { 'net462;net48;net8.0-windows;net10.0-windows' }
  & dotnet restore $lowerProject --packages (Join-Path $consumer 'packages') --configfile $configPath `
    "-p:TerminalPackageVersion=$Version" "-p:TerminalPackageId=$packageId"
  if ($LASTEXITCODE -ne 0) { throw 'Lower-layer consumer restore failed.' }
  & dotnet build $lowerProject -c Release --no-restore "-p:TerminalPackageVersion=$Version" `
    "-p:TerminalPackageId=$packageId"
  if ($LASTEXITCODE -ne 0) { throw 'Lower-layer consumer build failed.' }
  $arguments = if ($packageId -eq 'Brows.TTY.Win32') { @('transport') } else { @() }
  foreach ($framework in $frameworks.Split(';')) {
    if ($framework -like 'net4*') {
      & (Join-Path $lower "bin/Release/$framework/CorePackageConsumer.exe") @arguments
    }
    else {
      & dotnet (Join-Path $lower "bin/Release/$framework/CorePackageConsumer.dll") @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "Lower-layer consumer failed on $framework." }
  }
}
Write-Output 'All three packages, native architectures, dependency directions, and all consumers passed.'
