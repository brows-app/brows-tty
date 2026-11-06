# Brows.TTY

A terminal engine and WPF control powered by Ghostty, separated by dependency:

| Package | Contents | Targets | Depends on |
|---|---|---|---|
| `Brows.TTY` | Ghostty engine, input encoding, frame snapshots and native core | `net462`, `net48`, `net8.0`, `net10.0` | No UI or transport package |
| `Brows.TTY.Win32` | ConPTY sessions, process lifecycle and Windows key translation | `net462`, `net48`, `net8.0-windows`, `net10.0-windows` | `Brows.TTY` |
| `Brows.TTY.Windows` | WPF control, rendering, input and dispatcher lifecycle | `net462`, `net48`, `net8.0-windows`, `net10.0-windows` | `Brows.TTY.Win32` |

Reference **Brows.TTY.Windows** to use the WPF control below. Its namespace and
XAML mapping are `Brows.Windows.Controls.Terminal` and
`http://schemas.brows.app/winfx/2026/xaml/presentation`.
Applications using the former combined package must update their package
reference and rebuild; the WPF types now live in `Brows.TTY.Windows.dll`.

The core's .NET 10 assembly has no Windows framework or WPF dependency. Its native
bridge separates platform synchronization/clock primitives from process transport
and has a POSIX implementation. Native resolution uses the current OS and process
architecture. Linux hosting requires building and supplying `libbrows-ghostty.so`
and its Ghostty dependency; Linux binaries and a Unix PTY transport are not included
in these packages. Linux runtime execution is not validated. The .NET Framework
loader retains its Windows implementation behind `NETFRAMEWORK`.

Engine and transport implementation types remain internal, shared with the WPF
assembly through friend declarations. This split introduces no new public engine
API. Native **Windows x64 and ARM64** binaries are bundled.

## Usage

### Add the control

```xml
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:tty="http://schemas.brows.app/winfx/2026/xaml/presentation">
  <tty:Terminal FontFamily="Consolas" FontSize="16"
                CommandLine="powershell.exe -NoLogo"
                WorkingDirectory="C:\" />
</Window>
```

The default command is `%ComSpec%` (normally `cmd.exe`). The process starts when
the control loads. Removing it from the visual tree stops the session
asynchronously. To control startup yourself, set `AutoStart="False"` and call
`await terminal.StartAsync()`. Call `await terminal.StopAsync()` before shutting
down a hosting window when you need to await complete resource cleanup.

Use Windows 10 version 1809 or later for ConPTY. ARM64 requires an ARM64-capable
Windows/.NET runtime; an emulated x64 process loads the x64 native libraries.
32-bit processes are not supported. Use a monospaced font for terminal layout.

### Keyboard shortcuts

Focus the terminal to type commands. Brows.TTY handles the shortcuts below;
other keys are encoded by Ghostty using the application's active terminal modes.

| Shortcut | Action |
|---|---|
| Enter or Ctrl+C | Copy selected text and clear the selection. Without a selection, send the key to the process. |
| Ctrl+Shift+C or Ctrl+Insert | Copy selected text and clear the selection. |
| Ctrl+V, Ctrl+Shift+V, or Shift+Insert | Paste clipboard text using the terminal's paste mode. |
| Ctrl+Shift+Up | Scroll history up by the Windows wheel row count. |
| Ctrl+Shift+Down | Scroll history down by the Windows wheel row count. |
| Ctrl+Shift+PageUp | Scroll up by one viewport. |
| Ctrl+Shift+PageDown | Scroll down by one viewport. |
| Ctrl+Shift+Home | Jump to the earliest history. |
| Ctrl+Shift+End | Jump to the latest output. |
| Ctrl+=, Ctrl+Shift+=, or Ctrl+numpad + | Increase the font size by one. |
| Ctrl+- or Ctrl+numpad - | Decrease the font size by one, down to a zoom minimum of one. |
| Ctrl+0 or Ctrl+numpad 0 | Restore the configured FontSize. |
| Alt+F4 | Close the host window. |
| Alt+Space | Open the host window's system menu. |

Ctrl+Shift+Up/Down scrolls by one viewport when Windows is configured for page
scrolling. Font reset uses the latest externally configured FontSize, and zooming
preserves its WPF binding. Alt+F4 and Alt+Space remain available to the host window.

Each locally handled shortcut has a WPF dependency property that defaults to
`true`. Set one to `false` to send that key combination through terminal input
instead of performing its local action. Changes apply to subsequent key presses;
text and release events from an already handled press stay local.

