# vatSys Window Manager

A vatSys plugin that saves your window layout for a position and puts it back: which windows are
open, where they are, how the strip windows are set up, and what each radar display is showing.
A layout can load by itself when you start vatSys or change position.

## What a layout holds

| Item | Saved and restored |
|------|--------------------|
| Main window | Place, size, maximised or not. Display centre, range, visible maps, Dynamic Infill, Terrain Heights, ATC list and scale marker positions, the stored view (Store/Restore keys). |
| Strip windows | Strip mode (State or Beacon) and sort mode. Which of the five state windows are showing. Every Beacon, ADEP and ADES window, in order, whether showing or hidden. Where the Other window sits. |
| ATIS windows | Which ATIS windows are open, by callsign. Missing ones are opened, extra ones closed. |
| Arrival lists | Which airports have a list open. Works with vatSys's own list and with OzServer's. |
| Second ASD and Ground windows | How many, each one's position, centre, range, maps, and the same display details as the main window. |
| VSCS | Place, size and state. |
| Other vatSys windows | System Messages, ATC Messages, Strip Setup, Sectors, Info, MET, Restricted Areas: open or closed, and where. Current Messages and Errors: where (vatSys shows and hides those itself). |
| Other plugins' windows | Open or closed, and where. See below. |
| Every window | Minimised, maximised or normal; hidden or showing; stacking order. |

Not part of a layout: private message windows, flight plan windows and dialogs, text notes drawn on
the display, and anything inside another plugin's window (for example which aerodrome OzStrips is
showing).

## Other plugins' windows

A layout remembers where another plugin's window was and whether it was open. Only that plugin can
open its own window, so Window Manager reopens it by clicking the plugin's own menu item for you.

It works out which menu item opens which window in two ways:

- It watches. When you click a plugin's menu item and a window belonging to that same plugin
  appears, it remembers the pair. This is kept between sessions in `openers.json`.
- For a window it has never seen opened, it will use a menu item only if exactly one item belongs
  to the same plugin and has text that names the window (for example an item called OzStrips for a
  window titled "OzStrips :: YBCG"). It does not guess beyond that.

If it cannot tell, the load finishes and the Errors window says which window was left closed. Open
that window from its menu once, and it will be reopened with the layout from then on.

A plugin window that is open but not in the layout being loaded is left alone unless all of these
are true: it has a close button, Window Manager knows how to reopen it, and either it has seen that
window being opened or the window is saved in one of your layouts. A plugin's pop-up notice or
prompt is never closed.

## Installation

