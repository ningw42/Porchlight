# Porchlight for Windows

Porchlight is a one-pane **viewer** for programs already running on this machine or a remote machine configured in HerdR. It reads the machine and terminal lists through one existing local gateway core and opens the selected program in a native WinUI 3 terminal. Select another tab to switch viewers on the same gateway connection; neither switching nor closing the **window** stops a program. Closing a **tab** is different: its × button ends that terminal on its owning core. There is no terminal creation, shell, second PTY, or core management in the app.

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

1. Use an **existing** core owned by your desktop user. On startup, Porchlight opens the first live tab in the first space with live terminals on the selected machine (Local by default), in sidebar order. It waits briefly for that machine’s layout; if it’s unavailable it uses the terminal labels. If nothing is live, nothing is attached or created. The sidebar shows the running version and stable core ID. If the connection fails, the sidebar refresh button retries the same configured gateway. To change endpoints, relaunch with `--pipe=…`. There is no pipe editor or manual disconnect action. Nothing starts if the pipe is missing.
2. Under **Local** or a remote machine’s alias, select a **space by name**. Machines start expanded; collapsing one or refreshing the directory does not change the selection. The title bar has a sidebar toggle and one tab per live terminal in the selected space. The tabs sit above the workspace, not the open sidebar. Drag the empty title bar above the sidebar or after the tabs to move the window. Tab headers follow Herdr’s naming rules; the tooltip includes the full address (`tN` locally, `machine/tN` remotely) so identically named terminals are distinguishable. Switching spaces attaches to the tab you last used in the destination space, or to its first live tab if you haven’t visited it or that terminal is gone. Choosing a saved space that has no **live terminal** releases the viewer without attaching. You can select a different tab at any time. The choice is remembered per machine and space in this window only. Switching tabs or spaces releases the previous viewer first; refreshing the catalog does not attach to a different terminal.
3. When a terminal opens at startup or after you switch spaces or tabs, you can type without clicking the pane first. The workspace stays blank when no terminal is open; Porchlight reports errors and program exits in the sidebar. Resize the window to change the grid. Ctrl+C copies a selection; without a selection it goes to the terminal. Ctrl+V / Ctrl+Shift+V pastes through the native control. VT-requested clipboard writes are disabled. The app does not log terminal output, input, or clipboard data.
4. To **end a terminal**, click the × on its tab. The selected tab shows the button; hover over an unselected tab to close it without switching viewers. The button is available when the gateway advertises `terminal.kill`. Porchlight waits for the core’s termination event rather than hiding the tab locally; it does not erase the finished record.
5. To **leave programs running**, close the window instead. To verify the core before reattaching, reopen the EXE with `--browse`, check the **same core ID**, and select the original terminal tab. The core repaints the current screen with up to 2,000 requested history lines, then streams live VT. It is not a transcript. The metadata connection stays open between terminal choices and after a program exits.

**Endpoint selection** (first applicable source wins):

- Explicit `--pipe=…` at launch.
- Non-empty `HC_SOCKET`.
- Top-level TOML `socket` in `<XDG_CONFIG_HOME>\herdr-core\config.toml` (otherwise `%APPDATA%\herdr-core\config.toml`). Unreadable/malformed configuration is not a reason to try the default pipe.
- `\\.\pipe\herdr-core-<user>-<session>.sock`: `USERNAME` with only ASCII letters/digits, trimmed `HC_SESSION` or `default`, as in HerdR 0.1.22 on Windows. No namespace scan and no retry on another core. `HC_TERMINAL_SOCKET` identifies a *hosting* terminal's core; it does not override a different effective `HC_SOCKET`, config or session.

For example, **inside a HerdR terminal**, to deliberately choose the core hosting it (without choosing or attaching to the current terminal):

```powershell
if (!$env:HC_TERMINAL_SOCKET) { throw 'Supply the exact local core pipe instead.' }
& $app "--pipe=$env:HC_TERMINAL_SOCKET" --browse
```

