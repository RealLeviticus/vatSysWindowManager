using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace vatSysWindowManager
{
    /// <summary>The radar display (ASDControlDX) inside the main form, second ASD windows and Ground windows.</summary>
    internal static partial class Vatsys
    {
        private static bool asdResolved;
        private static Type tAsdControl;
        private static MethodInfo mSetZoom, mSetDisplayCenter, mIsMapVisible, mSetMapVisible, mGetRenderParams;
        private static PropertyInfo pDisplayPosition, pScreenCentre;
        private static FieldInfo fRange, fAtcList, fScale, fStoredCentre, fStoredRange, fInitialZoomSet;
        private static FieldInfo fMainMaps, fMainDynamicInfill, fMainTerrain;
        private static readonly Dictionary<Type, FieldInfo> AsdFieldByFormType = new Dictionary<Type, FieldInfo>();

        // Resolved on first use rather than in field initialisers: the two halves of this class are
        // separate files, and the order their static initialisers run in is not defined.
        private static void EnsureAsdResolved()
        {
            if (asdResolved) return;
            asdResolved = true;

            tAsdControl = FindType("vatsys.ASDControlDX");
            var tRenderParams = FindType("vatsys.RenderParams");

            mSetZoom = FindMethod(tAsdControl, "SetZoom", I, typeof(double), typeof(bool), typeof(bool), typeof(bool));
            mSetDisplayCenter = FindMethod(tAsdControl, "SetDisplayCenter", I, typeof(Coordinate), typeof(bool), typeof(bool));
            mIsMapVisible = FindMethod(tAsdControl, "IsMapVisible", I, typeof(DisplayMaps.Map));
            mSetMapVisible = FindMethod(tAsdControl, "SetMapVisible", I, typeof(DisplayMaps.Map), typeof(bool));
            mGetRenderParams = FindMethod(tAsdControl, "GetRenderParams", I, typeof(bool));
            pDisplayPosition = FindProperty(tAsdControl, "DisplayPosition", I);
            pScreenCentre = FindProperty(tRenderParams, "ScreenCentre", I);
            fRange = FindField(tAsdControl, "range", I);
            fAtcList = FindField(tAsdControl, "atcListRectangle", I);
            fScale = FindField(tAsdControl, "scaleRectangle", I);
            fStoredCentre = FindField(tAsdControl, "StoredCentreLL", I);
            fStoredRange = FindField(tAsdControl, "StoredRange", I);
            fInitialZoomSet = FindField(tAsdControl, "initialZoomSet", I);

            fMainMaps = FindField(TMainForm, "mapsToolStripMenuItem", I);
            fMainDynamicInfill = FindField(TMainForm, "dynamicInfillToolStripMenuItem", I);
            fMainTerrain = FindField(TMainForm, "terrainHeightsToolStripMenuItem", I);

            foreach (var windowType in new[] { TAsdWindow, TGroundWindow })
            {
                FindField(windowType, "positionsToolStripMenuItem", I);
                FindField(windowType, "mapsToolStripMenuItem", I);
                FindField(windowType, "freeToolStripMenuItem", I);
                FindMethod(windowType, "positionItem_Click", I, typeof(object), typeof(EventArgs));
                FindMethod(windowType, "freeToolStripMenuItem_Click", I, typeof(object), typeof(EventArgs));
            }
        }

        /// <summary>The radar display control on a form, or null when the form has none.</summary>
        internal static Control AsdControl(Form form)
        {
            EnsureAsdResolved();
            if (!Alive(form) || tAsdControl == null) return null;

            var type = form.GetType();
            if (!AsdFieldByFormType.TryGetValue(type, out var field))
            {
                field = type.GetFields(I).FirstOrDefault(f => f.FieldType == tAsdControl);
                AsdFieldByFormType[type] = field;
            }

            try
            {
                return field?.GetValue(form) as Control;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// False until a new display has drawn once. Its first draw sets the zoom to the position's
        /// default, which would undo a range applied before it.
        /// </summary>
        internal static bool IsAsdReady(Control asd)
        {
            EnsureAsdResolved();
            if (asd == null || asd.IsDisposed || !asd.IsHandleCreated) return false;
            if (fInitialZoomSet == null) return true;
            try
            {
                return (bool)fInitialZoomSet.GetValue(asd);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>The name of the position a display is showing, or null for Free.</summary>
        internal static string DisplayPositionName(Form form)
        {
            var asd = AsdControl(form);
            try
            {
                return asd == null ? null : (pDisplayPosition?.GetValue(asd) as LogicalPositions.Position)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string MapKey(DisplayMaps.Map map) => (map.Folder ?? string.Empty) + "|" + (map.Name ?? string.Empty);

        private static ToolStripMenuItem MenuField(Form form, string fieldName)
        {
            try
            {
                return form.GetType().GetField(fieldName, I)?.GetValue(form) as ToolStripMenuItem;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<ToolStripMenuItem> Descendants(ToolStripMenuItem root)
        {
            if (root == null) yield break;
            foreach (var item in root.DropDownItems.OfType<ToolStripMenuItem>())
            {
                yield return item;
                foreach (var child in Descendants(item)) yield return child;
            }
        }

        // ---- reading ------------------------------------------------------------------------------

        internal static AsdView ReadView(Form form)
        {
            var asd = AsdControl(form);
            if (asd == null) return null;

            var view = new AsdView();
            try
            {
                view.Position = (pDisplayPosition?.GetValue(asd) as LogicalPositions.Position)?.Name;

                var render = mGetRenderParams?.Invoke(asd, new object[] { false });
                if (render != null && pScreenCentre?.GetValue(render) is Coordinate centre)
                {
                    view.CentreLat = centre.Latitude;
                    view.CentreLon = centre.Longitude;
                }

                if (fRange?.GetValue(asd) is double range && range > 0) view.Range = range;

                var mapsMenu = MenuField(form, "mapsToolStripMenuItem");
                if (mapsMenu != null && mIsMapVisible != null)
                {
                    view.Maps = new List<string>();
                    foreach (var item in Descendants(mapsMenu))
                    {
                        if (item.Tag is DisplayMaps.Map map && (bool)mIsMapVisible.Invoke(asd, new object[] { map }))
                        {
                            view.Maps.Add(MapKey(map));
                        }
                    }
                }

                view.AtcList = ReadRect(fAtcList, asd);
                view.Scale = ReadRect(fScale, asd);

                if (fStoredCentre != null && fStoredRange != null)
                {
                    view.HasStoredView = true;
                    if (fStoredCentre.GetValue(asd) is Coordinate stored)
                    {
                        view.StoredLat = stored.Latitude;
                        view.StoredLon = stored.Longitude;
                    }
                    view.StoredRange = Convert.ToInt32(fStoredRange.GetValue(asd));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Reading the display state of '{form.Text}' failed", ex);
            }
            return view;
        }

        // SharpDX.RectangleF keeps Left/Top/Right/Bottom as fields; X/Y/Width/Height are properties.
        private static FloatRect ReadRect(FieldInfo field, object asd)
        {
            try
            {
                var boxed = field?.GetValue(asd);
                if (boxed == null) return null;
                var type = boxed.GetType();
                var rect = new FloatRect
                {
                    Left = Convert.ToSingle(type.GetField("Left").GetValue(boxed)),
                    Top = Convert.ToSingle(type.GetField("Top").GetValue(boxed)),
                    Right = Convert.ToSingle(type.GetField("Right").GetValue(boxed)),
                    Bottom = Convert.ToSingle(type.GetField("Bottom").GetValue(boxed))
                };
                // vatSys sizes these to their contents on every frame, and an ATC list with nobody in it
                // has no size at all. Only the position is meaningful, so an empty rectangle is kept.
                return rect;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteRect(FieldInfo field, object asd, FloatRect rect)
        {
            if (field == null || rect == null) return;
            try
            {
                var boxed = field.GetValue(asd);
                if (boxed == null) return;
                var type = boxed.GetType();

                // Move it, keeping whatever size vatSys currently has for it.
                var width = Convert.ToSingle(type.GetField("Right").GetValue(boxed)) - Convert.ToSingle(type.GetField("Left").GetValue(boxed));
                var height = Convert.ToSingle(type.GetField("Bottom").GetValue(boxed)) - Convert.ToSingle(type.GetField("Top").GetValue(boxed));
                type.GetField("Left").SetValue(boxed, rect.Left);
                type.GetField("Top").SetValue(boxed, rect.Top);
                type.GetField("Right").SetValue(boxed, rect.Left + Math.Max(0f, width));
                type.GetField("Bottom").SetValue(boxed, rect.Top + Math.Max(0f, height));
                field.SetValue(asd, boxed);
            }
            catch (Exception ex)
            {
                Log.Error($"Writing {field.Name} failed", ex);
            }
        }

        /// <summary>
        /// Writes the ATC list and scale marker positions again if vatSys does not have them. Its
        /// render thread reads, clamps and writes these rectangles back on every frame, so a write
        /// made while a frame is in progress can be overwritten by that frame.
        /// </summary>
        internal static void ReassertRects(Form form, AsdView view)
        {
            var asd = AsdControl(form);
            if (asd == null || view == null) return;
            Reassert(fAtcList, asd, view.AtcList);
            Reassert(fScale, asd, view.Scale);
        }

        private static void Reassert(FieldInfo field, object asd, FloatRect wanted)
        {
            if (field == null || wanted == null) return;
            var now = ReadRect(field, asd);
            // Position only: vatSys sizes these to their contents.
            if (now != null && Math.Abs(now.Left - wanted.Left) <= 1f && Math.Abs(now.Top - wanted.Top) <= 1f) return;
            WriteRect(field, asd, wanted);
        }

        // ---- applying ------------------------------------------------------------------------------

        /// <summary>
        /// Puts a display back the way it was saved: position, then centre, then range, then maps.
        /// Range comes after the window has its final size and after the centre, because vatSys
        /// derives the zoom from both. Applied once; nothing here re-applies on a timer.
        /// </summary>
        internal static void ApplyView(Form form, AsdView view, bool isMain, RestoreReport report)
        {
            var asd = AsdControl(form);
            if (asd == null || view == null) return;
            var label = string.IsNullOrWhiteSpace(form.Text) ? form.Name : form.Text;

            try
            {
                // The main display's position is the controller's position; a layout never changes it.
                if (!isMain) ApplyPosition(form, asd, view.Position, report);

                if (view.CentreLat.HasValue && view.CentreLon.HasValue && mSetDisplayCenter != null)
                {
                    // setVisCenter: false, as vatSys's own LoadPosition passes. True would move the
                    // controller's network visibility centre.
                    mSetDisplayCenter.Invoke(asd, new object[] { new Coordinate(view.CentreLat.Value, view.CentreLon.Value), true, false });
                }

                if (view.Range.HasValue && view.Range.Value > 0 && mSetZoom != null)
                {
                    mSetZoom.Invoke(asd, new object[] { view.Range.Value, true, true, true });
                }

                if (view.Maps != null) ApplyMaps(form, asd, view.Maps, label, report);

                WriteRect(fAtcList, asd, view.AtcList);
                WriteRect(fScale, asd, view.Scale);

                if (view.HasStoredView && fStoredCentre != null && fStoredRange != null)
                {
                    fStoredCentre.SetValue(asd, view.StoredLat.HasValue && view.StoredLon.HasValue
                        ? new Coordinate(view.StoredLat.Value, view.StoredLon.Value)
                        : null);
                    fStoredRange.SetValue(asd, view.StoredRange ?? -1);
                }

                MMI.RequestRedraw();
            }
            catch (Exception ex)
            {
                report?.Problem($"the display in \"{label}\" could not be fully restored");
                Log.Error($"Applying the display state of '{label}' failed", ex);
            }
        }

        // Changing position goes through the window's own menu handler, so its title, its Position
        // menu tick and its Maps menu ticks all change the way they do on a real click.
        private static void ApplyPosition(Form form, Control asd, string wanted, RestoreReport report)
        {
            var current = (pDisplayPosition?.GetValue(asd) as LogicalPositions.Position)?.Name;
            if (string.IsNullOrWhiteSpace(wanted)) wanted = null;
            if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return;

            var type = form.GetType();
            if (wanted == null)
            {
                var freeItem = MenuField(form, "freeToolStripMenuItem");
                var freeHandler = type.GetMethod("freeToolStripMenuItem_Click", I, null, new[] { typeof(object), typeof(EventArgs) }, null);
                if (freeItem != null && freeHandler != null) freeHandler.Invoke(form, new object[] { freeItem, EventArgs.Empty });
                return;
            }

            var item = Descendants(MenuField(form, "positionsToolStripMenuItem"))
                .FirstOrDefault(i => i.Tag is LogicalPositions.Position p && string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
            var handler = type.GetMethod("positionItem_Click", I, null, new[] { typeof(object), typeof(EventArgs) }, null);
            if (item == null || handler == null)
            {
                report?.Problem($"display position \"{wanted}\" was not found");
                return;
            }
            handler.Invoke(form, new object[] { item, EventArgs.Empty });
        }

        // Only maps that have an entry in the window's own Maps menu are touched. Maps another plugin
        // draws for itself are that plugin's to show and hide.
        private static void ApplyMaps(Form form, Control asd, List<string> wanted, string label, RestoreReport report)
        {
            var menu = MenuField(form, "mapsToolStripMenuItem");
            if (menu == null || mIsMapVisible == null || mSetMapVisible == null) return;

            var want = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Descendants(menu))
            {
                if (!(item.Tag is DisplayMaps.Map map)) continue;

                var key = MapKey(map);
                // Version 1 layouts stored the bare map name.
                var show = want.Contains(key) || want.Contains(map.Name ?? string.Empty);
                if (show)
                {
                    found.Add(key);
                    found.Add(map.Name ?? string.Empty);
                }

                if (item.Checked != show) item.Checked = show;
                if ((bool)mIsMapVisible.Invoke(asd, new object[] { map }) != show)
                {
                    mSetMapVisible.Invoke(asd, new object[] { map, show });
                }
            }

            var gone = want.Where(w => !found.Contains(w)).Select(w => w.Substring(w.IndexOf('|') + 1)).Distinct().ToList();
            if (gone.Count > 0 && LegacyMapNames(wanted))
            {
                // A version 1 layout listed every ticked item in the Maps menu by its text, including
                // ones other plugins put there. Those were never maps, so they are not worth a warning.
                Log.Info($"\"{label}\": {gone.Count} saved Maps-menu name(s) are not maps in this dataset ({string.Join(", ", gone.Take(8))})");
            }
            else if (gone.Count > 0)
            {
                report?.Problem($"{gone.Count} map(s) saved for \"{label}\" no longer exist ({string.Join(", ", gone.Take(5))}{(gone.Count > 5 ? ", ..." : string.Empty)})");
            }
        }

        // Version 2 always writes "Folder|Name". A list with no '|' anywhere came from version 1.
        private static bool LegacyMapNames(List<string> maps) => maps.Count > 0 && maps.All(m => m.IndexOf('|') < 0);

        // ---- the two Maps-menu toggles that are not maps -----------------------------------------------

        internal static bool? DynamicInfill
        {
            get
            {
                try { return MMI.DynamicInfill; } catch { return null; }
            }
        }

        internal static bool? TerrainHeights
        {
            get
            {
                try { return MMI.TerrainHeights; } catch { return null; }
            }
        }

        internal static void SetDynamicInfill(bool value)
        {
            EnsureAsdResolved();
            try
            {
                MMI.DynamicInfill = value;
                if (fMainDynamicInfill?.GetValue(MainForm) is ToolStripMenuItem item) item.Checked = value;
            }
            catch (Exception ex)
            {
                Log.Error("Setting Dynamic Infill failed", ex);
            }
        }

        internal static void SetTerrainHeights(bool value)
        {
            EnsureAsdResolved();
            try
            {
                MMI.TerrainHeights = value;
                if (fMainTerrain?.GetValue(MainForm) is ToolStripMenuItem item) item.Checked = value;
            }
            catch (Exception ex)
            {
                Log.Error("Setting Terrain Heights failed", ex);
            }
        }
    }
}
