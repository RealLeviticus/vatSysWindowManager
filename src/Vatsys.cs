using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PInvoke;
using vatsys;
using vatsys.Plugin;

namespace vatSysWindowManager
{
    internal sealed class StripWindowInfo
    {
        public Form Form;
        /// <summary>State, Beacon, ADEP, ADES, Other.</summary>
        public string Type;
        public string State;
        public string Point;
    }

    internal sealed class ArrivalWindowInfo
    {
        public Form Form;
        public string Airport;
        /// <summary>False for another plugin's replacement of vatSys's arrival list.</summary>
        public bool IsVatsys;
    }

    /// <summary>
    /// Every vatSys member this plugin depends on that is not public API.
    ///
    /// vatSys documents only the thin vatsys.Plugin surface, so the rest is found by reflection.
    /// Each member is looked up once, here, and <see cref="SelfCheck"/> lists what a given vatSys
    /// build does not have. A missing member turns off only the feature that needs it; nothing
    /// guesses at an alternative. docs/vatsys-internals.md is the same list for people, to recheck
    /// against a decompile after a vatSys update.
    /// </summary>
    internal static partial class Vatsys
    {
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly List<string> Missing = new List<string>();
        internal static readonly Assembly Assembly = typeof(MMI).Assembly;

        // ---- types ----------------------------------------------------------------------------
        private static readonly Type TStripWindow = FindType("vatsys.StripWindow");
        private static readonly Type TStripTypes = FindType("vatsys.StripWindow+StripWindowTypes");
        private static readonly Type TAtisWindow = FindType("vatsys.ATISWindow");
        private static readonly Type TSequenceWindow = FindType("vatsys.SequenceWindow");
        private static readonly Type TStripSetupWindow = FindType("vatsys.StripSetupWindow");
        internal static readonly Type TMainForm = FindType("vatsys.MainForm");
        internal static readonly Type TAsdWindow = FindType("vatsys.ASDWindow");
        internal static readonly Type TGroundWindow = FindType("vatsys.ASMGCSWindow");

        // ---- MMI ------------------------------------------------------------------------------
        private static readonly FieldInfo FMainForm = FindField(typeof(MMI), "MainForm", S);
        private static readonly FieldInfo FVscs = FindField(typeof(MMI), "VSCSWindow", S);
        private static readonly FieldInfo FPlacements = FindField(typeof(MMI), "BaseFormPlacements", S);
        private static readonly FieldInfo FPluginMenuItems = FindField(typeof(MMI), "PluginMenuItems", S);
        private static readonly FieldInfo FOtherW = FindField(typeof(MMI), "OtherW", S);
        private static readonly FieldInfo FStripSetupWindow = FindField(typeof(MMI), "StripSetupWindow", S);

        private static readonly Dictionary<string, FieldInfo> StateWindowFields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase)
        {
            { "Jurisdiction", FindField(typeof(MMI), "JurisdictionW", S) },
            { "Announced", FindField(typeof(MMI), "AnnouncedW", S) },
            { "Preactive", FindField(typeof(MMI), "PreactiveW", S) },
            { "HandoverIn", FindField(typeof(MMI), "HandoverInW", S) },
            { "HandoverOut", FindField(typeof(MMI), "HandoverOutW", S) }
        };

        private static readonly PropertyInfo PBeaconW = FindProperty(typeof(MMI), "BeaconW", S);
        private static readonly PropertyInfo PAtisWindows = FindProperty(typeof(MMI), "ATISWindows", S);
        private static readonly PropertyInfo PArrivalLists = FindProperty(typeof(MMI), "ArrivalLists", S);
        private static readonly PropertyInfo PStripMode = FindProperty(typeof(MMI), "StripMode", S);
        private static readonly PropertyInfo PStripSortMode = FindProperty(typeof(MMI), "StripSortMode", S);

