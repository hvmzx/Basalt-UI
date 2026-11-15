# Basalt UI

A Windows tray app around [basalt](https://github.com/jesdga95/basalt): start and stop the inference
server, edit its config file from a form instead of by hand, and watch what it is doing.

Native WPF on .NET 10 (`net10.0-windows`). The tray icon comes from
`System.Windows.Forms.NotifyIcon`, the charts are drawn by hand, the JSON is `System.Text.Json`, and the
window chrome, the tray menu and the dialog boxes are all drawn by this app. One package is referenced —
`Microsoft.Web.WebView2`, the wrapper for the Chromium runtime that Windows 11 already ships — and it is
there only so basalt's own stats page can open inside the app instead of in a browser tab. The only other
machine requirement is the .NET 10 Desktop Runtime.

```
dotnet build src\BasaltUI\BasaltUI.csproj -c Release
src\BasaltUI\bin\Release\net10.0-windows\BasaltUI.exe
```

For the tray icon, run it installed rather than straight from the build folder:

```
powershell -ExecutionPolicy Bypass -File install.ps1 -Launch
```

That copies the build to `%LocalAppData%\Programs\BasaltUI` and starts it there. Windows 11 refuses
tray-icon registration (`Shell_NotifyIcon` returns access denied, the icon silently never appears)
for executables that live inside a protected/sandboxed development workspace like this project
folder — the same binary anywhere else registers fine. The tray icon itself lands in the corner
overflow; drag it out once if you want it beside the clock.

## What it does

| | |
| --- | --- |
| **Start / Stop / Restart** | Starts `basalt.exe serve --config <file>` in an invisible console and pipes its output into the app's log pane. Stop sends the Ctrl+C that basalt itself listens for, so the engine gets its QUIT, the slots close and the caches flush; a second Ctrl+C part-way through the grace is what basalt calls "force", and only if the process is still there when the grace (60 s) runs out does the app terminate the tree. |
| **Tray** | Icon with a live state dot and a tooltip naming the model and the load. Its right-click menu is the app's own sheet — rounded, dark, the same palette and the same glyphs as the window — not the system strip, and it is rebuilt on every open so each line says what it can do right now: Load, Unload, Open web UI and Open stats JSON are dim while nothing answers, Start is dim while a server is already up. Opening Basalt UI, Settings and Quit are always there. Closing the window minimises to the tray. |
| **Look** | The windows draw their own chrome — there is no system title bar anywhere in the app: the top strip of each window *is* the caption, holding the wordmark, the state pill and the actions, with the app's own minimise, maximise and close at its right edge, while drag, double-click, Snap and resize keep working because the strip is a real `WindowChrome` caption and the corners are rounded by DWM. Below it sits an elevation design: rounded cards on the ground, hairlines only where a line carries meaning, one blue accent spent on the primary action, live data and focus. A dark and a light palette ship; Windows' own app-theme setting picks which one runs. The window geometry is remembered, minimized-to-tray included. |
| **Dashboard** | `/health` + `/metrics` + `/slots` polled together: model, quant, KV, context, expert-cache hit rate, slots and queue, tokens/s and prefill/s sparklines drawn from basalt's own `history` arrays, per-GPU util / power / temperature bars, PCIe traffic, and the last requests with prompt reuse. |
| **RAM and VRAM as Windows counts them** | The memory bars, and every figure in a load preview, are the numbers Task Manager prints, taken from the places Task Manager takes them. RAM: `GlobalMemoryStatusEx` for the physical memory Windows can actually use and what is left of it, plus `GetPhysicallyInstalledSystemMemory` for the installed line under it — so a 64 GB machine says "55 GB in use of 61.7 GB usable (64 GB installed)", the same three figures its memory page leads with, and it says them while basalt is stopped too, because they were never the engine's to report. Card memory: DXGI's `DedicatedVideoMemory` for the card and `\GPU Adapter Memory(luid_…)\Dedicated Usage` — the counter Task Manager's GPU page samples — for what fills it, the card picked out of the counter set by the LUID DXGI gives it. That is why this app says 31.4 GB on a 32 GB RTX 5090 where `nvidia-smi` and basalt's own `/stats` say 31.8 GB: NVML counts the 420 MiB the driver keeps for itself, so the two can never match, and only one of them is the number on the screen next to this window. `dxgi.dll` and `pdh.dll` are reached through vtable slots and plain exports rather than a COM reference, so the app still ships with one package. If either OS half cannot be reached, both halves come from `nvidia-smi` instead and the source is named everywhere it is shown — a used figure from one world is never divided into a total from the other. The unit words are Windows' own (KB, MB, GB stepping by 1024), because two programs agreeing on a GB that quietly means GiB beats one that spells it GiB and looks wrong. |
| **Models** | The `models` entries of the config file as a list: pick one as the default, switch to it (`/load`), load or unload without touching the process. |
| **Nothing answering** | A button that cannot do anything reads disabled, and "anything" means the server replying. With basalt stopped, *Web UI*, *Open /health*, *Load*, *Unload*, *Switch* and *Restart* are all dim — there is nothing to tell — and *Start* is the one live thing in the header. The Models list says which kind of empty it is: "basalt is stopped — start it and the models it would serve show up here" rather than a folder that looks empty. Settings, *Find models online…* and *Check for a newer basalt* stay live, because none of them needs a server. |
| **Console text you can take with you** | The log is a document, not a label strip: drag through any part of a line, or Shift-click to extend, and it copies with Ctrl+C — the timestamp and the coloured body are separately selectable runs, so an error line's time can be lifted without its message. It stays read-only, and the tail still follows new lines until you scroll up, which stops the jump and leaves you holding the place you were reading. *Save…* writes what is currently filtered to a text file. |
| **Settings** | A page inside the main window (the header arrow takes you back to the dashboard), five places over one document: **Basalt** (exe path, config path, api key, extra arguments, grace, poll rate, and the update check + download fields), **Models** (per-entry settings, plus a jump to the Hugging Face browser), **Server**, **Engine defaults**, and a **Raw JSON** tab, editing through the DOM so keys this app has never heard of survive the round trip. Every save writes the JSON to a temp file and moves it over the original, after copying the previous content to `%APPDATA%\BasaltUI\config-backups\basalt_config.yyyyMMdd-HHmmss[-n].json` (40 kept). |
| **A newer basalt** | The GitHub releases API is asked once soon after launch and every six hours the app stays open, github.com first and a public mirror of the same call if it does not answer. When the newest release is ahead of what is installed, a banner says which version, when it was published and how big the Windows zip is, with the release notes a click away. *Update* deals with whatever is holding the files: nothing answering means the zip goes straight in, a server this app started is stopped without a ceremony, and a server answering on the port that this app did not start — the usual case for someone who runs basalt from their own terminal — is named by pid, asked about in the app's own dialog, then sent the same polite Ctrl+C the Stop button uses and adopted, since Ctrl+C has to be addressed to a process the app knows. If something answers and no process behind it can be named from the port table, the app says so and touches nothing. The server is not started again automatically: when the install lands, the window asks whether to start basalt now. `StopAsync` still refuses to terminate a process it did not start, so a stranger's server that ignores two Ctrl+C is left alone and the update stops before replacing anything. Unpacking goes over the install directory (files that are already identical are skipped), and keeps `basalt.exe.previous` beside the exe as the rollback. Running the same release a second time is then a no-op — the exe already there is the one the release carries, so it is left where it is and the rollback copy from the first install is not replaced; a missing companion file is still laid down again. "Not now" hides one release until a newer one appears; a release that is still the newest is announced in the console once, not on every check. The zip is kept in `%APPDATA%\BasaltUI\updates\`, so an install can be retried without downloading again. `basalt.exe` carries no version resource, so the number the engine announced over `/props` is remembered against that exact copy of the file — which is what lets the app say “0.3.3 is out, this machine has 0.3.2” while the server is stopped. |
| **Models from Hugging Face** | *Find models online…* opens a window with two lists: repos on the left, files inside the picked one on the right. The two packs basalt's author publishes — `jesdga/Qwen3.8-Flash-Next-Basalt` and `jesdga/DeepSeek-V4.1-Flash-1017-Basalt` — are pinned at the top of every list marked *official*, whatever the search said and whether or not the network answered, so the starting point is never a search box to fill in. Below them come the repos Hugging Face returns for the query, by downloads, with their tags, likes and "needs an account" where a repo is gated; a token in `HF_TOKEN` or `HUGGING_FACE_HUB_TOKEN` is sent automatically. Opening a repo lists its files with sizes, `.basalt` and `.gguf` first, and says which are already in the models folder. Downloads go to that folder, one job at a time, fetched as many byte ranges at once (8 by default) — each range request gets its own freshly signed CDN address, which is what turns a 60 GB pull from minutes into less than it would take one connection. A pause writes a checkpoint next to the `.part` file, so a half-done download resumes where it was; at the end the file is hashed against the sha256 Hugging Face published for it, and only then moved into place. |
| **How it would load** | Pick a file — one already in the folder, or one still on Hugging Face — and a card says what loading it on *this* machine would look like before anything is committed: the dense and attention weights, the routed experts, the KV cache for the configured context × parallel slots, and the vision projector; how much of the expert pool would sit in VRAM, how much in system RAM (`resident-experts`), and how much would be left to stream from the SSD on demand, against the free VRAM, free RAM and free space on the models drive measured just now, with basalt's own `vram-reserve-mib` and `ram-reserve-mib` held back. For a `.basalt` pack the split is read from the file's own section table — the 64 KiB header names every part and its byte count — so the figures are the model's, not a guess; a plain `.gguf` gets the usual MoE split (≈ 92 % experts) and the card says it is estimating. The verdict is one line: everything in VRAM, "≈X of experts in VRAM, ≈Y in RAM", or "≈Z of experts would stream from the SSD — replies slow down", and it admits where the engine has the last word — the exact tiering is basalt's `[server] auto` decision when it loads, which the console then shows. The KV line knows the engine's two escapes from that arithmetic: with `kv-resident` the resident cells are named and split between VRAM and pinned RAM, and above a 32,768-cell context the cache is elastic — a slot is priced at its first 32,768 cells and borrows the expert tier's VRAM while a long request runs — so the card says which of the two it is pricing rather than quoting one figure the engine will not honour. |
| **Before you press Start** | The file is checked the way `serve/config.py` checks it: a key basalt does not take, a model setting sitting in the `server` block, a server option in a model entry, an alias used twice, `parallel` together with `batch` or `slots`, a `version` older than this basalt, an `api-key` in the file (basalt refuses that one; the app passes `--api-key` instead). The messages quote basalt's own wording. |
| **A server started elsewhere** | If something already answers on the configured port the app offers to watch it (metrics and log, no process control) and will not kill a process it did not start. |
| **Web UI** | The *Web UI* button opens basalt's own stats page inside the app, hosted on WebView2 (the Chromium runtime Windows 11 already ships) with the app's own user-data folder, so the browser never has to come up. A reload button sits in that window's strip. The button is dim while nothing answers on the configured port — with no server there is no page to open, so it does not pretend otherwise; the same goes for *Open /health* on the Model card. |

## Files

```
src\BasaltUI\                     the app
  MainWindow.xaml(.cs)            dashboard: bars, sparklines, model list, log pane — and the settings page over it
  SettingsPage.xaml(.cs)          the form tabs + Raw JSON, hosted inside the main window (back arrow returns)
  ModelsBrowserWindow.xaml(.cs)   Hugging Face search, repo file list, download queue
  Shell\Dialog.xaml(.cs)          the app's own modal question box (update, unsaved changes)
  Shell\Raise.cs                  pulls the browser window a click launched up to the front
  WebUiWindow.xaml(.cs)           the engine's dashboard hosted in-app (WebView2; runtime ships with Windows 11)
  ReleaseNotesPage.xaml(.cs)      a release's changelog, a page over the dashboard like Settings
  Core\ConfigDocument.cs          the config file: DOM reader/writer, backups, basalt's own validation rules
  Core\BasaltProcess.cs           one server process: start, log pipe, Ctrl+C stop, last-resort kill
  Core\BasaltClient.cs            the endpoints, with the api key when one is set
  Core\StatusPoller.cs            one beat: /health, /metrics, /slots, /props, /api/logs
  Core\NativeMethods.cs           console attach + Ctrl+C, the TCP table, the process table
  Core\Net.cs                     the two HttpClient pools: short JSON calls, long streams; proxy from settings
  Core\UpdateChecker.cs           the releases API, the installed version, and which of the two is ahead
  Core\UpdateInstaller.cs         zipped release over the install directory, with a rollback copy
  Core\HuggingFace.cs             search, file listing with sizes and sha256s, the download address
  Core\SegmentedDownload.cs       resumable many-range download, checked against its sha256
  Themes\Dark.xaml                the dark tokens: every colour of the app, one flat file of brushes
  Themes\Light.xaml               the same roles with light values; Windows' app-theme setting picks which of the two runs
  Themes\Theme.xaml               every style and template, written against the tokens by DynamicResource, so one style set serves both themes
  Shell\TrayIcon.cs               the tray icon and its menu
design\settings-catalog.json      the 119 settings this app knows, with type, scope and help text
design\api-reference.md           the endpoints as observed off a live 0.3.2 server
design\ui-review.md               the interface rules this UI was held to, and what each one changed
tools\Probe\                      a console harness that runs the same Core classes (see below)
tools\Shots\                      builds the real windows off-screen and writes their PNGs (see below)
tools\audit_scopes.py             check the catalog's blocks against serve/config.py and the engine binary
tools\audit_catalog.py            check that every catalog key appears somewhere in the installed basalt
tools\selftest.ps1                run the harness against your own install and write a report
```

App-owned state lives in `%APPDATA%\BasaltUI` (`settings.json`, `logs\basalt-ui.log`,
`config-backups\`, `updates\` for release zips the app downloaded); set `BASALTUI_HOME` somewhere else
to keep a second, separate instance. A folder that does not exist and cannot be created used to be fatal
in silence: every save and every log line failed quietly, the app forgot everything, and a dialog pointed
at a log file that had never been written. Now the app tries, in order, `BASALTUI_HOME`,
`%APPDATA%\BasaltUI`, `%LOCALAPPDATA%\BasaltUI`, `C:\ProgramData\BasaltUI`, `%TEMP%\BasaltUI` and a
`BasaltUI` folder beside the exe, says plainly in the log pane and a tray balloon which one it ended up
with and what refused the earlier ones, and keeps running on whatever it found — settings simply do not
survive a restart until one of those places is writable. It also moves `TMP`/`TEMP` into its own folder
when it can, because WPF unpacks its font cache into `%TEMP%\WPF` while painting the first window and an
anti-ransomware rule that refuses that write takes the whole window down. `basalt-probe.exe --paths`
prints the place it settled on, every refusal verbatim, where temp points now, and whether a write there
and in the WPF scratch folder actually succeeds. basalt's own config file is never moved — the app edits
the path you point it at. Model files go wherever Settings → Models says, and nowhere else.

## Checking it against your install

```
powershell -ExecutionPolicy Bypass -File tools\selftest.ps1
```

It prints the report and keeps a copy in a `selftest-*.txt`. It reads your config file, asks the
running server for its status with GET requests only, and starts a **throwaway** server of its own on
port 18085 with `--mock-engine` — with a config file that flag makes basalt skip the model file
entirely, so nothing is loaded and no VRAM is taken — to prove start → `/health` → graceful stop.
The server on your usual port is only ever read, never stopped.

Two things can only be verified that way, on a normal desktop session: the app's view of the process
tree (which processes belong to a server) and the arrival of Ctrl+C in basalt's console. Both depend
on seeing other processes, which a restricted or service session cannot do.

The part of this that is pure arithmetic — reading a releases payload, deciding which version is ahead,
which file of a repo is loadable, how a paused download is laid out on disk — is checked offline:

```
dotnet build tools\Probe\Probe.csproj -c Release
tools\Probe\bin\Release\net10.0-windows\basalt-probe.exe --checks
```

That harness compiles the app's own `Core` files, so it is the same code, not a re-implementation. It
needs no network; the release JSON it parses is written the way GitHub sends it. Everything it asserts
about Hugging Face and GitHub is about addresses and arithmetic — whether those sites answer can only be
seen on a machine that can reach them. Among the offline assertions is one on the settings file itself,
which is worth keeping: a file the app cannot serialise means it quietly remembers nothing, and the only
trace of that is a line in the log.

Those last two sentences do not cover what happens on the wire, so there is a mode that puts the answer on
the other end of a socket — a fake GitHub and a fake Hugging Face, both on 127.0.0.1, started by the
harness itself:

```
tools\Probe\bin\Release\net10.0-windows\basalt-probe.exe --wire
```

It streams a 160 MB model file over four connections and checks the bytes and the sha256 of what arrives,
follows the redirect to the separate CDN address Hugging Face hands out (and shows that the token is
offered to Hugging Face but not handed to the machine the redirect pointed at), cuts a transfer short and
resumes it — the checkpoint, and at a different number of connections than it started with — refuses a
file whose hash is not the one the repo published, and installs a release zip into a folder that only
pretends to be basalt, checking the new bytes, the rollback copy, and that the engine this machine
actually runs was not touched. About 44 assertions, a little under two minutes, nothing leaving the
machine: the answers are the fake's, the code being asked is the app's.

## Seeing it without a desktop

Design changes here are judged on pixels, not on descriptions, so the windows are built, shown and
photographed by a tool:

```
dotnet build tools\Shots\Shots.csproj -c Release
tools\Shots\bin\Release\net10.0-windows\basalt-shots.exe .\shots
```

It merges the app's own theme — `Themes\Dark.xaml` (or `Light.xaml` with `--light`) for the tokens, then
`Themes\Theme.xaml` for the styles, the same two files the app's App.xaml merges and the same choice it
makes from Windows' app-theme setting (the compiled resources when WPF will hand them over, the same
files read from disk when it will not — never a palette copy kept for the tool's comfort), then writes

| | |
| --- | --- |
| `00-cold.png` | the main window on a machine where basalt has never run — every empty state at once |
| `01-main.png` + `-header -buttons -metrics -log` | the dashboard, with crops at 2× so 1 px borders and small text can actually be read |
| `07-machine.png` | the same dashboard with its left column scrolled down to the memory bars — the RAM figure lives under four GPU bars, so short of scrolling there is no picture of it — and the run prints what the two bars say, in words, next to the scroll viewer's extent against its viewport |
| `02-settings.png` | the form |
| `02b-settings-server.png` + `-fields` | the Server tab, over the machine's own config file |
| `02c-settings-models.png` + `-preview` | the Models tab, waited on until the load-preview card has answered |
| `03-models.png`, `04-models-hint.png` + `-search` | the Hugging Face browser, up to a minute after opening: with a network that is the catalogue, without one it is the browser's own offline error — a state worth seeing — and the second has the box emptied so the field's hint is in the shot |
| `05-dialog.png` | the question box, built through its private constructor and shown non-modally for the capture |
| `06-notes.png` | a release's notes as a page over the dashboard, which also proves the header's arrow and title switch and exercises the markdown |

Each window is shown at `Left = Top = -32000` off every monitor, the dispatcher is pumped to idle, the
layout updated, and the tree rasterised straight into a `RenderTargetBitmap` at 1.5× — so this exercises
the same templates, converters and bindings the user's screen does, with nothing drawn for the camera.
Before each image it prints what the pixels say about the theme (`ground`, `lit`, `ink`, `faint`,
`accent` as shares of the surface), which is how a `Foreground` that silently inherited the background
got caught. `maps.txt` lists what each number means. The exit code is the number of images, so a build
that produced four PNGs out of five fails the run.

Two things about that rasterisation are worth knowing before trusting an image:

- `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` is **required**. Through a GPU on a session
  with no real display — a service, a remote session, or a scheduled job — the render target comes back
  holding only the root's own background, every child missing, and the picture looks like a window with
  nothing in it rather than like a failure.
- The process has to reach `%APPDATA%\BasaltUI` or one of its fallbacks; set `BASALTUI_HOME` to a
  writable folder when it cannot, or the log pane in the shot says so instead of showing the dashboard.

Everything in those pictures is seeded, and it is seeded through the front door: one made-up `ServerStatus`
is handed over the same way a poller beat arrives (a reflection call on the private `OnStatus`, and that is
the only reflection in the tool apart from emptying the search box), so the model list, the request table
and the warnings are rebuilt by the same code a real beat rebuilds them. That is deliberate — a machine
whose server has served exactly three requests does not show what a long session looks like, and a
sparkline that photographs flat tells a reviewer nothing. It also means the images are the same on any
machine: this one's server is a 145 GB model, and the previous run of this tool drew whatever that had
been doing.

Nothing here starts, stops or restarts anything, and nothing here reaches the desktop: showing a window
starts the app's own start-up, which takes the single-instance lock, so the tool runs `ShotsApp`, the
`App` with that start-up overridden. Without it, a capture run beside a running copy of Basalt UI raises
the copy's window onto the screen, shuts the tool down from the inside — every image after the first fails
with "The Application object is being shut down" — and leaves a second tray icon for the length of the run.

## When basalt changes its mind

Release 0.3.3 changed two things this app cares about:

- `--no-answer-room` is gone, and a config file saying `"answer-room": false` now stops the start; the
  setting (engine block or a model entry) takes a number or `auto`. If the installed engine is 0.3.3 or
  newer and the file still says `false`, the dashboard says so before the next start instead of letting
  the engine refuse. The form still offers the switch on/off, which is legal either way; a number is a
  Raw JSON edit.
- `/props` answers with a `basalt` object carrying the version and a `capabilities` list, which is where
  the update check reads its number from when a server is running.

## After a basalt upgrade

The catalog is a copy of what this basalt accepts, so re-audit it when the engine updates:

```
python tools\audit_scopes.py        # which block each key may live in
python tools\audit_catalog.py       # does every key still exist in the install
```

Both derive their expectations from `serve/config.py` and from the flag names scraped out of
`bin\basalt.exe`, and both must end with zero findings. `design\settings-catalog.json` carries a
`notes` list recording where the docs and the binary disagreed (0.3.2: `--diag-tier-frozen` is in the
docs and not in the binary, so it is not in the catalog — writing it would stop the start).