| Shortcut | Property |
|---|---|
| Enter | `PressEnterToCopy` |
| Ctrl+C | `PressControlPlusCToCopy` |
| Ctrl+Shift+C | `PressControlPlusShiftPlusCToCopy` |
| Ctrl+Insert | `PressControlPlusInsertToCopy` |
| Ctrl+V | `PressControlPlusVToPaste` |
| Ctrl+Shift+V | `PressControlPlusShiftPlusVToPaste` |
| Shift+Insert | `PressShiftPlusInsertToPaste` |
| Ctrl+Shift+Up | `PressControlPlusShiftPlusUpToScroll` |
| Ctrl+Shift+Down | `PressControlPlusShiftPlusDownToScroll` |
| Ctrl+Shift+PageUp | `PressControlPlusShiftPlusPageUpToScrollOnePage` |
| Ctrl+Shift+PageDown | `PressControlPlusShiftPlusPageDownToScrollOnePage` |
| Ctrl+Shift+Home | `PressControlPlusShiftPlusHomeToScrollToTop` |
| Ctrl+Shift+End | `PressControlPlusShiftPlusEndToScrollToBottom` |
| Ctrl+= (OemPlus) | `PressControlPlusEqualsToIncreaseFontSize` |
| Ctrl+Shift+= (OemPlus) | `PressControlPlusShiftPlusEqualsToIncreaseFontSize` |
| Ctrl+Numpad + (Add) | `PressControlPlusAddToIncreaseFontSize` |
| Ctrl+- (OemMinus) | `PressControlPlusMinusToDecreaseFontSize` |
| Ctrl+Numpad - (Subtract) | `PressControlPlusSubtractToDecreaseFontSize` |
| Ctrl+0 (D0) | `PressControlPlus0ToResetFontSize` |
| Ctrl+Numpad 0 | `PressControlPlusNumPad0ToResetFontSize` |

For example, this returns Ctrl+C and Ctrl+Shift+PageUp to the terminal:

```xml
<tty:Terminal PressControlPlusCToCopy="False"
              PressControlPlusShiftPlusPageUpToScrollOnePage="False" />
```

### Mouse interaction

- Drag to select text. Hold Shift while dragging when an application captures
  the mouse to select text locally.
- Right-click selected text to copy it and clear the selection.
- The mouse wheel scrolls terminal history or is forwarded to a mouse-aware
  application. Hold Shift to scroll history locally.

### Control API

- `Paste(string)` sends text using Ghostty's bracketed-paste handling. It does
  not append Enter. `CopySelection()` copies and clears the selection;
  `PasteClipboard()` pastes clipboard text.
- `Title`, `IsRunning`, and `LastError` are read-only dependency properties.
  `ProcessExited` reports the root process exit code after output is drained;
  `Failed` reports automatic-operation/input failures. Explicit async lifecycle
  calls propagate their exceptions to the caller.
- Command and working-directory changes apply to the next start. Call StopAsync
  then StartAsync to restart. All control members belong to its WPF dispatcher.
- Applications can change text/cell colors and the terminal's default foreground,
  background, and palette through terminal escape sequences. Default-color
  queries and resets are supported; changes repaint existing text and empty cells.

## Native design and performance

This repository implements its own C bridge and C# P/Invoke declarations. There
are no external .NET Ghostty bindings. The pinned upstream revision and compiler
checksums are recorded in [native/versions.json](native/versions.json).

