# vatSys internals this plugin depends on

Checked against vatSys **0.4.9305.35667** on 27 Nov 2026.

This is our own analysis. It names vatSys classes and members so the claims can be rechecked; it
contains no vatSys source code, and a decompile must never be committed to this repository.

## Rechecking after a vatSys update

1. Run `.\tests\Run.ps1`. The "vatSys internals are all present" test loads the installed
   `vatSys.exe` and reports any member below that no longer exists. That catches renames and
   removals. It does not catch a member that still exists but now behaves differently.
2. For behaviour, decompile the new build (`ilspycmd -p -o <scratch folder> "<vatSys>\bin\vatSys.exe"`)
   and reread the members under "Behaviour relied on".
3. At run time the plugin does the same name check at start-up and writes anything missing to
   `WindowManager.log`. A missing member turns off only the feature that needs it.

All lookups are in `src\Vatsys.cs` and `src\Vatsys.Asd.cs`. Nothing else in the plugin uses
reflection against vatSys, except `PluginWindows.cs`, which reads WinForms' own event list.

## Members looked up by reflection

### `vatsys.MMI` (static)

| Member | Kind | Used for |
|--------|------|----------|
| `MainForm` | field | The main window. Target for everything owned by it. |
| `JurisdictionW`, `AnnouncedW`, `PreactiveW`, `HandoverInW`, `HandoverOutW` | fields | The five state strip windows. |
| `OtherW` | field | The Other strip window. |
| `BeaconW` | property | Beacon, ADEP and ADES strip windows, in Strip Setup order. |
| `CreateBeaconStripWindow(StripWindowTypes, string, int)` | method | Defining a beacon window. |
| `DeleteBeaconStripWindow(StripWindow)` | method | Removing a beacon window definition. |
| `OpenStripWindow(StripWindow)` | method | Showing a strip window the way vatSys does. |
| `StripMode`, `StripSortMode` | property setters | Getters are public; setters are internal. |
| `StripSetupWindow` | field | To keep its three sort buttons in step. |
| `ATISWindows` | property | Open ATIS windows. |
| `ArrivalLists` | property | Open arrival lists. |
| `VSCSWindow` | field | The VSCS window. |
| `BaseFormPlacements` | field | vatSys's own record of where each named window goes. |
| `PluginMenuItems` | field | Menu items other plugins have registered. |
| `SystemMessagesWindow`, `ATCMessagesWindow`, `SectorsWindow`, `InfoDisplayWindow`, `METRequestWindow`, `RestrictedAreaWindow`, `CurrentMessagesWindow`, `CurrentErrorsWindow` | fields | Single-instance windows. |

### Window types

| Type | Members | Used for |
|------|---------|----------|
| `vatsys.StripWindow` | fields `WindowType`, `State`, `Beacon`; enum `StripWindowTypes` | Identifying a strip window. Read only: all three are set by the constructor. |
| `vatsys.ATISWindow` | field `ATISCallsign` | Identifying an ATIS window. |
| `vatsys.SequenceWindow` | property `Airport` | Identifying an arrival list. |
| `vatsys.StripSetupWindow` | fields `AlphaSortButton`, `TimeSortButton`, `LevelSortButton` | Sort button ticks. |
| `vatsys.ASDWindow`, `vatsys.ASMGCSWindow` | constructor; fields `positionsToolStripMenuItem`, `mapsToolStripMenuItem`, `freeToolStripMenuItem`; methods `positionItem_Click`, `freeToolStripMenuItem_Click` | Opening a second display and changing its position through its own handler. |
| `vatsys.MainForm` | fields `mapsToolStripMenuItem`, `dynamicInfillToolStripMenuItem`, `terrainHeightsToolStripMenuItem`; methods `systemToolStripMenuItem_Click`, `controllerToolStripMenuItem_Click`, `setupToolStripMenuItem2_Click`, `sectorsToolStripMenuItem_Click`, `dispInfoToolStripMenuItem_Click`, `requestToolStripMenuItem1_Click`, `restrictedAreasToolStripMenuItem_Click` | Map menu ticks, and opening single-instance windows exactly as the menu does. |

### `vatsys.ASDControlDX` (the radar display)

