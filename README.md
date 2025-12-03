# vatSys Window Manager Plugin

A plugin for vatSys that saves and restores window layouts, ASD (display) state, and common window-specific settings. Useful for quickly restoring complex desktop arrangements for different positions (sectors/roles) or for automatically applying preferred layouts when switching positions.

## Features
- Save the current vatSys window layout (window positions, size, show state).
- Restore saved layouts for the current position.
- Auto-load a chosen layout for a given position.
- Save and restore ASD state (center coordinates and range (range not working yet) ).
- Restore window-specific metadata where available: chat recipients, ATIS callsigns, sequence/arrival windows, strip windows (beacon/type/state), checked map layers, ASD display position/type and range.
- Manage plugin-created windows (they will be closed before a restore).
- Manage Z-order for non-main windows to keep tool windows on top.
- UI integration via a `Window Layouts` menu.

## Installation
- Download the plugin from the releases section: https://github.com/RealLeviticus/vatSysWindowManager/releases
- Right-click the downloaded ZIP file, select `Properties`, and click `Unblock` (if present).
- Unzip the archive.
- Copy the plugin DLL (and the `Layouts` folder, if present) to the base vatSys plugins directory:
  - `[...]\Program Files (x86)\vatSys\bin\Plugins`
- Start vatSys and verify the plugin is installed by checking `Info > About`.
- Ensure the following setting is turned on in vatSys so position/activity is available to the plugin:
  - `Settings > Activity Privacy > Display current activity as a status message`

Notes:
- The plugin targets .NET Framework 4.7.2 and uses `Newtonsoft.Json` and `PInvoke`.
- The plugin uses reflection against internal vatSys types — incompatible vatSys versions may break functionality.

## Usage
- Open vatSys and set up the windows and ASD exactly as you want them saved.
- From the Windows vatSys menu: `Window Layouts`:
  - `Save current layout` — saves a snapshot. You'll be prompted for a layout name (default is the current position).
  - `Layouts` — opens a hierarchical menu grouped by position. For each saved layout you can:
    - `Load` — apply the layout immediately.
    - `Auto load for this position` — toggle automatic loading for that position.
    - `Delete` — remove the layout file.
  - `Load current position layout` — attempts to restore the layout for the current position (respects the autoload map, legacy single-file layout, or falls back to the first available layout).

Behavior details:
- When restoring, plugin closes any plugin-created windows it previously opened, then re-creates windows found in the saved snapshot.
- If a saved layout included ASD state, the ASD center and range are applied.
- For some window types (chat/PM, ATIS, sequence/arrival, strips) the plugin will use vatSys API methods to open the right window with the saved parameters.
- Map layers and ASD range are applied when possible.

## Files and storage
- Layout files are saved as JSON files with extension `*.layout.json`.
- Default storage locations (created automatically):
  - `<plugin-folder>\Layouts\` (preferred when plugin can determine its own location), or
  - `%USERPROFILE%\Documents\vatSysWindowManager\` (fallback).
- Layout file naming:
  - `POSITION__LayoutName.layout.json` (safe-file-name normalized)
  - Legacy single-file layout: `POSITION.layout.json`
- Auto-load configuration is stored at `autoload.json` inside the layout root. It maps position name -> layout name.

Example:
- `Layouts\LONDON__MyOps.layout.json`
- `Layouts\autoload.json`

You can manually edit `autoload.json` if you need to set autoload entries outside the UI. The file is a simple JSON dictionary.

## Building from source
- Target: .NET Framework 4.7.2, C# 7.3
- NuGet packages: `Newtonsoft.Json` and `PInvoke` (or equivalent).
- The plugin uses MEF export `[Export(typeof(IPlugin))]` and depends on the `vatsys` assemblies (the host app).
- After building, copy the compiled DLL into vatSys plugin folder as required by the host.

## Limitations and compatibility
- The plugin relies on reflection into internal vatSys types and private members (e.g., ASD internals, `MMI` fields, `vatsys.*` types). Changes to vatSys internals may break the plugin.
- Not all window state can be restored in every vatSys version — some controls or internal fields may not exist.
- The plugin tries to be resilient; failures during reflection are ignored and logged via `System.Diagnostics.Debug.WriteLine`.
- Restores that require invoking non-public methods or setting private fields may be subject to permission and version constraints.

## Troubleshooting
- Menu not visible: wait a short time after vatSys starts — the plugin waits for the UI to be ready before adding the menu. Ensure the plugin DLL is loaded by vatSys (check vatSys plugin logs if available).
- Layout not applied: check that the layout file exists in the layout directory and that it contains the expected `Windows` entries.
- ASD/range/maps not restored: vatSys internals may have changed; check debug output for reflection exceptions.
- If windows fail to re-create, ensure vatSys APIs used by the plugin (for opening PM/ATIS/arrival/strip windows) are available in your vatSys version.

## Troubleshooting tips (quick)
- Layout root: check the `Layouts` folder next to the plugin DLL or `%USERPROFILE%\Documents\vatSysWindowManager\`.
- Debug output: run vatSys under a debugger or inspect `Debug.WriteLine` output to diagnose reflection/restore issues.
- To clear an autoload mapping, either use the UI `Auto load for this position` toggle or delete/modify `autoload.json`.

## Contributing / Development notes
- The project uses reflection heavily. When updating for new vatSys versions, search for field and method name changes (e.g., `mainASDCentre`, `MAIN_ASD_RANGE`, `positionsToolStripMenuItem`, `OpenPMWindow`, etc.).
- Keep `Newtonsoft.Json` and `PInvoke` versions compatible with the host.
- Add additional metadata capture/restore for other windows by extending `CaptureMetadata`, `CreateWindow`, and `TryRestoreWindow`.

## License & Warranty
- Use at your own risk. The plugin is provided without warranty. Review the repository LICENSE file (if present) for license details.

---

If you want, I can produce a compact `README.md` pull request-ready file or adjust wording to match an existing project README style.
