using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace vatSysWindowManager.Tests
{
    /// <summary>
    /// A small console test runner. Exit code 0 means every test passed.
    ///
    /// Usage: vatSysWindowManager.Tests.exe [vatSys install folder] [folder of real layouts to smoke-test]
    /// </summary>
    internal static class Program
    {
        private static int failures;
        private static int checks;
        private static string vatSysBin;

        [STAThread]
        private static int Main(string[] args)
        {
            var install = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : @"C:\Program Files (x86)\vatSys";
            vatSysBin = Path.Combine(install, "bin");

            // vatSys and the libraries it ships are loaded from the install, never copied beside the tests.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                foreach (var extension in new[] { ".dll", ".exe" })
                {
                    var candidate = Path.Combine(vatSysBin, name + extension);
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                return null;
            };

            Run("v1 state layout is upgraded", UpgradeStateLayout);
            Run("v1 beacon layout is upgraded", UpgradeBeaconLayout);
            Run("v2 layout round-trips", RoundTrip);
            Run("arrival airport parsing", ArrivalParsing);
            Run("off-screen windows are moved on screen", OnScreen);
            Run("layout store", Store);
            Run("vatSys internals are all present", Internals);
            if (args.Length > 1 && Directory.Exists(args[1])) Run("real layouts load", () => RealLayouts(args[1]));
            if (args.Contains("--windows")) Run("real windows are put back", RealWindows);

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? $"PASSED ({checks} checks)" : $"FAILED: {failures} of {checks} checks");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            var before = failures;
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failures++;
                checks++;
                Console.WriteLine($"  threw: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
            }
            Console.WriteLine($"[{(failures == before ? "ok" : "FAIL")}] {name}");
        }

        private static void Check(bool condition, string what)
        {
            checks++;
            if (condition) return;
            failures++;
            Console.WriteLine("  failed: " + what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'");
        }

        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "fixtures", name));

        // ---- upgrade from version 1 --------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpgradeStateLayout()
        {
            var layout = LayoutUpgrade.Parse(Fixture("v1-state.layout.json"));

            Check(layout.Upgraded, "marked as upgraded");
            Equal(Layout.CurrentVersion, layout.Version, "version");
            Equal("Test TCU", layout.Position, "position");
            Equal("State", layout.Strips.Mode, "strip mode");
            Equal("Alpha", layout.Strips.SortMode, "strip sort mode");
            Check(!layout.Strips.BeaconsComplete, "v1 beacon list is not treated as complete");
            Check(!layout.RestoreSectors, "sectors are not restored unless asked");
            Equal(2, layout.ControlledSectors.Count, "sectors are still kept");

            var kinds = layout.Windows.GroupBy(w => w.Kind).ToDictionary(g => g.Key, g => g.Count());
            Equal(1, kinds[WindowKind.Main], "main window");
            Equal(2, kinds[WindowKind.StripState], "state strip windows");
            Equal(1, kinds[WindowKind.Ground], "ground window");
            Equal(1, kinds[WindowKind.Vscs], "VSCS");
            Equal(1, kinds[WindowKind.Arrival], "arrival list");
            Equal(1, kinds[WindowKind.Plugin], "plugin window");
            Equal(2, kinds[WindowKind.Single], "single-instance windows (Strip Setup, System Messages)");

            // The blank ATIS window 1.1.x created must not survive; the real one must.
            var atis = layout.Windows.Where(w => w.Kind == WindowKind.Atis).ToList();
            Equal(1, atis.Count, "ATIS windows");
            Equal("YSSY_ATIS", atis[0].Key, "ATIS callsign");

            Check(!layout.Windows.Any(w => w.Title == "ML-BIK_CTR"), "private message windows are dropped");
            Check(!layout.Windows.Any(w => w.TypeName == "vatsys.FlightPlanWindow"), "unmanaged vatSys windows are dropped");
            Check(layout.Windows.Any(w => w.Kind == WindowKind.Single && w.Key == "SystemMessagesWindow"), "System Messages is kept as a single-instance window");

            var main = layout.Windows.Single(w => w.Kind == WindowKind.Main);
            Equal(WindowStateName.Maximized, main.State, "main window state");
            Check(main.Actual == null, "a maximised window has no actual rectangle");
            Equal(-33.9460983276367, main.Asd.CentreLat.Value, "centre latitude");
            Equal(209.000958305468, main.Asd.Range.Value, "range");
            Check(main.Asd.Maps.SequenceEqual(new[] { "ALL_CTA", "ALL_SECTORS", "SY_TCU" }), "maps without the menu toggles");
            Equal(true, layout.DynamicInfill, "dynamic infill taken out of the map list");
            Equal(false, layout.TerrainHeights, "terrain heights");

            // A window saved while minimised reported (-32000,-32000). That must never be used.
            var vscs = layout.Windows.Single(w => w.Kind == WindowKind.Vscs);
            Equal(WindowStateName.Minimized, vscs.State, "VSCS state");
            Check(vscs.Actual == null, "a minimised window has no actual rectangle");
            Equal(1919, vscs.Normal.Left, "VSCS restore rectangle");

            var arrival = layout.Windows.Single(w => w.Kind == WindowKind.Arrival);
            Equal("YSSY", arrival.Key, "arrival airport");

            var jurisdiction = layout.Windows.Single(w => w.Kind == WindowKind.StripState && w.Key == "Jurisdiction");
            Equal(1456, jurisdiction.Actual.Left, "strip window actual rectangle");

            var plugin = layout.Windows.Single(w => w.Kind == WindowKind.Plugin);
            Equal("MaxRumsey.OzStripsPlugin.GUI.MainForm", plugin.TypeName, "plugin window type");
            Equal(1993, plugin.Actual.Left, "plugin window actual rectangle");

            // The last window in a v1 file ended on top.
            Equal(0, layout.Windows.Last().Z, "z-order of the last window");
            Check(layout.Windows.First().Z > layout.Windows.Last().Z, "z-order runs from the end of the file");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpgradeBeaconLayout()
        {
            var layout = LayoutUpgrade.Parse(Fixture("v1-beacon.layout.json"));

            Equal("Beacon", layout.Strips.Mode, "strip mode inferred from the windows");
            Check(layout.Strips.SortMode == null, "no sort mode means leave it alone");

            var beacons = layout.Windows.Where(w => w.Kind == WindowKind.StripBeacon).ToList();
            Equal(2, beacons.Count, "beacon windows");
            Equal("ADEP", beacons[0].StripType, "first beacon type");
            Equal("YMML", beacons[0].Key, "first beacon point");
            Equal("Beacon", beacons[1].StripType, "second beacon type");
            Equal("BOYSE", beacons[1].Key, "second beacon point");
            Equal(1, layout.Windows.Count(w => w.Kind == WindowKind.StripOther), "the Other window");
            Equal(0, layout.ControlledSectors.Count, "empty sector list is kept as empty");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RoundTrip()
        {
            var layout = new Layout
            {
                Position = "P",
                LayoutName = "L",
                SavedUtc = new DateTime(2026, 11, 27, 1, 2, 3, DateTimeKind.Utc),
                Strips = new StripSetup { Mode = "Beacon", SortMode = "Time", BeaconsComplete = true },
                DynamicInfill = false,
                RestoreSectors = true,
                ControlledSectors = new List<string> { "AAA" }
            };
            layout.Windows.Add(new WindowEntry
            {
                Kind = WindowKind.StripBeacon,
                Key = "BOYSE",
                StripType = "Beacon",
                Visible = false,
                State = WindowStateName.Normal,
                Normal = new IntRect(1, 2, 300, 400),
                Z = 3
            });
            layout.Windows.Add(new WindowEntry
            {
                Kind = WindowKind.Asd,
                Asd = new AsdView
                {
                    Position = "X",
                    CentreLat = -30.5,
                    CentreLon = 150.25,
                    Range = 123.456,
                    Maps = new List<string> { "F|M" },
                    AtcList = new FloatRect { Left = 1, Top = 2, Right = 3, Bottom = 4 },
                    HasStoredView = true,
                    StoredRange = -1
                }
            });
            layout.Windows.Add(new WindowEntry
            {
                Kind = WindowKind.Plugin,
                TypeName = "Some.Plugin.Window",
                Opener = new MenuOpener { Category = "Windows", Path = new List<string> { "Some Plugin", "Open" } }
            });

            var json = LayoutUpgrade.Serialize(layout);
            var back = LayoutUpgrade.Parse(json);

            Check(!back.Upgraded, "a version 2 file is not an upgrade");
            Equal("Beacon", back.Strips.Mode, "mode");
            Check(back.Strips.BeaconsComplete, "beacons complete");
            Equal(false, back.DynamicInfill, "dynamic infill");
            Check(back.RestoreSectors, "restore sectors");
            Equal(3, back.Windows.Count, "windows");
            Check(!back.Windows[0].Visible, "hidden window stays hidden");
            Check(back.Windows[0].Normal.SameAs(new IntRect(1, 2, 300, 400)), "rectangle");
            Equal(3, back.Windows[0].Z, "z-order");
            Equal(123.456, back.Windows[1].Asd.Range.Value, "range");
            Equal(4f, back.Windows[1].Asd.AtcList.Bottom, "ATC list rectangle");
            Check(back.Windows[1].Asd.HasStoredView && back.Windows[1].Asd.StoredLat == null, "an empty stored view is kept as empty");
            Equal("Windows > Some Plugin > Open", back.Windows[2].Opener.ToString(), "plugin menu item");
            Equal(json, LayoutUpgrade.Serialize(back), "serialising twice gives the same text");

            var header = LayoutUpgrade.ReadHeader(json);
            Equal("P", header.Position, "header position");
            Equal("L", header.LayoutName, "header name");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ArrivalParsing()
        {
            Equal("YSSY", LayoutUpgrade.AirportFromArrivalTitle("A:YSSY List"), "vatSys title");
            Equal("YMML", LayoutUpgrade.AirportFromArrivalTitle("A: ymml List"), "title with a space");
            Check(LayoutUpgrade.AirportFromArrivalTitle("Strip Setup") == null, "not an arrival title");
            Check(LayoutUpgrade.AirportFromArrivalTitle("A:") == null, "empty airport");
            Equal("YBBN", LayoutUpgrade.AirportFromArrivalName("OzServerArrivalListYBBNWindow"), "OzServer window name");
            Check(LayoutUpgrade.AirportFromArrivalName("sequence0Window") == null, "vatSys window name carries no airport");
        }

        // ---- geometry -----------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OnScreen()
        {
            var primary = new IntRect(0, 0, 1920, 1040);
            var left = new IntRect(-1920, 0, 0, 1040);
            var both = new List<IntRect> { primary, left };
            var one = new List<IntRect> { primary };

            var inside = new IntRect(100, 100, 500, 400);
            Check(Geometry.EnsureOnScreen(inside, one, out var moved).SameAs(inside) && !moved, "a window on screen is left alone");

            var onLeft = new IntRect(-1700, 200, -1000, 800);
            Check(Geometry.EnsureOnScreen(onLeft, both, out moved).SameAs(onLeft) && !moved, "a window on the second screen is left alone while it is connected");

            var rescued = Geometry.EnsureOnScreen(onLeft, one, out moved);
            Check(moved, "a window on a missing screen is moved");
            Check(rescued.Left >= 0 && rescued.Right <= 1920 && rescued.Top >= 0 && rescued.Bottom <= 1040, "and ends up fully on the remaining screen");
            Equal(onLeft.Width, rescued.Width, "keeping its width");
            Equal(onLeft.Height, rescued.Height, "and its height");

            var huge = new IntRect(5000, 5000, 9000, 8000);
            var shrunk = Geometry.EnsureOnScreen(huge, one, out moved);
            Check(moved && shrunk.Width == 1920 && shrunk.Height == 1040, "a window bigger than the screen is cut down to it");

            var titleBarOff = new IntRect(100, -200, 500, 300);
            Check(Geometry.EnsureOnScreen(titleBarOff, one, out moved).Top >= 0 && moved, "a window whose title bar is above the screen is brought down");

            var snappedMaximised = new IntRect(-8, -8, 1928, 1048);
            Check(Geometry.EnsureOnScreen(snappedMaximised, one, out moved).SameAs(snappedMaximised) && !moved, "a maximised window's overhang is not treated as off screen");

            Check(Geometry.EnsureOnScreen(null, one, out moved) == null, "no rectangle in, none out");
            Check(Geometry.EnsureOnScreen(inside, new List<IntRect>(), out moved).SameAs(inside), "no screens known: leave it");
        }

        // ---- storage --------------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Store()
        {
            var root = Path.Combine(Path.GetTempPath(), "wm-tests-" + Guid.NewGuid().ToString("N"));
            LayoutStore.RootOverride = root;
            try
            {
                Equal("Sydney TCU__Night.layout.json", LayoutStore.FileNameFor("Sydney TCU", "Night"), "file name");
                Equal("Sydney TCU.layout.json", LayoutStore.FileNameFor("Sydney TCU", null), "legacy file name");
                Equal("A_B__C_D.layout.json", LayoutStore.FileNameFor("A/B", "C:D"), "unsafe characters");
                Equal("DEFAULT.layout.json", LayoutStore.FileNameFor("  ", null), "no position");
                Equal("Night", LayoutStore.InferLayoutName(@"C:\x\Sydney TCU__Night.layout.json", "Sydney TCU"), "name from file");
                Equal("Sydney TCU", LayoutStore.InferLayoutName(@"C:\x\Sydney TCU.layout.json", "Sydney TCU"), "name from a legacy file");

                var layout = new Layout { Position = "Pos", LayoutName = "One", SavedUtc = DateTime.UtcNow, Strips = new StripSetup { Mode = "State" } };
                var path = LayoutStore.PathFor("Pos", "One");
                LayoutStore.Save(layout, path);
                Check(File.Exists(path), "saved");
                Check(!File.Exists(path + ".tmp"), "no temporary file left behind");

                layout.Strips.Mode = "Beacon";
                LayoutStore.Save(layout, path);
                Equal("Beacon", LayoutStore.Load(path).Strips.Mode, "saving over an existing layout");

                File.WriteAllText(Path.Combine(root, "broken.layout.json"), "{ not json");
                File.Copy(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "fixtures", "v1-state.layout.json"), Path.Combine(root, "Test TCU__Test TCU.layout.json"));

                var all = LayoutStore.List();
                Equal(2, all.Count, "an unreadable file is skipped, not fatal");
                Equal(1, LayoutStore.ListFor("pos").Count, "listing is case-insensitive on position");
                Equal(1, LayoutStore.ListFor("Test TCU").Count, "a version 1 file is listed");

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Pos", "One" } };
                LayoutStore.SaveAutoLoadMap(map);
                Equal("One", LayoutStore.LoadAutoLoadMap()["POS"], "auto-load map");

                var openers = new Dictionary<string, MenuOpener> { { "T", new MenuOpener { Category = "Windows", Path = new List<string> { "X" } } } };
                LayoutStore.SaveOpeners(openers);
                Equal("Windows > X", LayoutStore.LoadOpeners()["T"].ToString(), "learned menu items");
            }
            finally
            {
                LayoutStore.RootOverride = null;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- against the real vatSys ------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Internals()
        {
            if (!File.Exists(Path.Combine(vatSysBin, "vatSys.exe")))
            {
                Check(false, $"vatSys.exe not found in {vatSysBin}");
                return;
            }

            var missing = Vatsys.SelfCheck();
            Console.WriteLine($"  vatSys {Vatsys.Assembly.GetName().Version}: {(missing.Count == 0 ? "every member found" : missing.Count + " missing")}");
            foreach (var m in missing) Console.WriteLine("  missing: " + m);
            Equal(0, missing.Count, "missing members");

            Check(Vatsys.StripsAvailable, "strip windows available");
            Check(Vatsys.CanCreateBeacons, "beacon windows can be created");
            Check(Vatsys.AtisAvailable, "ATIS windows available");
            Check(Vatsys.ArrivalsAvailable, "arrival lists available");
            Check(Vatsys.Singles.All(s => s.Field != null), "every single-instance window field found");
            Check(Vatsys.Singles.Where(s => !s.Automatic).All(s => s.Opener != null), "every single-instance menu handler found");
        }

        // ---- real windows -----------------------------------------------------------------------------------
        //
        // Opens a few small plain windows for a second or two and checks that capture followed by
        // apply puts each one back: place, size, minimised, maximised, hidden and stacking order.
        // Only run when asked for (--windows), because it shows windows on the desktop.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RealWindows()
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            var owner = new System.Windows.Forms.Form { Text = "wm-test owner", StartPosition = System.Windows.Forms.FormStartPosition.Manual, ShowInTaskbar = false, Bounds = new System.Drawing.Rectangle(work.Left + 40, work.Top + 40, 300, 200) };
            var forms = new List<System.Windows.Forms.Form>();

            System.Windows.Forms.Form Make(string name, int x, int y)
            {
                var f = new System.Windows.Forms.Form { Text = name, Name = name, StartPosition = System.Windows.Forms.FormStartPosition.Manual, ShowInTaskbar = false, Bounds = new System.Drawing.Rectangle(work.Left + x, work.Top + y, 320, 180) };
                f.Show(owner);
                forms.Add(f);
                return f;
            }

            void Pump()
            {
                for (var i = 0; i < 5; i++)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(20);
                }
            }

            IntRect RectOf(System.Windows.Forms.Form f)
            {
                PInvoke.User32.GetWindowRect(f.Handle, out var r);
                return new IntRect(r.left, r.top, r.right, r.bottom);
            }

            WindowEntry Snap(System.Windows.Forms.Form f)
            {
                var e = new WindowEntry { Kind = WindowKind.Plugin };
                Placement.Capture(f, e, Native.ZOrderTopDown());
                return e;
            }

            try
            {
                owner.Show();
                Pump();

                // 1. A normal window goes back to where it was.
                var normal = Make("wm-test normal", 100, 100);
                Pump();
                var normalEntry = Snap(normal);
                var normalRect = RectOf(normal);
                Equal(WindowStateName.Normal, normalEntry.State, "normal: captured state");
                Check(normalEntry.Visible && normalEntry.Actual != null && normalEntry.Actual.SameAs(normalRect), "normal: captured the rectangle on screen");
                normal.Bounds = new System.Drawing.Rectangle(work.Left + 400, work.Top + 300, 500, 260);
                Pump();
                Placement.Apply(normal, normalEntry, null);
                Pump();
                Check(RectOf(normal).SameAs(normalRect), $"normal: put back (wanted {normalRect}, got {RectOf(normal)})");

                // 2. A maximised window is maximised again, and keeps its restore rectangle.
                var max = Make("wm-test max", 150, 150);
                Pump();
                var maxRestore = RectOf(max);
                max.WindowState = System.Windows.Forms.FormWindowState.Maximized;
                Pump();
                var maxEntry = Snap(max);
                Equal(WindowStateName.Maximized, maxEntry.State, "maximised: captured state");
                Check(maxEntry.Actual == null, "maximised: no actual rectangle");
                max.WindowState = System.Windows.Forms.FormWindowState.Normal;
                max.Bounds = new System.Drawing.Rectangle(work.Left + 500, work.Top + 50, 200, 150);
                Pump();
                Placement.Apply(max, maxEntry, null);
                Pump();
                Check(Native.IsZoomed(max.Handle), "maximised: maximised again");
                max.WindowState = System.Windows.Forms.FormWindowState.Normal;
                Pump();
                Check(RectOf(max).SameAs(maxRestore), $"maximised: restore rectangle kept (wanted {maxRestore}, got {RectOf(max)})");

                // 3. A minimised window is minimised again and never sent to (-32000,-32000).
                var min = Make("wm-test min", 200, 200);
                Pump();
                var minRestore = RectOf(min);
                min.WindowState = System.Windows.Forms.FormWindowState.Minimized;
                Pump();
                var minEntry = Snap(min);
                Equal(WindowStateName.Minimized, minEntry.State, "minimised: captured state");
                Check(minEntry.Actual == null, "minimised: no actual rectangle");
                min.WindowState = System.Windows.Forms.FormWindowState.Normal;
                min.Bounds = new System.Drawing.Rectangle(work.Left + 600, work.Top + 400, 250, 150);
                Pump();
                Placement.Apply(min, minEntry, null);
                Pump();
                Check(Native.IsIconic(min.Handle), "minimised: minimised again");
                min.WindowState = System.Windows.Forms.FormWindowState.Normal;
                Pump();
                Check(RectOf(min).SameAs(minRestore), $"minimised: comes back where it was (wanted {minRestore}, got {RectOf(min)})");

                // 4. A hidden window stays hidden, and shows where it was when next shown.
                var hidden = Make("wm-test hidden", 250, 250);
                Pump();
                var hiddenRect = RectOf(hidden);
                hidden.Hide();
                Pump();
                var hiddenEntry = Snap(hidden);
                Check(!hiddenEntry.Visible, "hidden: captured as hidden");
                Check(hiddenEntry.Normal != null, "hidden: its place is still captured");
                hidden.Bounds = new System.Drawing.Rectangle(work.Left + 700, work.Top + 100, 200, 120);
                Pump();
                Placement.Apply(hidden, hiddenEntry, null);
                Pump();
                Check(!Native.IsWindowVisible(hidden.Handle), "hidden: still hidden after apply");
                hidden.Show();
                Pump();
                Check(RectOf(hidden).SameAs(hiddenRect), $"hidden: shows where it was (wanted {hiddenRect}, got {RectOf(hidden)})");

                // 5. Applying to a showing window from a hidden-state entry does not hide it, and vice versa.
                Placement.Apply(normal, hiddenEntry, null);
                Pump();
                Check(Native.IsWindowVisible(normal.Handle), "apply never hides a showing window");
                Placement.Apply(normal, normalEntry, null);
                Pump();

                // 6. Stacking order.
                var a = Make("wm-test a", 300, 60);
                var b = Make("wm-test b", 340, 90);
                var c = Make("wm-test c", 380, 120);
                Pump();
                c.BringToFront(); b.BringToFront(); a.BringToFront(); // a on top, then b, then c
                Pump();
                var entries = new[] { a, b, c }.ToDictionary(f => Snap(f), f => f);
                var before = Native.ZOrderTopDown();
                Check(before.IndexOf(a.Handle) < before.IndexOf(b.Handle) && before.IndexOf(b.Handle) < before.IndexOf(c.Handle), "z-order: set up as a, b, c");
                c.BringToFront(); // now c, a, b
                Pump();
                Placement.ApplyZOrder(entries);
                Pump();
                var after = Native.ZOrderTopDown();
                Check(after.IndexOf(a.Handle) < after.IndexOf(b.Handle) && after.IndexOf(b.Handle) < after.IndexOf(c.Handle), "z-order: restored to a, b, c");

                // 7. A window saved on a screen that is gone is brought onto a real one.
                var lost = new WindowEntry { Kind = WindowKind.Plugin, Visible = true, State = WindowStateName.Normal, Normal = new IntRect(-9000, -9000, -8700, -8800) };
                var report = new RestoreReport("test");
                LayoutStore.RootOverride = Path.Combine(Path.GetTempPath(), "wm-tests-log");
                Placement.Apply(normal, lost, report);
                LayoutStore.RootOverride = null;
                Pump();
                var rescued = RectOf(normal);
                Check(System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(rescued.Left, rescued.Top, rescued.Width, rescued.Height))), "off-screen: ended up on a screen");
                Equal(1, report.Problems.Count, "off-screen: reported");
            }
            finally
            {
                foreach (var f in forms) f.Dispose();
                owner.Dispose();
                try { Directory.Delete(Path.Combine(Path.GetTempPath(), "wm-tests-log"), true); } catch { }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RealLayouts(string folder)
        {
            foreach (var file in Directory.GetFiles(folder, "*.layout.json"))
            {
                var layout = LayoutUpgrade.Parse(File.ReadAllText(file));
                var summary = string.Join(", ", layout.Windows.GroupBy(w => w.Kind).Select(g => $"{g.Key}x{g.Count()}"));
                Console.WriteLine($"  {Path.GetFileName(file)}: {layout.Strips?.Mode}/{layout.Strips?.SortMode ?? "-"} {summary}");
                Check(layout.Windows.Count > 0, Path.GetFileName(file) + " has windows");
                Check(layout.Windows.All(w => w.Kind != WindowKind.Atis || !string.IsNullOrWhiteSpace(w.Key)), Path.GetFileName(file) + " has no blank ATIS windows");
                Check(layout.Windows.Count(w => w.Kind == WindowKind.Main) <= 1, Path.GetFileName(file) + " has one main window");
            }
        }
    }
}