Use `--machine=yuki` to start on that configured remote instead of Local. To open an exact terminal, use `--select=yuki/t6` (or `--select=t6` locally); if it is missing, Porchlight does not attach a substitute. Add `--browse` for metadata-only startup: it shows the initial space but waits for you to choose a tab or switch spaces. No selection is written to disk. The app opens one existing, same-user, local `\\.\pipe\…` directly, not `hc connect` (which can autostart). No `hc.exe` is required by the app. If negotiation fails, check the actual running core and selected pipe; on Windows `hc server status --json` is a non-starting check of the CLI's effective endpoint, not necessarily an explicitly selected GUI pipe. The app never installs, replaces or upgrades the core.

**How remote works:** The gateway's `machine.list` supplies machine aliases, stable core IDs and connection states; the event subscription supplies mirrored live terminals. Only `connected` machines are attachable. Porchlight pins the route to the selected core ID and requires the gateway's attach reply to match the full displayed address. It uses the existing HerdR relay, not a separate SSH connection, and does not load keys, prompt for passwords, log in, add machines or start a remote core. Configure or repair the route in HerdR itself (`hc machine ls` to inspect), then choose **Refresh machines** in the sidebar. If `machine.list` isn't advertised, the viewer remains local-only. A remote line can drop while the gateway is still connected; input during that gap can be lost. Select another terminal or space, then choose the original terminal for a fresh stream if necessary.

