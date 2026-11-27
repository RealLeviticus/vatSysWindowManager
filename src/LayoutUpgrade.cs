using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace vatSysWindowManager
{
    /// <summary>
    /// Reads a layout file of any version. Version 1 (plugin 1.1.x) stored every window as a type
    /// name, a form name and a bag of strings; it is converted here to the current model so that
    /// existing layouts keep working. The file itself is only rewritten when the layout is next
    /// saved over.
    /// </summary>
    internal static class LayoutUpgrade
    {
        internal static Layout Parse(string json)
        {
            var root = JObject.Parse(json);
            var version = root.Value<int?>("Version") ?? 1;
            if (version >= 2)
            {
                var layout = root.ToObject<Layout>() ?? new Layout();
                layout.Windows = (layout.Windows ?? new List<WindowEntry>()).Where(w => w != null && !string.IsNullOrEmpty(w.Kind)).ToList();
                return layout;
            }

            return FromVersion1(root);
        }

        /// <summary>Position and name only, for listing layouts without converting them.</summary>
        internal static (string Position, string LayoutName) ReadHeader(string json)
        {
            var root = JObject.Parse(json);
            return (root.Value<string>("Position"), root.Value<string>("LayoutName"));
        }

        private static Layout FromVersion1(JObject root)
        {
            var layout = new Layout
            {
                Version = Layout.CurrentVersion,
                Upgraded = true,
                Position = root.Value<string>("Position"),
                LayoutName = root.Value<string>("LayoutName"),
                SavedUtc = root.Value<DateTime?>("SavedUtc") ?? DateTime.MinValue,
                ControlledSectors = root["ControlledSectors"]?.ToObject<List<string>>(),
                // Version 1 always re-applied sectors. That is now a per-layout choice, off by default.
                RestoreSectors = false,
                Strips = new StripSetup
                {
                    Mode = root.Value<string>("StripMode"),
                    SortMode = root.Value<string>("StripSortMode"),
                    BeaconsComplete = false
                }
            };

            var windows = (root["Windows"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            for (var i = 0; i < windows.Count; i++)
            {
                var entry = ConvertWindow(windows[i], layout);
                if (entry == null) continue;

                // Version 1 brought each window to the front in file order, so the last one ended on top.
                entry.Z = windows.Count - 1 - i;
                layout.Windows.Add(entry);
            }

            // The oldest files kept the main display's centre and range only at the top level.
            var mainView = layout.Windows.FirstOrDefault(w => w.Kind == WindowKind.Main)?.Asd;
            if (mainView != null && root["Asd"] is JObject asd)
            {
                if (!mainView.CentreLat.HasValue || !mainView.CentreLon.HasValue)
                {
                    mainView.CentreLat = asd.Value<double?>("LatitudeDeg");
                    mainView.CentreLon = asd.Value<double?>("LongitudeDeg");
                }
                var range = asd.Value<double?>("Range");
                if (!mainView.Range.HasValue && range.HasValue && range.Value > 0) mainView.Range = range;
            }

            if (string.IsNullOrWhiteSpace(layout.Strips.Mode))
            {
                // Version 1 before strip mode was saved: infer it the way 1.1.x did.
                if (layout.Windows.Any(w => w.Kind == WindowKind.StripState)) layout.Strips.Mode = "State";
                else if (layout.Windows.Any(w => w.Kind == WindowKind.StripBeacon)) layout.Strips.Mode = "Beacon";
                else layout.Strips.Mode = null;
            }

            return layout;
        }

        private static WindowEntry ConvertWindow(JObject w, Layout layout)
        {
            var typeName = w.Value<string>("TypeName") ?? string.Empty;
            var formName = w.Value<string>("FormName") ?? string.Empty;
            var title = w.Value<string>("Title") ?? string.Empty;
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (w["Metadata"] is JObject m)
            {
                foreach (var p in m.Properties()) meta[p.Name] = p.Value?.ToString();
            }

            var entry = new WindowEntry
            {
                TypeName = typeName,
                FormName = formName,
                Title = title,
                Visible = true
            };

            // Placement.
            var placement = w["Placement"] as JObject;
            var showCmd = placement?.Value<int?>("ShowCmd") ?? 1;
            if (placement != null)
            {
                entry.Normal = new IntRect(
                    placement.Value<int?>("Left") ?? 0,
                    placement.Value<int?>("Top") ?? 0,
                    placement.Value<int?>("Right") ?? 0,
                    placement.Value<int?>("Bottom") ?? 0);
                if (!entry.Normal.IsUsable) entry.Normal = null;
            }

            if (meta.TryGetValue("WindowState", out var ws) && string.Equals(ws, "Maximized", StringComparison.OrdinalIgnoreCase) || showCmd == 3)
            {
                entry.State = WindowStateName.Maximized;
            }
            else if (showCmd == 2 || showCmd == 6 || showCmd == 7)
            {
                entry.State = WindowStateName.Minimized;
            }

            if (entry.State == WindowStateName.Normal &&
                TryInt(meta, "ActualLeft", out var al) && TryInt(meta, "ActualTop", out var at) &&
                TryInt(meta, "ActualRight", out var ar) && TryInt(meta, "ActualBottom", out var ab))
            {
                var actual = new IntRect(al, at, ar, ab);
                // A minimised window reports (-32000,-32000); never treat that as a place to put it.
                if (actual.IsUsable) entry.Actual = actual;
            }

            // Kind.
            if (typeName == "vatsys.MainForm")
            {
                entry.Kind = WindowKind.Main;
                entry.Asd = ConvertAsd(meta, layout, isMain: true);
            }
            else if (typeName == "vatsys.ASDWindow")
            {
                entry.Kind = WindowKind.Asd;
                entry.Asd = ConvertAsd(meta, layout, isMain: false);
            }
            else if (typeName == "vatsys.ASMGCSWindow")
            {
                entry.Kind = WindowKind.Ground;
                entry.Asd = ConvertAsd(meta, layout, isMain: false);
            }
            else if (typeName == "vatsys.StripWindow")
            {
                meta.TryGetValue("StripWindowType", out var stripType);
                meta.TryGetValue("HMIState", out var hmi);
                meta.TryGetValue("Beacon", out var beacon);
                switch ((stripType ?? string.Empty).Trim())
                {
                    case "State":
                        if (string.IsNullOrWhiteSpace(hmi)) return null;
                        entry.Kind = WindowKind.StripState;
                        entry.Key = hmi.Trim();
                        break;
                    case "Beacon":
                    case "ADEP":
                    case "ADES":
                        if (string.IsNullOrWhiteSpace(beacon)) return null;
                        entry.Kind = WindowKind.StripBeacon;
                        entry.StripType = stripType.Trim();
                        entry.Key = beacon.Trim();
                        break;
                    case "Other":
                        entry.Kind = WindowKind.StripOther;
                        break;
                    default:
                        return null;
                }
            }
            else if (typeName == "vatsys.ATISWindow")
            {
                // 1.1.x could create ATIS windows with no callsign and then save them. Drop those.
                if (!meta.TryGetValue("ATISCallsign", out var callsign) || string.IsNullOrWhiteSpace(callsign)) return null;
                entry.Kind = WindowKind.Atis;
                entry.Key = callsign.Trim();
            }
            else if (typeName == "vatsys.SequenceWindow" || typeName.EndsWith(".ArrivalListWindow", StringComparison.Ordinal))
            {
                string airport = null;
                if (meta.TryGetValue("Airport", out var a) && !string.IsNullOrWhiteSpace(a)) airport = a;
                else if (meta.TryGetValue("AirportHint", out var h) && !string.IsNullOrWhiteSpace(h)) airport = h;
                else airport = AirportFromArrivalTitle(title) ?? AirportFromArrivalName(formName);
                if (string.IsNullOrWhiteSpace(airport)) return null;
                entry.Kind = WindowKind.Arrival;
                entry.Key = airport.Trim().ToUpperInvariant();
            }
            else if (typeName == "vatsys.VSCSWindow")
            {
                entry.Kind = WindowKind.Vscs;
            }
            else if (typeName == "vatsys.ChatWindow")
            {
                meta.TryGetValue("ChatType", out var chatType);
                if (string.Equals(chatType, "System", StringComparison.OrdinalIgnoreCase)) entry.Key = "SystemMessagesWindow";
                else if (string.Equals(chatType, "ATC", StringComparison.OrdinalIgnoreCase)) entry.Key = "ATCMessagesWindow";
                else return null; // private message windows are not part of a layout
                entry.Kind = WindowKind.Single;
            }
            else if (SingleKeysByType.TryGetValue(typeName, out var singleKey))
            {
                entry.Kind = WindowKind.Single;
                entry.Key = singleKey;
            }
            else if (typeName.StartsWith("vatsys.", StringComparison.Ordinal))
            {
                // Any other vatSys window (flight plan, dialogs, ...) is not something a layout owns.
                return null;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(typeName)) return null;
                entry.Kind = WindowKind.Plugin;
            }

            return entry;
        }

        /// <summary>vatSys single-instance windows by type name, mapped to the MMI field that holds them.</summary>
        internal static readonly Dictionary<string, string> SingleKeysByType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "vatsys.StripSetupWindow", "StripSetupWindow" },
            { "vatsys.SectorsWindow", "SectorsWindow" },
            { "vatsys.InfoDisplayWindow", "InfoDisplayWindow" },
            { "vatsys.METWindow", "METRequestWindow" },
            { "vatsys.RestrictedAreaWindow", "RestrictedAreaWindow" },
            { "vatsys.CurrentMessageWindow", "CurrentMessagesWindow" },
            { "vatsys.ErrorWindow", "CurrentErrorsWindow" }
        };

        private static AsdView ConvertAsd(Dictionary<string, string> meta, Layout layout, bool isMain)
        {
            var view = new AsdView();
            if (meta.TryGetValue("DisplayPosition", out var pos) && !string.IsNullOrWhiteSpace(pos)) view.Position = pos.Trim();
            if (TryDouble(meta, "CentreLat", out var lat) && TryDouble(meta, "CentreLon", out var lon))
            {
                view.CentreLat = lat;
                view.CentreLon = lon;
            }
            if (TryDouble(meta, "Range", out var range) && range > 0) view.Range = range;

            if (meta.TryGetValue("Maps", out var maps) && maps != null)
            {
                var list = maps.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (isMain)
                {
                    // 1.1.x saved these two menu toggles in the same list as the maps.
                    layout.DynamicInfill = list.RemoveAll(s => string.Equals(s, "Dynamic Infill", StringComparison.OrdinalIgnoreCase)) > 0;
                    layout.TerrainHeights = list.RemoveAll(s => string.Equals(s, "Terrain Heights", StringComparison.OrdinalIgnoreCase)) > 0;
                }
                view.Maps = list;
            }

            return view;
        }

        /// <summary>"A:YSSY List" gives YSSY.</summary>
        internal static string AirportFromArrivalTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var text = title.Trim();
            if (!text.StartsWith("A:", StringComparison.OrdinalIgnoreCase)) return null;
            text = text.Substring(2).Trim();
            var end = text.IndexOf(' ');
            if (end > 0) text = text.Substring(0, end);
            return text.Length >= 3 && text.Length <= 5 && text.All(char.IsLetterOrDigit) ? text.ToUpperInvariant() : null;
        }

        /// <summary>"OzServerArrivalListYSSYWindow" gives YSSY.</summary>
        internal static string AirportFromArrivalName(string formName)
        {
            const string prefix = "OzServerArrivalList";
            const string suffix = "Window";
            if (string.IsNullOrWhiteSpace(formName) || !formName.StartsWith(prefix, StringComparison.Ordinal) || !formName.EndsWith(suffix, StringComparison.Ordinal)) return null;
            var middle = formName.Substring(prefix.Length, formName.Length - prefix.Length - suffix.Length);
            return middle.Length >= 3 && middle.Length <= 5 ? middle.ToUpperInvariant() : null;
        }

        private static bool TryInt(Dictionary<string, string> meta, string key, out int value)
        {
            value = 0;
            return meta.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryDouble(Dictionary<string, string> meta, string key, out double value)
        {
            value = 0;
            return meta.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        internal static string Serialize(Layout layout)
        {
            return JsonConvert.SerializeObject(layout, Formatting.Indented, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }
    }
}
