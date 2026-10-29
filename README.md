# DoomGuy

A Doom-style desktop companion. It watches Claude Code sessions and anything else that reports to it, keeps
reminders, and when minimized floats the original Doom status bar on the desktop, where the marine's face
is the health bar.

Every graphic, font and sound is read at runtime from your own Doom IWAD (found automatically in the usual
Steam and GOG folders, e.g. `Ultimate Doom\base\DOOM.WAD`, or picked on first run). This repository contains
no Doom data at all; you need a legitimate copy of The Ultimate Doom or Doom II.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
- `DOOM.WAD` or `DOOM2.WAD` from your own copy of the game

## Building and running

```
dotnet run
```

For a standalone exe that doesn't need .NET installed:

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o App
App\DoomGuy.exe
```

- `--mini` starts minimized as the status bar.
- Settings and state live in `%LOCALAPPDATA%\DoomGuy` (`settings.json`, `state.json`). Set `DOOMGUY_DATA` to use
  another folder, e.g. to run a test copy beside your own (give it another `Port` in its `settings.json`).
- Only one copy runs per data folder; starting it again brings the window up.

## The screen

- `_` or Esc minimizes to the status bar, `X` asks the quit question. Tab switches tabs (with Doom's screen melt).
- The status bar / head stays on top of every window: it re-asserts its place whenever another window comes
  to the front (and every second), and comes back on screen if a monitor change strands it. HIDE IN FULLSCREEN
  (off by default) only hides it for a borderless fullscreen game or video on the same monitor.
- **TASKS**: Claude sessions and reported jobs. Click a finished one to mark it seen; `X` dismisses.
- **REMIND**: type `stand up in 30m`, `call mum at 18:00`, `water every 1h`, `rent tomorrow 9am`, then Enter.
- **LOG**: everything that happened. **SETUP**: sound, volume, god mode (silent), bar/face, sizes,
  hide-in-fullscreen, start with Windows, Claude hooks.

Face-only mode is the quiet one: no HUD text at all. When a task finishes (a Claude task or a job reporting
done/failed) or anything sends a `/notify`, the marine announces it in a speech bubble beside his head. It
stays until you click it; if more are queued the bubble says how many, and each click brings the next. With
nothing to announce, hovering the head shows a short summary in the same bubble. The bubble opens
towards the middle of the screen, points at the head, and its text is 3/4 of FACE SIZE. Headlines wrap over
two lines and messages over four instead of being cut off; a strip in the announcement's colour (gold done,
red failed, yellow warning, blue info) marks what kind it is. It follows HD MODE: smooth like the main
window with it on, crisp 8-bit pixels with it off.

Minimized: drag to move, click to mark everything seen (and reopen a due reminder), double-click to open,
hover for a summary. Full bar or head only is SETUP -> STATUS BAR.

## What the status bar means

| Slot | Meaning |
|---|---|
| AMMO | tasks working right now |
| HEALTH / face | 100% when nothing needs you. Each session waiting on a permission costs 25, each due reminder 20, each unread failure 10. At 0 he's dead. |
| ARMS 2-7 | the first six tasks: yellow = working, blinking = needs you or finished and unread |
| ARMOR | progress of the most recently updated job |
| Keys | blue = finished task unread, yellow = reminder due, red = a session needs you |
| BULL / SHEL / RCKT / CELL | working/tasks, done/started today, due/reminders, errors/messages today |

**HD mode** (SETUP -> HD MODE, on by default) draws the window with the modern renderer (UI/Renderers.cs): a 1280x800 canvas with dark gunmetal panels, cut corners, red accents and glows, and all of Doom's own art (status bar, digits, keys, sprites, skull, thermometer, title, font) upscaled with xBRZ. Layout is still in 320x200 units and the melt still runs. HD MODE off = the original 8-bit screen. The bar's AMMO/HEALTH/ARMOR words are replaced by what each slot tracks (LABEL in the HUD tab overrides). Face: at startup the 42 face frames are upscaled 6x with xBRZ
(the pixel-art scaler GZDoom uses), so outlines and shading are smooth but it is still the same drawing.
When the face changes, in-between frames are generated: motion is estimated on the original pixels and
both frames are warped towards each other (3-5 frames at 35 fps), so glances, grimaces and blood ease in
instead of cutting. Head turns are a rotation no warp can match, so the head swings and the two frames
hand over in the middle frame only. Face-only mode is the head alone on the desktop at its own FACE SIZE
(6x = every HD pixel). `DoomGuy.exe dump-faces <folder>` writes contact sheets of all of this.

The face does what it does in the game: looks around, glances aside when something goes wrong, says ouch on a
big hit, grins when a task completes (with the gold pickup flash), grits his teeth when tools are firing fast,
gets the golden eyes in god mode.

## HUD: what the numbers follow

HEALTH (and the face), AMMO and ARMOR can each follow any source, picked in the **HUD** tab with `< >`.
Bindings live in **profiles** (HOME, WORK, ADD for more; click a profile's name to rename it). Switch with
`< >`, `DoomGuy.exe profile WORK` or `/profile?name=WORK`.

| Source | Value |
|---|---|
| NEEDS YOU | 100% until something needs you (the original health) |
| WORKING TASKS / ACTIVE CHATS / DONE TODAY | counts |
| PLAN LEFT (5H) / (7D) | Claude plan limit left; needs the Claude status line (turn on in the HUD tab, Pro/Max only) |
| CONTEXT LEFT | context left in the latest Claude chat (exact from the status line, else estimated from the transcript; WINDOW K) |
| TOKENS TODAY / TOKEN BUDGET | from Claude's transcripts: output tokens today (k), or % of a daily budget left (BUDGET M) |
| JOB PROGRESS | latest `/progress` job |
| UGS BADGE | newest result of a badge (e.g. Cook) from the UGS metadata server or Horde's `/ugs`: green 100, yellow 50, red 15. SERVER, PROJECT (stream path as UGS knows it), BADGE, optional TOKEN (bearer). Polls `{SERVER}/api/build?project=..&lastbuildid=0` every 60 s. |
| WEB URL | a number or green/yellow/red from a URL; FIELD picks a JSON value (a.b.0.c) |
| COMMAND | a PowerShell command; its last output line is the value |
| PUSHED | whatever was last sent to `/gauge?name=NAME&value=..` or `DoomGuy.exe gauge NAME VALUE` |

**INVERT** (on the NOW line under the slots) makes a slot count down instead. A percent becomes 100 minus it;
a count becomes a percent that is 100% at zero and 0% at **EMPTY AT** (5 unless set). So with HEALTH on WORKING
TASKS, inverted, he's at full health with nothing running and dead with five jobs on the go.

When health drops (a badge goes red, a limit runs low), the face takes the hit like in the game: red flash,
wince, more blood. The Claude status line (optional) also shows `DoomGuy 85% | 5h 23% used | 7d 61% used`.

## Calling it

Local HTTP on `http://127.0.0.1:6666` (loopback only). JSON, form bodies and query strings all work; GET works too.

