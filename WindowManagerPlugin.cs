using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using PInvoke;
using vatsys;
using vatsys.Plugin;

namespace vatSysWindowManager
{
    [Export(typeof(IPlugin))]
    public class WindowManagerPlugin : IPlugin
    {
        private readonly ToolStripMenuItem rootMenuItem;
        private readonly ToolStripMenuItem saveLayoutMenuItem;
        private readonly ToolStripMenuItem loadLayoutMenuItem;
        private readonly ToolStripMenuItem reloadLayoutMenuItem;
        private EventHandler primePositionChangedHandler;
        private readonly HashSet<Form> pluginWindows = new HashSet<Form>();
        private readonly HashSet<Form> managedZOrder = new HashSet<Form>();
        private readonly Dictionary<string, string> autoLoadLayouts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool menuRegistered;

        public string Name => "WindowManager";

        public WindowManagerPlugin()
        {
            saveLayoutMenuItem = new ToolStripMenuItem("Save current layout");
            saveLayoutMenuItem.Click += (s, e) => SaveLayoutForCurrentPosition();

            loadLayoutMenuItem = new ToolStripMenuItem("Layouts");
            loadLayoutMenuItem.DropDownOpening += (s, e) => BuildLoadMenuItems();

            reloadLayoutMenuItem = new ToolStripMenuItem("Load current position layout");
            reloadLayoutMenuItem.Click += (s, e) => RestoreLayoutForCurrentPosition();

            rootMenuItem = new ToolStripMenuItem("Window Layouts");
            rootMenuItem.DropDownItems.Add(saveLayoutMenuItem);
            rootMenuItem.DropDownItems.Add(loadLayoutMenuItem);
            rootMenuItem.DropDownItems.Add(new ToolStripSeparator());
            rootMenuItem.DropDownItems.Add(reloadLayoutMenuItem);

            LoadAutoLoadMap();

            _ = Task.Run(async () =>
            {
                var ready = await EnsureUiReady();
                if (!ready) return;
                RunOnUiThread(AddMenuItem);
                await Task.Delay(1500);
                RunOnUiThread(RestoreLayoutForCurrentPosition);
                RunOnUiThread(HookPrimePositionChanged);
            });
        }

        public void OnFDRUpdate(FDP2.FDR updated) { }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }

        private async Task<bool> EnsureUiReady()
        {
            var attempts = 0;
            while (Application.OpenForms.Count == 0 && attempts < 50)
            {
                attempts++;
                await Task.Delay(100);
            }

            return Application.OpenForms.Count > 0;
        }

        private void AddMenuItem()
        {
            if (menuRegistered) return;

            try
            {
                var item = new CustomToolStripMenuItem(CustomToolStripMenuItemWindowType.Main, CustomToolStripMenuItemCategory.Windows, rootMenuItem);
                MMI.AddCustomMenuItem(item);
                menuRegistered = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to add menu item: {ex}");
            }
        }

