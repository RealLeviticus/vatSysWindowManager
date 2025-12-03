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

            rootMenuItem = new ToolStripMenuItem("Window Layouts");
            rootMenuItem.DropDownItems.Add(saveLayoutMenuItem);
            rootMenuItem.DropDownItems.Add(loadLayoutMenuItem);

            LoadAutoLoadMap();

            _ = Task.Run(async () =>
            {
                var ready = await EnsureUiReady();
                if (!ready) return;
                RunOnUiThread(AddMenuItem);
                await Task.Delay(1500);
                RunOnUiThread(() => RestoreLayoutForCurrentPosition(requireAutoLoad: true));
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

                primePositionChangedHandler = (s, e) => RunOnUiThread(() => RestoreLayoutForCurrentPosition(requireAutoLoad: true));
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

            var currentPosition = GetPositionKey();
            var layouts = LoadAllLayouts()
                .Where(l => string.Equals(l.Position, currentPosition, StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.LayoutName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (layouts.Count == 0)
            {
                loadLayoutMenuItem.DropDownItems.Add(new ToolStripMenuItem("(no saved layouts for this position)") { Enabled = false });
                return;
            }

            foreach (var layout in layouts)
            {
                var layoutItem = new ToolStripMenuItem(layout.LayoutName)
                {
                    Checked = IsAutoLoad(currentPosition, layout.LayoutName)
                };

                var loadItem = new ToolStripMenuItem("Load");
                loadItem.Click += (s, e) => RestoreLayoutFromFile(layout.Path);

                var autoItem = new ToolStripMenuItem("Auto load for this position")
                {
                    CheckOnClick = true,
                    Checked = IsAutoLoad(currentPosition, layout.LayoutName)
                };
                autoItem.Click += (s, e) =>
                {
                    SetAutoLoad(currentPosition, layout.LayoutName, autoItem.Checked);
                    BuildLoadMenuItems();
                };

                var deleteItem = new ToolStripMenuItem("Delete");
                deleteItem.Click += (s, e) => DeleteLayout(layout, currentPosition);

                layoutItem.DropDownItems.Add(loadItem);
                layoutItem.DropDownItems.Add(new ToolStripSeparator());
                layoutItem.DropDownItems.Add(autoItem);
                layoutItem.DropDownItems.Add(new ToolStripSeparator());
                layoutItem.DropDownItems.Add(deleteItem);
                loadLayoutMenuItem.DropDownItems.Add(layoutItem);
            }
        }

        private void BuildPositionMenu(Form form)
        {
            try
            {
                var method = form.GetType().GetMethod("PositionsToolStripMenuItem_DropDownOpened", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ??
                             form.GetType().GetMethod("positionsToolStripMenuItem_DropDownOpened", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
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
                    Asd = GetAsdState(),
                    ControlledSectors = GetControlledSectorNames()
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

        private void RestoreLayoutForCurrentPosition(bool requireAutoLoad = false)
        {
            try
            {
                ClosePluginWindows();

                var position = GetPositionKey();
                var path = ResolveLayoutPathForPosition(position, requireAutoLoad);
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
                if (snapshot == null) return;
                if (!string.Equals(snapshot.Position, GetPositionKey(), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"Layout \"{snapshot.LayoutName}\" was saved for position \"{snapshot.Position}\" and cannot be loaded while you are on \"{GetPositionKey()}\".", "vatSys Window Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                CloseWindowsNotInSnapshot(snapshot);
                ApplyAsdState(snapshot.Asd);
                ApplyControlledSectors(snapshot.ControlledSectors);

                if (snapshot.Windows == null || snapshot.Windows.Count == 0) return;

                // Restore windows
                foreach (var entry in snapshot.Windows)
                {
                    TryRestoreWindow(entry);
                }

                // Reapply special placements after everything is up to ensure late-opening windows are aligned.
                EnforceSpecialPlacements(snapshot);
                CloseOzStripsIfNotInSnapshot(snapshot);
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

            try
            {
                if (User32.GetWindowRect(form.Handle, out var rect))
                {
                    entry.Metadata["ActualLeft"] = rect.left.ToString(CultureInfo.InvariantCulture);
                    entry.Metadata["ActualTop"] = rect.top.ToString(CultureInfo.InvariantCulture);
                    entry.Metadata["ActualRight"] = rect.right.ToString(CultureInfo.InvariantCulture);
                    entry.Metadata["ActualBottom"] = rect.bottom.ToString(CultureInfo.InvariantCulture);
                }

                // Track maximized/normal state explicitly.
                if (form.WindowState == FormWindowState.Maximized)
                {
                    entry.Metadata["WindowState"] = "Maximized";
                }
            }
            catch { }

            return entry;
        }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: could not capture form {form?.Name}: {ex}");
                return null;
            }
        }

        private string ResolveLayoutPathForPosition(string position, bool onlyAutoLoad)
        {
            var layouts = LoadAllLayouts().Where(l => string.Equals(l.Position, position, StringComparison.OrdinalIgnoreCase)).ToList();

            if (autoLoadLayouts.TryGetValue(position, out var autoLayoutName))
            {
                var match = layouts.FirstOrDefault(l => string.Equals(l.LayoutName, autoLayoutName, StringComparison.OrdinalIgnoreCase));
                if (match != null && File.Exists(match.Path)) return match.Path;
            }

            if (onlyAutoLoad)
            {
                return null;
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
                var displayPos = GetDisplayPositionInfo(asd);
                if (displayPos != null)
                {
                    if (!string.IsNullOrWhiteSpace(displayPos.Name))
                    {
                        meta["DisplayPosition"] = displayPos.Name;
                    }

                    if (!string.IsNullOrWhiteSpace(displayPos.Callsign))
                    {
                        meta["DisplayPositionCallsign"] = displayPos.Callsign;
                    }

                    if (!string.IsNullOrWhiteSpace(displayPos.FullName))
                    {
                        meta["DisplayPositionFullName"] = displayPos.FullName;
                    }
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

                var centre = GetAsdCentre(asd);
                if (centre != null)
                {
                    meta["CentreLat"] = centre.Value.Latitude.ToString(CultureInfo.InvariantCulture);
                    meta["CentreLon"] = centre.Value.Longitude.ToString(CultureInfo.InvariantCulture);
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

            if (window == null && IsOzStrips(entry))
            {
                var main = Application.OpenForms.Cast<Form>().FirstOrDefault(f => string.Equals(f.Name, "MainForm", StringComparison.OrdinalIgnoreCase)) ??
                           Application.OpenForms.Cast<Form>().FirstOrDefault();
                if (main != null && TryClickOzStripsMenu(main))
                {
                    window = FindOzStripsByTitle();
                }
            }

            if (window != null && placement != null)
            {
                ApplyPlacement(window, placement.Value, entry.Metadata);
                if (IsVsCs(entry))
                {
                    EnsureVsCsPlacement(window, placement.Value, entry.Metadata);
                }
                else if (IsOzStrips(entry))
                {
                    EnsureOzStripsPlacement(window, placement.Value, entry.Metadata);
                }
            }
            else if (IsOzStrips(entry))
            {
                // If OzStrips opens slightly later, watch for it and apply placement once available.
                EnsureOzStripsPlacementWhenAvailable(entry);
            }

            if (window != null)
            {
                entry.Metadata.TryGetValue("AsdType", out var asdType);

                var displayName = entry.Metadata.TryGetValue("DisplayPosition", out var displayPosition) ? displayPosition : null;
                var displayCallsign = entry.Metadata.TryGetValue("DisplayPositionCallsign", out var displayCallsignMeta) ? displayCallsignMeta : null;
                var displayFull = entry.Metadata.TryGetValue("DisplayPositionFullName", out var displayFullMeta) ? displayFullMeta : null;
                ApplyDisplayPosition(window, displayName, displayCallsign, displayFull, asdType);

                if (entry.Metadata.TryGetValue("CentreLat", out var centreLat) &&
                    entry.Metadata.TryGetValue("CentreLon", out var centreLon))
                {
                    ApplyCentre(window, centreLat, centreLon, asdType);
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
            var forms = Application.OpenForms.Cast<Form>().ToList();

            if (IsVsCs(entry))
            {
                var vscs = GetVsCsWindow();
                if (vscs != null) return vscs;
            }

            if (IsOzStrips(entry))
            {
                var oz = forms.FirstOrDefault(f => !f.IsDisposed && (f.Text ?? string.Empty).IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0);
                if (oz != null) return oz;
            }

            var exact = forms.FirstOrDefault(f =>
                string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.Ordinal) &&
                string.Equals(f.Name, entry.FormName, StringComparison.Ordinal));
            if (exact != null) return exact;

            var byTitle = forms.FirstOrDefault(f =>
                string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.Ordinal) &&
                string.Equals(f.Text, entry.Title, StringComparison.Ordinal));
            if (byTitle != null) return byTitle;

            var typeMatches = forms.Where(f => string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.Ordinal)).ToList();
            if (typeMatches.Count == 1) return typeMatches[0];

            return null;
        }

        private string BuildWindowKey(string typeName, string formName)
        {
            return $"{typeName ?? string.Empty}|{formName ?? string.Empty}";
        }

        private void CloseWindowsNotInSnapshot(LayoutSnapshot snapshot)
        {
            if (snapshot?.Windows == null) return;

            try
            {
                var allowed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in snapshot.Windows)
                {
                    allowed.Add(BuildWindowKey(entry.TypeName, entry.FormName));
                }

                var ozStripsInLayout = snapshot.Windows.Any(w =>
                    (!string.IsNullOrWhiteSpace(w.TypeName) && w.TypeName.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(w.Title) && w.Title.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0));

                var toClose = new List<Form>();
                foreach (Form form in Application.OpenForms)
                {
                    if (form == null || form.IsDisposed) continue;
                    if (IsMainVatSysForm(form)) continue;

                    var key = BuildWindowKey(form.GetType().FullName, form.Name);
                    if (!allowed.Contains(key))
                    {
                        // If OzStrips isn't part of this layout, close it explicitly.
                        if (!ozStripsInLayout &&
                            ((form.GetType().FullName ?? string.Empty).IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (!string.IsNullOrWhiteSpace(form.Text) && form.Text.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0)))
                        {
                            toClose.Add(form);
                            continue;
                        }

                        toClose.Add(form);
                    }
                }

                foreach (var form in toClose)
                {
                    try
                    {
                        if (form != null && !form.IsDisposed) form.Close();
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to close extra windows: {ex}");
            }
        }

        private Form CreateWindow(WindowLayoutEntry entry)
        {
            try
            {
                if (IsVsCs(entry))
                {
                    var vscs = GetVsCsWindow();
                    if (vscs != null)
                    {
                        vscs.Show();
                        TrackPluginWindow(vscs);
                        return vscs;
                    }
                }

                var resolvedType = Type.GetType(entry.TypeName) ??
                                   AppDomain.CurrentDomain.GetAssemblies()
                                       .Select(a => a.GetType(entry.TypeName, false))
                                       .FirstOrDefault(t => t != null);

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

                var type = resolvedType ?? typeof(MMI).Assembly.GetType(entry.TypeName);
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

                // Fallback: try to activate via main menu item text (helps plugin menu items like OzStrips)
                var main = Application.OpenForms.Cast<Form>().FirstOrDefault(f => string.Equals(f.Name, "MainForm", StringComparison.OrdinalIgnoreCase)) ??
                           Application.OpenForms.Cast<Form>().FirstOrDefault();
                if (main != null)
                {
                    // Special-case OzStrips: trigger the Windows > OzStrips menu item explicitly.
                    if (IsOzStrips(entry))
                    {
                        if (TryClickOzStripsMenu(main))
                        {
                            for (var i = 0; i < 12; i++)
                            {
                                var oz = FindOzStripsByTitle();
                                if (oz != null)
                                {
                                    TrackPluginWindow(oz);
                                    return oz;
                                }
                                System.Threading.Thread.Sleep(150);
                            }
                        }
                    }

                    var candidates = BuildMenuCandidateTexts(entry).ToArray();

                    foreach (var text in candidates)
                    {
                        if (!ClickMenuItemByText(main, text)) continue;

                        // Poll briefly for the window to appear
                        for (var i = 0; i < 6; i++)
                        {
                            var opened = FindWindow(entry.TypeName ?? string.Empty, f =>
                                string.Equals(f.Text, entry.Title, StringComparison.OrdinalIgnoreCase) ||
                                f.Text?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (opened != null)
                            {
                                TrackPluginWindow(opened);
                                return opened;
                            }

                            System.Threading.Thread.Sleep(200);
                        }

                        // If OzStrips menu entry opened a non-typed window, grab by title
                        if (string.Equals(text, "OzStrips", StringComparison.OrdinalIgnoreCase))
                        {
                            var oz = FindOzStripsByTitle();
                            if (oz != null)
                            {
                                TrackPluginWindow(oz);
                                return oz;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: create window failed for {entry.TypeName}: {ex}");
            }

            return null;
        }

        private bool ClickMenuItemByText(Form form, string text, bool matchExact = false)
        {
            if (form == null || string.IsNullOrWhiteSpace(text)) return false;

            try
            {
                var menus = new List<ToolStripMenuItem>();

                var menuFields = form.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(f => typeof(MenuStrip).IsAssignableFrom(f.FieldType))
                    .ToList();

                foreach (var field in menuFields)
                {
                    var ms = field.GetValue(form) as MenuStrip;
                    if (ms?.Items != null)
                    {
                        menus.AddRange(ms.Items.OfType<ToolStripMenuItem>());
                    }
                }

                if (menus.Count == 0 && form.MainMenuStrip?.Items != null)
                {
                    menus.AddRange(form.MainMenuStrip.Items.OfType<ToolStripMenuItem>());
                }

                if (menus.Count == 0)
                {
                    menus.AddRange(form.Controls.OfType<MenuStrip>().SelectMany(ms => ms.Items.OfType<ToolStripMenuItem>()));
                }

                if (menus.Count == 0) return false;

                return ClickMenuItemRecursive(menus, text, matchExact);
            }
            catch
            {
                return false;
            }
        }

        private bool ClickMenuItemRecursive(IEnumerable<ToolStripMenuItem> items, string text, bool matchExact = false)
        {
            string Normalize(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                return s.Replace("&", string.Empty).Trim();
            }

            var target = Normalize(text);
            if (string.IsNullOrWhiteSpace(target)) return false;

            foreach (var item in items)
            {
                var itemText = Normalize(item.Text);
                var tagText = Normalize(item.Tag as string);

                var equal = string.Equals(itemText, target, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(tagText, target, StringComparison.OrdinalIgnoreCase);
                var contains = itemText.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;

                if ((matchExact && equal) || (!matchExact && (equal || contains)))
                {
                    item.PerformClick();
                    return true;
                }

                if (item.HasDropDownItems)
                {
                    if (ClickMenuItemRecursive(item.DropDownItems.OfType<ToolStripMenuItem>(), text, matchExact))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private IEnumerable<string> BuildMenuCandidateTexts(WindowLayoutEntry entry)
        {
            var list = new List<string>();
            void Add(string s)
            {
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }

            Add(entry.Title);
            if (!string.IsNullOrWhiteSpace(entry.Title))
            {
                var firstToken = entry.Title.Split(new[] { ':', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                Add(firstToken);
            }
            Add(entry.FormName);
            if (!string.IsNullOrWhiteSpace(entry.TypeName))
            {
                Add(entry.TypeName.Split('.').LastOrDefault());
            }
            Add("VSCS"); // ensure VSCS menu text is considered
            if (IsOzStrips(entry)) Add("OzStrips");

            return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private bool IsOzStrips(WindowLayoutEntry entry)
        {
            return (!string.IsNullOrWhiteSpace(entry.TypeName) && entry.TypeName.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!string.IsNullOrWhiteSpace(entry.Title) && entry.Title.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool IsVsCs(WindowLayoutEntry entry)
        {
            return (!string.IsNullOrWhiteSpace(entry.TypeName) && entry.TypeName.IndexOf("VSCSWindow", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!string.IsNullOrWhiteSpace(entry.Title) && entry.Title.IndexOf("VSCS", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private Form GetVsCsWindow()
        {
            try
            {
                var field = typeof(MMI).GetField("VSCSWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(null) as Form;
            }
            catch
            {
                return null;
            }
        }

        private bool IsVsCs(Form form)
        {
            if (form == null) return false;
            var type = form.GetType().FullName ?? string.Empty;
            if (type.IndexOf("VSCSWindow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var title = form.Text ?? string.Empty;
            return title.IndexOf("VSCS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsOzStrips(Form form)
        {
            if (form == null) return false;
            var type = form.GetType().FullName ?? string.Empty;
            if (type.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var title = form.Text ?? string.Empty;
            return title.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Form FindOzStripsByTitle()
        {
            return Application.OpenForms.Cast<Form>()
                .FirstOrDefault(f => f != null && !f.IsDisposed &&
                                     (f.Text ?? string.Empty).IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void EnforceSpecialPlacements(LayoutSnapshot snapshot)
        {
            if (snapshot?.Windows == null) return;

            foreach (var entry in snapshot.Windows)
            {
                var placement = entry.Placement?.ToWindowPlacement();
                if (placement == null) continue;

                if (IsVsCs(entry))
                {
                    var vs = GetVsCsWindow() ?? FindExisting(entry);
                    if (vs != null)
                    {
                        EnsureVsCsPlacement(vs, placement.Value, entry.Metadata);
                    }
                }
                else if (IsOzStrips(entry))
                {
                    var oz = FindOzStripsByTitle() ?? FindExisting(entry);
                    if (oz != null)
                    {
                        EnsureOzStripsPlacement(oz, placement.Value, entry.Metadata);
                    }
                    else
                    {
                        EnsureOzStripsPlacementWhenAvailable(entry);
                    }
                }
            }
        }

        private void CloseOzStripsIfNotInSnapshot(LayoutSnapshot snapshot)
        {
            if (snapshot?.Windows == null) return;
            var shouldBeOpen = snapshot.Windows.Any(IsOzStrips);
            if (shouldBeOpen) return;

            void TryClose()
            {
                try
                {
                    var oz = FindOzStripsByTitle();
                    if (oz != null && !oz.IsDisposed) oz.Close();
                }
                catch
                {
                    // ignore
                }
            }

            SchedulePlacementRetries(Application.OpenForms.Cast<Form>().FirstOrDefault(), TryClose, 200, 600, 1200);
        }

        private bool TryClickOzStripsMenu(Form mainForm)
        {
            ToolStripMenuItem windowsMenu = null;
            try
            {
                var windowsMenuField = mainForm.GetType().GetField("windowsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                windowsMenu = windowsMenuField?.GetValue(mainForm) as ToolStripMenuItem;
                if (windowsMenu == null) return false;

                try
                {
                    windowsMenu.PerformClick();
                    Application.DoEvents();

                    if (!windowsMenu.DropDown.Visible)
                    {
                        windowsMenu.ShowDropDown();
                        Application.DoEvents();
                    }
                }
                catch
                {
                    // ignore, continue to search items
                }

                foreach (ToolStripMenuItem item in windowsMenu.DropDownItems)
                {
                    var text = item.Text?.Replace("&", string.Empty);
                    if (!string.IsNullOrWhiteSpace(text) && text.IndexOf("OzStrips", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        item.PerformClick();
                        return true;
                    }
                }

                if (ClickMenuItemRecursive(new[] { windowsMenu }, "OzStrips"))
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try
                {
                    windowsMenu?.HideDropDown();
                }
                catch
                {
                    // ignore
                }
            }

            return false;
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

        private DisplayPositionInfo GetDisplayPositionInfo(Control asdControl)
        {
            try
            {
                var prop = asdControl.GetType().GetProperty("DisplayPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var pos = prop?.GetValue(asdControl);
                if (pos == null) return null;

                var name = pos.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pos) as string;
                var callsign = pos.GetType().GetField("Callsign", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pos) as string;
                var fullName = pos.GetType().GetField("FullName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pos) as string;

                return new DisplayPositionInfo
                {
                    Name = name,
                    Callsign = callsign,
                    FullName = fullName
                };
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

        private (double Latitude, double Longitude)? GetAsdCentre(Control asdControl)
        {
            try
            {
                var renderCentre = GetRenderCentre(asdControl);
                if (renderCentre != null) return renderCentre;

                var candidates = new[]
                {
                    asdControl.GetType().GetField("StoredCentreLL", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(asdControl),
                    asdControl.GetType().GetField("settingVisCenter", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(asdControl)
                };

                foreach (var coord in candidates)
                {
                    var parsed = GetCoordinateValues(coord);
                    if (parsed != null) return parsed;
                }

                var mainCentreField = typeof(MMI).GetField("mainASDCentre", BindingFlags.Static | BindingFlags.NonPublic);
                var mainCoord = mainCentreField?.GetValue(null);
                return GetCoordinateValues(mainCoord);
            }
            catch
            {
                return null;
            }
        }

        private (double Latitude, double Longitude)? GetRenderCentre(Control asdControl)
        {
            try
            {
                var getRender = asdControl.GetType().GetMethod("GetRenderParams", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                var render = getRender?.Invoke(asdControl, new object[] { false });
                if (render == null) return null;

                var screenCentreProp = render.GetType().GetProperty("ScreenCentre", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var screenCentre = screenCentreProp?.GetValue(render);
                return GetCoordinateValues(screenCentre);
            }
            catch
            {
                return null;
            }
        }

        private void ApplyDisplayPosition(Form form, string positionName, string positionCallsign, string positionFullName, string expectedAsdType, int remainingRetries = 1)
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

                var targetPosition = FindLogicalPosition(positionName, positionCallsign, positionFullName);

                var applied = false;

                if (targetPosition != null && TryInvokeLoadPosition(asd, targetPosition))
                {
                    applied = true;
                }
                else if (targetPosition != null)
                {
                    var field = asd.GetType().GetField("displayPosition", BindingFlags.Instance | BindingFlags.NonPublic);
                    field?.SetValue(asd, targetPosition);
                    var prop = asd.GetType().GetProperty("DisplayPosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    prop?.SetValue(asd, targetPosition, null);
                    applied = true;
                }

                if (!applied && remainingRetries > 0)
                {
                    var delay = remainingRetries == 1 ? 600 : 250;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(delay);
                        RunOnUiThread(() =>
                        {
                            if (form != null && !form.IsDisposed)
                            {
                                ApplyDisplayPosition(form, positionName, positionCallsign, positionFullName, expectedAsdType, remainingRetries - 1);
                            }
                        });
                    });
                }
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

        private object FindLogicalPosition(string name, string callsign, string fullName)
        {
            var positionsType = typeof(MMI).Assembly.GetType("vatsys.LogicalPositions");
            var positionsProp = positionsType?.GetProperty("Positions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var list = positionsProp?.GetValue(null) as System.Collections.IEnumerable;
            if (list == null) return null;

            foreach (var pos in list)
            {
                var posName = pos.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pos) as string;
                var posCallsign = pos.GetType().GetField("Callsign", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pos) as string;
                var posFull = pos.GetType().GetField("FullName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pos) as string;

                if (!string.IsNullOrWhiteSpace(name) && string.Equals(posName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pos;
                }

                if (!string.IsNullOrWhiteSpace(fullName) && string.Equals(posFull, fullName, StringComparison.OrdinalIgnoreCase))
                {
                    return pos;
                }

                if (!string.IsNullOrWhiteSpace(callsign) && string.Equals(posCallsign, callsign, StringComparison.OrdinalIgnoreCase))
                {
                    return pos;
                }
            }

            return null;
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

        private bool TryClickPositionMenuByName(Form form, params string[] names)
        {
            try
            {
                var menuField = form.GetType().GetField("positionsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (menuField == null) return false;
                var menu = menuField.GetValue(form) as ToolStripMenuItem;
                if (menu == null) return false;

                var candidates = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                if (candidates.Count == 0) return false;

                return ClickPositionMenuByTag(menu.DropDownItems.OfType<ToolStripMenuItem>(), candidates) ||
                       ClickMenuItemRecursive(menu.DropDownItems.OfType<ToolStripMenuItem>(), candidates.First(), matchExact: true) ||
                       candidates.Any(c => ClickMenuItemRecursive(menu.DropDownItems.OfType<ToolStripMenuItem>(), c, matchExact: false));
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private bool ClickPositionMenuByTag(IEnumerable<ToolStripMenuItem> items, string targetName)
        {
            return ClickPositionMenuByTag(items, new[] { targetName });
        }

        private bool ClickPositionMenuByTag(IEnumerable<ToolStripMenuItem> items, IEnumerable<string> targetNames)
        {
            var targets = targetNames?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (targets == null || targets.Count == 0) return false;

            foreach (var item in items)
            {
                if (targets.Any(t => MatchesPositionTag(item.Tag, t)))
                {
                    item.PerformClick();
                    return true;
                }

                if (item.HasDropDownItems)
                {
                    if (ClickPositionMenuByTag(item.DropDownItems.OfType<ToolStripMenuItem>(), targets))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool MatchesPositionTag(object tag, string targetName)
        {
            if (tag == null || string.IsNullOrWhiteSpace(targetName)) return false;

            try
            {
                var name = GetStringField(tag, "Name");
                var full = GetStringField(tag, "FullName");
                var callsign = GetStringField(tag, "Callsign");

                return string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(full, targetName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(callsign, targetName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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

        private void ApplyRange(Form form, string rangeValue, string expectedAsdType, int remainingRetries = 3)
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

                if (!ApplyRangeWithZoom(asd, r))
                {
                    AttachRangeRestoreOnChange(asd, r, actualAsdType);
                    ApplyRangeToControl(asd, r, actualAsdType);
                }

                if (remainingRetries > 0)
                {
                    var delayMs = remainingRetries >= 3 ? 250 : (remainingRetries == 2 ? 700 : 1400);
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

        private void ApplyCentre(Form form, string latitudeValue, string longitudeValue, string expectedAsdType, int remainingRetries = 3)
        {
            if (string.IsNullOrWhiteSpace(latitudeValue) || string.IsNullOrWhiteSpace(longitudeValue)) return;

            try
            {
                if (!double.TryParse(latitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return;
                if (!double.TryParse(longitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return;

                var asd = GetAsdControl(form);
                if (asd == null) return;

                var actualAsdType = GetAsdType(asd);
                if (!string.IsNullOrWhiteSpace(expectedAsdType) &&
                    !string.IsNullOrWhiteSpace(actualAsdType) &&
                    !string.Equals(actualAsdType, expectedAsdType, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!SetAsdCentre(asd, lat, lon, actualAsdType))
                {
                    return;
                }

                if (remainingRetries > 0)
                {
                    var delayMs = remainingRetries >= 3 ? 250 : (remainingRetries == 2 ? 700 : 1400);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(delayMs);
                        RunOnUiThread(() =>
                        {
                            if (form != null && !form.IsDisposed)
                            {
                                ApplyCentre(form, latitudeValue, longitudeValue, expectedAsdType, remainingRetries - 1);
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply centre failed: {ex}");
            }
        }

        private bool ApplyRangeWithZoom(Control asd, double r)
        {
            try
            {
                var storedRangeField = asd.GetType().GetField("StoredRange", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (storedRangeField != null)
                {
                    var rounded = (int)Math.Round(r);
                    storedRangeField.SetValue(asd, rounded);
                }

                var setZoom = asd.GetType().GetMethod(
                    "SetZoom",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(double), typeof(bool), typeof(bool), typeof(bool) },
                    null);

                if (setZoom == null) return false;

                setZoom.Invoke(asd, new object[] { r, true, true, true });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply range via SetZoom failed: {ex}");
                return false;
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

        private bool SetAsdCentre(Control asd, double latitude, double longitude, string actualAsdType)
        {
            try
            {
                var coordType = typeof(MMI).Assembly.GetType("vatsys.Coordinate");
                if (coordType == null) return false;

                var coord = Activator.CreateInstance(coordType, new object[] { latitude, longitude });

                var storedCentreField = asd.GetType().GetField("StoredCentreLL", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                storedCentreField?.SetValue(asd, coord);

                var setCentre = asd.GetType().GetMethod(
                    "SetDisplayCenter",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { coordType, typeof(bool), typeof(bool) },
                    null);

                if (setCentre != null)
                {
                    setCentre.Invoke(asd, new[] { coord, true, true });
                }
                else
                {
                    return false;
                }

                try
                {
                    asd.Invalidate(true);
                    asd.Refresh();
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to set ASD centre: {ex}");
                return false;
            }
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

        private void ApplyPlacement(Form form, User32.WINDOWPLACEMENT placement, Dictionary<string, string> metadata = null)
        {
            try
            {
                if (!form.IsHandleCreated)
                {
                    var _ = form.Handle;
                }

                var desiredShow = GetDesiredShow(metadata, placement.showCmd, out var desiredFormState);
                placement.showCmd = desiredShow;
                User32.SetWindowPlacement(form.Handle, placement);

                var targetRect = GetTargetRect(placement, metadata);
                var width = targetRect.right - targetRect.left;
                var height = targetRect.bottom - targetRect.top;
                if (width > 0 && height > 0)
                {
                    User32.SetWindowPos(
                        form.Handle,
                        IntPtr.Zero,
                        targetRect.left,
                        targetRect.top,
                        width,
                        height,
                        User32.SetWindowPosFlags.SWP_NOZORDER |
                        User32.SetWindowPosFlags.SWP_NOOWNERZORDER |
                        User32.SetWindowPosFlags.SWP_NOACTIVATE |
                        User32.SetWindowPosFlags.SWP_FRAMECHANGED);

                    User32.ShowWindow(form.Handle, desiredShow);
                    if (form.WindowState != desiredFormState)
                    {
                        form.WindowState = desiredFormState;
                    }

                    // If the window is still not at the expected coordinates (e.g., VSCS), force a MoveWindow.
                    if (User32.GetWindowRect(form.Handle, out var current))
                    {
                        if (Math.Abs(current.left - targetRect.left) > 1 ||
                            Math.Abs(current.top - targetRect.top) > 1 ||
                            Math.Abs((current.right - current.left) - width) > 1 ||
                            Math.Abs((current.bottom - current.top) - height) > 1)
                        {
                            User32.MoveWindow(form.Handle, targetRect.left, targetRect.top, width, height, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply placement failed for {form?.Name}: {ex}");
            }
        }

        private RECT GetTargetRect(User32.WINDOWPLACEMENT placement, Dictionary<string, string> metadata)
        {
            RECT targetRect = placement.rcNormalPosition;

            if (metadata != null &&
                TryParseInt(metadata, "ActualLeft", out var al) &&
                TryParseInt(metadata, "ActualTop", out var at) &&
                TryParseInt(metadata, "ActualRight", out var ar) &&
                TryParseInt(metadata, "ActualBottom", out var ab))
            {
                targetRect.left = al;
                targetRect.top = at;
                targetRect.right = ar;
                targetRect.bottom = ab;
            }

            return targetRect;
        }

        private User32.WindowShowStyle GetDesiredShow(Dictionary<string, string> metadata, User32.WindowShowStyle fallback, out FormWindowState formState)
        {
            formState = FormWindowState.Normal;
            var desiredShow = fallback;

            if (metadata != null && metadata.TryGetValue("WindowState", out var state) &&
                string.Equals(state, "Maximized", StringComparison.OrdinalIgnoreCase))
            {
                desiredShow = User32.WindowShowStyle.SW_SHOWMAXIMIZED;
                formState = FormWindowState.Maximized;
            }

            return desiredShow;
        }

        private void EnsureVsCsPlacement(Form form, User32.WINDOWPLACEMENT placement, Dictionary<string, string> metadata)
        {
            if (form == null || form.IsDisposed) return;
            if (!IsVsCs(form)) return;

            var targetRect = GetTargetRect(placement, metadata);
            var desiredShow = GetDesiredShow(metadata, placement.showCmd, out var desiredFormState);

            void Reapply()
            {
                if (form == null || form.IsDisposed) return;
                try
                {
                    if (!form.IsHandleCreated)
                    {
                        var _ = form.Handle;
                    }

                    var width = targetRect.right - targetRect.left;
                    var height = targetRect.bottom - targetRect.top;

                    form.StartPosition = FormStartPosition.Manual;
                    form.WindowState = FormWindowState.Normal;

                    if (width > 0 && height > 0)
                    {
                        try
                        {
                            var savedField = form.GetType().GetField("savedState", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (savedField != null)
                            {
                                savedField.SetValue(form, placement);
                            }
                            var usePlacementField = form.GetType().BaseType?.GetField("usePlacement", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (usePlacementField != null)
                            {
                                usePlacementField.SetValue(form, true);
                            }
                        }
                        catch
                        {
                            // ignore
                        }

                        User32.SetWindowPlacement(form.Handle, placement);
                        form.DesktopBounds = new Rectangle(targetRect.left, targetRect.top, width, height);
                        User32.MoveWindow(form.Handle, targetRect.left, targetRect.top, width, height, true);
                    }

                    User32.ShowWindow(form.Handle, desiredShow);
                    if (desiredFormState == FormWindowState.Maximized)
                    {
                        form.WindowState = FormWindowState.Maximized;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                if (form.InvokeRequired)
                {
                    form.BeginInvoke(new Action(Reapply));
                }
                else
                {
                    Reapply();
                }
            }
            catch
            {
                // ignore
            }

            SchedulePlacementRetries(form, Reapply, targetRect, 0, 150, 400, 800, 1400, 2200);
        }

        private void EnsureOzStripsPlacement(Form form, User32.WINDOWPLACEMENT placement, Dictionary<string, string> metadata)
        {
            if (form == null || form.IsDisposed) return;
            if (!IsOzStrips(form)) return;

            void Reapply()
            {
                if (form == null || form.IsDisposed) return;
                ApplyPlacement(form, placement, metadata);
            }

            var targetRect = GetTargetRect(placement, metadata);
            SchedulePlacementRetries(form, Reapply, targetRect, 120, 350, 800, 1500, 2500);
        }

        private void EnsureOzStripsPlacementWhenAvailable(WindowLayoutEntry entry)
        {
            if (entry?.Placement == null) return;
            var placement = entry.Placement.ToWindowPlacement();
            var metadata = entry.Metadata;
            var targetRect = placement.rcNormalPosition;

            var delays = new[] { 0, 200, 500, 900, 1500, 2500, 3500 };
            foreach (var delay in delays)
            {
                Task.Run(() =>
                {
                    try
                    {
                        System.Threading.Thread.Sleep(delay);
                        var oz = FindOzStripsByTitle() ?? FindExisting(entry);
                        if (oz != null)
                        {
                            if (IsWindowInPlace(oz.Handle, targetRect))
                            {
                                return;
                            }

                            EnsureOzStripsPlacement(oz, placement, metadata);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                });
            }
        }

        private void SchedulePlacementRetries(Form form, Action applyAction, params int[] delaysMs)
        {
            var empty = new RECT();
            SchedulePlacementRetries(form, applyAction, empty, delaysMs);
        }

        private void SchedulePlacementRetries(Form form, Action applyAction, RECT targetRect, params int[] delaysMs)
        {
            if (applyAction == null || form == null || form.IsDisposed) return;

            foreach (var delay in delaysMs ?? Array.Empty<int>())
            {
                Task.Run(() =>
                {
                    try
                    {
                        System.Threading.Thread.Sleep(Math.Max(0, delay));
                        if (form.IsDisposed) return;

                        if (IsWindowInPlace(form.Handle, targetRect))
                        {
                            return;
                        }

                        if (form.InvokeRequired)
                        {
                            form.BeginInvoke(applyAction);
                        }
                        else
                        {
                            applyAction();
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                });
            }
        }

        private bool IsWindowInPlace(IntPtr handle, RECT targetRect, int tolerance = 1)
        {
            try
            {
                if (handle == IntPtr.Zero) return false;
                if (!User32.GetWindowRect(handle, out var current)) return false;

                var width = targetRect.right - targetRect.left;
                var height = targetRect.bottom - targetRect.top;

                return Math.Abs(current.left - targetRect.left) <= tolerance &&
                       Math.Abs(current.top - targetRect.top) <= tolerance &&
                       Math.Abs((current.right - current.left) - width) <= tolerance &&
                       Math.Abs((current.bottom - current.top) - height) <= tolerance;
            }
            catch
            {
                return false;
            }
        }

        private bool TryParseInt(Dictionary<string, string> metadata, string key, out int value)
        {
            value = 0;
            if (metadata == null) return false;
            if (!metadata.TryGetValue(key, out var str)) return false;
            return int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
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

        private List<string> GetControlledSectorNames()
        {
            var names = new List<string>();

            try
            {
                var prop = typeof(MMI).GetProperty("SectorsControlled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var current = prop?.GetValue(null) as System.Collections.IEnumerable;
                if (current == null) return names;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sector in current)
                {
                    var name = GetStringField(sector, "Name") ??
                               GetStringField(sector, "FullName") ??
                               GetStringField(sector, "Callsign");

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    name = name.Trim();
                    if (seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to capture controlled sectors: {ex}");
            }

            return names;
        }

        private void ApplyControlledSectors(List<string> sectors)
        {
            if (sectors == null) return;

            try
            {
                var asm = typeof(MMI).Assembly;
                var volumesType = asm.GetType("vatsys.SectorsVolumes");
                var sectorType = asm.GetType("vatsys.SectorsVolumes+Sector");
                if (volumesType == null || sectorType == null) return;

                var available = GetAvailableSectors(volumesType).ToList();
                var listType = typeof(List<>).MakeGenericType(sectorType);
                var desired = Activator.CreateInstance(listType) as System.Collections.IList;
                if (desired == null) return;

                foreach (var name in sectors.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    var match = FindSectorByName(name, available);
                    if (match != null)
                    {
                        desired.Add(match);
                    }
                }

                var setWithList = typeof(MMI).GetMethod(
                    "SetControlledSectors",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { listType },
                    null);

                if (setWithList != null)
                {
                    setWithList.Invoke(null, new[] { desired });
                }
                else
                {
                    var clearMethod = typeof(MMI).GetMethod(
                        "SetControlledSectors",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);

                    if (clearMethod != null && desired.Count == 0)
                    {
                        clearMethod.Invoke(null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to apply controlled sectors: {ex}");
            }
        }

        private IEnumerable<object> GetAvailableSectors(Type sectorsVolumesType)
        {
            try
            {
                var sectorsField = sectorsVolumesType.GetField("Sectors", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var list = sectorsField?.GetValue(null) as System.Collections.IEnumerable;

                if (list == null)
                {
                    var load = sectorsVolumesType.GetMethod("LoadSectors", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    load?.Invoke(null, null);
                    list = sectorsField?.GetValue(null) as System.Collections.IEnumerable;
                }

                if (list == null) return Enumerable.Empty<object>();

                return list.Cast<object>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to get available sectors: {ex}");
                return Enumerable.Empty<object>();
            }
        }

        private object FindSectorByName(string name, IEnumerable<object> available)
        {
            if (string.IsNullOrWhiteSpace(name) || available == null) return null;

            var target = name.Trim();

            foreach (var sector in available)
            {
                var sectorName = GetStringField(sector, "Name");
                var full = GetStringField(sector, "FullName");
                var callsign = GetStringField(sector, "Callsign");

                if (string.Equals(sectorName, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(callsign, target, StringComparison.OrdinalIgnoreCase))
                {
                    return sector;
                }
            }

            return null;
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

        private (double Latitude, double Longitude)? GetCoordinateValues(object coord)
        {
            if (coord == null) return null;

            var lat = GetCoordinateValue(coord, "get_Latitude");
            var lon = GetCoordinateValue(coord, "get_Longitude");
            if (lat == null || lon == null) return null;

            return (lat.Value, lon.Value);
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

        private void EnsurePrimePosition(string targetPositionName)
        {
            if (string.IsNullOrWhiteSpace(targetPositionName)) return;

            try
            {
                RunOnUiThread(() =>
                {
                    var current = GetPrimePositionName();
                    if (string.Equals(current, targetPositionName, StringComparison.OrdinalIgnoreCase)) return;

                    var main = Application.OpenForms.Cast<Form>().FirstOrDefault(f => string.Equals(f.Name, "MainForm", StringComparison.OrdinalIgnoreCase)) ??
                               Application.OpenForms.Cast<Form>().FirstOrDefault();

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    if (TrySetPrimePositionDirect(targetPositionName))
                    {
                        return;
                    }

                        if (main != null)
                        {
                            TrySelectPrimePositionFromMenu(main, targetPositionName);
                            var updated = GetPrimePositionName();
                            if (string.Equals(updated, targetPositionName, StringComparison.OrdinalIgnoreCase))
                            {
                                return;
                            }
                        }

                        System.Threading.Thread.Sleep(250);
                }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to set prime position: {ex}");
            }
        }

        private bool TrySetPrimePositionDirect(string targetPositionName)
        {
            try
            {
                var pos = FindLogicalPosition(targetPositionName, null, null);
                if (pos == null) return false;

                var setPrime = typeof(MMI).GetMethod("SetPrimePosition", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { pos.GetType() }, null);
                setPrime?.Invoke(null, new[] { pos });

                var loadStrips = typeof(MMI).GetMethod("LoadPositionStripWindows", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { pos.GetType() }, null);
                loadStrips?.Invoke(null, new[] { pos });

                TrySetSettingsPosition(targetPositionName);
                TryForcePrimeField(pos);

                var current = GetPrimePositionName();
                return string.Equals(current, targetPositionName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void TrySetSettingsPosition(string positionName)
        {
            try
            {
                var settingsType = typeof(MMI).Assembly.GetType("vatsys.Properties.Settings");
                var defaultProp = settingsType?.GetProperty("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var instance = defaultProp?.GetValue(null);
                var positionProp = settingsType?.GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                positionProp?.SetValue(instance, positionName);
            }
            catch { }
        }

        private void TryForcePrimeField(object position)
        {
            try
            {
                var primeField = typeof(MMI).GetField("primePosition", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                primeField?.SetValue(null, position);

                var evt = typeof(MMI).GetField("PrimePositonChanged", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var handler = evt?.GetValue(null) as EventHandler;
                handler?.Invoke(null, EventArgs.Empty);
            }
            catch { }
        }

        private void TrySelectPrimePositionFromMenu(Form main, string targetPositionName)
        {
            if (string.IsNullOrWhiteSpace(targetPositionName)) return;

            try
            {
                BuildPositionMenu(main);

                var menuField = main.GetType().GetField("positionsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var menu = menuField?.GetValue(main) as ToolStripMenuItem;
                if (menu == null) return;

                var expand = main.GetType().GetMethod("ExpandPositionMenuItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                expand?.Invoke(main, new object[] { menu });

                // Prefer matching by menu item tag (LogicalPositions.Position) when possible.
                if (ClickPositionMenuByTag(menu.DropDownItems.OfType<ToolStripMenuItem>(), targetPositionName))
                {
                    var current = GetPrimePositionName();
                    if (string.Equals(current, targetPositionName, StringComparison.OrdinalIgnoreCase)) return;
                }

                // First try exact in the main dropdown
                if (ClickMenuItemRecursive(menu.DropDownItems.OfType<ToolStripMenuItem>(), targetPositionName, matchExact: true))
                {
                    var current = GetPrimePositionName();
                    if (string.Equals(current, targetPositionName, StringComparison.OrdinalIgnoreCase)) return;
                }

                // Some positions live under class submenus (e.g., Class C/D). Walk all sub-items with partial match.
                ClickMenuItemRecursive(menu.DropDownItems.OfType<ToolStripMenuItem>(), targetPositionName, matchExact: false);

                var updated = GetPrimePositionName();
                if (!string.Equals(updated, targetPositionName, StringComparison.OrdinalIgnoreCase))
                {
                    // Try invoking the drop-down handler to ensure menu is fully populated, then retry.
                    var handler = main.GetType().GetMethod("positionsToolStripMenuItem_DropDownOpened", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (handler != null)
                    {
                        handler.Invoke(main, new object[] { menu, EventArgs.Empty });
                        ClickMenuItemRecursive(menu.DropDownItems.OfType<ToolStripMenuItem>(), targetPositionName, matchExact: true);
                        updated = GetPrimePositionName();
                        if (string.Equals(updated, targetPositionName, StringComparison.OrdinalIgnoreCase)) return;
                    }

                    // Fallback: try any partial match again
                    ClickMenuItemRecursive(menu.DropDownItems.OfType<ToolStripMenuItem>(), targetPositionName, matchExact: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to select position via menu: {ex}");
            }
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
        public List<string> ControlledSectors { get; set; }
    }

    internal class DisplayPositionInfo
    {
        public string Name { get; set; }
        public string Callsign { get; set; }
        public string FullName { get; set; }
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