**Where tab names come from:** A terminal in an unnamed Herdr tab follows the foreground program: for example, `pwsh` → **Git Changes** when you enter `lazygit` → `pwsh` when you leave. A root agent report takes precedence over the program’s command line; a useful live title can replace an agent’s program name. Herdr’s **Rename tab** saves `spaces[].tabs[].name` in its layout. Porchlight honors that name, including Herdr’s exception for agent tabs named exactly after their program. **Rename pane** sets a separate pane header, not the tab caption. Porchlight has one tab per terminal: split siblings share their stored layout tab name, but each follows its own program when unnamed. Without a saved placement, a core launch name (`hc run --name`) is used if present. Porchlight loads Herdr’s optional personal `programs.json` when it connects; reconnect or relaunch after editing it. None of this requires attaching, reading the screen, or writing a layout. See [Herdr’s tab naming rules](https://github.com/herdrdev/herdr-app/blob/v0.1.18/defaults/mods/tabs/main.js#L14-L28).

**Where spaces come from:** Porchlight reads each connected machine’s [`herdr-app` layout document](https://github.com/herdrdev/herdr-app/blob/v0.1.18/src/mirror.rs) with `doc.get` when supported. A terminal pane’s placement takes precedence over its `herdr.space` label; that label is a machine-local space **ID**, not its name. An explicit space name wins. For an unnamed space, Porchlight follows [HerdR App’s naming rule](https://github.com/herdrdev/herdr-app/blob/v0.1.18/src/model.rs#L268-L280): use the first terminal pane in tab/tree order, prefer its checkout root to its current folder, and fall back to the space’s saved folder. The first pane’s observed `foreground.cwd` takes precedence over its launch `cwd`. For a checkout lookup, Porchlight asks the existing gateway to run only `git -C <cwd> rev-parse --show-toplevel --abbrev-ref HEAD` as separate arguments on the terminal’s own machine. This starts a short-lived Git process, not a shell or a terminal. If Git or `machine.exec` is unavailable, the lookup fails, or the path is a UNC/device path, the reported folder is used instead. The [display formatter](https://github.com/herdrdev/herdr-app/blob/v0.1.18/src/model.rs#L635-L648) shows the last folder component, or `~` for the viewer’s home or an empty path. Two spaces with the same label remain distinct by ID. If no document is available, a labelled terminal is grouped under its space ID. The sidebar follows the machine’s saved `spaces[]` order, including rearrangements [HerdR writes back](https://github.com/herdrdev/herdr-app/blob/v0.1.18/defaults/mods/spaces/main.js#L313-L350). Names do not control position. Label-only spaces with no saved position follow in ID order, and **Unassigned** comes last. Porchlight never writes a layout or changes labels.

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

## Sidebar smoke test

After building Debug, run `pwsh -NoProfile -File .\tests\smoke\MachineSpaces.ps1 -Exe $app` on the interactive desktop (PowerShell 7.4+; `$app` is defined above). It launches a separate viewer against a private fake gateway, exercises the sidebar and space-scoped terminal tabs, switches between synthetic terminal streams on the same pipe, and closes a remote tab without affecting its local twin or the window. Add `-CheckFocus` and leave the test window in the foreground to also verify keyboard focus. It never opens a real core or attaches to a user program. Pass `-Startup Select` or `-Startup Browse` to exercise exact-target or metadata-only startup instead of the default.

To compare space-switch responsiveness between builds on the same desktop, add `-MeasureSwitchTabs 12`. This alternates between two synthetic 12-terminal spaces and reports the warm median from selection until UI Automation sees a laid-out destination header. Each space switch also requests its terminal; the header timing is not a measurement of attach or repaint latency.

## Smoke test on an existing core

The unit/transport tests above use private random named pipes and a fake peer. They exercise endpoint precedence, cancellation, negotiation, split frames/events/UTF-8, stale and missed snapshots, ordered input and resize, both capability gates, credit **after** render, bad frames/identity, exit vs disconnect and close during attach or without an acknowledgment.

For the **real GUI** test, also install Python 3 x64 (`python.exe` on PATH). Build Debug first. From a HerdR terminal on the target interactive desktop:

```powershell
if (!$env:HC_TERMINAL_SOCKET) { throw 'Pass the exact local Windows core pipe to -Pipe.' }
pwsh -NoProfile -File .\tests\smoke\RealAttach.ps1 -Pipe $env:HC_TERMINAL_SOCKET
```

Alternatively pass a literal full local pipe to `-Pipe`. The test never accepts an existing terminal ID. It opens a non-starting pipe, lists **metadata**, and creates one uniquely named and labelled `pl-smoke-*` Python program. Leave the two successive Porchlight windows alone while it runs. UI Automation verifies the core, fixture ownership, and exact address/name before selecting the terminal tab; the test reads **only its own** program's native pane. Keyboard messages and resize are addressed to the newly launched window, not sent globally. It checks a one-time alternate-screen banner, key acceptance, actual program grid, survival after close, same PID and state after reopen, then a user-key-triggered exit. It verifies ownership again before cleanup on the **original pipe**. It cannot reconnect elsewhere to clean up. If interrupted, the fixture self-expires after three minutes; verify the printed unique name, label and core before removing a leftover finished test record.

## Boundaries and limitations

- `src/Porchlight.Core/` owns the generation-1 hello, framed event snapshot/changes and at most one active interactive attachment. A switch closes the old attachment channel, not the gateway socket. One reader handles repaint and live output with a per-attach UTF-8 decoder. A bounded FIFO serializes writes, including resize, focus, credits and detach. Credits count original bytes accepted by the decoder/renderer, not characters or GPU presentation. An empty detach acknowledgment, transport loss and explicit process exit are different outcomes. Reconnect negotiates from scratch; no stale channel, decoder or queued input is reused.
- **Prefer one active viewer.** The alpha's `WriteInput` has no origin bit. Only writes nested on this connection's synchronous output stack are candidates for `AUTOMATIC_INPUT`, and only if **both** hello and attach advertise it. All other writes use DATA. No byte-pattern guessing or suppressed replies. Deferred focus/reply writes and reentrancy can misattribute activity and change grid ownership with multiple viewers. This is not a guarantee of multi-viewer arbitration.
- Local same-user flat named pipe to the gateway; local terminals and directly configured HerdR machine aliases only. No arbitrary remote endpoints, multi-hop selection, background observer panes, client-side automatic retry, saved session, installer or settings page. Human IME/AltGr/Narrator, multiple-DPI displays, clean-machine deployment and ARM64 are not qualified. Known alpha build warnings: `MSB3842` native SDK compatibility and `PRI263` missing default strings on a fresh PRI build.
- `src/Porchlight.Core/ProgramNames.json` contains the names and aliases from [Herdr’s default program table](https://github.com/herdrdev/herdr-app/blob/v0.1.18/defaults/programs.json), with icon, color and launch data omitted ([Apache-2.0 license](src/Porchlight.Core/Herdr-LICENSE.txt)).
- `src/Porchlight.App/` hosts the published native control. Settings, key bindings and manifest are adapted from the [tagged MIT sample](https://github.com/lgztx96/XamlToolkit.WinUI.Terminal/tree/0.1.0-alpha/TerminalSample.DotNet); [notice](src/Porchlight.App/Upstream-LICENSE.txt).