| Member | Kind | Used for |
|--------|------|----------|
| `DisplayPosition` | property | The position shown. |
| `SetDisplayCenter(Coordinate, bool, bool)` | method | Centre. Third argument is always `false`. |
| `SetZoom(double, bool, bool, bool)` | method | Range. |
| `IsMapVisible(Map)`, `SetMapVisible(Map, bool)` | methods | Maps. |
| `GetRenderParams(bool)` then `RenderParams.ScreenCentre` | method, property | Reading the current centre. |
| `range` | field | Reading the exact range. |
| `atcListRectangle`, `scaleRectangle` | fields | `SharpDX.RectangleF`: fields are `Left`, `Top`, `Right`, `Bottom`. |
| `StoredCentreLL`, `StoredRange` | fields | The controller's stored view. Saved and restored as its own item. |
| `initialZoomSet` | field | Whether a new display has drawn yet. |

## Public API used directly

`MMI.PrimePosition`, `MMI.PrimePositonChanged`, `MMI.StripMode`, `MMI.StripSortMode` (getters),
`MMI.OpenATISWindow`, `MMI.OpenArrivalListWindow`, `MMI.SectorsControlled`,
`MMI.SetControlledSectors`, `MMI.DynamicInfill`, `MMI.TerrainHeights`, `MMI.RequestRedraw`,
`MMI.AddCustomMenuItem`, `SectorsVolumes.Sectors`, `Network.IsConnected`, `Errors.Add`,
`DisplayMaps.Map`, `LogicalPositions.Position`, `Coordinate`, `BaseForm.Show(owner)`,
`BaseForm.HasCloseButton`.

## Behaviour relied on

These are true of 0.4.9305.35667 and have no public contract.

**Position change.** `MainForm.SetPosition` calls `MMI.SetPrimePosition` (which raises
`PrimePositonChanged`), then `MMI.LoadPositionStripWindows`, then the display's `LoadPosition`.
So the event fires before vatSys has loaded the new position's strip windows and display. The
plugin posts its restore so that it runs after `SetPosition` returns. If vatSys ever raised the
event last, the post would be harmless.

**Start-up.** vatSys constructs plugins part-way through `MainForm`'s constructor, on the UI
thread, and reads `MMI.PluginMenuItems` straight afterwards. The strip windows are created later in
the same constructor, and the saved position is applied after that. The plugin adds its menu item
in its constructor and waits for the main form to be visible before loading anything.

**Strip windows.** The five state windows and the Other window are created once and only shown or
hidden (`HideOnClose`). `MMI.OpenStripWindow(type, state, beacon)` shows a beacon window only if it
is already in `BeaconW`; `CreateBeaconStripWindow` is what defines one, and it creates it hidden.
A new beacon window is named `beacon{N}StripWindow` after the number that exist. In Beacon mode a
strip goes only to beacon windows that are showing. `MMI.StripMode`'s setter always reassigns every
strip and raises its event, even for the same value; `StripSortMode`'s only acts on a change.

**ATIS and arrival lists.** `MMI.OpenATISWindow` throws if the callsign is already open or five
are open, and names the window `atis{N}Window`. `MMI.OpenArrivalListWindow` is synchronous, shows an
existing list for the airport rather than making a second, and names a new one `sequence{N}Window`.
Closing either kind disposes it and removes it from its list.

**Placement.** `BaseForm.ShowWithPlacement` shows a window natively from
`MMI.BaseFormPlacements[Name]`, so such windows can be missing from `Application.OpenForms`. The
plugin takes its list of windows from the UI thread instead. `BaseForm.OnFormClosing` writes the
window's placement back to that dictionary.

**Displays.** A new display sets its zoom to the position's default on its first paint. The
position menu handlers on `ASDWindow` and `ASMGCSWindow` set the title and the menu ticks as well
as loading the position. Map menu ticks are only updated by a click or a position change, so the
plugin sets them alongside `SetMapVisible`. `SetDisplayCenter` with a third argument of `true` moves
the network visibility centre for the main display.

**Sectors.** `MMI.SetControlledSectors` hands off to nobody every tracked aircraft in a sector that
is being removed. The plugin only calls it while not connected, with a non-empty list, when the
layout's option is on.

**Other plugins.** vatSys places a plugin menu item by its category. The plugin identifies one by
category plus the text on the way down to it, and reads which assembly handles its click from
WinForms' `Component.Events` list (`ToolStripItem.EventClick`). A menu item is only ever paired
with a window from the same plugin (same DLL, or a DLL in the same folder). If WinForms stored its
event list differently, nothing would be paired and other plugins' windows would be placed but not
reopened.

**Display rectangles.** The render thread reads, clamps and writes back `atcListRectangle` and
`scaleRectangle` on every frame. The plugin writes them, then checks for a few seconds and writes
again if a frame in progress overwrote them.

**OzServer.** OzServer closes each vatSys arrival list as it opens and shows its own window for the
same airport. The plugin treats any non-vatSys form whose type name contains `ArrivalList` and
which has a public string `Airport` property as an arrival list.