ConPTY uses Microsoft's redistributable console host from the
[Windows Terminal project](https://github.com/microsoft/terminal), bundled as
`conpty.dll` and `OpenConsole.exe` for each architecture. This allows foreground
and background queries to reach Ghostty even on Windows versions whose inbox
ConPTY consumes those queries. Applications such as Copilot can then detect
terminal colors and apply their own background theme. Windows Terminal itself
does not need to be installed.

The Win32 native bridge owns ConPTY, separate blocking reader/writer threads, a bounded
input queue, Ghostty parsing and state, and terminal input encoding. Shell output
feeds Ghostty directly without a managed callback or dispatcher hop. Native code
resolves colors and graphemes and exposes dirty rows in batches. Synchronization
protects frame snapshots from concurrent terminal writes.

WPF retains frozen row drawings, groups backgrounds and ASCII glyph runs, and
rebuilds only changed rows at up to approximately 60 frames per second. Complex
graphemes use WPF text shaping and font fallback. There is no UI element per cell.
Ghostty handles alternate screens, scrollback, resize reflow, cursor state, and
synchronized output with a bounded render hold. ConPTY sessions keep historical
rows in scrollback when growing the window so their active screen and cursor
coordinates stay aligned with ConPTY.

The .NET 10 build uses `Span<byte>` and stack allocation for small UTF-8 input,
`ArrayPool<byte>` for larger input, and `GC.AllocateUninitializedArray<T>` for
blittable snapshot arrays that are completely overwritten before use. Rented
input buffers are cleared before return. These APIs are guarded by
`NET5_0_OR_GREATER`; .NET Framework uses pinned byte arrays and regular array
allocation. `System.Threading.Lock` is guarded by `NET9_0_OR_GREATER`, with an
`object` monitor fallback. Both builds use the same `DllImport` ABI and native
engine. Snapshot arrays remain owned by each frame so later native writes cannot
change data being rendered by WPF.

This is a WPF renderer for **libghostty-vt**, not a host for Ghostty's desktop
window/GPU renderer. Inline image protocols, clickable hyperlink UI, and terminal
screen-reader automation are not currently implemented. Host clipboard access
is through the control's explicit copy/paste actions, not OSC clipboard commands.

## Build and validate

Prerequisites: Windows, Git, and .NET SDK 10.0.402 (see `global.json`).
The native script downloads checksum-verified
Zig 0.17.0 into `.tools/` and fetches the pinned Ghostty source into
`native/ghostty/`. It also downloads the checksum-verified
`Microsoft.Windows.Console.ConPTY` runtime pinned in `native/versions.json`.
Both architectures use Zig's bundled Windows GNU toolchain;
Visual C++ build tools and redistributables are not required. SIMD remains enabled.

```powershell
./scripts/Build-Native.ps1
dotnet build brows-tty.slnx -c Release
dotnet test brows-tty.slnx -c Release --no-build --logger trx --results-directory out/test-results
dotnet pack brows-tty.slnx -c Release --no-build
./scripts/Verify-Package.ps1
dotnet run --project samples/Brows.TTY.Sample -c Release -f net10.0-windows
```

Generated outputs are under `out/`. All three source libraries are packable;
samples and tests are not. Packing fails if required native binaries are missing.
The core package owns Ghostty and `brows-ghostty.dll`; the Win32 package owns
`brows-tty-win32.dll`, `conpty.dll`, and `OpenConsole.exe`. Their `buildTransitive`
rules copy their own assets for AnyCPU and .NET Framework consumers. The WPF
package obtains native files through its dependencies.

NUnit integration tests load the real native library, run real ConPTY shells,
round-trip color queries through a native console test program,
exercise WPF input/lifecycle in an invisible HWND, and render terminal output to
a PNG. `tests.runsettings` uses process isolation because WPF owns process-wide
rendering state. Cross-compiling ARM64 is separate from running on an ARM64 host;
local execution evidence is recorded in [_agents/Progress.md](_agents/Progress.md).
The package check uses a fresh isolated NuGet cache, verifies the dependency
direction and native asset ownership, and runs six consumers: core, UI-free Win32
transport, and WPF, each on both frameworks.

The sample offers Command Prompt and Windows PowerShell restart buttons, process
status, and a scrollable sidebar for its 20 local shortcuts. Each restart button
stops the current process, selects its shell command, starts it, and returns
focus to the terminal. Checked shortcut options enable local handling; unchecked
options send the chord through terminal input. Changes apply to subsequent key
presses, so refocus the terminal after changing a checkbox. A press already
handled locally remains local through its text and release events. Very narrow
sample windows clip the terminal at its minimum layout width; expanding the
window reveals it again without reflowing the shell to a single column.
Source namespaces start at `Brows`; the control is `Brows.Windows.Controls.Terminal`.

## GitHub workflow

[workflow.yml](.github/workflows/workflow.yml) validates pushes to `dev` and
published GitHub releases on a Windows x64 runner. It installs the SDK from
`global.json`, builds native x64 and ARM64 assets, builds all managed targets,
runs both test targets sequentially, and packs and verifies all three packages
and six consuming applications. Test reports and packages are uploaded as
workflow artifacts. ARM64 binaries are cross-compiled, not runtime-tested.

Published releases also push the verified packages to nuget.org. The release
name supplies the package version, for example `1.2.3` or `1.2.3-beta.1`;
development builds use `VersionPrefix` and `VersionSuffix` from
`Directory.Build.props`. The same version is used for restore, build, pack,
package dependency versions, and consumer verification.

Publishing uses `NuGet/login@v1` with `id-token: write`. Set the repository secret
`NUGET_USER` to the nuget.org username and create a
[trusted publishing policy](https://www.nuget.org/account/trustedpublishing)
for this repository, using the exact workflow filename `workflow.yml` and no
environment. The policy's account or organization must own all three packages.
Before the first publish, run the build, pack, and package verification commands
above locally. Authentication and publication must also be verified on GitHub.

## License

MIT. Ghostty, Microsoft ConPTY, and their dependencies retain their own licenses;
notices are included in
[native/THIRD-PARTY-NOTICES.txt](native/THIRD-PARTY-NOTICES.txt) and in the package.