```
curl "http://127.0.0.1:6666/notify?message=Build+finished&level=success"      # level: info|success|warn|error, sound=NAME|none
curl -d "stand up in 30m" http://127.0.0.1:6666/remind                            # or text= + in=30m | at=17:30 | every=1h
curl "http://127.0.0.1:6666/progress?id=bake&title=Light+bake&percent=40"       # state=running|done|failed|remove
curl http://127.0.0.1:6666/state
curl http://127.0.0.1:6666/perf                                                   # memory, caches, frame cost since the last /perf
```

Or the exe as a command: `DoomGuy.exe notify "text" --level success`, `DoomGuy.exe remind "stand up in 30m"`,
`DoomGuy.exe progress bake 40 "Light bake"`, `DoomGuy.exe progress bake done`.

## Claude Code

SETUP -> CLAUDE HOOKS (or `DoomGuy.exe install-hooks`) adds async curl hooks to `~/.claude/settings.json`
(backup next to it as `settings.json.doomguy-backup`) for SessionStart, UserPromptSubmit, Pre/PostToolUse,
PostToolUseFailure, PermissionRequest, Notification, Stop, StopFailure and SessionEnd. They're async, so they
never slow Claude down, and they fail silently when DoomGuy isn't running. `uninstall-hooks` removes only these.

## Credits and licence

- Upscaling uses a C# port of [xBRZ](https://sourceforge.net/projects/xbrz/) by Zenju (GPL-3.0).
- Status bar layout follows the original Doom source (`st_stuff.c`), released by id Software under the GPL.

DoomGuy is released under the GNU General Public License v3.0; see [LICENSE](LICENSE).

DOOM is a trademark of id Software LLC, a ZeniMax Media company. This is an unofficial fan project, not
affiliated with or endorsed by id Software, ZeniMax or Bethesda, and it ships none of their game data.