        private void HookPrimePositionChanged()
        {
            try
            {
                if (primePositionChangedHandler != null) return;
                var field = typeof(MMI).GetField("PrimePositonChanged", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) return;

                primePositionChangedHandler = (s, e) => RunOnUiThread(RestoreLayoutForCurrentPosition);
                var current = field.GetValue(null) as EventHandler;
                current += primePositionChangedHandler;
                field.SetValue(null, current);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to hook PrimePositonChanged: {ex}");
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (action == null) return;

            var main = Application.OpenForms.Cast<Form>().FirstOrDefault();
            if (main != null && main.InvokeRequired)
            {
                main.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        private void BuildLoadMenuItems()
        {
            loadLayoutMenuItem.DropDownItems.Clear();

            var layouts = LoadAllLayouts();
            if (layouts.Count == 0)
            {
                loadLayoutMenuItem.DropDownItems.Add(new ToolStripMenuItem("(no saved layouts)") { Enabled = false });
                return;
            }

            foreach (var group in layouts.GroupBy(l => l.Position, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var positionItem = new ToolStripMenuItem(string.IsNullOrWhiteSpace(group.Key) ? "(No position)" : group.Key);
                foreach (var layout in group.OrderBy(l => l.LayoutName, StringComparer.OrdinalIgnoreCase))
                {
                    var layoutItem = new ToolStripMenuItem(layout.LayoutName)
                    {
                        Checked = IsAutoLoad(group.Key, layout.LayoutName)
                    };

                    var loadItem = new ToolStripMenuItem("Load");
                    loadItem.Click += (s, e) => RestoreLayoutFromFile(layout.Path);

                    var autoItem = new ToolStripMenuItem("Auto load for this position")
                    {
                        CheckOnClick = true,
                        Checked = IsAutoLoad(group.Key, layout.LayoutName)
                    };
                    autoItem.Click += (s, e) =>
                    {
                        SetAutoLoad(group.Key, layout.LayoutName, autoItem.Checked);
                        BuildLoadMenuItems();
                    };

                    var deleteItem = new ToolStripMenuItem("Delete");
                    deleteItem.Click += (s, e) => DeleteLayout(layout, group.Key);

                    layoutItem.DropDownItems.Add(loadItem);
                    layoutItem.DropDownItems.Add(new ToolStripSeparator());
                    layoutItem.DropDownItems.Add(autoItem);
                    layoutItem.DropDownItems.Add(new ToolStripSeparator());
                    layoutItem.DropDownItems.Add(deleteItem);
                    positionItem.DropDownItems.Add(layoutItem);
                }

                loadLayoutMenuItem.DropDownItems.Add(positionItem);
            }
        }

        private void BuildPositionMenu(Form form)
        {
            try
            {
                var method = form.GetType().GetMethod("PositionsToolStripMenuItem_DropDownOpened", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var menuField = form.GetType().GetField("positionsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var menu = menuField?.GetValue(form) as ToolStripMenuItem;

                if (method != null && menu != null)
                {
                    method.Invoke(form, new object[] { menu, EventArgs.Empty });
                }
            }
            catch
            {
                // ignore
            }
        }

        private string GetPositionKey()
        {
            var prime = GetPrimePositionName();
            if (!string.IsNullOrWhiteSpace(prime)) return prime.Trim();

            var position = GetVatSysPosition();
            if (!string.IsNullOrWhiteSpace(position)) return position.Trim();

            return "DEFAULT";
        }

        private string GetVatSysPosition()
        {
            try
            {
                var settingsType = typeof(MMI).Assembly.GetType("vatsys.Properties.Settings");
                if (settingsType == null) return null;

                var defaultProp = settingsType.GetProperty("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var instance = defaultProp?.GetValue(null);
                if (instance == null) return null;

                var positionProp = settingsType.GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = positionProp?.GetValue(instance);
                return value as string ?? value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private string GetPrimePositionName()
        {
            try
            {
                var pos = MMI.PrimePosition;
                return pos?.Name;
            }
            catch
            {
                return null;
            }
        }

        private string LayoutRoot()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrWhiteSpace(pluginDir) && Directory.Exists(pluginDir))
                {
                    var root = Path.Combine(pluginDir, "Layouts");
                    Directory.CreateDirectory(root);
                    return root;
                }
            }
            catch
            {
                // fall back to documents path
            }

            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "vatSysWindowManager");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private string AutoLoadConfigPath()
        {
            return Path.Combine(LayoutRoot(), "autoload.json");
        }

        private string LayoutFilePath(string position, string layoutName = null)
        {
            var safePos = SanitizeForFile(position, "DEFAULT");
            var safeLayout = SanitizeForFile(layoutName, safePos);
            var suffix = string.IsNullOrWhiteSpace(layoutName) ? string.Empty : $"__{safeLayout}";
            return Path.Combine(LayoutRoot(), $"{safePos}{suffix}.layout.json");
        }

        private string SanitizeForFile(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var safe = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
        }

        private void SaveLayoutForCurrentPosition()
        {
            try
            {
                var position = GetPositionKey();
                var layoutName = PromptForLayoutName(position);
                if (string.IsNullOrWhiteSpace(layoutName)) return;

                var snapshot = new LayoutSnapshot
                {
                    Position = position,
                    LayoutName = layoutName,
                    SavedUtc = DateTime.UtcNow,
                    Windows = new List<WindowLayoutEntry>(),
                    Asd = GetAsdState()
                };

                foreach (Form form in Application.OpenForms)
                {
                    var entry = BuildEntry(form);
                    if (entry != null)
                    {
                        snapshot.Windows.Add(entry);
                    }
                }

                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(LayoutFilePath(position, layoutName), json);

                MessageBox.Show($"Saved layout \"{layoutName}\" for {position}.", "vatSys Window Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save window layout:\n{ex.Message}", "vatSys Window Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RestoreLayoutForCurrentPosition()
        {
            try
            {
                ClosePluginWindows();

                var position = GetPositionKey();
                var path = ResolveLayoutPathForPosition(position);
                if (path == null) return;

                RestoreLayoutFromFile(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: restore failed: {ex}");
            }
        }

        private void RestoreLayoutFromFile(string path)
        {
            try
            {
                ClosePluginWindows();

                if (!File.Exists(path)) return;

                var snapshot = JsonConvert.DeserializeObject<LayoutSnapshot>(File.ReadAllText(path));
                if (snapshot?.Windows == null || snapshot.Windows.Count == 0) return;

                ApplyAsdState(snapshot.Asd);

                foreach (var entry in snapshot.Windows)
                {
                    TryRestoreWindow(entry);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: restore failed from file {path}: {ex}");
            }
        }

        private WindowLayoutEntry BuildEntry(Form form)
        {
            try
            {
                if (form == null || form.IsDisposed || !form.Visible) return null;

                var placement = User32.GetWindowPlacement(form.Handle);
            var entry = new WindowLayoutEntry
            {
                FormName = form.Name,
                TypeName = form.GetType().FullName,
                Title = form.Text,
                Placement = WindowPlacementDto.From(placement),
                Metadata = CaptureMetadata(form)
            };

            return entry;
        }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: could not capture form {form?.Name}: {ex}");
                return null;
            }
        }

        private string ResolveLayoutPathForPosition(string position)
        {
            var layouts = LoadAllLayouts().Where(l => string.Equals(l.Position, position, StringComparison.OrdinalIgnoreCase)).ToList();

            if (autoLoadLayouts.TryGetValue(position, out var autoLayoutName))
            {
                var match = layouts.FirstOrDefault(l => string.Equals(l.LayoutName, autoLayoutName, StringComparison.OrdinalIgnoreCase));
                if (match != null && File.Exists(match.Path)) return match.Path;
            }

            var legacy = LayoutFilePath(position);
            if (File.Exists(legacy)) return legacy;

            var first = layouts.FirstOrDefault();
            if (first != null && File.Exists(first.Path)) return first.Path;

            return null;
        }

        private Dictionary<string, string> CaptureMetadata(Form form)
        {
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var typeName = form.GetType().FullName ?? string.Empty;

            if (typeName.EndsWith("ChatWindow", StringComparison.Ordinal))
            {
                var recipient = GetStringField(form, "Recipient");
                if (!string.IsNullOrWhiteSpace(recipient)) meta["Recipient"] = recipient.Trim();

                var chatType = GetEnumField(form, "WindowType");
                if (chatType != null) meta["ChatType"] = chatType.ToString();
            }
            else if (typeName.EndsWith("ATISWindow", StringComparison.Ordinal))
            {
                var callsign = GetStringField(form, "ATISCallsign");
                if (!string.IsNullOrWhiteSpace(callsign)) meta["ATISCallsign"] = callsign.Trim();
            }
            else if (typeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
            {
                var airport = GetPropertyString(form, "Airport");
                if (!string.IsNullOrWhiteSpace(airport)) meta["Airport"] = airport.Trim();
            }
            else if (typeName.EndsWith("StripWindow", StringComparison.Ordinal))
            {
                var beacon = GetStringField(form, "Beacon");
                if (!string.IsNullOrWhiteSpace(beacon)) meta["Beacon"] = beacon.Trim();

                var stripType = GetEnumField(form, "WindowType");
                if (stripType != null) meta["StripWindowType"] = stripType.ToString();

                var hmiState = GetEnumField(form, "State");
                if (hmiState != null) meta["HMIState"] = hmiState.ToString();
            }

            var asd = GetAsdControl(form);
            if (asd != null)
            {
                var displayPos = GetDisplayPositionName(asd);
                if (!string.IsNullOrWhiteSpace(displayPos))
                {
                    meta["DisplayPosition"] = displayPos;
                }

                var r = GetRangeValue(asd);
                if (r != null)
                {
                    meta["Range"] = r.Value.ToString(CultureInfo.InvariantCulture);
                }

                var asdType = GetAsdType(asd);
                if (!string.IsNullOrWhiteSpace(asdType))
                {
                    meta["AsdType"] = asdType;
                }

                var mapChecks = GetCheckedMaps(asd);
                if (mapChecks?.Count > 0)
                {
                    meta["Maps"] = string.Join(";;", mapChecks);
                }
            }

            return meta;
        }

        private string GetStringField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(instance) as string;
        }

        private object GetEnumField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(instance);
        }

        private string GetPropertyString(object instance, string propertyName)
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var value = prop?.GetValue(instance);
            return value as string ?? value?.ToString();
        }

        private Form TryRestoreWindow(WindowLayoutEntry entry)
        {
            var placement = entry.Placement?.ToWindowPlacement();
            if (placement != null)
            {
                SetBaseFormPlacement(entry.FormName, placement.Value);
            }

            var window = FindExisting(entry) ?? CreateWindow(entry);

            if (window != null && placement != null)
            {
                ApplyPlacement(window, placement.Value);
            }

            if (window != null)
            {
                entry.Metadata.TryGetValue("AsdType", out var asdType);

                if (entry.Metadata.TryGetValue("DisplayPosition", out var displayPosition))
                {
                    ApplyDisplayPosition(window, displayPosition, asdType);
                }

                if (entry.Metadata.TryGetValue("Range", out var range))
                {
                    ApplyRange(window, range, asdType);
                }

                if (entry.Metadata.TryGetValue("Maps", out var maps))
                {
                    ApplyCheckedMaps(window, maps);
                }

                BringToFrontSafe(window);
                if (!IsMainVatSysForm(window))
                {
                    EnsureZOrder(window);
                }
            }

            return window;
        }

        private Form FindExisting(WindowLayoutEntry entry)
        {
            return Application.OpenForms.Cast<Form>().FirstOrDefault(f =>
                string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.Ordinal) &&
                string.Equals(f.Name, entry.FormName, StringComparison.Ordinal));
        }

        private Form CreateWindow(WindowLayoutEntry entry)
        {
            try
            {
                if (entry.TypeName.EndsWith("ChatWindow", StringComparison.Ordinal) &&
                    entry.Metadata.TryGetValue("Recipient", out var recipient))
                {
                    MMI.OpenPMWindow(recipient);
                    var w = FindWindow(entry.TypeName, f => string.Equals(GetStringField(f, "Recipient"), recipient, StringComparison.OrdinalIgnoreCase));
                    if (w != null) TrackPluginWindow(w);
                    return w;
                }

                if (entry.TypeName.EndsWith("ATISWindow", StringComparison.Ordinal) &&
                    entry.Metadata.TryGetValue("ATISCallsign", out var atis))
                {
                    MMI.OpenATISWindow(atis);
                    var w = FindWindow(entry.TypeName, f => string.Equals(GetStringField(f, "ATISCallsign"), atis, StringComparison.OrdinalIgnoreCase));
                    if (w != null) TrackPluginWindow(w);
                    return w;
                }

                if (entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal) &&
                    entry.Metadata.TryGetValue("Airport", out var airport))
                {
                    MMI.OpenArrivalListWindow(airport);
                    var w = FindWindow(entry.TypeName, f => string.Equals(GetPropertyString(f, "Airport"), airport, StringComparison.OrdinalIgnoreCase));
                    if (w != null) TrackPluginWindow(w);
                    return w;
                }

                if (entry.TypeName.EndsWith("StripWindow", StringComparison.Ordinal) &&
                    entry.Metadata.TryGetValue("Beacon", out var beacon) &&
                    entry.Metadata.TryGetValue("StripWindowType", out var stripTypeName))
                {
                    CreateStripWindow(stripTypeName, entry.Metadata.TryGetValue("HMIState", out var hmi) ? hmi : null, beacon);
                    return FindWindow(entry.TypeName, f => string.Equals(GetStringField(f, "Beacon"), beacon, StringComparison.OrdinalIgnoreCase));
                }

                var type = Type.GetType(entry.TypeName) ?? typeof(MMI).Assembly.GetType(entry.TypeName);
                if (type != null && typeof(Form).IsAssignableFrom(type))
                {
                    var instance = Activator.CreateInstance(type) as Form;
                    instance?.Show();
                    if (instance != null)
                    {
                        TrackPluginWindow(instance);
                        BringToFrontSafe(instance);
                        if (!IsMainVatSysForm(instance))
                        {
                            EnsureZOrder(instance);
                        }
                    }
                    return instance;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: create window failed for {entry.TypeName}: {ex}");
            }

            return null;
        }

        private void CreateStripWindow(string stripTypeName, string hmiStateName, string beacon)
        {
            try
            {
                var stripEnumType = typeof(MMI).Assembly.GetType("vatsys.StripWindow+StripWindowTypes");
                var hmiEnumType = typeof(MMI).Assembly.GetType("vatsys.MMI+HMIStates");

                if (stripEnumType == null || hmiEnumType == null) return;

                object stripValue;
                try
                {
                    stripValue = Enum.Parse(stripEnumType, stripTypeName, true);
                }
                catch
                {
                    return;
                }

                object hmiValue;
                try
                {
                    hmiValue = Enum.Parse(hmiEnumType, string.IsNullOrWhiteSpace(hmiStateName) ? "None" : hmiStateName, true);
                }
                catch
                {
                    hmiValue = Enum.ToObject(hmiEnumType, 0);
                }

                var method = typeof(MMI).GetMethod("OpenStripWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { stripEnumType, hmiEnumType, typeof(string) }, null);
                method?.Invoke(null, new[] { stripValue, hmiValue, beacon ?? string.Empty });

                var created = FindWindow("vatsys.StripWindow", f => string.Equals(GetStringField(f, "Beacon"), beacon, StringComparison.OrdinalIgnoreCase));
                if (created != null) TrackPluginWindow(created);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: create strip window failed: {ex}");
            }
        }

        private Control GetAsdControl(Form form)
        {
            var fields = form.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var field in fields)
            {
                if (typeof(Control).IsAssignableFrom(field.FieldType) &&
                    string.Equals(field.FieldType.FullName, "vatsys.ASDControlDX", StringComparison.Ordinal))
                {
                    return field.GetValue(form) as Control;
                }
            }

            return null;
        }

        private string GetDisplayPositionName(Control asdControl)
        {
            try
            {
                var prop = asdControl.GetType().GetProperty("DisplayPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var pos = prop?.GetValue(asdControl);
                if (pos == null) return null;
                var nameProp = pos.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public);
                return nameProp?.GetValue(pos) as string;
            }
            catch
            {
                return null;
            }
        }

        private string GetAsdType(Control asdControl)
        {
            try
            {
                var field = asdControl.GetType().GetField("asdType", BindingFlags.Instance | BindingFlags.NonPublic);
                var val = field?.GetValue(asdControl);
                return val?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private void ApplyDisplayPosition(Form form, string positionName, string expectedAsdType)
        {
            try
            {
                var asd = GetAsdControl(form);
                if (asd == null) return;

                var actualAsdType = GetAsdType(asd);
                if (!string.IsNullOrWhiteSpace(expectedAsdType) &&
                    !string.IsNullOrWhiteSpace(actualAsdType) &&
                    !string.Equals(actualAsdType, expectedAsdType, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var positionsType = typeof(MMI).Assembly.GetType("vatsys.LogicalPositions");
                var positionsProp = positionsType?.GetProperty("Positions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var list = positionsProp?.GetValue(null) as System.Collections.IEnumerable;
                if (list == null) return;

                object targetPosition = null;
                foreach (var pos in list)
                {
                    var nameField = pos.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public);
                    var n = nameField?.GetValue(pos) as string;
                    if (string.Equals(n, positionName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetPosition = pos;
                        break;
                    }
                }

                if (targetPosition == null) return;

                BuildPositionMenu(form);
                if (TryClickPositionMenu(form, targetPosition))
                {
                    return;
                }

                if (TryInvokeLoadPosition(asd, targetPosition))
                {
                    return;
                }

                var field = asd.GetType().GetField("displayPosition", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(asd, targetPosition);
                var prop = asd.GetType().GetProperty("DisplayPosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                prop?.SetValue(asd, targetPosition, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply display position failed: {ex}");
            }
        }

        private bool TryInvokeLoadPosition(Control asdControl, object targetPosition)
        {
            try
            {
                var loadMethod = asdControl.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (!string.Equals(m.Name, "LoadPosition", StringComparison.Ordinal)) return false;
                        var parameters = m.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(targetPosition);
                    });

                if (loadMethod == null) return false;

                loadMethod.Invoke(asdControl, new[] { targetPosition });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: LoadPosition invoke failed: {ex}");
                return false;
            }
        }

        private bool TryClickPositionMenu(Form form, object targetPosition)
        {
            try
            {
            var menuField = form.GetType().GetField("positionsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (menuField == null) return false;
            var menu = menuField.GetValue(form) as ToolStripMenuItem;
            if (menu == null) return false;

            var targetName = targetPosition.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(targetPosition) as string;
            if (string.IsNullOrWhiteSpace(targetName)) return false;

                foreach (ToolStripMenuItem item in menu.DropDownItems)
                {
                    if (item.Tag != null && ReferenceEquals(item.Tag, targetPosition))
                    {
                        item.PerformClick();
                        return true;
                    }

                    var tagName = item.Tag as string;
                    var tagObjName = item.Tag?.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item.Tag) as string;

                    if (string.Equals(tagName, targetName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tagObjName, targetName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.Text, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        item.PerformClick();
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private void TrackPluginWindow(Form form)
        {
            if (form == null) return;
            if (pluginWindows.Contains(form)) return;
            pluginWindows.Add(form);
            form.FormClosed += (s, e) =>
            {
                pluginWindows.Remove(form);
            };
        }

        private void ClosePluginWindows()
        {
            foreach (var f in pluginWindows.ToList())
            {
                try
                {
                    if (f != null && !f.IsDisposed) f.Close();
                }
                catch { }
            }
            pluginWindows.Clear();
        }

        private double? GetRangeValue(Control asdControl)
        {
            try
            {
                var field = asdControl.GetType().GetField("range", BindingFlags.Instance | BindingFlags.NonPublic);
                var val = field?.GetValue(asdControl);
                if (val is double d) return d;
                return val != null ? Convert.ToDouble(val, CultureInfo.InvariantCulture) : (double?)null;
            }
            catch
            {
                return null;
            }
        }

        private void ApplyRange(Form form, string rangeValue, string expectedAsdType, int remainingRetries = 2)
        {
            if (string.IsNullOrWhiteSpace(rangeValue)) return;

            try
            {
                if (!double.TryParse(rangeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return;

                var asd = GetAsdControl(form);
                if (asd == null) return;

                var actualAsdType = GetAsdType(asd);
                if (!string.IsNullOrWhiteSpace(expectedAsdType) &&
                    !string.IsNullOrWhiteSpace(actualAsdType) &&
                    !string.Equals(actualAsdType, expectedAsdType, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                AttachRangeRestoreOnChange(asd, r, actualAsdType);
                ApplyRangeToControl(asd, r, actualAsdType);

                if (remainingRetries > 0)
                {
                    var delayMs = remainingRetries == 2 ? 300 : 1000;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(delayMs);
                        RunOnUiThread(() =>
                        {
                            if (form != null && !form.IsDisposed)
                            {
                                ApplyRange(form, rangeValue, expectedAsdType, remainingRetries - 1);
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply range failed: {ex}");
            }
        }

        private void ApplyRangeToControl(Control asd, double r, string actualAsdType)
        {
            var rangeField = asd.GetType().GetField("range", BindingFlags.Instance | BindingFlags.NonPublic);
            var storedRangeField = asd.GetType().GetField("StoredRange", BindingFlags.Instance | BindingFlags.NonPublic);
            var defRangeField = asd.GetType().GetField("DefRange", BindingFlags.Instance | BindingFlags.NonPublic);
            var initialZoomField = asd.GetType().GetField("initialZoomSet", BindingFlags.Instance | BindingFlags.NonPublic);
            var zoomingField = asd.GetType().GetField("zooming", BindingFlags.Instance | BindingFlags.NonPublic);
            var zoomRectField = asd.GetType().GetField("zoomrect", BindingFlags.Instance | BindingFlags.NonPublic);
            var zoomCenterField = asd.GetType().GetField("zoomcenter", BindingFlags.Instance | BindingFlags.NonPublic);
            var isMainAsd = string.Equals(actualAsdType, "MainASD", StringComparison.OrdinalIgnoreCase);

            initialZoomField?.SetValue(asd, true);
            zoomingField?.SetValue(asd, false);

            if (zoomRectField != null)
            {
                try { zoomRectField.SetValue(asd, Activator.CreateInstance(zoomRectField.FieldType)); } catch { }
            }
            if (zoomCenterField != null)
            {
                try { zoomCenterField.SetValue(asd, Activator.CreateInstance(zoomCenterField.FieldType)); } catch { }
            }

            rangeField?.SetValue(asd, r);

            if (storedRangeField != null)
            {
                var value = storedRangeField.FieldType == typeof(double)
                    ? (object)r
                    : Convert.ChangeType(r, storedRangeField.FieldType, CultureInfo.InvariantCulture);
                storedRangeField.SetValue(asd, value);
            }

            if (defRangeField != null)
            {
                var value = defRangeField.FieldType == typeof(double)
                    ? (object)r
                    : Convert.ChangeType(r, defRangeField.FieldType, CultureInfo.InvariantCulture);
                defRangeField.SetValue(asd, value);
            }

            if (isMainAsd)
            {
                var mainRangeField = typeof(MMI).GetField("MAIN_ASD_RANGE", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (mainRangeField != null)
                {
                    var value = mainRangeField.FieldType == typeof(double)
                        ? (object)r
                        : Convert.ChangeType(r, mainRangeField.FieldType, CultureInfo.InvariantCulture);
                    mainRangeField.SetValue(null, value);
                }
            }

            var refresh = asd.GetType().GetMethod("OnRangeChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            refresh?.Invoke(asd, null);

            try
            {
                var setRange = asd.GetType().GetMethod("SetRange", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
                setRange?.Invoke(asd, null);
            }
            catch { }

            refresh?.Invoke(asd, null);

            MMI.RequestRedraw();

            try
            {
                asd.Invalidate(true);
                asd.Refresh();
            }
            catch { }
        }

        private void AttachRangeRestoreOnChange(Control asd, double targetRange, string actualAsdType)
        {
            try
            {
                var eventField = asd.GetType().GetField("RangeChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (eventField == null) return;

                EventHandler handler = null;
                handler = (s, e) =>
                {
                    try
                    {
                        var current = eventField.GetValue(asd) as EventHandler;
                        if (current != null)
                        {
                            current -= handler;
                            eventField.SetValue(asd, current);
                        }

                        ApplyRangeToControl(asd, targetRange, actualAsdType);
                    }
                    catch { }
                };

                var existing = eventField.GetValue(asd) as EventHandler;
                existing += handler;
                eventField.SetValue(asd, existing);
            }
            catch
            {
                // ignore
            }
        }

        private List<string> GetCheckedMaps(Control asdControl)
        {
            try
            {
                var menuField = asdControl.FindForm()?.GetType().GetField("mapsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var menu = menuField?.GetValue(asdControl.FindForm()) as ToolStripMenuItem;
                if (menu == null) return null;

                var list = new List<string>();
                CollectCheckedMenuItems(menu.DropDownItems, list);
                return list;
            }
            catch
            {
                return null;
            }
        }

        private void CollectCheckedMenuItems(ToolStripItemCollection items, List<string> collector)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem mi)
                {
                    var key = GetMenuItemKey(mi);
                    if (mi.Checked && !string.IsNullOrEmpty(key))
                    {
                        collector.Add(key);
                    }

                    if (mi.HasDropDownItems)
                    {
                        CollectCheckedMenuItems(mi.DropDownItems, collector);
                    }
                }
            }
        }

        private string GetMenuItemKey(ToolStripMenuItem item)
        {
            if (item.Tag != null)
            {
                var nameProp = item.Tag.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                var nameField = item.Tag.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public);
                var name = nameProp?.GetValue(item.Tag) as string ?? nameField?.GetValue(item.Tag) as string;
                if (!string.IsNullOrWhiteSpace(name)) return name;

                var tagText = item.Tag.ToString();
                if (!string.IsNullOrWhiteSpace(tagText)) return tagText;
            }

            return item.Text;
        }

        private void ApplyCheckedMaps(Form form, string mapsValue)
        {
            if (string.IsNullOrWhiteSpace(mapsValue)) return;

            var desired = new HashSet<string>(mapsValue.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            if (desired.Count == 0) return;

            try
            {
                var menuField = form.GetType().GetField("mapsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var menu = menuField?.GetValue(form) as ToolStripMenuItem;
                if (menu == null) return;

                ApplyMapState(menu.DropDownItems, desired);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply maps failed: {ex}");
            }
        }

        private void ApplyMapState(ToolStripItemCollection items, HashSet<string> desired)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem mi)
                {
                    var key = GetMenuItemKey(mi);
                    var shouldBeChecked = !string.IsNullOrWhiteSpace(key) && desired.Contains(key);
                    if (mi.Checked != shouldBeChecked)
                    {
                        mi.PerformClick();
                    }

                    if (mi.HasDropDownItems)
                    {
                        ApplyMapState(mi.DropDownItems, desired);
                    }
                }
            }
        }

        private Form FindWindow(string typeName, Func<Form, bool> predicate)
        {
            return Application.OpenForms.Cast<Form>()
                .FirstOrDefault(f => string.Equals(f.GetType().FullName, typeName, StringComparison.Ordinal) && predicate(f));
        }

        private void ApplyPlacement(Form form, User32.WINDOWPLACEMENT placement)
        {
            try
            {
                if (!form.IsHandleCreated)
                {
                    var _ = form.Handle;
                }

                User32.SetWindowPlacement(form.Handle, placement);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply placement failed for {form?.Name}: {ex}");
            }
        }

        private void BringToFrontSafe(Form form)
        {
            try
            {
                if (form == null || form.IsDisposed) return;
                if (!form.Visible) form.Show();
                form.Activate();
                form.BringToFront();
            }
            catch
            {
                // ignore
            }
        }

        private void EnsureZOrder(Form form)
        {
            try
            {
                if (IsMainVatSysForm(form)) return;
                if (form == null || form.IsDisposed) return;
                if (managedZOrder.Contains(form))
                {
                    form.TopMost = true;
                    form.BringToFront();
                    return;
                }

                managedZOrder.Add(form);

                var main = Application.OpenForms.Cast<Form>().FirstOrDefault(f => string.Equals(f.Name, "MainForm", StringComparison.OrdinalIgnoreCase));
                if (main != null && form.Owner == null && !ReferenceEquals(form, main))
                {
                    try { form.Owner = main; } catch { }
                }

                form.TopMost = true;
                form.BringToFront();

                form.Activated += (s, e) =>
                {
                    try
                    {
                        form.TopMost = true;
                        form.BringToFront();
                    }
                    catch { }
                };

                form.Deactivate += (s, e) =>
                {
                    try
                    {
                        form.TopMost = false;
                    }
                    catch { }
                };

                form.FormClosed += (s, e) =>
                {
                    managedZOrder.Remove(form);
                };
            }
            catch
            {
                // ignore
            }
        }

        private bool IsMainVatSysForm(Form form)
        {
            if (form == null) return false;
            var typeName = form.GetType().FullName ?? string.Empty;
            if (string.Equals(typeName, "vatsys.MainForm", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(form.Name, "MainForm", StringComparison.OrdinalIgnoreCase)) return true;

            var main = Application.OpenForms.Cast<Form>().FirstOrDefault();
            return ReferenceEquals(form, main);
        }

        private void SetBaseFormPlacement(string name, User32.WINDOWPLACEMENT placement)
        {
            try
            {
                var field = typeof(MMI).GetField("BaseFormPlacements", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) return;

                var dict = field.GetValue(null) as Dictionary<string, User32.WINDOWPLACEMENT>;
                if (dict == null)
                {
                    dict = new Dictionary<string, User32.WINDOWPLACEMENT>(StringComparer.Ordinal);
                    field.SetValue(null, dict);
                }

                dict[name] = placement;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to set BaseFormPlacements: {ex}");
            }
        }

        private AsdState GetAsdState()
        {
            try
            {
                var mmiType = typeof(MMI);
                var centerField = mmiType.GetField("mainASDCentre", BindingFlags.Static | BindingFlags.NonPublic);
                var rangeField = mmiType.GetField("MAIN_ASD_RANGE", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                var coord = centerField?.GetValue(null);
                if (coord == null || rangeField == null) return null;

                var lat = GetCoordinateValue(coord, "get_Latitude");
                var lon = GetCoordinateValue(coord, "get_Longitude");
                if (lat == null || lon == null) return null;

                var rangeVal = rangeField.GetValue(null);
                var range = rangeVal is int i ? i : Convert.ToInt32(rangeVal);

                return new AsdState
                {
                    LatitudeDeg = lat.Value,
                    LongitudeDeg = lon.Value,
                    Range = range
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to get ASD state: {ex}");
                return null;
            }
        }

        private void ApplyAsdState(AsdState asd)
        {
            if (asd == null) return;

            try
            {
                var mmiType = typeof(MMI);
                var centerField = mmiType.GetField("mainASDCentre", BindingFlags.Static | BindingFlags.NonPublic);
                var rangeField = mmiType.GetField("MAIN_ASD_RANGE", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                var coordType = typeof(MMI).Assembly.GetType("vatsys.Coordinate");
                if (centerField == null || rangeField == null || coordType == null) return;

                var coord = Activator.CreateInstance(coordType, new object[] { asd.LatitudeDeg, asd.LongitudeDeg });
                centerField.SetValue(null, coord);
                rangeField.SetValue(null, asd.Range);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to apply ASD state: {ex}");
            }
        }

        private double? GetCoordinateValue(object coord, string methodName)
        {
            try
            {
                var method = coord.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var val = method?.Invoke(coord, null);
                if (val is double d) return d;
                return Convert.ToDouble(val);
            }
            catch
            {
                return null;
            }
        }

        private void LoadAutoLoadMap()
        {
            try
            {
                var path = AutoLoadConfigPath();
                if (!File.Exists(path)) return;

                var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (map == null) return;

                autoLoadLayouts.Clear();
                foreach (var kv in map)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        autoLoadLayouts[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to load autoload map: {ex}");
            }
        }

        private void SaveAutoLoadMap()
        {
            try
            {
                File.WriteAllText(AutoLoadConfigPath(), JsonConvert.SerializeObject(autoLoadLayouts, Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to save autoload map: {ex}");
            }
        }

        private bool IsAutoLoad(string position, string layoutName)
        {
            return autoLoadLayouts.TryGetValue(position, out var value) &&
                   string.Equals(value, layoutName, StringComparison.OrdinalIgnoreCase);
        }

        private void SetAutoLoad(string position, string layoutName, bool enabled)
        {
            if (enabled)
            {
                autoLoadLayouts[position] = layoutName;
            }
            else
            {
                if (autoLoadLayouts.ContainsKey(position))
                {
                    autoLoadLayouts.Remove(position);
                }
            }

            SaveAutoLoadMap();
        }

        private List<LayoutInfo> LoadAllLayouts()
        {
            var list = new List<LayoutInfo>();
            if (!Directory.Exists(LayoutRoot())) return list;

            foreach (var file in Directory.GetFiles(LayoutRoot(), "*.layout.json"))
            {
                try
                {
                    var snapshot = JsonConvert.DeserializeObject<LayoutSnapshot>(File.ReadAllText(file));
                    if (snapshot == null) continue;

                    var position = string.IsNullOrWhiteSpace(snapshot.Position) ? "DEFAULT" : snapshot.Position;
                    var layoutName = !string.IsNullOrWhiteSpace(snapshot.LayoutName)
                        ? snapshot.LayoutName
                        : InferLayoutNameFromFile(file, position);

                    list.Add(new LayoutInfo
                    {
                        Position = position,
                        LayoutName = layoutName,
                        Path = file
                    });
                }
                catch
                {
                    // ignore bad layout files
                }
            }

            return list;
        }

        private string InferLayoutNameFromFile(string file, string position)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var safePos = SanitizeForFile(position, "DEFAULT");
            var prefix = safePos + "__";

            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(prefix.Length);
            }

            if (string.Equals(fileName, safePos, StringComparison.OrdinalIgnoreCase))
            {
                return position;
            }

            return fileName;
        }

        private string PromptForLayoutName(string defaultName)
        {
            string result = null;

            void ShowDialog()
            {
                using (var form = new Form())
                using (var text = new TextBox())
                using (var label = new Label())
                using (var ok = new Button())
                using (var cancel = new Button())
                {
                    form.Text = "Save layout";
                    form.FormBorderStyle = FormBorderStyle.FixedDialog;
                    form.StartPosition = FormStartPosition.CenterParent;
                    form.MinimizeBox = false;
                    form.MaximizeBox = false;
                    form.ClientSize = new Size(300, 120);

                    label.AutoSize = true;
                    label.Text = "Layout name:";
                    label.Left = 10;
                    label.Top = 10;

                    text.Left = 10;
                    text.Top = 30;
                    text.Width = 270;
                    text.Text = defaultName ?? string.Empty;

                    ok.Text = "Save";
                    ok.DialogResult = DialogResult.OK;
                    ok.Left = 125;
                    ok.Top = 70;
                    ok.Width = 70;

                    cancel.Text = "Cancel";
                    cancel.DialogResult = DialogResult.Cancel;
                    cancel.Left = 210;
                    cancel.Top = 70;
                    cancel.Width = 70;

                    form.Controls.Add(label);
                    form.Controls.Add(text);
                    form.Controls.Add(ok);
                    form.Controls.Add(cancel);
                    form.AcceptButton = ok;
                    form.CancelButton = cancel;

                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        result = text.Text.Trim();
                    }
                }
            }

            RunOnUiThread(ShowDialog);
            return result;
        }

        private void DeleteLayout(LayoutInfo layout, string position)
        {
            try
            {
                var confirm = MessageBox.Show(
                    $"Delete layout \"{layout.LayoutName}\" for position \"{position}\"?",
                    "Delete layout",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes) return;

                if (File.Exists(layout.Path))
                {
                    File.Delete(layout.Path);
                }

                if (IsAutoLoad(position, layout.LayoutName))
                {
                    SetAutoLoad(position, layout.LayoutName, false);
                }

                BuildLoadMenuItems();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete layout:\n{ex.Message}", "Delete layout", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal class LayoutSnapshot
    {
        public string Position { get; set; }
        public string LayoutName { get; set; }
        public DateTime SavedUtc { get; set; }
        public List<WindowLayoutEntry> Windows { get; set; }
        public AsdState Asd { get; set; }
    }

    internal class AsdState
    {
        public double LatitudeDeg { get; set; }
        public double LongitudeDeg { get; set; }
        public int Range { get; set; }
    }

    internal class WindowLayoutEntry
    {
        public string TypeName { get; set; }
        public string FormName { get; set; }
        public string Title { get; set; }
        public WindowPlacementDto Placement { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal class WindowPlacementDto
    {
        public int ShowCmd { get; set; }
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        public static WindowPlacementDto From(User32.WINDOWPLACEMENT placement)
        {
            return new WindowPlacementDto
            {
                ShowCmd = (int)placement.showCmd,
                MinX = placement.ptMinPosition.x,
                MinY = placement.ptMinPosition.y,
                MaxX = placement.ptMaxPosition.x,
                MaxY = placement.ptMaxPosition.y,
                Left = placement.rcNormalPosition.left,
                Top = placement.rcNormalPosition.top,
                Right = placement.rcNormalPosition.right,
                Bottom = placement.rcNormalPosition.bottom
            };
        }

        public User32.WINDOWPLACEMENT ToWindowPlacement()
        {
            return new User32.WINDOWPLACEMENT
            {
                length = System.Runtime.InteropServices.Marshal.SizeOf<User32.WINDOWPLACEMENT>(),
                showCmd = (User32.WindowShowStyle)ShowCmd,
                ptMinPosition = new POINT { x = MinX, y = MinY },
                ptMaxPosition = new POINT { x = MaxX, y = MaxY },
                rcNormalPosition = new RECT
                {
                    left = Left,
                    top = Top,
                    right = Right,
                    bottom = Bottom
                }
            };
        }
    }

    internal class LayoutInfo
    {
        public string Position { get; set; }
        public string LayoutName { get; set; }
        public string Path { get; set; }
    }
}


