using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
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
        private readonly HashSet<Form> windowsUsedDuringRestore = new HashSet<Form>();
        private bool menuRegistered;
        private bool isRestoringLayout;
        private readonly string arrivalLogPath;
        private LayoutSnapshot lastArrivalSnapshot;
        private HashSet<Form> lastArrivalDesired;

        // Constants for BindingFlags to reduce duplication
        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        // Constants for retry delays
        private const int ShortRetryDelayMs = 250;
        private const int MediumRetryDelayMs = 600;
        private const int LongRetryDelayMs = 1200;
        private const int FinalRetryDelayMs = 1400;
        private const int VeryLongRetryDelayMs = 2000;

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

            MigrateLegacyLayouts();
            LoadAutoLoadMap();

            arrivalLogPath = Path.Combine(LayoutRoot(), "arrival_debug.log");
            SafeLogArrival("=== Plugin started ===");

            _ = Task.Run(async () =>
            {
                var ready = await EnsureUiReady();
                if (!ready) return;
                RunOnUiThread(AddMenuItem);
                await Task.Delay(1500);
                RunOnUiThread(() => RestoreLayoutForCurrentPosition(requireAutoLoad: true));
                RunOnUiThread(HookPrimePositionChanged);
                RunOnUiThread(HookArrivalEvents);
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
                var field = typeof(MMI).GetField("PrimePositonChanged", StaticFlags);
                if (field == null) return;

                primePositionChangedHandler = (s, e) => RunOnUiThread(() =>
                {
                    CloseNonDefaultOnPositionChange();
                    RestoreLayoutForCurrentPosition(requireAutoLoad: true);
                });
                var current = field.GetValue(null) as EventHandler;
                current += primePositionChangedHandler;
                field.SetValue(null, current);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to hook PrimePositonChanged: {ex}");
            }
        }

        private void HookArrivalEvents()
        {
            try
            {
                var addEvent = typeof(MMI).GetMethod("add_ArrivalListWindowsChanged", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (addEvent != null)
                {
                    EventHandler handler = (s, e) =>
                    {
                        DumpArrivalWindows("ArrivalListWindowsChanged");
                        TryCleanupArrivalsFromEvent();
                    };
                    addEvent.Invoke(null, new object[] { handler });
                }
                DumpArrivalWindows("Initial arrival dump");
            }
            catch (Exception ex)
            {
                LogError("HookArrivalEvents", ex);
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

        // Helper method to resolve types across all loaded assemblies
        private Type ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;

            return Type.GetType(typeName) ??
                   AppDomain.CurrentDomain.GetAssemblies()
                       .Select(a => a.GetType(typeName, false))
                       .FirstOrDefault(t => t != null);
        }

        // Helper method to check if a form is valid (not null and not disposed)
        private bool IsFormValid(Form form) => form != null && !form.IsDisposed;

        // Helper method to normalize airport codes
        private string NormalizeAirport(string value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        // Helper method to safely get metadata string value
        private bool TryGetMetadataString(Dictionary<string, string> metadata, string key, out string value)
        {
            value = null;
            if (metadata == null) return false;
            return metadata.TryGetValue(key, out value);
        }

        // Helper method for consistent error logging
        private void LogError(string operation, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WindowManager [{operation}] failed: {ex.Message}");
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

                var overrideItem = new ToolStripMenuItem("Override current layout");
                overrideItem.Click += (s, e) => OverrideLayout(layout, currentPosition);

                var deleteItem = new ToolStripMenuItem("Delete");
                deleteItem.Click += (s, e) => DeleteLayout(layout, currentPosition);

                layoutItem.DropDownItems.Add(loadItem);
                layoutItem.DropDownItems.Add(new ToolStripSeparator());
                layoutItem.DropDownItems.Add(autoItem);
                layoutItem.DropDownItems.Add(new ToolStripSeparator());
                layoutItem.DropDownItems.Add(overrideItem);
                layoutItem.DropDownItems.Add(new ToolStripSeparator());
                layoutItem.DropDownItems.Add(deleteItem);
                loadLayoutMenuItem.DropDownItems.Add(layoutItem);
            }
        }

        private void BuildPositionMenu(Form form)
        {
            try
            {
                var method = form.GetType().GetMethod("PositionsToolStripMenuItem_DropDownOpened", InstanceFlags) ??
                             form.GetType().GetMethod("positionsToolStripMenuItem_DropDownOpened", InstanceFlags);
                var menuField = form.GetType().GetField("positionsToolStripMenuItem", InstanceFlags);
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

        private void CloseAllArrivalWindows()
        {
            RunOnUiThread(() =>
            {
                try { MMI.CloseArrivalListWindows(); } catch { }
                DumpArrivalWindows("After CloseArrivalListWindows");
            });
        }

        private string InferStripMode(LayoutSnapshot snapshot)
        {
            try
            {
                if (snapshot?.Windows == null) return null;

                foreach (var entry in snapshot.Windows)
                {
                    if (entry == null) continue;
                    var meta = entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (meta.TryGetValue("StripWindowType", out var type) &&
                        string.Equals(type, "State", StringComparison.OrdinalIgnoreCase))
                    {
                        return "State";
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("InferStripMode", ex);
            }

            return null;
        }

        private string GetStripModeName()
        {
            try
            {
                var prop = typeof(MMI).GetProperty("StripMode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var value = prop?.GetValue(null);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private void ApplyStripMode(string modeName)
        {
            if (string.IsNullOrWhiteSpace(modeName)) return;

            try
            {
                var enumType = typeof(MMI).GetNestedType("StripModes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                               Type.GetType("vatsys.MMI+StripModes");
                if (enumType == null || !enumType.IsEnum) return;

                var parsed = Enum.Parse(enumType, modeName, true);
                RunOnUiThread(() =>
                {
                    try
                    {
                        var prop = typeof(MMI).GetProperty("StripMode", StaticFlags);
                        prop?.SetValue(null, parsed);
                    }
                    catch (Exception ex)
                    {
                        LogError("SetStripMode", ex);
                    }
                });
            }
            catch
            {
                // ignore
            }
        }

        private string GetStripSortModeName()
        {
            try
            {
                var prop = typeof(MMI).GetProperty("StripSortMode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var value = prop?.GetValue(null);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private void ApplyStripSortMode(string modeName)
        {
            if (string.IsNullOrWhiteSpace(modeName)) return;

            try
            {
                var enumType = typeof(MMI).GetNestedType("StripSortModes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                               Type.GetType("vatsys.MMI+StripSortModes");
                if (enumType == null || !enumType.IsEnum) return;

                var parsed = Enum.Parse(enumType, modeName, true);
                RunOnUiThread(() =>
                {
                    try
                    {
                        var prop = typeof(MMI).GetProperty("StripSortMode", StaticFlags);
                        prop?.SetValue(null, parsed);
                    }
                    catch (Exception ex)
                    {
                        LogError("SetStripSortMode", ex);
                    }
                });
            }
            catch
            {
                // ignore
            }
        }

        private IEnumerable<Form> GetArrivalWindowsFromMMI()
        {
            List<Form> result = new List<Form>();

            try
            {
                var field = typeof(MMI).GetField("arrivalListsW", StaticFlags);
                var list = field?.GetValue(null) as System.Collections.IEnumerable;
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        if (item is Form f) result.Add(f);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return result;
        }

        private IEnumerable<Form> GetAtisWindowsFromMMI()
        {
            List<Form> result = new List<Form>();

            try
            {
                var field = typeof(MMI).GetField("atisW", StaticFlags);
                var list = field?.GetValue(null) as System.Collections.IEnumerable;
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        if (item is Form f) result.Add(f);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("GetAtisWindowsFromMMI", ex);
            }

            return result;
        }

        private string LayoutRoot()
        {
            // Prefer Documents; fall back to plugin folder if Documents is unavailable/redirected.
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrWhiteSpace(docs))
                {
                    var root = Path.Combine(docs, "vatSys Window Manager");
                    Directory.CreateDirectory(root);
                    return root;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: failed to use Documents for layout root: {ex}");
            }

            var pluginRoot = Path.Combine(GetPluginDirectory(), "Layouts");
            Directory.CreateDirectory(pluginRoot);
            return pluginRoot;
        }

        private void MigrateLegacyLayouts()
        {
            try
            {
                var destination = LayoutRoot();

                var legacyRoots = new[]
                {
                    Path.Combine(GetPluginDirectory(), "Layouts"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vatSysWindowManager"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vatSysWindowManager", "Layouts"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vatSys Window Manager"),
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var legacyRoot in legacyRoots)
                {
                    if (string.Equals(legacyRoot, destination, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.Exists(legacyRoot)) continue;

                    var files = Directory.GetFiles(legacyRoot, "*.layout.json", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(legacyRoot, "autoload.json", SearchOption.TopDirectoryOnly));

                    foreach (var file in files)
                    {
                        var destFile = Path.Combine(destination, Path.GetFileName(file));
                        if (string.Equals(file, destFile, StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            if (File.Exists(destFile)) continue; // keep existing layouts/configs in the new location
                            File.Move(file, destFile);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"WindowManager: failed to migrate layout '{file}' to '{destFile}': {ex}");
                        }
                    }

                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(legacyRoot).Any())
                        {
                            Directory.Delete(legacyRoot, true);
                        }
                    }
                    catch
                    {
                        // ignore cleanup issues
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: layout migration failed: {ex}");
            }
        }

        private string GetPluginDirectory()
        {
            try
            {
                var location = Assembly.GetExecutingAssembly().Location;
                var dir = Path.GetDirectoryName(location);
                if (!string.IsNullOrWhiteSpace(dir)) return dir;
            }
            catch
            {
                // ignore and fall through to other options
            }

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir)) return baseDir;
            }
            catch
            {
                // ignore and fall through to current directory
            }

            return Directory.GetCurrentDirectory();
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

        private IEnumerable<Form> EnumerateFormsForSave()
        {
            var seen = new HashSet<IntPtr>();

            foreach (Form form in Application.OpenForms)
            {
                if (form == null || form.IsDisposed) continue;
                if (ShouldAlwaysSave(form) && !form.Visible) continue;
                var handle = form.IsHandleCreated ? form.Handle : IntPtr.Zero;
                if (handle != IntPtr.Zero && !seen.Add(handle)) continue;
                yield return form;
            }

            foreach (var arrival in GetArrivalWindowsFromMMI())
            {
                if (arrival == null || arrival.IsDisposed) continue;
                var handle = arrival.IsHandleCreated ? arrival.Handle : IntPtr.Zero;
                if (handle != IntPtr.Zero && !seen.Add(handle)) continue;
                yield return arrival;
            }

            foreach (var atis in GetAtisWindowsFromMMI())
            {
                if (atis == null || atis.IsDisposed) continue;
                var handle = atis.IsHandleCreated ? atis.Handle : IntPtr.Zero;
                if (handle != IntPtr.Zero && !seen.Add(handle)) continue;
                yield return atis;
            }

            var vscs = GetVsCsWindow();
            System.Diagnostics.Debug.WriteLine($"WindowManager: GetVsCsWindow returned: {(vscs != null ? vscs.GetType().FullName : "null")}");
            if (vscs != null && !vscs.IsDisposed)
            {
                var handle = vscs.IsHandleCreated ? vscs.Handle : IntPtr.Zero;
                System.Diagnostics.Debug.WriteLine($"WindowManager: VSCS handle: {handle}, HandleCreated: {vscs.IsHandleCreated}");
                if (handle != IntPtr.Zero && seen.Add(handle))
                {
                    System.Diagnostics.Debug.WriteLine($"WindowManager: Yielding VSCS window for enumeration");
                    yield return vscs;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"WindowManager: VSCS not yielded - handle zero: {handle == IntPtr.Zero}, already seen: {!seen.Add(handle)}");
                }
            }
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
                    ControlledSectors = GetControlledSectorNames(),
                    StripMode = GetStripModeName(),
                    StripSortMode = GetStripSortModeName()
                };

                foreach (Form form in EnumerateFormsForSave())
                {
                    System.Diagnostics.Debug.WriteLine($"WindowManager: Enumerating form: {form?.GetType().FullName} - {form?.Text} - Visible: {form?.Visible}");

                    var entry = BuildEntry(form);
                    if (entry != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"WindowManager: Built entry for: {entry.TypeName}");

                        if (entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
                        {
                            if (entry.Metadata != null && entry.Metadata.TryGetValue("Airport", out var ap))
                            {
                                SafeLogArrival($"Captured arrival window save: {ap} title={entry.Title}");
                            }
                            else
                            {
                                SafeLogArrival($"Captured arrival window save: (no airport) title={entry.Title}");
                            }
                        }
                        else if (entry.TypeName.EndsWith("ATISWindow", StringComparison.Ordinal))
                        {
                            System.Diagnostics.Debug.WriteLine($"WindowManager: Captured ATIS window: {entry.Title}");
                        }
                        else if (entry.TypeName.IndexOf("VSCSWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"WindowManager: Captured VSCS window: {entry.Title}");
                        }

                        snapshot.Windows.Add(entry);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"WindowManager: BuildEntry returned null for: {form?.GetType().FullName}");
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
                isRestoringLayout = true;
                windowsUsedDuringRestore.Clear();
                ClosePluginWindows();
                CloseAllArrivalWindows();

                if (!File.Exists(path)) return;

                var snapshot = JsonConvert.DeserializeObject<LayoutSnapshot>(File.ReadAllText(path));
                if (snapshot == null) return;
                if (!string.Equals(snapshot.Position, GetPositionKey(), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"Layout \"{snapshot.LayoutName}\" was saved for position \"{snapshot.Position}\" and cannot be loaded while you are on \"{GetPositionKey()}\".", "vatSys Window Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Apply strip mode and sort mode up-front so any strip windows created later use the correct type.
                ApplyStripMode(snapshot.StripMode ?? InferStripMode(snapshot));
                ApplyStripSortMode(snapshot.StripSortMode);

                CloseWindowsNotInSnapshot(snapshot);
                ApplyAsdState(snapshot.Asd);
                ApplyControlledSectors(snapshot.ControlledSectors);

                if (snapshot.Windows == null || snapshot.Windows.Count == 0) return;

                var restoredIndices = new HashSet<int>();

                // Restore windows
                for (var i = 0; i < snapshot.Windows.Count; i++)
                {
                    var window = TryRestoreWindow(snapshot.Windows[i]);
                    if (window != null)
                    {
                        restoredIndices.Add(i);
                    }
                }

                RestoreUnmatchedVatSysWindows(snapshot, restoredIndices);

                // Run arrival restore asynchronously so we don't block the UI thread.
                var arrivalIndices = new HashSet<int>(restoredIndices);
                Task.Run(() => EnsureArrivalWindows(snapshot, arrivalIndices));

                EnsureStateStripWindows(snapshot);
                CloseExtraStripWindows(snapshot);

                // Reapply special placements after everything is up to ensure late-opening windows are aligned.
                EnforceSpecialPlacements(snapshot);
                CloseOzStripsIfNotInSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: restore failed from file {path}: {ex}");
            }
            finally
            {
                isRestoringLayout = false;
                windowsUsedDuringRestore.Clear();
            }
        }

        private WindowLayoutEntry BuildEntry(Form form)
        {
            try
            {
                if (form == null || form.IsDisposed) return null;
                var shouldAlwaysSave = ShouldAlwaysSave(form);
                if (!form.Visible && !shouldAlwaysSave) return null;

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
            catch (Exception ex)
            {
                LogError("BuildEntry_CaptureWindowState", ex);
            }

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
                meta["SavedTitle"] = form.Text;

                var airport = GetPropertyString(form, "Airport");
                if (!string.IsNullOrWhiteSpace(airport)) meta["Airport"] = airport.Trim();
                var hint = ParseAirportFromText(form.Text);
                if (!string.IsNullOrWhiteSpace(hint)) meta["AirportHint"] = hint;
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
            var field = instance.GetType().GetField(fieldName, InstanceFlags);
            return field?.GetValue(instance) as string;
        }

        private object GetEnumField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, InstanceFlags);
            return field?.GetValue(instance);
        }

        private void TrySetPropertyString(object instance, string propertyName, string value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(propertyName)) return;

            try
            {
                var prop = instance.GetType().GetProperty(propertyName, InstanceFlags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(instance, value);
                }
            }
            catch
            {
                // ignore
            }
        }

        private string GetPropertyString(object instance, string propertyName)
        {
            var prop = instance.GetType().GetProperty(propertyName, InstanceFlags);
            var value = prop?.GetValue(instance);
            return value as string ?? value?.ToString();
        }

        private Form TryRestoreWindow(WindowLayoutEntry entry)
        {
            if (entry?.TypeName != null && entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
            {
                // Arrival windows are handled in a dedicated pass to avoid duplicates.
                return null;
            }

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

            if (window == null && IsOzStrips(entry))
            {
                // If OzStrips opens slightly later, watch for it and apply placement once available.
                EnsureOzStripsPlacementWhenAvailable(entry);
            }

            if (window == null) return null;

            FinalizeRestoredWindow(window, entry, placement);
            return window;
        }

        private void RestoreUnmatchedVatSysWindows(LayoutSnapshot snapshot, HashSet<int> restoredIndices)
        {
            if (snapshot?.Windows == null || snapshot.Windows.Count == 0) return;

            var restored = restoredIndices ?? new HashSet<int>();

            for (var i = 0; i < snapshot.Windows.Count; i++)
            {
                if (restored.Contains(i)) continue;
                var entry = snapshot.Windows[i];
                if (entry == null) continue;
                if (entry.TypeName != null && entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
                {
                    // Arrival windows are handled in a dedicated pass.
                    continue;
                }
                if (entry.TypeName != null && entry.TypeName.EndsWith("ATISWindow", StringComparison.Ordinal))
                {
                    // ATIS windows are handled in FindExisting pass.
                    continue;
                }

                // Only broaden for vatsys forms to avoid surprising third-party plugins.
                if (string.IsNullOrWhiteSpace(entry.TypeName) ||
                    !entry.TypeName.StartsWith("vatsys.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var placement = entry.Placement?.ToWindowPlacement();
                if (placement != null)
                {
                    SetBaseFormPlacement(entry.FormName, placement.Value);
                }

                var window = FindBroadExisting(entry) ??
                             CreateWindow(entry) ??
                             TryCreateWithFallback(entry);

                if (window != null)
                {
                    FinalizeRestoredWindow(window, entry, placement);
                }
                else if (IsOzStrips(entry))
                {
                    EnsureOzStripsPlacementWhenAvailable(entry);
                }
            }
        }

        private void EnsureArrivalWindows(LayoutSnapshot snapshot, HashSet<int> restoredIndices)
        {
            if (snapshot?.Windows == null || snapshot.Windows.Count == 0) return;

            var created = new HashSet<Form>();
            lastArrivalSnapshot = snapshot;
            lastArrivalDesired = created;

            var targets = snapshot.Windows
                .Select((entry, index) => new { entry, index })
                .Where(x => x.entry != null && !string.IsNullOrWhiteSpace(x.entry.TypeName) && x.entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
                .ToList();
            if (targets.Count == 0) return;
            SafeLogArrival($"EnsureArrivalWindows target count={targets.Count}");

            try
            {
                RunOnUiThread(() =>
                {
                    try { MMI.CloseArrivalListWindows(); } catch { }
                });

                // Pre-fire menu requests for all targets to avoid sequential waits.
                var preFireAirports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in targets)
                {
                    var entry = item.entry;
                    var metadata = entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (TryGetAirportCandidates(entry, metadata, out var airports) && airports.Count > 0)
                    {
                        var ap = airports[0];
                        if (!string.IsNullOrWhiteSpace(ap) && preFireAirports.Add(ap))
                        {
                            SafeLogArrival($"Pre-fire arrival menu for {ap}");
                            FireArrivalMenu(ap);
                        }
                    }
                }

                foreach (var item in targets)
                {
                    var entry = item.entry;
                    if (restoredIndices != null && restoredIndices.Contains(item.index)) continue;

                    var placement = entry.Placement?.ToWindowPlacement();
                    var metadata = entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (!TryGetAirportCandidates(entry, metadata, out var airports) || airports.Count == 0)
                    {
                        airports = new List<string> { string.Empty };
                    }

                    SafeLogArrival($"Arrival restore target airports: {string.Join(",", airports)}");
                    // Reuse any already-open arrival window first to avoid duplicates.
                    var window = FindArrivalMatch(entry.TypeName, airports, created);
                    if (window == null)
                    {
                        window = CreateArrivalQuick(entry.TypeName, airports, metadata, created);
                    }

                    if (window != null)
                    {
                        FinalizeRestoredWindow(window, entry, placement);
                        created.Add(window);
                    }
                    else
                    {
                        SafeLogArrival($"Arrival creation failed for {string.Join(",", airports)}");
                    }
                }

                CloseExtraArrivalWindows(snapshot, created);
            }
            catch (Exception ex)
            {
                SafeLogArrival($"EnsureArrivalWindows exception: {ex}");
                // Attempt cleanup even if something failed.
                try { CloseExtraArrivalWindows(snapshot, created); } catch { }
            }

            // Schedule a few follow-up cleanups to catch any late-spawned arrivals.
            ScheduleArrivalCleanup(snapshot, created, 0, 700, 2000, 4000, 8000, 12000);
        }

        private void ScheduleArrivalCleanup(LayoutSnapshot snapshot, HashSet<Form> desired, params int[] delays)
        {
            if (delays == null || delays.Length == 0) return;

            foreach (var delay in delays)
            {
                Task.Run(async () =>
                {
                    if (delay > 0) await Task.Delay(delay);
                    RunOnUiThread(() =>
                    {
                        SafeLogArrival($"Running arrival cleanup delay={delay}ms");
                        try { CloseExtraArrivalWindows(snapshot, desired); } catch { }
                    });
                });
            }
        }

        private Form FindArrivalMatch(string typeName, List<string> airports, HashSet<Form> assigned)
        {
            var airportSet = airports?.Select(a => a?.Trim().ToUpperInvariant()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? new List<string>();

            foreach (Form form in Application.OpenForms)
            {
                if (form == null || form.IsDisposed) continue;
                if (assigned != null && assigned.Contains(form)) continue;
                if (!string.Equals(form.GetType().FullName, typeName, StringComparison.OrdinalIgnoreCase)) continue;

                if (airportSet.Count == 0) return form;

                var airportProp = GetPropertyString(form, "Airport");
                var parsedTitle = ParseAirportFromText(form.Text);

                foreach (var airport in airportSet)
                {
                    if (string.Equals(airportProp, airport, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(parsedTitle, airport, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(form.Text) && form.Text.IndexOf(airport, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return form;
                    }
                }
            }

            return null;
        }

        private Form CreateArrivalViaMenu(string typeName, string airport, Dictionary<string, string> metadata, int attempts = 5, int delayMs = 120)
        {
            Form found = null;
            var targetAirport = airport ?? string.Empty;

            RunOnUiThread(() =>
            {
                try
                {
                    // Try the built-in helper first.
                    MMI.OpenArrivalListWindow(targetAirport);

                    // Also simulate the menu field typing + enter to mirror user action.
                    TrySimulateArrivalMenu(targetAirport);
                }
                catch { }
            });

            found = WaitForForm(typeName, f =>
                (string.IsNullOrWhiteSpace(targetAirport) ||
                 string.Equals(GetPropertyString(f, "Airport"), targetAirport, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(f.Text) && f.Text.IndexOf(targetAirport, StringComparison.OrdinalIgnoreCase) >= 0)),
                attempts, delayMs);

            if (found != null)
            {
                if (!string.IsNullOrWhiteSpace(targetAirport))
                {
                    TrySetPropertyString(found, "Airport", targetAirport);
                }

                if (metadata != null && metadata.TryGetValue("SavedTitle", out var savedTitle) && !string.IsNullOrWhiteSpace(savedTitle))
                {
                    found.Text = savedTitle;
                }
            }

            return found;
        }

        private void TrySimulateArrivalMenu(string airport)
        {
            try
            {
                var main = Application.OpenForms.Cast<Form>()
                    .FirstOrDefault(f => string.Equals(f.Name, "MainForm", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(f.GetType().FullName, "vatsys.MainForm", StringComparison.OrdinalIgnoreCase));
                if (main == null) return;

                var windowsMenuField = main.GetType().GetField("windowsToolStripMenuItem", InstanceFlags);
                var windowsMenu = windowsMenuField?.GetValue(main) as ToolStripMenuItem;
                if (windowsMenu == null) return;

                windowsMenu.ShowDropDown();

                ToolStripMenuItem arrivalItem = null;
                foreach (ToolStripMenuItem item in windowsMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    var text = item.Text?.Replace("&", string.Empty).Trim();
                    if (string.Equals(text, "Arrival List", StringComparison.OrdinalIgnoreCase))
                    {
                        arrivalItem = item;
                        break;
                    }
                }
                if (arrivalItem == null) return;
                arrivalItem.ShowDropDown();

                ToolStripMenuItem openNew = null;
                foreach (ToolStripMenuItem item in arrivalItem.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    var text = item.Text?.Replace("&", string.Empty).Trim();
                    if (string.Equals(text, "Open New", StringComparison.OrdinalIgnoreCase))
                    {
                        openNew = item;
                        break;
                    }
                }
                if (openNew == null) return;
                openNew.ShowDropDown();

                var field = main.GetType().GetField("arrivalListAirportTextField", InstanceFlags);
                var control = field?.GetValue(main) as Control;
                if (control != null)
                {
                    control.Text = airport ?? string.Empty;
                    var onReturn = main.GetType().GetMethod("ArrivalListAirportTextField_OnReturn", InstanceFlags);
                    onReturn?.Invoke(main, new object[] { control, EventArgs.Empty });
                }
            }
            catch
            {
                // ignore
            }
        }

        private Form CreateArrivalManually(string typeName, string airport, Dictionary<string, string> metadata)
        {
            try
            {
                var type = ResolveType(typeName);

                if (type != null && typeof(Form).IsAssignableFrom(type))
                {
                    Form instance = null;
                    var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    if (ctor != null)
                    {
                        instance = ctor.Invoke(new object[] { airport ?? string.Empty }) as Form;
                    }
                    else
                    {
                        instance = Activator.CreateInstance(type) as Form;
                        TrySetPropertyString(instance, "Airport", airport ?? string.Empty);
                    }

                    if (instance != null)
                    {
                        RunOnUiThread(() =>
                        {
                            instance.Show();
                            if (metadata != null && metadata.TryGetValue("SavedTitle", out var savedTitle) && !string.IsNullOrWhiteSpace(savedTitle))
                            {
                                instance.Text = savedTitle;
                            }
                        });
                        return instance;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private void FireArrivalMenu(string airport)
        {
            RunOnUiThread(() =>
            {
                try
                {
                    var target = airport ?? string.Empty;
                    MMI.OpenArrivalListWindow(target);
                    TrySimulateArrivalMenu(target);
                }
                catch { }
            });
        }

        private Form CreateArrivalWithRetries(string typeName, List<string> airports, Dictionary<string, string> metadata, HashSet<Form> created)
        {
            var candidates = airports ?? new List<string>();
            if (candidates.Count == 0) candidates.Add(string.Empty);

            foreach (var airport in candidates)
            {
                SafeLogArrival($"Attempt menu arrival for {airport}");
                var viaMenu = CreateArrivalViaMenu(typeName, airport, metadata);
                if (viaMenu != null && (created == null || !created.Contains(viaMenu)))
                {
                    SafeLogArrival($"Menu arrival success for {airport}");
                    return viaMenu;
                }

                SafeLogArrival($"Attempt manual arrival for {airport}");
                var manual = CreateArrivalManually(typeName, airport, metadata);
                if (manual != null && (created == null || !created.Contains(manual)))
                {
                    SafeLogArrival($"Manual arrival success for {airport}");
                    return manual;
                }
            }

            return null;
        }

        private Form CreateArrivalQuick(string typeName, List<string> airports, Dictionary<string, string> metadata, HashSet<Form> created)
        {
            var candidates = airports ?? new List<string>();
            if (candidates.Count == 0) candidates.Add(string.Empty);

            foreach (var airport in candidates)
            {
                SafeLogArrival($"Attempt menu arrival (waited) for {airport}");
                var viaMenu = CreateArrivalViaMenu(typeName, airport, metadata, attempts: 20, delayMs: 180);
                if (viaMenu != null && (created == null || !created.Contains(viaMenu)))
                {
                    SafeLogArrival($"Menu arrival success for {airport}");
                    return viaMenu;
                }
            }

            return null;
        }

        private void CloseExtraArrivalWindows(LayoutSnapshot snapshot, HashSet<Form> desired)
        {
            if (snapshot?.Windows == null) return;

            var desiredCounts = snapshot.Windows
                .Where(w => w != null && (w.TypeName ?? string.Empty).EndsWith("SequenceWindow", StringComparison.Ordinal))
                .SelectMany(w =>
                {
                    var meta = w.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (TryGetAirportCandidates(w, meta, out var airports) && airports.Count > 0)
                    {
                        return airports.Select(a => a?.Trim().ToUpperInvariant()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
                    }
                    return new List<string> { string.Empty };
                })
                .GroupBy(a => a ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            SafeLogArrival($"CloseExtraArrivalWindows desired counts: {string.Join(";", desiredCounts.Select(kv => $"{kv.Key}={kv.Value}"))}");

            foreach (Form form in Application.OpenForms)
            {
                if (form == null || form.IsDisposed) continue;
                if (!IsArrivalWindow(form)) continue;
                if (desired != null && desired.Contains(form)) continue;

                var airport = GetPropertyString(form, "Airport")?.Trim().ToUpperInvariant() ?? string.Empty;
                var titleAirport = ParseAirportFromText(form.Text)?.Trim().ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(titleAirport)) airport = titleAirport;

                SafeLogArrival($"Arrival present {airport} text={form.Text}");

                if (!desiredCounts.TryGetValue(airport, out var allowed))
                {
                    SafeLogArrival($"Closing arrival (not desired) {airport} {form.Text}");
                    try { form.Close(); } catch { }
                    continue;
                }

                if (!used.ContainsKey(airport)) used[airport] = 0;
                used[airport]++;

                if (used[airport] > allowed)
                {
                    SafeLogArrival($"Closing extra arrival {airport} {form.Text} allowed={allowed}");
                    try { form.Close(); } catch { }
                }
            }
        }

        private void TryCleanupArrivalsFromEvent()
        {
            try
            {
                if (lastArrivalSnapshot == null) return;
                CloseExtraArrivalWindows(lastArrivalSnapshot, lastArrivalDesired);
            }
            catch (Exception ex)
            {
                SafeLogArrival($"TryCleanupArrivalsFromEvent error: {ex}");
            }
        }

        private void CloseExtraStripWindows(LayoutSnapshot snapshot)
        {
            if (snapshot?.Windows == null) return;

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Windows)
            {
                if (entry == null) continue;
                if ((entry.TypeName ?? string.Empty).EndsWith("StripWindow", StringComparison.Ordinal))
                {
                    var meta = entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var type = meta.TryGetValue("StripWindowType", out var t) ? t : string.Empty;
                    var hmi = meta.TryGetValue("HMIState", out var h) ? h : string.Empty;
                    var beacon = meta.TryGetValue("Beacon", out var b) ? b : string.Empty;
                    allowed.Add(BuildStripKey(type, hmi, beacon));
                }
            }

            // If we're saving state strips, prefer to close any beacon/ADEP extras outright.
            var savedStripMode = snapshot.StripMode ?? InferStripMode(snapshot);

            foreach (Form form in Application.OpenForms)
            {
                if (form == null || form.IsDisposed) continue;
                if ((form.GetType().FullName ?? string.Empty).IndexOf("StripWindow", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (IsOzStrips(form)) continue;

                var type = GetEnumField(form, "WindowType")?.ToString() ?? string.Empty;
                var hmi = GetEnumField(form, "State")?.ToString() ?? string.Empty;
                var beacon = GetStringField(form, "Beacon") ?? string.Empty;
                var key = BuildStripKey(type, hmi, beacon);

                var isBeaconMode = string.Equals(type, "Beacon", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(savedStripMode, "Beacon", StringComparison.OrdinalIgnoreCase);

                if (!allowed.Contains(key) || (string.Equals(savedStripMode, "State", StringComparison.OrdinalIgnoreCase) && isBeaconMode))
                {
                    try { form.Close(); } catch { }
                }
            }
        }

        private string BuildStripKey(string type, string hmi, string beacon)
        {
            return $"{type?.Trim()}|{hmi?.Trim()}|{beacon?.Trim()}";
        }

        private void EnsureStateStripWindows(LayoutSnapshot snapshot)
        {
            if (snapshot?.Windows == null) return;

            var entries = snapshot.Windows
                .Where(e => e != null && (e.TypeName ?? string.Empty).EndsWith("StripWindow", StringComparison.Ordinal))
                .ToList();
            if (entries.Count == 0) return;

            foreach (var entry in entries)
            {
                var meta = entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var type = meta.TryGetValue("StripWindowType", out var t) ? t : string.Empty;
                var hmi = meta.TryGetValue("HMIState", out var h) ? h : string.Empty;
                var beacon = meta.TryGetValue("Beacon", out var b) ? b : string.Empty;

                // If already present, skip creation.
                var existing = Application.OpenForms.Cast<Form>()
                    .FirstOrDefault(f =>
                        f != null && !f.IsDisposed &&
                        (f.GetType().FullName ?? string.Empty).IndexOf("StripWindow", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        string.Equals(GetEnumField(f, "WindowType")?.ToString(), type, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(GetEnumField(f, "State")?.ToString(), hmi, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(GetStringField(f, "Beacon") ?? string.Empty, beacon ?? string.Empty, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    FinalizeRestoredWindow(existing, entry, entry.Placement?.ToWindowPlacement());
                    continue;
                }

                // Create missing strip window.
                CreateStripWindow(type, hmi, beacon);
                var created = FindWindow("vatsys.StripWindow", f =>
                    string.Equals(GetEnumField(f, "WindowType")?.ToString(), type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetEnumField(f, "State")?.ToString(), hmi, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetStringField(f, "Beacon") ?? string.Empty, beacon ?? string.Empty, StringComparison.OrdinalIgnoreCase));

                if (created != null)
                {
                    FinalizeRestoredWindow(created, entry, entry.Placement?.ToWindowPlacement());
                }
            }
        }

        private void FinalizeRestoredWindow(Form window, WindowLayoutEntry entry, User32.WINDOWPLACEMENT? placement)
        {
            if (window == null || entry == null) return;

            var metadata = entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (isRestoringLayout)
            {
                windowsUsedDuringRestore.Add(window);
            }

            if (placement != null)
            {
                ApplyPlacement(window, placement.Value, metadata);
                if (IsOzStrips(entry))
                {
                    EnsureOzStripsPlacement(window, placement.Value, metadata);
                }
                else if (IsVsCs(entry))
                {
                    EnsureVSCSPlacement(window, placement.Value, metadata);
                }
            }
            else if (IsOzStrips(entry))
            {
                EnsureOzStripsPlacementWhenAvailable(entry);
            }

            metadata.TryGetValue("AsdType", out var asdType);

            var displayName = metadata.TryGetValue("DisplayPosition", out var displayPosition) ? displayPosition : null;
            var displayCallsign = metadata.TryGetValue("DisplayPositionCallsign", out var displayCallsignMeta) ? displayCallsignMeta : null;
            var displayFull = metadata.TryGetValue("DisplayPositionFullName", out var displayFullMeta) ? displayFullMeta : null;
            ApplyDisplayPosition(window, displayName, displayCallsign, displayFull, asdType);

            if (metadata.TryGetValue("CentreLat", out var centreLat) &&
                metadata.TryGetValue("CentreLon", out var centreLon))
            {
                ApplyCentre(window, centreLat, centreLon, asdType);
            }

            if (metadata.TryGetValue("Range", out var range))
            {
                ApplyRange(window, range, asdType);
            }

            if (metadata.TryGetValue("Maps", out var maps))
            {
                ApplyCheckedMaps(window, maps);
                ReapplyAsdView(window, metadata, asdType);
            }

            if (entry.TypeName.EndsWith("StripWindow", StringComparison.Ordinal))
            {
                ApplyStripState(window, metadata);
            }

            UpdateWindowTitleFromMetadata(window, metadata);

            BringToFrontSafe(window);
            if (!IsMainVatSysForm(window))
            {
                EnsureZOrder(window);
            }
        }

        private Form FindExisting(WindowLayoutEntry entry)
        {
            var forms = new List<Form>();

            // Add forms from Application.OpenForms
            forms.AddRange(Application.OpenForms.Cast<Form>()
                .Where(f => f != null && !f.IsDisposed)
                .Where(f => !isRestoringLayout || !windowsUsedDuringRestore.Contains(f)));

            // Add ATIS windows from MMI.atisW (they're not in Application.OpenForms)
            foreach (var atisWindow in GetAtisWindowsFromMMI())
            {
                if (atisWindow != null && !atisWindow.IsDisposed &&
                    (!isRestoringLayout || !windowsUsedDuringRestore.Contains(atisWindow)) &&
                    !forms.Contains(atisWindow))
                {
                    var callsign = GetStringField(atisWindow, "ATISCallsign");
                    System.Diagnostics.Debug.WriteLine($"WindowManager: FindExisting adding ATIS window with callsign: '{callsign}', Title: '{atisWindow.Text}'");
                    forms.Add(atisWindow);
                }
            }

            // Add arrival windows from MMI.arrivalListsW (they might not be in Application.OpenForms)
            foreach (var arrival in GetArrivalWindowsFromMMI())
            {
                if (arrival != null && !arrival.IsDisposed &&
                    (!isRestoringLayout || !windowsUsedDuringRestore.Contains(arrival)) &&
                    !forms.Contains(arrival))
                {
                    forms.Add(arrival);
                }
            }

            if (IsVsCs(entry))
            {
                var vscs = GetVsCsWindow();
                if (vscs != null && forms.Contains(vscs)) return vscs;
            }

            if (IsOzStrips(entry))
            {
                var oz = forms.FirstOrDefault(IsOzStrips);
                if (oz != null) return oz;
            }

            var metadataMatch = FindMetadataMatch(forms, entry);
            if (metadataMatch != null) return metadataMatch;

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

        private Form FindBroadExisting(WindowLayoutEntry entry)
        {
            if (entry == null) return null;

            var forms = new List<Form>();

            // Add forms from Application.OpenForms
            forms.AddRange(Application.OpenForms.Cast<Form>()
                .Where(f => f != null && !f.IsDisposed)
                .Where(f => !isRestoringLayout || !windowsUsedDuringRestore.Contains(f)));

            // Add ATIS windows from MMI.atisW (they're not in Application.OpenForms)
            foreach (var atisWindow in GetAtisWindowsFromMMI())
            {
                if (atisWindow != null && !atisWindow.IsDisposed &&
                    (!isRestoringLayout || !windowsUsedDuringRestore.Contains(atisWindow)) &&
                    !forms.Contains(atisWindow))
                {
                    forms.Add(atisWindow);
                }
            }

            // Add arrival windows from MMI.arrivalListsW (they might not be in Application.OpenForms)
            foreach (var arrival in GetArrivalWindowsFromMMI())
            {
                if (arrival != null && !arrival.IsDisposed &&
                    (!isRestoringLayout || !windowsUsedDuringRestore.Contains(arrival)) &&
                    !forms.Contains(arrival))
                {
                    forms.Add(arrival);
                }
            }

            if (entry.TypeName.EndsWith("ATISWindow", StringComparison.Ordinal) &&
                TryGetMetadataString(entry.Metadata, "ATISCallsign", out var atis) &&
                !string.IsNullOrWhiteSpace(atis))
            {
                var atisMatch = forms.FirstOrDefault(f =>
                    string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetStringField(f, "ATISCallsign"), atis, StringComparison.OrdinalIgnoreCase));
                if (atisMatch != null) return atisMatch;
            }

            var byType = forms.FirstOrDefault(f => string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.OrdinalIgnoreCase));
            if (byType != null) return byType;

            if (!string.IsNullOrWhiteSpace(entry.FormName))
            {
                var byName = forms.FirstOrDefault(f => string.Equals(f.Name, entry.FormName, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
            }

            if (!string.IsNullOrWhiteSpace(entry.Title))
            {
                var byTitle = forms.FirstOrDefault(f => string.Equals(f.Text, entry.Title, StringComparison.OrdinalIgnoreCase));
                if (byTitle != null) return byTitle;
            }

            return null;
        }

        private IEnumerable<string> GetPrimeArrivalAirports()
        {
            var result = new List<string>();

            try
            {
                var prime = MMI.PrimePosition;
                var field = prime?.GetType().GetField("ArrivalListAirports", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    var list = field.GetValue(prime) as System.Collections.IEnumerable;
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            var s = item as string ?? item?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim().ToUpperInvariant());
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            return result;
        }

        private Form RestoreAtisWindow(WindowLayoutEntry entry, string atisCallsign)
        {
            if (string.IsNullOrWhiteSpace(atisCallsign)) return null;

            string callsign = atisCallsign.Trim();

            // Prefer an existing unused ATIS window with the same callsign - check MMI.atisW first
            foreach (var atis in GetAtisWindowsFromMMI())
            {
                if (atis != null && !atis.IsDisposed &&
                    (!isRestoringLayout || !windowsUsedDuringRestore.Contains(atis)))
                {
                    var atisField = GetStringField(atis, "ATISCallsign");
                    System.Diagnostics.Debug.WriteLine($"WindowManager: Checking ATIS window - Field: '{atisField}' vs Looking for: '{callsign}'");
                    if (string.Equals(atisField, callsign, StringComparison.OrdinalIgnoreCase))
                    {
                        System.Diagnostics.Debug.WriteLine($"WindowManager: Found matching ATIS window for: {callsign}");
                        TrackPluginWindow(atis);
                        return atis;
                    }
                }
            }

            // Also check Application.OpenForms as fallback
            var existing = Application.OpenForms.Cast<Form>()
                .FirstOrDefault(f =>
                    f != null &&
                    !f.IsDisposed &&
                    (!isRestoringLayout || !windowsUsedDuringRestore.Contains(f)) &&
                    string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.Ordinal) &&
                    string.Equals(GetStringField(f, "ATISCallsign"), callsign, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                TrackPluginWindow(existing);
                return existing;
            }

            // Don't create new ATIS windows - only reuse existing ones
            // ATIS windows are typically opened when the position loads, so they should already exist
            System.Diagnostics.Debug.WriteLine($"WindowManager: Could not find existing ATIS window for callsign: {callsign}");
            return null;
        }

        private void ReapplyAsdView(Form form, Dictionary<string, string> metadata, string asdType)
        {
            if (form == null || metadata == null) return;

            // Some map toggles reset view; reapply after a short delay.
            if (!metadata.TryGetValue("CentreLat", out var lat) || !metadata.TryGetValue("CentreLon", out var lon))
            {
                lat = null;
                lon = null;
            }
            metadata.TryGetValue("Range", out var range);

            var display = metadata.TryGetValue("DisplayPosition", out var disp) ? disp : null;
            var dispCallsign = metadata.TryGetValue("DisplayPositionCallsign", out var dispC) ? dispC : null;
            var dispFull = metadata.TryGetValue("DisplayPositionFullName", out var dispF) ? dispF : null;

            Task.Run(async () =>
            {
                await Task.Delay(200);
                RunOnUiThread(() =>
                {
                    if (form == null || form.IsDisposed) return;
                    ApplyDisplayPosition(form, display, dispCallsign, dispFull, asdType, remainingRetries: 1);
                    if (!string.IsNullOrWhiteSpace(lat) && !string.IsNullOrWhiteSpace(lon))
                    {
                        ApplyCentre(form, lat, lon, asdType, remainingRetries: 1);
                    }
                    if (!string.IsNullOrWhiteSpace(range))
                    {
                        ApplyRange(form, range, asdType, remainingRetries: 1);
                    }
                });
            });
        }

        private void UpdateWindowTitleFromMetadata(Form form, Dictionary<string, string> metadata)
        {
            if (form == null || metadata == null) return;

            var typeName = form.GetType().FullName ?? string.Empty;
            var asd = GetAsdControl(form);

            if (asd != null)
            {
                var displayName = metadata.TryGetValue("DisplayPositionFullName", out var full) && !string.IsNullOrWhiteSpace(full)
                    ? full
                    : metadata.TryGetValue("DisplayPosition", out var name) ? name : null;

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    if (typeName.EndsWith("ASMGCSWindow", StringComparison.Ordinal))
                    {
                        form.Text = $"Ground: {displayName}";
                    }
                    else
                    {
                        var baseTitle = form.Text;
                        var parts = baseTitle?.Split('-').Select(p => p.Trim()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
                        if (parts.Count > 0)
                        {
                            parts[parts.Count - 1] = displayName;
                            form.Text = string.Join(" - ", parts);
                        }
                        else
                        {
                            form.Text = displayName;
                        }
                    }
                }
                return;
            }

            if (typeName.EndsWith("ATISWindow", StringComparison.Ordinal))
            {
                // ATIS windows already have the correct title format, don't change it
                return;
            }

            if (typeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
            {
                if (metadata.TryGetValue("SavedTitle", out var savedTitle) && !string.IsNullOrWhiteSpace(savedTitle))
                {
                    form.Text = savedTitle;
                    return;
                }

                if (metadata.TryGetValue("Airport", out var airport) && !string.IsNullOrWhiteSpace(airport))
                {
                    form.Text = $"A: {airport} List";
                }
            }
        }

        private Form RestoreArrivalWindow(WindowLayoutEntry entry, List<string> airports)
        {
            var typeName = entry?.TypeName;
            if (string.IsNullOrWhiteSpace(typeName)) return null;

            var candidates = airports?.Count > 0 ? airports : new List<string>();
            if (candidates.Count == 0)
            {
                candidates.AddRange(GetPrimeArrivalAirports());
            }

            // If nothing captured, still try one attempt with empty airport which opens the last/default arrival list.
            if (candidates.Count == 0) candidates.Add(string.Empty);

            candidates = candidates
                .Select(a => a == null ? string.Empty : a.Trim().ToUpperInvariant())
                .Where(a => a != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Try to reuse an unused arrival window that matches any of the airports.
            foreach (var airport in candidates)
            {
                var existing = Application.OpenForms.Cast<Form>()
                    .FirstOrDefault(f =>
                        f != null &&
                        !f.IsDisposed &&
                        (!isRestoringLayout || !windowsUsedDuringRestore.Contains(f)) &&
                        string.Equals(f.GetType().FullName, typeName, StringComparison.Ordinal) &&
                        (string.Equals(GetPropertyString(f, "Airport"), airport, StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrWhiteSpace(airport) && !string.IsNullOrWhiteSpace(f.Text) && f.Text.IndexOf(airport, StringComparison.OrdinalIgnoreCase) >= 0)));
                if (existing != null)
                {
                    TrackPluginWindow(existing);
                    return existing;
                }
            }

            // Launch via MMI for each airport candidate until we find a match.
            foreach (var airport in candidates)
            {
                try
                {
                    var target = airport ?? string.Empty;
                    RunOnUiThread(() => MMI.OpenArrivalListWindow(target));
                }
                catch { }

                var found = WaitForForm(typeName, f =>
                    (string.IsNullOrWhiteSpace(airport) ||
                     string.Equals(GetPropertyString(f, "Airport"), airport, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(f.Text) && f.Text.IndexOf(airport ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)) &&
                    (!isRestoringLayout || !windowsUsedDuringRestore.Contains(f)), 14, 250);

                if (found != null)
                {
                    // If airport property is empty but we have a target, try to set it so placement sticks.
                    TrySetPropertyString(found, "Airport", airport);
                    TrackPluginWindow(found);
                    return found;
                }
            }

            // Manual creation fallback.
            var type = ResolveType(typeName);

            if (type != null && typeof(Form).IsAssignableFrom(type))
            {
                foreach (var airport in candidates)
                {
                    try
                    {
                        Form instance = null;
                        // Prefer ctor(string) if available to set airport up-front.
                        var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                        if (ctor != null)
                        {
                            instance = ctor.Invoke(new object[] { airport ?? string.Empty }) as Form;
                        }
                        else
                        {
                            instance = Activator.CreateInstance(type) as Form;
                            TrySetPropertyString(instance, "Airport", airport);
                        }

                        if (instance != null)
                        {
                            instance.Show();
                            TrackPluginWindow(instance);
                            return instance;
                        }
                    }
                    catch
                    {
                        // continue trying other airports
                    }
                }
            }

            return null;
        }

        private Form WaitForForm(string typeName, Func<Form, bool> predicate, int attempts, int delayMs)
        {
            for (var i = 0; i < attempts; i++)
            {
                var match = Application.OpenForms.Cast<Form>()
                    .FirstOrDefault(f =>
                        f != null &&
                        !f.IsDisposed &&
                        string.Equals(f.GetType().FullName, typeName, StringComparison.OrdinalIgnoreCase) &&
                        (predicate?.Invoke(f) ?? true));

                if (match != null) return match;
                Thread.Sleep(Math.Max(10, delayMs));
            }

            return null;
        }

        private bool TryGetAirportCandidates(WindowLayoutEntry entry, Dictionary<string, string> metadata, out List<string> airports)
        {
            var list = new List<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate)) return;
                var upper = candidate.Trim().ToUpperInvariant();
                if (set.Add(upper)) list.Add(upper);
            }

            if (metadata != null)
            {
                if (metadata.TryGetValue("Airport", out var airport)) Add(airport);
                if (metadata.TryGetValue("AirportHint", out var hint)) Add(hint);
            }

            Add(ParseAirportFromText(entry?.Title));
            Add(ParseAirportFromText(entry?.FormName));

            airports = list;
            return airports.Count > 0;
        }

        private string ParseAirportFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var tokens = text.Split(new[] { ' ', '-', '_', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in tokens)
            {
                var trimmed = t.Trim();
                if (trimmed.Length >= 3 && trimmed.Length <= 5 && trimmed.All(char.IsLetter))
                {
                    return trimmed.ToUpperInvariant();
                }
            }

            return null;
        }

        private Form FindMetadataMatch(IEnumerable<Form> forms, WindowLayoutEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.TypeName)) return null;

            var metadata = entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candidates = forms.Where(f => string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0) return null;

            if (entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal) &&
                TryGetAirportCandidates(entry, metadata, out var airports))
            {
                foreach (var airport in airports)
                {
                    var arrival = candidates.FirstOrDefault(f =>
                        string.Equals(GetPropertyString(f, "Airport"), airport, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(f.Text) && f.Text.IndexOf(airport, StringComparison.OrdinalIgnoreCase) >= 0));
                    if (arrival != null) return arrival;
                }
            }

            if (entry.TypeName.EndsWith("StripWindow", StringComparison.Ordinal))
            {
                foreach (var candidate in candidates)
                {
                    if (MatchesStripWindowMetadata(candidate, metadata))
                    {
                        return candidate;
                    }
                }
            }

            if (entry.TypeName.EndsWith("ChatWindow", StringComparison.Ordinal) &&
                metadata.TryGetValue("Recipient", out var recipient) &&
                !string.IsNullOrWhiteSpace(recipient))
            {
                var chat = candidates.FirstOrDefault(f =>
                    string.Equals(GetStringField(f, "Recipient"), recipient, StringComparison.OrdinalIgnoreCase));
                if (chat != null) return chat;
            }

            if (entry.TypeName.EndsWith("ATISWindow", StringComparison.Ordinal) &&
                metadata.TryGetValue("ATISCallsign", out var atis) &&
                !string.IsNullOrWhiteSpace(atis))
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: FindMetadataMatch looking for ATIS: {atis}, found {candidates.Count} candidate windows");
                foreach (var candidate in candidates)
                {
                    var callsign = GetStringField(candidate, "ATISCallsign");
                    System.Diagnostics.Debug.WriteLine($"WindowManager:   Candidate ATISCallsign field: '{callsign}'");
                }

                var atisWindow = candidates.FirstOrDefault(f =>
                    string.Equals(GetStringField(f, "ATISCallsign"), atis, StringComparison.OrdinalIgnoreCase));
                if (atisWindow != null)
                {
                    System.Diagnostics.Debug.WriteLine($"WindowManager: Found matching ATIS window in FindMetadataMatch");
                    return atisWindow;
                }
                System.Diagnostics.Debug.WriteLine($"WindowManager: No matching ATIS window found in FindMetadataMatch");
            }

            var asdMatch = FindAsdMatch(candidates, metadata);
            if (asdMatch != null) return asdMatch;

            return null;
        }

        private bool MatchesStripWindowMetadata(Form form, Dictionary<string, string> metadata)
        {
            if (form == null || metadata == null) return false;

            var hasBeacon = metadata.TryGetValue("Beacon", out var beacon) && !string.IsNullOrWhiteSpace(beacon);
            var hasType = metadata.TryGetValue("StripWindowType", out var stripType) && !string.IsNullOrWhiteSpace(stripType);
            var hasHmi = metadata.TryGetValue("HMIState", out var hmiState) && !string.IsNullOrWhiteSpace(hmiState);

            if (!hasBeacon && !hasType && !hasHmi) return false;

            if (hasBeacon)
            {
                var actualBeacon = GetStringField(form, "Beacon");
                if (!string.Equals(actualBeacon, beacon, StringComparison.OrdinalIgnoreCase)) return false;
            }

            if (hasType)
            {
                var actualType = GetEnumField(form, "WindowType")?.ToString();
                if (!string.Equals(actualType, stripType, StringComparison.OrdinalIgnoreCase)) return false;
            }

            if (hasHmi)
            {
                var actualHmi = GetEnumField(form, "State")?.ToString();
                if (!string.Equals(actualHmi, hmiState, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        private Form FindAsdMatch(IEnumerable<Form> candidates, Dictionary<string, string> metadata)
        {
            if (metadata == null) return null;

            var hasDisplay = metadata.TryGetValue("DisplayPosition", out var display) && !string.IsNullOrWhiteSpace(display);
            var hasCallsign = metadata.TryGetValue("DisplayPositionCallsign", out var displayCallsign) && !string.IsNullOrWhiteSpace(displayCallsign);
            var hasFull = metadata.TryGetValue("DisplayPositionFullName", out var displayFullName) && !string.IsNullOrWhiteSpace(displayFullName);
            var hasAsdType = metadata.TryGetValue("AsdType", out var expectedAsdType) && !string.IsNullOrWhiteSpace(expectedAsdType);

            if (!hasDisplay && !hasCallsign && !hasFull && !hasAsdType) return null;

            foreach (var form in candidates)
            {
                var asd = GetAsdControl(form);
                if (asd == null) continue;

                var info = GetDisplayPositionInfo(asd);

                if (hasDisplay && !string.Equals(info?.Name, display, StringComparison.OrdinalIgnoreCase)) continue;
                if (hasCallsign && !string.Equals(info?.Callsign, displayCallsign, StringComparison.OrdinalIgnoreCase)) continue;
                if (hasFull && !string.Equals(info?.FullName, displayFullName, StringComparison.OrdinalIgnoreCase)) continue;

                if (hasAsdType)
                {
                    var actualAsdType = GetAsdType(asd);
                    if (!string.Equals(actualAsdType, expectedAsdType, StringComparison.OrdinalIgnoreCase)) continue;
                }

                return form;
            }

            return null;
        }

        private Form TryCreateWithFallback(WindowLayoutEntry entry)
        {
            try
            {
                var type = ResolveType(entry.TypeName);

                if (type == null || !typeof(Form).IsAssignableFrom(type)) return null;

                var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderBy(c => c.GetParameters().Length)
                    .FirstOrDefault();

                if (ctor == null) return null;

                var args = BuildFallbackArgs(ctor.GetParameters(), entry);
                var instance = ctor.Invoke(args) as Form;
                instance?.Show();
                if (instance != null)
                {
                    TrackPluginWindow(instance);
                    return instance;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: fallback create failed for {entry?.TypeName}: {ex}");
            }

            return null;
        }

        private object[] BuildFallbackArgs(ParameterInfo[] parameters, WindowLayoutEntry entry)
        {
            var metadata = entry?.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var args = new object[parameters?.Length ?? 0];

            for (var i = 0; i < args.Length; i++)
            {
                args[i] = BuildFallbackArg(parameters[i], entry, metadata);
            }

            return args;
        }

        private object BuildFallbackArg(ParameterInfo parameter, WindowLayoutEntry entry, Dictionary<string, string> metadata)
        {
            if (parameter == null) return null;

            var type = parameter.ParameterType;

            if (type == typeof(string))
            {
                var candidates = new[]
                {
                    metadata.TryGetValue("Beacon", out var beacon) ? beacon : null,
                    metadata.TryGetValue("Airport", out var airport) ? airport : null,
                    metadata.TryGetValue("ATISCallsign", out var atis) ? atis : null,
                    metadata.TryGetValue("Recipient", out var recipient) ? recipient : null,
                    !string.IsNullOrWhiteSpace(entry?.Title) ? entry.Title : null,
                    !string.IsNullOrWhiteSpace(entry?.FormName) ? entry.FormName : null
                };

                var value = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                return value ?? string.Empty;
            }

            if (type.IsEnum)
            {
                var enumValue = TryGetEnumFromMetadata(type, metadata);
                if (enumValue != null) return enumValue;

                var values = Enum.GetValues(type);
                if (values.Length > 0) return values.GetValue(0);
                return Activator.CreateInstance(type);
            }

            if (parameter.HasDefaultValue) return parameter.DefaultValue;
            if (type.IsValueType) return Activator.CreateInstance(type);
            return null;
        }

        private object TryGetEnumFromMetadata(Type enumType, Dictionary<string, string> metadata)
        {
            if (enumType == null || metadata == null || !enumType.IsEnum) return null;

            foreach (var kv in metadata)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;

                try
                {
                    var parsed = Enum.Parse(enumType, kv.Value, true);
                    if (Enum.IsDefined(enumType, parsed))
                    {
                        return parsed;
                    }
                }
                catch
                {
                    // ignore and keep trying
                }
            }

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

                var resolvedType = ResolveType(entry.TypeName);

                if (entry.TypeName.EndsWith("ChatWindow", StringComparison.Ordinal) &&
                    TryGetMetadataString(entry.Metadata, "Recipient", out var recipient))
                {
                    MMI.OpenPMWindow(recipient);
                    var w = FindWindow(entry.TypeName, f => string.Equals(GetStringField(f, "Recipient"), recipient, StringComparison.OrdinalIgnoreCase));
                    if (w != null) TrackPluginWindow(w);
                    return w;
                }

                if (entry.TypeName.EndsWith("ATISWindow", StringComparison.Ordinal) &&
                    TryGetMetadataString(entry.Metadata, "ATISCallsign", out var atis))
                {
                    var w = RestoreAtisWindow(entry, atis);
                    if (w != null) return w;
                }

                if (entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
                {
                    if (TryGetAirportCandidates(entry, entry.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), out var airports))
                    {
                        var w = RestoreArrivalWindow(entry, airports);
                        if (w != null) return w;
                    }

                    // Fallback: try without an airport to let vatsys pick the default.
                    var fallbackArrival = RestoreArrivalWindow(entry, new List<string>());
                    if (fallbackArrival != null) return fallbackArrival;
                }

                if (entry.TypeName.EndsWith("StripWindow", StringComparison.Ordinal) &&
                    TryGetMetadataString(entry.Metadata, "Beacon", out var beacon) &&
                    TryGetMetadataString(entry.Metadata, "StripWindowType", out var stripTypeName))
                {
                    TryGetMetadataString(entry.Metadata, "HMIState", out var hmi);
                    CreateStripWindow(stripTypeName, hmi, beacon);
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

                var menuFields = form.GetType().GetFields(InstanceFlags)
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
                if (!(string.Equals(firstToken, "A", StringComparison.OrdinalIgnoreCase) && !entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal)))
                {
                    Add(firstToken);
                }
            }
            Add(entry.FormName);
            if (!string.IsNullOrWhiteSpace(entry.TypeName))
            {
                Add(entry.TypeName.Split('.').LastOrDefault());
            }
            Add("VSCS"); // ensure VSCS menu text is considered
            if (IsOzStrips(entry)) Add("OzStrips");

            // Avoid using arrival-style titles for strip windows to prevent accidental arrival list opens.
            if (entry.TypeName.EndsWith("StripWindow", StringComparison.Ordinal))
            {
                list = list.Where(s => !(s?.StartsWith("A:", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            }

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

                if (IsOzStrips(entry))
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
                var windowsMenuField = mainForm.GetType().GetField("windowsToolStripMenuItem", InstanceFlags);
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
            var fields = form.GetType().GetFields(InstanceFlags);
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
                    asdControl.GetType().GetField("StoredCentreLL", InstanceFlags)?.GetValue(asdControl),
                    asdControl.GetType().GetField("settingVisCenter", InstanceFlags)?.GetValue(asdControl)
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
                    var prop = asd.GetType().GetProperty("DisplayPosition", InstanceFlags);
                    prop?.SetValue(asd, targetPosition, null);
                    applied = true;
                }

                if (!applied && remainingRetries > 0)
                {
                    var delay = remainingRetries == 1 ? MediumRetryDelayMs : ShortRetryDelayMs;
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
            var menuField = form.GetType().GetField("positionsToolStripMenuItem", InstanceFlags);
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
                var menuField = form.GetType().GetField("positionsToolStripMenuItem", InstanceFlags);
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

        private bool IsArrivalWindow(Form form)
        {
            var typeName = form?.GetType().FullName ?? string.Empty;
            return typeName.EndsWith("SequenceWindow", StringComparison.Ordinal);
        }

        private bool IsAtisWindow(Form form)
        {
            var typeName = form?.GetType().FullName ?? string.Empty;
            return typeName.EndsWith("ATISWindow", StringComparison.Ordinal);
        }

        private bool ShouldAlwaysSave(Form form)
        {
            return IsArrivalWindow(form) || IsAtisWindow(form) || IsVsCs(form);
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

        private void SafeLogArrival(string message)
        {
            // Debug logging disabled for release to avoid creating extra files.
        }

        private void DumpArrivalWindows(string reason)
        {
            // Debug logging disabled for release.
        }

        private void CloseNonDefaultOnPositionChange()
        {
            try
            {
                ClosePluginWindows();
                TryCloseOzStrips();

                foreach (Form form in Application.OpenForms)
                {
                    if (form == null || form.IsDisposed) continue;
                    if (IsMainVatSysForm(form)) continue;
                    if (IsVsCs(form)) continue; // keep VSCS alive
                    if (IsOzStrips(form)) { TryCloseOzStrips(); continue; }

                    try
                    {
                        form.Close();
                    }
                    catch { }
                }
            }
            catch
            {
                // ignore
            }
        }

        private void TryCloseOzStrips()
        {
            try
            {
                var oz = FindOzStripsByTitle();
                if (oz != null && !oz.IsDisposed)
                {
                    oz.Close();
                }
            }
            catch
            {
                // ignore
            }
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
                    var delayMs = remainingRetries >= 3 ? ShortRetryDelayMs : (remainingRetries == 2 ? MediumRetryDelayMs : FinalRetryDelayMs);
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
                    var delayMs = remainingRetries >= 3 ? ShortRetryDelayMs : (remainingRetries == 2 ? MediumRetryDelayMs : FinalRetryDelayMs);
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
                var storedRangeField = asd.GetType().GetField("StoredRange", InstanceFlags);
                if (storedRangeField != null)
                {
                    var rounded = (int)Math.Round(r);
                    storedRangeField.SetValue(asd, rounded);
                }

                var setZoom = asd.GetType().GetMethod(
                    "SetZoom",
                    InstanceFlags,
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
                var mainRangeField = typeof(MMI).GetField("MAIN_ASD_RANGE", StaticFlags);
                if (mainRangeField != null)
                {
                    var value = mainRangeField.FieldType == typeof(double)
                        ? (object)r
                        : Convert.ChangeType(r, mainRangeField.FieldType, CultureInfo.InvariantCulture);
                    mainRangeField.SetValue(null, value);
                }
            }

            var refresh = asd.GetType().GetMethod("OnRangeChanged", InstanceFlags);
            refresh?.Invoke(asd, null);

            try
            {
                var setRange = asd.GetType().GetMethod("SetRange", InstanceFlags, null, Type.EmptyTypes, null);
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

                var storedCentreField = asd.GetType().GetField("StoredCentreLL", InstanceFlags);
                storedCentreField?.SetValue(asd, coord);

                var setCentre = asd.GetType().GetMethod(
                    "SetDisplayCenter",
                    InstanceFlags,
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
                var eventField = asd.GetType().GetField("RangeChanged", InstanceFlags);
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
                var menuField = asdControl.FindForm()?.GetType().GetField("mapsToolStripMenuItem", InstanceFlags);
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

        private void EnsureMapMenuBuilt(Form form, ToolStripMenuItem menu)
        {
            if (form == null || menu == null) return;

            try
            {
                var dropDownOpened = form.GetType().GetMethod("mapsToolStripMenuItem_DropDownOpened", InstanceFlags);
                dropDownOpened?.Invoke(form, new object[] { menu, EventArgs.Empty });
            }
            catch
            {
                // best effort
            }
        }

        private void ForceAsdRender(Control asdControl)
        {
            if (asdControl == null) return;

            try
            {
                var render = asdControl.GetType().GetMethod("Render", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (render != null)
                {
                    render.Invoke(asdControl, null);
                    return;
                }
            }
            catch
            {
                // ignore and fall back
            }

            try
            {
                asdControl.Invalidate(true);
                asdControl.Update();
            }
            catch { }
        }

        private void ScheduleMapRefresh(Control asdControl)
        {
            if (asdControl == null) return;

            void Refresh()
            {
                try
                {
                    asdControl.Invalidate(true);
                    asdControl.Update();
                }
                catch { }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(180);
                    RunOnUiThread(Refresh);
                    await Task.Delay(ShortRetryDelayMs);
                    RunOnUiThread(Refresh);
                }
                catch { }
            });
        }

        private void EnsureMapVisibility(Control asdControl, ToolStripMenuItem item, bool shouldBeVisible)
        {
            if (asdControl == null || item == null) return;

            try
            {
                var map = item.Tag;
                if (map == null) return;

                var setMapVisible = asdControl.GetType().GetMethod("SetMapVisible", InstanceFlags);
                setMapVisible?.Invoke(asdControl, new object[] { map, shouldBeVisible });
            }
            catch
            {
                // ignore and rely on the normal menu handlers
            }
        }

        private void ApplyStripState(Form form, Dictionary<string, string> metadata)
        {
            if (form == null || metadata == null) return;
            if (!metadata.TryGetValue("HMIState", out var hmiState) || string.IsNullOrWhiteSpace(hmiState)) return;

            try
            {
                var enumType = typeof(MMI).Assembly.GetType("vatsys.MMI+HMIStates");
                if (enumType == null || !enumType.IsEnum) return;

                var parsed = Enum.Parse(enumType, hmiState, true);

                var field = form.GetType().GetField("State", InstanceFlags);
                field?.SetValue(form, parsed);

                var prop = form.GetType().GetProperty("State", InstanceFlags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(form, parsed);
                }

                form.Invalidate();
            }
            catch
            {
                // ignore
            }
        }

        private void ApplyCheckedMaps(Form form, string mapsValue)
        {
            if (string.IsNullOrWhiteSpace(mapsValue)) return;

            var desired = new HashSet<string>(mapsValue.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            if (desired.Count == 0) return;

            try
            {
                var asd = GetAsdControl(form);
                var menuField = form.GetType().GetField("mapsToolStripMenuItem", InstanceFlags);
                var menu = menuField?.GetValue(form) as ToolStripMenuItem;
                if (menu == null) return;

                RunOnUiThread(() =>
                {
                    try
                    {
                        EnsureMapMenuBuilt(form, menu);
                        ApplyMapState(asd, menu.DropDownItems, desired, enforceVisibility: true);
                        asd?.Invalidate(true);
                        ScheduleMapRefresh(asd);

                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(320);
                            RunOnUiThread(() =>
                            {
                                try
                                {
                                    ApplyMapState(asd, menu.DropDownItems, desired, enforceVisibility: true);
                                    ScheduleMapRefresh(asd);
                                }
                                catch { }
                            });
                        });
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowManager: apply maps failed: {ex}");
            }
        }

        private void ApplyMapState(Control asdControl, ToolStripItemCollection items, HashSet<string> desired, bool enforceVisibility)
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
                    else if (enforceVisibility)
                    {
                        EnsureMapVisibility(asdControl, mi, shouldBeChecked);
                    }

                    if (mi.HasDropDownItems)
                    {
                        ApplyMapState(asdControl, mi.DropDownItems, desired, enforceVisibility);
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

        private void EnsureVSCSPlacement(Form form, User32.WINDOWPLACEMENT placement, Dictionary<string, string> metadata)
        {
            if (form == null || form.IsDisposed) return;
            if (!IsVsCs(form)) return;

            void Reapply()
            {
                if (form == null || form.IsDisposed) return;
                ApplyPlacement(form, placement, metadata);
            }

            var targetRect = GetTargetRect(placement, metadata);
            // VSCS may reposition itself, so retry with delays
            SchedulePlacementRetries(form, Reapply, targetRect, 150, 400, 900, 1600, 2800);
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
                var field = typeof(MMI).GetField("BaseFormPlacements", StaticFlags);
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
                var rangeField = mmiType.GetField("MAIN_ASD_RANGE", StaticFlags);

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
                var rangeField = mmiType.GetField("MAIN_ASD_RANGE", StaticFlags);

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
                var primeField = typeof(MMI).GetField("primePosition", StaticFlags);
                primeField?.SetValue(null, position);

                var evt = typeof(MMI).GetField("PrimePositonChanged", StaticFlags);
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

                var menuField = main.GetType().GetField("positionsToolStripMenuItem", InstanceFlags);
                var menu = menuField?.GetValue(main) as ToolStripMenuItem;
                if (menu == null) return;

                var expand = main.GetType().GetMethod("ExpandPositionMenuItems", InstanceFlags);
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
                    var handler = main.GetType().GetMethod("positionsToolStripMenuItem_DropDownOpened", InstanceFlags);
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

        private void OverrideLayout(LayoutInfo layout, string position)
        {
            try
            {
                var confirm = MessageBox.Show(
                    $"Override layout \"{layout.LayoutName}\" with current window configuration?",
                    "Override layout",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (confirm != DialogResult.Yes) return;

                var snapshot = new LayoutSnapshot
                {
                    Position = position,
                    LayoutName = layout.LayoutName,
                    SavedUtc = DateTime.UtcNow,
                    Windows = new List<WindowLayoutEntry>(),
                    Asd = GetAsdState(),
                    ControlledSectors = GetControlledSectorNames(),
                    StripMode = GetStripModeName(),
                    StripSortMode = GetStripSortModeName()
                };

                foreach (Form form in EnumerateFormsForSave())
                {
                    var entry = BuildEntry(form);
                    if (entry != null)
                    {
                        if (entry.TypeName.EndsWith("SequenceWindow", StringComparison.Ordinal))
                        {
                            if (entry.Metadata != null && entry.Metadata.TryGetValue("Airport", out var ap))
                            {
                                SafeLogArrival($"Captured arrival window override: {ap} title={entry.Title}");
                            }
                            else
                            {
                                SafeLogArrival($"Captured arrival window override: (no airport) title={entry.Title}");
                            }
                        }
                        snapshot.Windows.Add(entry);
                    }
                }

                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(layout.Path, json);

                MessageBox.Show($"Layout \"{layout.LayoutName}\" has been overridden for {position}.", "vatSys Window Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to override layout:\n{ex.Message}", "Override layout", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
        public string StripMode { get; set; }
        public string StripSortMode { get; set; }
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


