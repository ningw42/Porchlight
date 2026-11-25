# Porchlight for Windows

Porchlight is a one-pane **viewer** for programs already running on this machine or a remote machine configured in HerdR. It reads the machine and terminal lists through one existing local gateway core, without attaching. **Attach** opens the selected program in a native WinUI 3 terminal; **Detach** or closing the window releases the viewer, not the program. There is no terminal creation, shell, second PTY, or core management in the app.

## Build and launch

Use an interactive Windows x64 desktop, PowerShell 7, .NET **10 x64 SDK**, and Visual Studio or Build Tools with the **MSVC x64 desktop tools**, Windows SDK **10.0.26100.0** (including C++/WinRT headers) and the Visual C++ UWP desktop runtime SDK references. Runtime: x64 .NET 10 and **Windows App Runtime 1.8 x64**. The app is unpackaged and framework-dependent. NuGet access is needed on the first restore.

From the repository root:

```powershell
dotnet build Porchlight.slnx -c Debug -p:Platform=x64
dotnet test tests/Porchlight.Core.Tests/Porchlight.Core.Tests.csproj
$app = (Resolve-Path '.\src\Porchlight.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Porchlight.exe').Path
& $app
```

Launch **the EXE in its build directory** with its DLLs, manifest and resources beside it. The project restores `XamlToolkit.WinUI.Terminal` **0.1.0-alpha** and builds `Porchlight.NativeFeatures.dll` from `src/Porchlight.App/Native/NativeFeatures.cpp` with x64 MSVC. No prebuilt host helper or app registration is required. The terminal package includes `OpenConsole.exe`; Porchlight does not launch it.

## Connect to a real program

1. Use an **existing** core owned by your desktop user. Startup resolves one endpoint and connects for **metadata only**. Check the pipe path, core name, stable core ID and running version displayed above the picker. If disconnected, correct the path and choose **Connect**. Nothing starts if the pipe is missing.
2. Choose **Machine**, then **Live terminal**. The first dropdown includes the local core and its configured machines, even those with no live programs. Open it again to refresh connection states. The second shows only live terminals for that machine, by full address (`tN` locally, `machine/tN` remotely) and optional name. Changing machines clears the terminal choice. Verify **Selected** before pressing **Attach**. Only then can the program's grid and focus change. There is no default attach, even when there is only one program. Use a program you intend to interact with.
3. Focus the pane to type. Resize the window to change the grid. Ctrl+C copies a selection; without a selection it goes to the terminal. Ctrl+V / Ctrl+Shift+V pastes through the native control. VT-requested clipboard writes are disabled. The app does not log terminal output, input, or clipboard data.
4. Close the window. Reopen the EXE, check the **same core ID**, select the same address and attach again. The core repaints the current screen with up to 2,000 requested history lines, then streams live VT. It is not a transcript. **Detach** also releases the pipe; use **Connect** for a fresh list without closing the window.

**Endpoint selection** (first applicable source wins):

- Explicit `--pipe=…`, or the exact path entered in the disconnected window.
- Non-empty `HC_SOCKET`.
- Top-level TOML `socket` in `<XDG_CONFIG_HOME>\herdr-core\config.toml` (otherwise `%APPDATA%\herdr-core\config.toml`). Unreadable/malformed configuration is not a reason to try the default pipe.
- `\\.\pipe\herdr-core-<user>-<session>.sock`: `USERNAME` with only ASCII letters/digits, trimmed `HC_SESSION` or `default`, as in HerdR 0.1.22 on Windows. No namespace scan and no retry on another core. `HC_TERMINAL_SOCKET` identifies a *hosting* terminal's core; it does not override a different effective `HC_SOCKET`, config or session.

For example, **inside a HerdR terminal**, to deliberately choose the core hosting it (without choosing or attaching to the current terminal):

```powershell
if (!$env:HC_TERMINAL_SOCKET) { throw 'Supply the exact local core pipe instead.' }
& $app "--pipe=$env:HC_TERMINAL_SOCKET"
```

To open the picker on a configured remote without selecting a terminal, add `--machine=yuki` (replace `yuki` with its HerdR alias). Alternatively `--select=yuki/t6` preselects that exact address; `--select=t6` is local. Neither option attaches. No choice is persisted. The app opens one existing, same-user, local `\\.\pipe\…` directly, not `hc connect` (which can autostart). No `hc.exe` is required by the app. If negotiation fails, check the actual running core and selected pipe; on Windows `hc server status --json` is a non-starting check of the CLI's effective endpoint, not necessarily an explicitly selected GUI pipe. The app never installs, replaces or upgrades the core.

**How remote works:** The gateway's `machine.list` supplies machine aliases, stable core IDs and connection states; the event subscription supplies mirrored live terminals. Only `connected` machines are attachable. Porchlight pins the route to the selected core ID and requires the gateway's attach reply to match the full displayed address. It uses the existing HerdR relay, not a separate SSH connection, and does not load keys, prompt for passwords, log in, add machines or start a remote core. Configure or repair the route in HerdR itself (`hc machine ls` to inspect), then reopen the Machine dropdown. If `machine.list` isn't advertised, the viewer remains local-only. A remote line can drop while the gateway is still connected: follow the relay notice in the pane; input during that gap can be lost. Detach/reconnect for a fresh stream if necessary.