1. Download the latest release from <https://github.com/RealLeviticus/vatSysWindowManager/releases>.
2. Right-click the ZIP, choose Properties, and tick Unblock if it is there. Unzip it.
3. With vatSys closed, copy `vatSysWindowManager.dll` into
   `C:\Program Files (x86)\vatSys\bin\Plugins\WindowManager\`.
4. Start vatSys. The plugin is listed under Info > About.

Install it in one place only. vatSys loads every DLL under its Plugins folders, including the
profile's `Plugins` folder; a second copy is detected and left idle, with a message in the Errors
window.

## Use

Everything is under **Windows > Window Layouts**.

- **Save current layout** asks for a name and saves what is on screen for the position you are on.
- **Layouts** lists the layouts saved for the current position. Each one has:
  - **Load**: apply it now.
  - **Auto load for this position**: load it when vatSys starts on this position or you change to it.
  - **Restore sectors (only when not connected)**: see below.
  - **Override with current layout**: replace it with what is on screen now.
  - **Delete**.
- **Open layouts folder** opens the folder holding the layouts and the log.

A layout only appears, and only loads, on the position it was saved for.

When a layout loads, windows it does not include are closed and windows it includes are opened and
placed. Changing to a position with no auto-load layout changes nothing: vatSys's own defaults for
that position apply.

### Sectors

A layout records the sectors you held when it was saved, but does not apply them unless you turn on
**Restore sectors** for that layout. Even then they are applied only while you are not connected
to the network, and never from a layout saved with no sectors. vatSys drops the aircraft you are
tracking in any sector that is removed, so this is never done as a side effect while you are
controlling.

Layouts saved by version 1.1.x always re-applied sectors. After updating, that is off for every
layout until you turn it on.

## Files

Layouts are in `Documents\vatSys Window Manager\`:

- `POSITION__Name.layout.json`: one per layout.
- `autoload.json`: which layout loads by itself for each position.
- `openers.json`: the plugin menu items learned for other plugins' windows.
- `WindowManager.log`: what the plugin did and anything it could not do.

Layouts from version 1.1.x load as they are. A layout is rewritten in the current format the next
time you override it.

## If something is not restored

After a load, anything that could not be restored is listed once in vatSys's Errors window and
written to `WindowManager.log`. Common cases:

- A map, position or sector saved in the layout no longer exists in the current dataset.
- A window was saved on a monitor that is no longer connected. It is moved onto a connected one.
- Another plugin's window could not be reopened (see above).
- A vatSys update changed something the plugin depends on. The log names what is missing at
  start-up, and only the affected feature is turned off.

## Building

Requires the .NET SDK and an installed vatSys (the build references `vatSys.exe` and the libraries
beside it; nothing of vatSys's is copied).

```powershell
dotnet build vatSysWindowManager.csproj -c Release
```

The DLL is written to `bin\Release\`. The build never writes into a vatSys Plugins folder. For a
vatSys installed somewhere else, add `-p:VatSysPath="D:\vatSys"`.

### Tests

```powershell
.\tests\Run.ps1
```

This builds to a temporary folder and runs the tests. They cover the layout file format, upgrading
version 1 layouts, storage, and screen geometry, and they load the installed `vatSys.exe` to check
that every internal member the plugin looks up is present. Add `-Windows` to also open a few small
windows on the desktop and check they are put back correctly, and
`-Layouts "$env:USERPROFILE\Documents\vatSys Window Manager"` to load your own layouts as a check.

### Testing a build in a copy of vatSys

The plugin folder under Program Files needs administrator rights, and a build under test should
not touch real layouts. Two environment variables, read when vatSys starts, make it possible to
try a build in a copy of the vatSys folder instead:

- `VATSYS_WINDOWMANAGER_LAYOUTS`: use this folder for layouts, the log and `openers.json` instead
  of `Documents\vatSys Window Manager`.
- `VATSYS_WINDOWMANAGER_DEBUG`: write step-by-step detail to the log, and accept commands from a
  file. With it set, the plugin runs each line of `command.txt` in the layouts folder, then
  deletes the file. Without it, none of this is active.

| Command | Does |
|---------|------|
| `save <name>` / `load <name>` | Save or load a layout for the current position, with no dialogs. |
| `position <name>` | Change position through the same vatSys method the Position menu uses. |
| `dump` | Log every window the plugin would save, with its place, state and display view. |
| `menu` | Build the Layouts menu and log its items. |
| `plugins` | Log other plugins' menu items and windows, and the menu item found for each window. |
| `open asd` / `open ground` / `open atis <callsign>` / `open arrival <airport>` / `open single <name>` | Open a window. |
| `window <title>\|minimize` (or `maximize`, `normal`, `close`, `front`, `move x y w h`) | Act on a window by part of its title. |
| `strips mode\|sort\|beacon\|show\|hide\|showbeacon ...` | Change the strip setup. |
| `view <title>~<lat> <lon> <range>~<maps>~<x> <y>` | Set a display's centre, range, maps and ATC list position. |
| `userclick <Category> > <text>` | Click another plugin's menu item as the controller would. |

Start the copy with a normal PATH. A shell that has Git's `mingw64\bin` on its PATH makes
OzStrips load the wrong `libSkiaSharp.dll` and vatSys exits when OzStrips is opened.

## How it works, and what it depends on

vatSys documents only a small plugin interface. Most of what this plugin does goes through vatSys
members that are not public, found by reflection. They are all in `src\Vatsys.cs` and
`src\Vatsys.Asd.cs`, looked up once at start-up, and listed with the vatSys behaviours the plugin
relies on in [docs/vatsys-internals.md](docs/vatsys-internals.md). Recheck that list after a vatSys
update.

| File | Purpose |
|------|---------|
| `src\WindowManagerPlugin.cs` | Menu, start-up, loading on position change. |
| `src\Capture.cs` | Builds a layout from what is on screen. |
| `src\Restore.cs` | Applies a layout. |
| `src\Placement.cs` | Window rectangle, state, visibility and stacking order. |
| `src\Vatsys.cs`, `src\Vatsys.Asd.cs` | Every non-public vatSys member the plugin uses. |
| `src\PluginWindows.cs` | Other plugins' windows and the menu items that open them. |
| `src\LayoutModel.cs`, `src\LayoutUpgrade.cs`, `src\LayoutStore.cs` | File format, version 1 upgrade, storage. |
| `src\Ui.cs`, `src\Log.cs`, `src\Native.cs` | UI-thread dispatch, logging, Win32 calls. |
| `src\DebugCommands.cs` | The command file used for testing. Off unless `VATSYS_WINDOWMANAGER_DEBUG` is set. |

## Warranty

Use at your own risk. The plugin is provided without warranty.
