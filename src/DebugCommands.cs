using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace vatSysWindowManager
{
    /// <summary>
    /// A way to drive the plugin from a script, for testing a build in a copy of vatSys without
    /// clicking through its menus. It does nothing unless the VATSYS_WINDOWMANAGER_DEBUG
    /// environment variable is set when vatSys starts.
    ///
    /// When it is set, the plugin looks for a file called command.txt in the layouts folder a few
    /// times a second, runs each line as a command on the UI thread, deletes the file, and writes
    /// what happened to the log. The commands are listed in the README under "Testing a build in
    /// a copy of vatSys".
    /// </summary>
    internal static class DebugCommands
    {
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VATSYS_WINDOWMANAGER_DEBUG"));

        internal static void Start()
        {
            if (!Enabled) return;
            Log.Info("Debug commands are on (VATSYS_WINDOWMANAGER_DEBUG is set)");
            Ui.Poll("debug-commands", 300, 0, () =>
            {
                var path = Path.Combine(LayoutStore.Root, "command.txt");
                if (!File.Exists(path)) return false;

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(path);
                    File.Delete(path);
                }
                catch (IOException)
                {
                    return false; // still being written; next time
                }

                foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
                {
                    try
                    {
                        Log.Debug("command: " + line);
                        Run(line);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Command '{line}' failed", ex.GetBaseException());
                    }
                }
                Log.Debug("commands done");
                return false;
            });
        }

        private static string Describe(FloatRect r) => r == null ? "-" : $"({r.Left:F0},{r.Top:F0})-({r.Right:F0},{r.Bottom:F0})";

        private static void Run(string line)
        {
            var space = line.IndexOf(' ');
            var verb = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
            var argument = space < 0 ? string.Empty : line.Substring(space + 1).Trim();
            var position = Vatsys.PositionKey();

            switch (verb)
            {
                case "save":
                {
                    var layout = Capture.Snapshot(position, argument);
                    LayoutStore.Save(layout, LayoutStore.PathFor(position, argument));
                    Log.Debug($"saved \"{argument}\" for {position}: {layout.Windows.Count} windows");
                    break;
                }
                case "load":
                {
                    var info = LayoutStore.ListFor(position).FirstOrDefault(l => string.Equals(l.LayoutName, argument, StringComparison.OrdinalIgnoreCase));
                    if (info == null) throw new InvalidOperationException($"no layout \"{argument}\" for {position}");
                    Restore.Apply(LayoutStore.Load(info.Path));
                    break;
                }
                case "position":
                {
                    // The same private method the Position menu calls, so the plugin sees exactly what
                    // it sees when the controller changes position.
                    var target = LogicalPositions.Positions.FirstOrDefault(p => string.Equals(p.Name, argument, StringComparison.OrdinalIgnoreCase));
                    if (target == null) throw new InvalidOperationException($"no position \"{argument}\"");
                    var main = Vatsys.MainForm;
                    var method = main.GetType().GetMethod("SetPosition", I, null, new[] { typeof(LogicalPositions.Position) }, null);
                    if (method == null) throw new MissingMethodException("MainForm.SetPosition");
                    method.Invoke(main, new object[] { target });
                    Log.Debug($"position is now {Vatsys.PositionKey()}");
                    break;
                }
                case "open":
                {
                    // open asd | open ground | open single <MMI field> | open atis <callsign> | open arrival <airport>
                    var parts = argument.Split(new[] { ' ' }, 2);
                    var what = parts[0].ToLowerInvariant();
                    var name = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    if (what == "asd" || what == "ground") Vatsys.CreateDisplayWindow(what == "ground", null);
                    else if (what == "single") Vatsys.OpenSingle(Vatsys.FindSingle(name));
                    else if (what == "atis") MMI.OpenATISWindow(name);
                    else if (what == "arrival") MMI.OpenArrivalListWindow(name);
                    else throw new InvalidOperationException("open what?");
                    break;
                }
                case "window":
                {
                    // window <title part> | minimize|maximize|normal|close|move x y w h
                    var parts = argument.Split('|');
                    var title = parts[0].Trim();
                    var action = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    var form = Vatsys.AllForms().FirstOrDefault(f => Vatsys.IsVisible(f) && (f.Text ?? string.Empty).IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (form == null) throw new InvalidOperationException($"no visible window titled like \"{title}\"");
                    var words = action.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    switch (words.Length == 0 ? string.Empty : words[0].ToLowerInvariant())
                    {
                        case "minimize": form.WindowState = FormWindowState.Minimized; break;
                        case "maximize": form.WindowState = FormWindowState.Maximized; break;
                        case "normal": form.WindowState = FormWindowState.Normal; break;
                        case "close": form.Close(); break;
                        case "front": form.BringToFront(); break;
                        case "move":
                            form.WindowState = FormWindowState.Normal;
                            form.SetBounds(int.Parse(words[1]), int.Parse(words[2]), int.Parse(words[3]), int.Parse(words[4]));
                            break;
                        default: throw new InvalidOperationException("window action?");
                    }
                    break;
                }
                case "strips":
                {
                    // strips mode <State|Beacon> | strips sort <Alpha|Time|Level> | strips beacon <Type> <Point> | strips show <state> | strips hide <state>
                    var words = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    switch (words[0].ToLowerInvariant())
                    {
                        case "mode": Vatsys.SetStripMode(words[1]); break;
                        case "sort": Vatsys.SetStripSortMode(words[1]); break;
                        case "beacon": Vatsys.CreateBeacon(words[1], words[2]); break;
                        case "show": Vatsys.ShowStripWindow(Vatsys.StateStripWindow(words[1])); break;
                        case "hide": Vatsys.StateStripWindow(words[1])?.Close(); break;
                        case "showbeacon":
                            Vatsys.ShowStripWindow(Vatsys.BeaconWindows().First(b => string.Equals(b.Point, words[1], StringComparison.OrdinalIgnoreCase)).Form);
                            break;
                        default: throw new InvalidOperationException("strips what?");
                    }
                    break;
                }
                case "pluginmenu":
                {
                    // pluginmenu <Category> > <text> > <text> : clicks another plugin's menu item
                    var parts = argument.Split('>').Select(p => p.Trim()).ToList();
                    var opener = new MenuOpener { Category = parts[0], Path = parts.Skip(1).ToList() };
                    Log.Debug($"clicked '{opener}': {PluginWindows.Click(opener)}");
                    break;
                }
                case "menu":
                {
                    foreach (var text in (WindowManagerPlugin.DescribeMenu?.Invoke() ?? string.Empty).Split('\n')) Log.Debug("  layouts menu: " + text);
                    break;
                }
                case "userclick":
                {
                    // userclick <Category> > <text> : clicks a plugin menu item as the controller would,
                    // so the plugin's own watching and learning sees it
                    var parts = argument.Split('>').Select(p => p.Trim()).ToList();
                    var item = PluginWindows.Resolve(new MenuOpener { Category = parts[0], Path = parts.Skip(1).ToList() });
                    if (item == null) throw new InvalidOperationException("no such plugin menu item");
                    item.PerformClick();
                    break;
                }
                case "plugins":
                {
                    foreach (var item in Vatsys.PluginMenuItems())
                    {
                        Log.Debug($"  menu item [{item.Window}/{item.Category}{(string.IsNullOrEmpty(item.CustomCategoryName) ? string.Empty : "/" + item.CustomCategoryName)}] \"{item.Item.Text}\"" +
                                  ((item.Item as ToolStripMenuItem)?.HasDropDownItems == true ? " > " + string.Join(" | ", ((ToolStripMenuItem)item.Item).DropDownItems.Cast<ToolStripItem>().Select(c => c.Text)) : string.Empty));
                    }
                    foreach (var form in PluginWindows.AllWindows())
                    {
                        Log.Debug($"  plugin window {(Vatsys.IsVisible(form) ? "vis" : "hid")} \"{form.Text}\" {form.GetType().FullName} name={form.Name} closable={PluginWindows.CanUserClose(form)} opener='{PluginWindows.OpenerFor(form)}' learned={PluginWindows.HasLearnedOpener(form.GetType().FullName)}");
                    }
                    break;
                }
                case "maps":
                {
                    // maps <title part> : lists the first maps in that window's Maps menu and whether each is showing
                    var form = Vatsys.AllForms().First(f => Vatsys.IsVisible(f) && Vatsys.AsdControl(f) != null && (f.Text ?? string.Empty).IndexOf(argument, StringComparison.OrdinalIgnoreCase) >= 0);
                    var view = Vatsys.ReadView(form);
                    Log.Debug($"  \"{form.Text}\" showing {view.Maps?.Count}: {string.Join("; ", view.Maps ?? new System.Collections.Generic.List<string>())}");
                    Log.Debug($"  atcList={Describe(view.AtcList)} scale={Describe(view.Scale)} stored={view.StoredLat},{view.StoredLon},{view.StoredRange}");
                    break;
                }
                case "view":
                {
                    // view <title part>~<lat> <lon> <range>~<map key to show>;<map key>...~<atc left> <atc top>
                    var parts = argument.Split('~');
                    var form = Vatsys.AllForms().First(f => Vatsys.IsVisible(f) && Vatsys.AsdControl(f) != null && (f.Text ?? string.Empty).IndexOf(parts[0].Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                    var now = Vatsys.ReadView(form);
                    var numbers = parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(n => double.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var view = new AsdView { Position = now.Position, CentreLat = numbers[0], CentreLon = numbers[1], Range = numbers[2], HasStoredView = false };
                    if (parts.Length > 2 && parts[2].Trim().Length > 0) view.Maps = parts[2].Split(';').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
                    if (parts.Length > 3)
                    {
                        var at = parts[3].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(n => float.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        view.AtcList = new FloatRect { Left = at[0], Top = at[1], Right = at[0] + 120, Bottom = at[1] + 80 };
                        view.Scale = new FloatRect { Left = at[0] + 200, Top = at[1], Right = at[0] + 320, Bottom = at[1] + 40 };
                    }
                    Vatsys.ApplyView(form, view, form == Vatsys.MainForm, null);
                    break;
                }
                case "dump":
                {
                    var layout = Capture.Snapshot(position, "dump");
                    Log.Debug($"dump {position}: strips {layout.Strips?.Mode}/{layout.Strips?.SortMode} infill={layout.DynamicInfill} sectors={string.Join(",", layout.ControlledSectors ?? new System.Collections.Generic.List<string>())}");
                    foreach (var w in layout.Windows)
                    {
                        Log.Debug($"  {w.Kind,-11} {w.Key,-14} {(w.Visible ? "vis" : "hid")} {w.State,-9} z={w.Z,-3} normal={w.Normal} actual={w.Actual} \"{w.Title}\" {w.TypeName}" +
                                  (w.Asd == null ? string.Empty : $" pos={w.Asd.Position} centre={w.Asd.CentreLat:F4},{w.Asd.CentreLon:F4} range={w.Asd.Range:F2} maps={w.Asd.Maps?.Count}") +
                                  (w.Opener == null ? string.Empty : $" opener='{w.Opener}'"));
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown command");
            }
        }
    }
}