**Use your Windows Terminal font** without editing its profile:

```powershell
$wt = Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json'
& $app "--wt-font=$wt"
```

This reads `profiles.defaults.font` (the **shared** defaults, not per-profile overrides) from the specified JSONC file at launch. The fallback face string is passed through verbatim, including quotes and order. Size, weight, cell dimensions, glyph flags, OpenType features and variable axes are passed to the native renderer. The imported feature map **replaces** Porchlight's defaults: for example `{"ss02":1}` sets only `ss02`, not `ss01` or `calt`. Explicit `--face`, `--ss01` and `--calt` switches can override the import. For Preview, point `--wt-font` at its own `settings.json`. Fonts must be installed locally; no profile or font file is modified.

**Use a named color scheme** from the same Windows Terminal settings, alongside its font:

```powershell
& $app "--wt-font=$wt" '--wt-scheme=Catppuccin Mocha'
```

The named entry in `schemes` supplies all 16 ANSI colors plus foreground, background, cursor and selection. The name must match and all 20 colors must be explicit `#RRGGBB` values; a missing or invalid scheme is an error, not a fallback. With `--wt-scheme` alone the app reads the packaged Windows Terminal **stable** settings file and keeps Porchlight's font defaults. The import affects the terminal pane, not Windows Terminal or the program; truecolor output still uses the RGB values requested by the program.

**Without an import**, font options are still per launch. Defaults: `Cascadia Code, KaiTi, SimHei, Segoe UI Emoji, Symbols Nerd Font Mono`; `ss01=0`, `calt=1`. Put installed families in your preferred order. The native `IMap<String, Single>` includes both tags even when zero. Example:

```powershell
& $app '--face=Cascadia Code, SimHei, KaiTi, Segoe UI Emoji, Symbols Nerd Font Mono' --ss01=1 --calt=0
```

## Smoke test on an existing core

The unit/transport tests above use private random named pipes and a fake peer. They exercise endpoint precedence, cancellation, negotiation, split frames/events/UTF-8, stale and missed snapshots, ordered input and resize, both capability gates, credit **after** render, bad frames/identity, exit vs disconnect and close during attach or without an acknowledgment.

For the **real GUI** test, also install Python 3 x64 (`python.exe` on PATH). Build Debug first. From a HerdR terminal on the target interactive desktop:

```powershell
if (!$env:HC_TERMINAL_SOCKET) { throw 'Pass the exact local Windows core pipe to -Pipe.' }
pwsh -NoProfile -File .\tests\smoke\RealAttach.ps1 -Pipe $env:HC_TERMINAL_SOCKET
```

Alternatively pass a literal full local pipe to `-Pipe`. The test never accepts an existing terminal ID. It opens a non-starting pipe, lists **metadata**, and creates one uniquely named and labelled `pl-smoke-*` Python program. Leave the two successive Porchlight windows alone while it runs. UI Automation verifies the core and selected address/name before invoking Attach; the test reads **only its own** program's native pane. Keyboard messages and resize are addressed to the newly launched window, not sent globally. It checks a one-time alternate-screen banner, key acceptance, actual program grid, survival after close, same PID and state after reopen, then a user-key-triggered exit. It verifies ownership again before cleanup on the **original pipe**. It cannot reconnect elsewhere to clean up. If interrupted, the fixture self-expires after three minutes; verify the printed unique name, label and core before removing a leftover finished test record.

## Boundaries and limitations

- `src/Porchlight.Core/` owns the generation-1 hello, framed event snapshot/changes and one optional interactive attach. One reader handles repaint and live output with a per-attach UTF-8 decoder. A bounded FIFO serializes writes, including resize, focus, credits and detach. Credits count original bytes accepted by the decoder/renderer, not characters or GPU presentation. An empty detach acknowledgment, transport loss and explicit process exit are different outcomes. Reconnect negotiates from scratch; no stale channel, decoder or queued input is reused.
- **Prefer one active viewer.** The alpha's `WriteInput` has no origin bit. Only writes nested on this connection's synchronous output stack are candidates for `AUTOMATIC_INPUT`, and only if **both** hello and attach advertise it. All other writes use DATA. No byte-pattern guessing or suppressed replies. Deferred focus/reply writes and reentrancy can misattribute activity and change grid ownership with multiple viewers. This is not a guarantee of multi-viewer arbitration.
- Local same-user flat named pipe to the gateway; local terminals and directly configured HerdR machine aliases only. No arbitrary remote endpoints, multi-hop selection, background observer panes, client-side automatic retry, saved session, tabs, installer or settings page. Human IME/AltGr/Narrator, multiple-DPI displays, clean-machine deployment and ARM64 are not qualified. Known alpha build warnings: `MSB3842` native SDK compatibility and `PRI263` missing default strings on a fresh PRI build.
- `src/Porchlight.App/` hosts the published native control. Settings, key bindings and manifest are adapted from the [tagged MIT sample](https://github.com/lgztx96/XamlToolkit.WinUI.Terminal/tree/0.1.0-alpha/TerminalSample.DotNet); [notice](src/Porchlight.App/Upstream-LICENSE.txt).
