# UI review — the rules this pass was held to

The app is WPF, so the design guidance was taken as **rules to judge the windows by**, not as an API to
call. Two Microsoft repos were read; only one of them contains any interface design.

| source | what it is | what came from it |
| --- | --- | --- |
| [microsoft/winappCli](https://github.com/microsoft/winappCli) · `plugins/winui/agent-plugin/skills/winui-design/` (`SKILL.md`, `references/layout-review.md`, `references/theme-accessibility.md`, `references/brushes-and-icons.md`) | the `winui-design` skill shipped with the Windows App CLI. MIT. | the app-silhouette table, the control-choice map, the state rubric, the theming and accessibility rules below |
| [microsoft/win-dev-skills](https://github.com/microsoft/win-dev-skills) | the Microsoft agent-skills catalogue. As of today it publishes **one** plugin, `windbg`, i.e. kernel and user-mode debugging skills. No UI skill. | nothing — read and found not to apply |

Quoted lines are the skill's own wording. Everything marked **here** is what this app does with it,
including two places where it deliberately departs from the text, because the skill is written for WinUI 3
and says in its first paragraph that it does not cover WPF.

## 1. Which app this is

The skill sorts apps into silhouettes before any control is chosen. A tray companion that reports a
running server and takes a handful of commands is its *developer tool / dashboard*: a status region, cards
of measures, a detail column, one place for raw output.

**Here** — the main window is exactly that: header with the live state, a left column of cards
(Throughput, GPU) over the console, a right column of cards (Model, Models). No tab strip pretending to be
navigation.

## 2. Controls: reach for the boring one

The skill's control map prefers the standard, well-known control and treats a custom one as a cost.

**Here** — everything on screen is a stock WPF control plus four drawn pieces that have no stock
equivalent: `Sparkline`, `Bar`, the tray glyph, and the state Pill. `Dark.xaml` restyles stock controls via
setters and triggers rather than wrapping them in new behaviour. A `NavigationView`, a `ContentDialog` or a
WebView2 would each have meant a NuGet package and a runtime the user's machine may not have, and the
app's own rule is zero packages; so the equivalents are a header, a `MessageBox`-free inline confirm and
*Web UI* opening the engine's own page in the browser.

## 3. States are part of the page

> “Loading — progress text with context, not a bare spinner. Empty — what happened plus a call to action,
> not just *no items*. Error — cause plus retry or repair, never colour alone. … If the state isn't in the
> view model, the page isn't done.”

This is the rule the last pass was built to satisfy, and each one is now visible in a screenshot:

| state | where | what the shot shows |
| --- | --- | --- |
| empty, with cause + next action | `00-cold.png` | `no gpu reported`, `nothing loaded — Load below, or double-click a model in the list`, `no models in that folder yet — Find models online… to fetch one`, `no engine output yet — the console fills up as soon as a server starts talking`, `no samples yet` inside every sparkline |
| loading, with context | search: `asking Hugging Face…`; repo: `reading the file list…`; download: file, byte counts, speed and a cancel button in the transfer card |
| error, with cause + retry | `The SSL connection could not be established, see inner exception. — this looks like a network or proxy problem; Settings → Basalt has a field for a proxy and one for a closer Hugging Face mirror — press Search to try again` |
| a thing that is not an error but is not normal | the banner: a server this app did not start, named by pid, with `Watch it here` / `Ignore` |

Colour is never the only signal: the log rows say `error:`, the Pill says `ready` / `busy` / `stopped`, the
fields say what they are.

## 4. Labels, hints, icons

> “Put a visible label next to the field; do not make the placeholder carry the meaning.” — “Give an
> icon-only button a ToolTip and a name.”

**Here** — the Hugging Face search box has a hint *and* a standing instruction line
(`04-models-hint.png` proves the hint appears only while the box is empty, `IsHitTestVisible=False` so it
never eats a click, and `AutomationProperties.Name` for a screen reader). Every header button is glyph
**plus** the word — `Settings`, `Web UI`, `Restart`, `Stop`, `Start` — with a ToolTip that says what the
glyph means in a sentence. Glyphs come from `Segoe Fluent Icons` with `Segoe MDL2 Assets` as the fallback,
because the font is not guaranteed to be there.

## 5. Colour and type

> “Do not use literal color values for semantic colors.” — “Don't author a full ControlTemplate for a
> standard control just to tweak chrome.”

**here, first rule:** every brush at a use site is a name with a job — `Window`, `Surface`, `SurfaceHi`,
`ControlBg`, `ControlHover`, `Field`, `Line`, `LineHi`, `TextPrimary`, `TextSecondary`, `TextFaint`,
`Accent`, `AccentInk`, `Slate`, `Warn`, `Bad`, `Good` — plus the state tints the Pill needs (`ReadyBg`,
`BusyBg`, `WaitBg`, `StopBg`) and the destructive pair (`DangerBg`, `DangerLine`, `DangerText`). No hex
left in a window's XAML.

**here, second rule: a deliberate departure.** WPF's default Aero2 templates hard-code near-white
gradients and light fills that brush setters cannot move, and the app needed a dark surface. So the
templates are hand-written in `Themes\Dark.xaml`, and with them the responsibility for the parts the
framework would otherwise have given free: keyboard focus is visible (`FocusRing`, not `Opacity`), the
CheckBox draws an actual tick, the TabItem shows a 2 px accent underline, the ScrollBar's thumb has a hit
target a mouse can find, and every state comes in a pair (hover, disabled) because nothing does it
automatically. Text contrast is over 7:1 for primary on surface and 4.6:1 for secondary.

Type: `FontText` for UI, `FontDisplay` for the wordmark, `FontMono` (Cascadia Mono → Consolas) for
anything that is a path, a number, JSON or log output — set on the control, not at the window, so a path
that leaves the console still reads as a path.

## 6. Layout

> “Auto rows so content is never clipped. Don't fix a window's size in pixels. One visible primary button.”

**Here** — both columns of the dashboard are in `ScrollViewers` (the right one once hid the *Address* card
below the fold; a screenshot caught it: a ScrollViewer's clipped viewport looks exactly like a bug and
often is). The console's log rows are a `Grid` of `Auto` + `*`, never a horizontal `StackPanel`: a
`StackPanel` measures its children at infinite width, so a long line would not wrap — the fix is visible in
`01-main-log.png`, where a three-line engine error aligns under its own timestamp. The Hugging Face
browser's three panes are star columns with `MinWidth`s, so it survives being dragged narrow instead of
refusing to shrink, and it has no hard-coded size, so DPI and a small screen are the OS's problem, not the
app's. Each window has exactly one accent-filled button — *Start*, *Save config*, *Search*, *Download the
model files here* — and the destructive *Stop* carries its own tint, so it cannot be taken for the primary.

## 7. Checking the design

Rules you cannot look at are opinions. `tools\Shots` builds the real windows off-screen at 144 dpi and
writes their PNGs, plus a printout of what the pixels say about the palette; see the *Seeing it without a
desktop* section of the README, including the one thing that makes or breaks such a tool
(`RenderMode.SoftwareOnly`).

The last pass found, in the images and nowhere else: a `Foreground` that resolved to the background
colour, a card that no scroll could reach, `True`/`False` and a bare `W` leaking into labels from
`StringFormat`, a mono font silently never applied because the formatter ignored the typeface argument,
log lines that would not wrap, a Pill whose local style had quietly overridden its own state colour — and
`â€œ` instead of `“` in a whole window's text, a UTF-8 file decoded as Windows-1252 by something and
re-saved, which no compile and no test would ever mention.
