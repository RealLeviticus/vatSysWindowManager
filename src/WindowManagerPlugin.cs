using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace vatSysWindowManager
{
    /// <summary>
    /// The plugin's entry point: the Window Layouts menu, and the two moments a layout loads by
    /// itself (start-up and a change of position).
    /// </summary>
    [Export(typeof(IPlugin))]
    public class WindowManagerPlugin : IPlugin
    {
        private const string Title = "vatSys Window Manager";
        private const string StartupJob = "startup";

        private static int instances;

        private readonly ToolStripMenuItem rootMenu;
        private readonly ToolStripMenuItem layoutsMenu;
        private Dictionary<string, string> autoLoad = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool started;
        private bool autoLoadQueued;

        public string Name => "WindowManager";

        public WindowManagerPlugin()
        {
            // vatSys loads every DLL under its Plugins folders. Two copies of this one would each add
            // a menu and each restore layouts over the other.
            if (Interlocked.Increment(ref instances) > 1)
            {
                Log.Warn("A second copy of the plugin was loaded and has been left idle. Remove the duplicate vatSysWindowManager.dll from vatSys's Plugins folders.");
                try { Errors.Add(new Exception("vatSys Window Manager is installed twice. Remove one copy of vatSysWindowManager.dll from the Plugins folders."), "Window Layouts"); } catch { }
                return;
            }

            try
            {
                var saveItem = new ToolStripMenuItem("Save current layout");
                saveItem.Click += (s, e) => Ui.Guard(SaveNewLayout, "Saving a layout")();

                layoutsMenu = new ToolStripMenuItem("Layouts");
                layoutsMenu.DropDownOpening += (s, e) => Ui.Guard(BuildLayoutsMenu, "Building the Layouts menu")();
                layoutsMenu.DropDownItems.Add(new ToolStripMenuItem("(loading)") { Enabled = false });

                var folderItem = new ToolStripMenuItem("Open layouts folder");
                folderItem.Click += (s, e) => Ui.Guard(() => Process.Start("explorer.exe", "\"" + LayoutStore.Root + "\""), "Opening the layouts folder")();

                rootMenu = new ToolStripMenuItem("Window Layouts");
                rootMenu.DropDownItems.Add(saveItem);
                rootMenu.DropDownItems.Add(layoutsMenu);
                rootMenu.DropDownItems.Add(new ToolStripSeparator());
                rootMenu.DropDownItems.Add(folderItem);

                // The main form reads the plugin menu items straight after it has constructed the
                // plugins, so adding it here needs no waiting.
                MMI.AddCustomMenuItem(new CustomToolStripMenuItem(CustomToolStripMenuItemWindowType.Main, CustomToolStripMenuItemCategory.Windows, rootMenu));
                PluginWindows.OwnMenu = rootMenu;
                DescribeMenu = DescribeLayoutsMenu;

                Start();
            }
            catch (Exception ex)
            {
                Log.Error("Start-up failed", ex);
            }
        }

        public void OnFDRUpdate(FDP2.FDR updated) { }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }

        private void Start()
        {
            LayoutStore.MigrateLegacy();
            autoLoad = LayoutStore.LoadAutoLoadMap();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Log.Info($"Window Manager {version} started in vatSys {Vatsys.Assembly.GetName().Version}");

            var missing = Vatsys.SelfCheck();
            if (missing.Count > 0)
            {
                Log.Warn("This version of vatSys is missing members the plugin uses; the features that need them are off: " + string.Join("; ", missing));
            }

            // Public event: the layout for the new position loads once vatSys has finished changing to it.
            MMI.PrimePositonChanged += (s, e) => Ui.Guard(OnPositionChanged, "Position change")();

            if (!EnsureUi())
            {
                Log.Warn("The plugin was not constructed on the UI thread; automatic loading starts with the first use of the menu.");
                return;
            }
        }

        /// <summary>Sets up the UI-thread timer. True once it exists. Safe to call from any menu handler.</summary>
        private bool EnsureUi()
        {
            if (Ui.Ready) return true;
            if (!Ui.Init()) return false;

            PluginWindows.Start();
            DebugCommands.Start();
            Ui.Poll(StartupJob, 200, 0, () =>
            {
                if (started) return true;
                if (!VatsysIsUp()) return false;
                started = true;
                AutoLoad("start-up");
                return true;
            });
            return true;
        }

        // vatSys constructs its plugins part-way through building the main form. Nothing can be
        // restored until the form is on screen and the strip windows exist.
        private static bool VatsysIsUp()
        {
            var main = Vatsys.MainForm;
            if (!Vatsys.Alive(main) || !main.IsHandleCreated || !main.Visible) return false;
            return !Vatsys.StripsAvailable || Vatsys.StateStripWindow("Jurisdiction") != null;
        }

        // ---- loading by itself ------------------------------------------------------------------------

        private void OnPositionChanged()
        {
            // Before start-up has finished, the start-up load covers it.
            if (!started || autoLoadQueued) return;

            // MMI.PrimePositonChanged is raised from inside MainForm.SetPosition, before vatSys has
            // loaded the new position's own strip windows and display. Restoring here would be undone
            // straight away, so the load is posted to run after SetPosition has returned.
            autoLoadQueued = true;
            Ui.Post(() =>
            {
                autoLoadQueued = false;
                AutoLoad("position change");
            }, "Loading the layout for the new position");
        }

        private void AutoLoad(string reason)
        {
            // Whatever was still being finished for the previous layout no longer applies.
            Restore.Cancel();

            var position = Vatsys.PositionKey();
            if (!autoLoad.TryGetValue(position, out var layoutName))
            {
                // No layout is set to load for this position: leave vatSys's own defaults untouched.
                return;
            }

            var info = LayoutStore.ListFor(position).FirstOrDefault(l => string.Equals(l.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase));
            if (info == null)
            {
                Log.Warn($"Auto-load layout \"{layoutName}\" for {position} no longer exists");
                return;
            }

            Log.Info($"Auto-loading \"{layoutName}\" for {position} ({reason})");
            Restore.Apply(LayoutStore.Load(info.Path));
        }

        // ---- menu ---------------------------------------------------------------------------------------

        /// <summary>For the debug command hook: builds the Layouts menu and describes it.</summary>
        internal static Func<string> DescribeMenu;

        private string DescribeLayoutsMenu()
        {
            BuildLayoutsMenu();
            var lines = new List<string>();
            foreach (ToolStripItem item in layoutsMenu.DropDownItems)
            {
                var menu = item as ToolStripMenuItem;
                var children = menu == null ? string.Empty : string.Join(" | ", menu.DropDownItems.Cast<ToolStripItem>().Select(c => c is ToolStripSeparator ? "-" : c.Text + ((c as ToolStripMenuItem)?.Checked == true ? " [x]" : string.Empty)));
                lines.Add($"{item.Text}{(menu?.Checked == true ? " [x]" : string.Empty)}{(item.Enabled ? string.Empty : " (disabled)")}: {children}");
            }
            return string.Join("\n", lines);
        }

        private void BuildLayoutsMenu()
        {
            EnsureUi();
            layoutsMenu.DropDownItems.Clear();

            var position = Vatsys.PositionKey();
            var layouts = LayoutStore.ListFor(position);
            if (layouts.Count == 0)
            {
                layoutsMenu.DropDownItems.Add(new ToolStripMenuItem("(no saved layouts for this position)") { Enabled = false });
                return;
            }

            foreach (var layout in layouts)
            {
                var info = layout;
                var isAuto = autoLoad.TryGetValue(position, out var auto) && string.Equals(auto, info.LayoutName, StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(info.LayoutName) { Checked = isAuto };

                var load = new ToolStripMenuItem("Load");
                load.Click += (s, e) => Ui.Guard(() => LoadLayout(info), "Loading a layout")();

                var autoItem = new ToolStripMenuItem("Auto load for this position") { Checked = isAuto };
                autoItem.Click += (s, e) => Ui.Guard(() =>
                {
                    if (isAuto) autoLoad.Remove(position);
                    else autoLoad[position] = info.LayoutName;
                    LayoutStore.SaveAutoLoadMap(autoLoad);
                }, "Changing auto-load")();

                var sectorsOn = false;
                try { sectorsOn = LayoutStore.Load(info.Path).RestoreSectors; } catch { }
                var sectors = new ToolStripMenuItem("Restore sectors (only when not connected)") { Checked = sectorsOn };
                sectors.Click += (s, e) => Ui.Guard(() => ToggleSectors(info, !sectorsOn), "Changing the sectors option")();

                var replace = new ToolStripMenuItem("Override with current layout");
                replace.Click += (s, e) => Ui.Guard(() => OverrideLayout(info), "Overriding a layout")();

                var delete = new ToolStripMenuItem("Delete");
                delete.Click += (s, e) => Ui.Guard(() => DeleteLayout(info, position), "Deleting a layout")();

                item.DropDownItems.Add(load);
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(autoItem);
                item.DropDownItems.Add(sectors);
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(replace);
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(delete);
                layoutsMenu.DropDownItems.Add(item);
            }
        }

        private void LoadLayout(LayoutInfo info)
        {
            EnsureUi();
            if (!File.Exists(info.Path))
            {
                MessageBox.Show(Vatsys.MainForm, $"Layout \"{info.LayoutName}\" no longer exists.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Layout layout;
            try
            {
                layout = LayoutStore.Load(info.Path);
            }
            catch (Exception ex)
            {
                Log.Error($"Reading layout '{info.Path}' failed", ex);
                MessageBox.Show(Vatsys.MainForm, $"Layout \"{info.LayoutName}\" could not be read:\n{ex.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var position = Vatsys.PositionKey();
            if (!string.Equals(layout.Position, position, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(Vatsys.MainForm, $"Layout \"{layout.LayoutName}\" was saved for position \"{layout.Position}\" and cannot be loaded while you are on \"{position}\".", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Restore.Apply(layout);
        }

        private void SaveNewLayout()
        {
            EnsureUi();
            var position = Vatsys.PositionKey();
            var name = PromptForLayoutName(position);
            if (string.IsNullOrWhiteSpace(name)) return;

            var path = LayoutStore.PathFor(position, name);
            var keepSectors = false;
            if (File.Exists(path))
            {
                var answer = MessageBox.Show(Vatsys.MainForm, $"A layout called \"{name}\" already exists for {position}. Replace it?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                try { keepSectors = LayoutStore.Load(path).RestoreSectors; } catch { }
            }

            SaveTo(path, position, name, keepSectors);
            MessageBox.Show(Vatsys.MainForm, $"Saved layout \"{name}\" for {position}.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OverrideLayout(LayoutInfo info)
        {
            EnsureUi();
            var answer = MessageBox.Show(Vatsys.MainForm, $"Override layout \"{info.LayoutName}\" with the current windows?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            var keepSectors = false;
            try { keepSectors = LayoutStore.Load(info.Path).RestoreSectors; } catch { }

            SaveTo(info.Path, Vatsys.PositionKey(), info.LayoutName, keepSectors);
            MessageBox.Show(Vatsys.MainForm, $"Layout \"{info.LayoutName}\" now holds the current windows.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void SaveTo(string path, string position, string name, bool restoreSectors)
        {
            try
            {
                var layout = Capture.Snapshot(position, name);
                layout.RestoreSectors = restoreSectors;
                LayoutStore.Save(layout, path);
                Log.Info($"Saved \"{name}\" for {position}: {layout.Windows.Count} windows, strips {layout.Strips?.Mode}/{layout.Strips?.SortMode}");
            }
            catch (Exception ex)
            {
                Log.Error($"Saving layout \"{name}\" failed", ex);
                MessageBox.Show(Vatsys.MainForm, $"The layout could not be saved:\n{ex.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
            }
        }

        private static void ToggleSectors(LayoutInfo info, bool enable)
        {
            if (enable)
            {
                var answer = MessageBox.Show(Vatsys.MainForm,
                    "With this on, loading the layout sets your controlled sectors to the ones held when it was saved.\n\n" +
                    "It only does so while you are not connected to the network, and never from a layout saved with no sectors.\n\nTurn it on?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            var layout = LayoutStore.Load(info.Path);
            layout.RestoreSectors = enable;
            LayoutStore.Save(layout, info.Path);
        }

        private void DeleteLayout(LayoutInfo info, string position)
        {
            var answer = MessageBox.Show(Vatsys.MainForm, $"Delete layout \"{info.LayoutName}\" for position \"{position}\"?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;

            if (File.Exists(info.Path)) File.Delete(info.Path);
            if (autoLoad.TryGetValue(position, out var auto) && string.Equals(auto, info.LayoutName, StringComparison.OrdinalIgnoreCase))
            {
                autoLoad.Remove(position);
                LayoutStore.SaveAutoLoadMap(autoLoad);
            }
            Log.Info($"Deleted \"{info.LayoutName}\" for {position}");
        }

        private static string PromptForLayoutName(string defaultName)
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
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(300, 110);

                label.AutoSize = true;
                label.Text = "Layout name:";
                label.Location = new Point(10, 10);

                text.Location = new Point(10, 30);
                text.Width = 280;
                text.Text = defaultName ?? string.Empty;

                ok.Text = "Save";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(135, 70, 70, 26);

                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(220, 70, 70, 26);

                form.Controls.AddRange(new Control[] { label, text, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog(Vatsys.MainForm) == DialogResult.OK ? text.Text.Trim() : null;
            }
        }
    }
}
