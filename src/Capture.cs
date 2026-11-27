using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace vatSysWindowManager
{
    /// <summary>
    /// Builds a layout from what vatSys has on screen now.
    ///
    /// Windows are taken from vatSys's own registries (its strip window fields, its beacon, ATIS and
    /// arrival lists), not from a scan of open forms, so a window is saved as what it is rather than
    /// guessed from its type name and title.
    /// </summary>
    internal static class Capture
    {
        internal static Layout Snapshot(string position, string layoutName)
        {
            var z = Native.ZOrderTopDown();
            var layout = new Layout
            {
                Position = position,
                LayoutName = layoutName,
                SavedUtc = DateTime.UtcNow,
                Strips = new StripSetup
                {
                    Mode = Vatsys.StripMode,
                    SortMode = Vatsys.StripSortMode,
                    BeaconsComplete = Vatsys.StripsAvailable
                },
                DynamicInfill = Vatsys.DynamicInfill,
                TerrainHeights = Vatsys.TerrainHeights,
                ControlledSectors = ControlledSectorNames()
            };

            var taken = new HashSet<Form>();

            WindowEntry Add(Form form, string kind, string key = null)
            {
                if (!Vatsys.Alive(form) || !taken.Add(form)) return null;
                var entry = new WindowEntry { Kind = kind, Key = key };
                Placement.Capture(form, entry, z);
                layout.Windows.Add(entry);
                return entry;
            }

            // Main window and its display.
            var main = Vatsys.MainForm;
            var mainEntry = Add(main, WindowKind.Main);
            if (mainEntry != null) mainEntry.Asd = Vatsys.ReadView(main);

            // Strip windows: all five state windows whether shown or not, every beacon window in
            // vatSys's order, and the Other window (which vatSys shows and hides by itself).
            if (Vatsys.StripsAvailable)
            {
                foreach (var name in Vatsys.StateWindowNames)
                {
                    Add(Vatsys.StateStripWindow(name), WindowKind.StripState, name);
                }

                foreach (var beacon in Vatsys.BeaconWindows())
                {
                    var entry = Add(beacon.Form, WindowKind.StripBeacon, beacon.Point);
                    if (entry != null) entry.StripType = beacon.Type;
                }

                Add(Vatsys.OtherStripWindow, WindowKind.StripOther);
            }

            if (Vatsys.AtisAvailable)
            {
                foreach (var atis in Vatsys.AtisWindows()) Add(atis.Key, WindowKind.Atis, atis.Value);
            }

            if (Vatsys.ArrivalsAvailable)
            {
                foreach (var arrival in Vatsys.ArrivalWindows())
                {
                    // One entry per airport, whichever window is showing it.
                    if (layout.Windows.Any(w => w.Kind == WindowKind.Arrival && w.Key == arrival.Airport)) continue;
                    Add(arrival.Form, WindowKind.Arrival, arrival.Airport);
                }
            }

            Add(Vatsys.Vscs, WindowKind.Vscs);

            foreach (var single in Vatsys.Singles)
            {
                var form = Vatsys.SingleForm(single);
                // Only worth recording once vatSys has given it a window.
                if (Vatsys.Alive(form) && form.IsHandleCreated) Add(form, WindowKind.Single, single.Key);
            }

            // Second ASD and Ground windows, and other plugins' windows: no registry holds these, so
            // they come from the UI thread's own list of windows.
            foreach (var form in Vatsys.AllForms())
            {
                if (taken.Contains(form) || !Vatsys.IsVisible(form)) continue;

                var type = form.GetType();
                if (Vatsys.TAsdWindow != null && type == Vatsys.TAsdWindow)
                {
                    var entry = Add(form, WindowKind.Asd);
                    if (entry != null) entry.Asd = Vatsys.ReadView(form);
                }
                else if (Vatsys.TGroundWindow != null && type == Vatsys.TGroundWindow)
                {
                    var entry = Add(form, WindowKind.Ground);
                    if (entry != null) entry.Asd = Vatsys.ReadView(form);
                }
                else if (PluginWindows.IsPluginWindow(form))
                {
                    var entry = Add(form, WindowKind.Plugin);
                    if (entry != null) entry.Opener = PluginWindows.OpenerFor(form);
                }
                // Anything else of vatSys's (flight plan windows, private messages, dialogs) is not
                // part of a layout.
            }

            return layout;
        }

        private static List<string> ControlledSectorNames()
        {
            var names = new List<string>();
            try
            {
                foreach (var sector in MMI.SectorsControlled)
                {
                    if (sector == null || sector.IsDummy || string.IsNullOrWhiteSpace(sector.Name)) continue;
                    var name = sector.Name.Trim();
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Reading controlled sectors failed", ex);
            }
            return names;
        }
    }
}