        private static readonly MethodInfo MCreateBeacon = FindMethod(typeof(MMI), "CreateBeaconStripWindow", S, TStripTypes, typeof(string), typeof(int));
        private static readonly MethodInfo MDeleteBeacon = FindMethod(typeof(MMI), "DeleteBeaconStripWindow", S, TStripWindow);
        private static readonly MethodInfo MOpenStripWindow = FindMethod(typeof(MMI), "OpenStripWindow", S, TStripWindow);

        // ---- window members ---------------------------------------------------------------------
        private static readonly FieldInfo FStripType = FindField(TStripWindow, "WindowType", I);
        private static readonly FieldInfo FStripState = FindField(TStripWindow, "State", I);
        private static readonly FieldInfo FStripBeacon = FindField(TStripWindow, "Beacon", I);
        private static readonly FieldInfo FAtisCallsign = FindField(TAtisWindow, "ATISCallsign", I);
        private static readonly PropertyInfo PSequenceAirport = FindProperty(TSequenceWindow, "Airport", I);

        private static readonly Dictionary<string, FieldInfo> SortButtons = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase)
        {
            { "Alpha", FindField(TStripSetupWindow, "AlphaSortButton", I) },
            { "Time", FindField(TStripSetupWindow, "TimeSortButton", I) },
            { "Level", FindField(TStripSetupWindow, "LevelSortButton", I) }
        };

        /// <summary>
        /// vatSys's single-instance windows that a layout owns: the MMI field that holds each one, the
        /// main form's own menu handler that opens it (so opening it here is exactly a menu click),
        /// and whether vatSys shows and hides it by itself.
        /// </summary>
        internal sealed class SingleWindow
        {
            public string Key;
            public FieldInfo Field;
            public MethodInfo Opener;
            /// <summary>Shown and hidden by vatSys as things happen. Only its place is restored.</summary>
            public bool Automatic;
        }

        internal static readonly List<SingleWindow> Singles = new List<SingleWindow>
        {
            Single("SystemMessagesWindow", "systemToolStripMenuItem_Click"),
            Single("ATCMessagesWindow", "controllerToolStripMenuItem_Click"),
            Single("StripSetupWindow", "setupToolStripMenuItem2_Click"),
            Single("SectorsWindow", "sectorsToolStripMenuItem_Click"),
            Single("InfoDisplayWindow", "dispInfoToolStripMenuItem_Click"),
            Single("METRequestWindow", "requestToolStripMenuItem1_Click"),
            Single("RestrictedAreaWindow", "restrictedAreasToolStripMenuItem_Click"),
            Single("CurrentMessagesWindow", null, automatic: true),
            Single("CurrentErrorsWindow", null, automatic: true)
        };

        private static SingleWindow Single(string key, string handler, bool automatic = false)
        {
            return new SingleWindow
            {
                Key = key,
                Field = FindField(typeof(MMI), key, S),
                Opener = handler == null ? null : FindMethod(TMainForm, handler, I, typeof(object), typeof(EventArgs)),
                Automatic = automatic
            };
        }

        // ---- lookup helpers -----------------------------------------------------------------------

        private static Type FindType(string name)
        {
            var type = Assembly.GetType(name, false);
            if (type == null) Missing.Add("type " + name);
            return type;
        }

        private static FieldInfo FindField(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            var field = type.GetField(name, flags);
            if (field == null) Missing.Add($"field {type.Name}.{name}");
            return field;
        }

        private static PropertyInfo FindProperty(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            var property = type.GetProperty(name, flags);
            if (property == null) Missing.Add($"property {type.Name}.{name}");
            return property;
        }

        private static MethodInfo FindMethod(Type type, string name, BindingFlags flags, params Type[] parameters)
        {
            if (type == null || parameters.Any(p => p == null)) return null;
            var method = type.GetMethod(name, flags, null, parameters, null);
            if (method == null) Missing.Add($"method {type.Name}.{name}({string.Join(", ", parameters.Select(p => p.Name))})");
            return method;
        }

        /// <summary>The members this vatSys build does not have. Empty when everything was found.</summary>
        internal static IReadOnlyList<string> SelfCheck()
        {
            EnsureAsdResolved();
            return Missing.Distinct().ToList();
        }

        private static T Read<T>(FieldInfo field, object instance = null) where T : class
        {
            try
            {
                return field?.GetValue(instance) as T;
            }
            catch
            {
                return null;
            }
        }

        private static List<Form> ReadForms(PropertyInfo property)
        {
            var result = new List<Form>();
            try
            {
                if (property?.GetValue(null) is IEnumerable items)
                {
                    foreach (var item in items)
                    {
                        if (item is Form form && !form.IsDisposed) result.Add(form);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Reading MMI.{property?.Name} failed", ex);
            }
            return result;
        }

        internal static bool Alive(Form form) => form != null && !form.IsDisposed;

        // ---- general ------------------------------------------------------------------------------

        internal static Form MainForm => Read<Form>(FMainForm);

        internal static Form Vscs => Read<Form>(FVscs);

        internal static string PositionKey()
        {
            try
            {
                var name = MMI.PrimePosition?.Name;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch
            {
                // no position selected yet
            }
            return "DEFAULT";
        }

        internal static bool IsConnected()
        {
            try
            {
                return Network.IsConnected;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsVatsysType(Type type) => type != null && type.Assembly == Assembly;

        /// <summary>Every form on the UI thread, shown or hidden.</summary>
        internal static List<Form> AllForms()
        {
            var forms = Native.FormsOnThread(Ui.ThreadId);
            var seen = new HashSet<Form>(forms);
            foreach (Form form in Application.OpenForms)
            {
                if (form != null && !form.IsDisposed && seen.Add(form)) forms.Add(form);
            }
            return forms;
        }

        internal static bool IsVisible(Form form)
        {
            return Alive(form) && form.IsHandleCreated && Native.IsWindowVisible(form.Handle);
        }

        /// <summary>
        /// vatSys's own record of where each of its windows goes, keyed by form name. Writing it means
        /// a window opened later through BaseForm.ShowWithPlacement appears where the layout wants it.
        /// </summary>
        internal static void SeedPlacement(string formName, User32.WINDOWPLACEMENT placement)
        {
            if (string.IsNullOrWhiteSpace(formName)) return;
            try
            {
                if (FPlacements?.GetValue(null) is Dictionary<string, User32.WINDOWPLACEMENT> map)
                {
                    map[formName] = placement;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Writing MMI.BaseFormPlacements failed", ex);
            }
        }

        internal static List<CustomToolStripMenuItem> PluginMenuItems()
        {
            try
            {
                if (FPluginMenuItems?.GetValue(null) is List<CustomToolStripMenuItem> items) return items.Where(i => i?.Item != null).ToList();
            }
            catch (Exception ex)
            {
                Log.Error("Reading MMI.PluginMenuItems failed", ex);
            }
            return new List<CustomToolStripMenuItem>();
        }

        // ---- strip windows ----------------------------------------------------------------------------

        internal static bool StripsAvailable =>
            StateWindowFields.Values.All(f => f != null) && PBeaconW != null && FStripType != null && FStripState != null && FStripBeacon != null;

        internal static IEnumerable<string> StateWindowNames => StateWindowFields.Keys;

        internal static Form StateStripWindow(string hmiState)
        {
            return hmiState != null && StateWindowFields.TryGetValue(hmiState, out var field) ? Read<Form>(field) : null;
        }

        internal static Form OtherStripWindow => Read<Form>(FOtherW);

        internal static StripWindowInfo DescribeStrip(Form form)
        {
            if (form == null || TStripWindow == null || !TStripWindow.IsInstanceOfType(form)) return null;
            try
            {
                return new StripWindowInfo
                {
                    Form = form,
                    Type = FStripType?.GetValue(form)?.ToString(),
                    State = FStripState?.GetValue(form)?.ToString(),
                    Point = (FStripBeacon?.GetValue(form) as string ?? string.Empty).Trim()
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Beacon, ADEP and ADES windows in vatSys's own order (the order Strip Setup shows them in).</summary>
        internal static List<StripWindowInfo> BeaconWindows()
        {
            return ReadForms(PBeaconW).Select(DescribeStrip).Where(i => i != null).ToList();
        }

        internal static bool CanCreateBeacons => MCreateBeacon != null && MDeleteBeacon != null && TStripTypes != null;

        /// <summary>Defines a beacon window the way Strip Setup does. vatSys creates it hidden.</summary>
        internal static bool CreateBeacon(string stripType, string point)
        {
            if (!CanCreateBeacons) return false;
            try
            {
                var type = Enum.Parse(TStripTypes, stripType, true);
                MCreateBeacon.Invoke(null, new[] { type, point, (object)(-1) });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Creating the {stripType} {point} strip window failed", ex);
                return false;
            }
        }

        internal static void DeleteBeacon(Form window)
        {
            try
            {
                MDeleteBeacon?.Invoke(null, new object[] { window });
            }
            catch (Exception ex)
            {
                Log.Error("Removing a beacon strip window failed", ex);
            }
        }

        /// <summary>Shows a strip window the way vatSys does (owned by the main form, strips reassigned).</summary>
        internal static void ShowStripWindow(Form window)
        {
            try
            {
                if (MOpenStripWindow != null)
                {
                    MOpenStripWindow.Invoke(null, new object[] { window });
                }
                else if (window is BaseForm baseForm)
                {
                    baseForm.Show(MainForm);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Showing a strip window failed", ex);
            }
        }

        internal static string StripMode
        {
            get
            {
                try { return MMI.StripMode.ToString(); } catch { return null; }
            }
        }

        internal static string StripSortMode
        {
            get
            {
                try { return MMI.StripSortMode.ToString(); } catch { return null; }
            }
        }

        /// <summary>
        /// Sets the strip mode. The setter always reassigns every strip to its window and raises
        /// StripModeChanged, so calling it with the current value is how to force a refresh.
        /// </summary>
        internal static bool SetStripMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode) || PStripMode == null) return false;
            try
            {
                PStripMode.SetValue(null, Enum.Parse(typeof(MMI.StripModes), mode, true));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Setting strip mode '{mode}' failed", ex);
                return false;
            }
        }

        internal static bool SetStripSortMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode) || PStripSortMode == null) return false;
            try
            {
                PStripSortMode.SetValue(null, Enum.Parse(typeof(MMI.StripSortModes), mode, true));
                SyncSortButtons(mode);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Setting strip sort mode '{mode}' failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Strip Setup only sets its three sort buttons in its constructor and when one is clicked; it
        /// does not look at them again when the sort mode changes underneath it.
        /// </summary>
        private static void SyncSortButtons(string mode)
        {
            var setup = Read<Form>(FStripSetupWindow);
            if (!Alive(setup)) return;
            foreach (var pair in SortButtons)
            {
                try
                {
                    var button = pair.Value?.GetValue(setup);
                    var property = button?.GetType().GetProperty("Checked", I);
                    if (property != null && property.CanWrite)
                    {
                        property.SetValue(button, string.Equals(pair.Key, mode, StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch
                {
                    // cosmetic only
                }
            }
        }

        // ---- ATIS windows --------------------------------------------------------------------------------

        internal static bool AtisAvailable => PAtisWindows != null && FAtisCallsign != null;

        internal static List<KeyValuePair<Form, string>> AtisWindows()
        {
            var result = new List<KeyValuePair<Form, string>>();
            foreach (var form in ReadForms(PAtisWindows))
            {
                string callsign = null;
                try { callsign = FAtisCallsign?.GetValue(form) as string; } catch { }
                if (!string.IsNullOrWhiteSpace(callsign)) result.Add(new KeyValuePair<Form, string>(form, callsign.Trim()));
            }
            return result;
        }

        // ---- arrival lists -----------------------------------------------------------------------------------

        internal static bool ArrivalsAvailable => PArrivalLists != null && PSequenceAirport != null;

        /// <summary>
        /// Every arrival list on screen: vatSys's own, and any other plugin's stand-in for one.
        /// OzServer closes each vatSys list as it opens and puts its own window in its place, so a
        /// list is identified by its airport, not by its type.
        /// </summary>
        internal static List<ArrivalWindowInfo> ArrivalWindows()
        {
            var result = new List<ArrivalWindowInfo>();
            foreach (var form in ReadForms(PArrivalLists))
            {
                string airport = null;
                try { airport = PSequenceAirport?.GetValue(form) as string; } catch { }
                if (!string.IsNullOrWhiteSpace(airport))
                {
                    result.Add(new ArrivalWindowInfo { Form = form, Airport = airport.Trim().ToUpperInvariant(), IsVatsys = true });
                }
            }

            foreach (var form in AllForms())
            {
                var airport = ForeignArrivalAirport(form);
                if (airport != null && IsVisible(form) && result.All(r => r.Form != form))
                {
                    result.Add(new ArrivalWindowInfo { Form = form, Airport = airport, IsVatsys = false });
                }
            }
            return result;
        }

        /// <summary>
        /// The airport of another plugin's arrival list, or null when the form is not one. Recognised
        /// by shape rather than by name: not vatSys's, a type called ...ArrivalList..., and a public
        /// string property called Airport.
        /// </summary>
        internal static string ForeignArrivalAirport(Form form)
        {
            if (form == null || form.IsDisposed) return null;
            var type = form.GetType();
            if (IsVatsysType(type) || type.Name.IndexOf("ArrivalList", StringComparison.OrdinalIgnoreCase) < 0) return null;
            try
            {
                var property = type.GetProperty("Airport", BindingFlags.Instance | BindingFlags.Public);
                var airport = property?.PropertyType == typeof(string) ? property.GetValue(form) as string : null;
                return string.IsNullOrWhiteSpace(airport) ? null : airport.Trim().ToUpperInvariant();
            }
            catch
            {
                return null;
            }
        }

        // ---- single-instance windows -----------------------------------------------------------------------------

        internal static SingleWindow FindSingle(string key) =>
            Singles.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

        internal static Form SingleForm(SingleWindow single) => single == null ? null : Read<Form>(single.Field);

        /// <summary>
        /// Opens a single-instance window by running the main form's own menu handler for it. Those
        /// handlers either show the window or toggle it, so this is only called when it is not showing.
        /// </summary>
        internal static bool OpenSingle(SingleWindow single)
        {
            var main = MainForm;
            if (single == null || !Alive(main)) return false;
            try
            {
                if (single.Opener != null)
                {
                    single.Opener.Invoke(main, new object[] { main, EventArgs.Empty });
                    return true;
                }

                if (SingleForm(single) is BaseForm existing && !existing.IsDisposed)
                {
                    existing.Show(main);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Opening {single.Key} failed", ex);
            }
            return false;
        }

        // ---- second ASD and Ground windows ---------------------------------------------------------------------------

        /// <summary>Opens a new Air or Ground window the way the Windows menu does: construct, then show owned by the main form.</summary>
        internal static Form CreateDisplayWindow(bool ground, IntRect bounds)
        {
            var type = ground ? TGroundWindow : TAsdWindow;
            var main = MainForm;
            if (type == null || !Alive(main)) return null;
            try
            {
                if (!(Activator.CreateInstance(type, true) is BaseForm window)) return null;
                if (bounds != null && bounds.IsUsable)
                {
                    // Start it where it is going, so it does not flash in the middle of the main window first.
                    window.StartPosition = FormStartPosition.Manual;
                    window.Bounds = new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                }
                window.Show(main);
                return window;
            }
            catch (Exception ex)
            {
                Log.Error($"Opening a new {(ground ? "Ground" : "ASD")} window failed", ex);
                return null;
            }
        }
    }
}
