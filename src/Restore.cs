using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using PInvoke;
using vatsys;

namespace vatSysWindowManager
{
    /// <summary>
    /// Puts vatSys back the way a layout describes.
    ///
    /// One pass on the UI thread, in a fixed order, with no sleeping and no blind retries. Each kind
    /// of window is opened and closed through the method vatSys itself uses for it, then placed.
    /// The few things that cannot be finished in that pass (a new display that has not drawn yet, a
    /// window another plugin opens a moment later) wait on a short list that is checked until each
    /// is done or eight seconds have passed.
    /// </summary>
    internal static class Restore
    {
        private const string DeferredJob = "restore-deferred";
        private const int DeferredTimeoutMs = 8000;
        private const int SettleMs = 3000;

        private sealed class Pending
        {
            public string What;
            /// <summary>Returns true once done.</summary>
            public Func<bool> Try;
            /// <summary>Run instead of reporting a problem when time runs out.</summary>
            public Action OnTimeout;
        }

        private sealed class Run
        {
            public Layout Layout;
            public RestoreReport Report;
            public readonly Dictionary<WindowEntry, Form> Placed = new Dictionary<WindowEntry, Form>();
            public readonly List<Pending> Pending = new List<Pending>();
            /// <summary>Re-run for a few seconds after the load. They never hold the load open on their own.</summary>
            public readonly List<Action> Watchers = new List<Action>();
            public DateTime Started = DateTime.UtcNow;
            /// <summary>True once the first pass is over and the short list is being worked through.</summary>
            public bool Deferred;
            /// <summary>True when a window was placed after the first pass, so stacking order needs doing again.</summary>
            public bool PlacedLate;
        }

        private static Run current;

        /// <summary>True while a layout is being applied, so the plugin can tell its own actions from the controller's.</summary>
        internal static bool IsRunning { get; private set; }

        internal static void Apply(Layout layout)
        {
            if (layout == null) return;
            if (IsRunning)
            {
                // Reached only if something in the middle of a load pumped messages. One at a time.
                Log.Warn("A layout load was requested while another was in progress and was ignored");
                return;
            }
            Cancel();

            var run = new Run { Layout = layout, Report = new RestoreReport(layout.LayoutName ?? "layout") };
            run.Report.Note($"loading (file version {(layout.Upgraded ? 1 : layout.Version)}, {layout.Windows.Count} windows)");

            IsRunning = true;
            try
            {
                Step(run, "the main window", RestoreMain);
                Step(run, "controlled sectors", RestoreSectors);
                Step(run, "strip windows", RestoreStrips);
                Step(run, "ATIS windows", RestoreAtis);
                Step(run, "arrival lists", RestoreArrivals);
                Step(run, "vatSys windows", RestoreSingles);
                Step(run, "ASD windows", r => RestoreDisplays(r, ground: false));
                Step(run, "Ground windows", r => RestoreDisplays(r, ground: true));
                Step(run, "other plugins' windows", RestorePluginWindows);
                Step(run, "the VSCS window", RestoreVscs);
                Placement.ApplyZOrder(run.Placed);

                Log.Debug($"after load: strips {Vatsys.StripMode}/{Vatsys.StripSortMode}; " +
                          $"state windows showing: {string.Join(",", Vatsys.StateWindowNames.Where(n => Vatsys.IsVisible(Vatsys.StateStripWindow(n))))}; " +
                          $"beacon windows: {string.Join(",", Vatsys.BeaconWindows().Select(b => $"{b.Type} {b.Point}{(Vatsys.IsVisible(b.Form) ? "" : " (hidden)")}"))}; " +
                          $"ATIS: {string.Join(",", Vatsys.AtisWindows().Select(a => a.Value))}; " +
                          $"arrivals: {string.Join(",", Vatsys.ArrivalWindows().Select(a => a.Airport))}; " +
                          $"pending: {run.Pending.Count}");
            }
            finally
            {
                IsRunning = false;
            }

            if (run.Pending.Count == 0 && run.Watchers.Count == 0)
            {
                run.Report.Flush();
                return;
            }

            run.Deferred = true;
            current = run;
            Ui.Poll(DeferredJob, 150, DeferredTimeoutMs, () => Continue(run), () => Finish(run, timedOut: true));
        }

        /// <summary>
        /// Stops finishing off the last load. Called when another load starts and when the position
        /// changes: what was still pending belonged to a layout that no longer applies.
        /// </summary>
        internal static void Cancel()
        {
            Ui.Cancel(DeferredJob);
            var run = current;
            current = null;
            run?.Report.Flush();
        }

        private static void Step(Run run, string what, Action<Run> action)
        {
            try
            {
                action(run);
            }
            catch (Exception ex)
            {
                run.Report.Problem($"{what} could not be restored");
                Log.Error($"Restoring {what} failed", ex);
            }
        }

        private static bool Continue(Run run)
        {
            IsRunning = true;
            try
            {
                foreach (var pending in run.Pending.ToList())
                {
                    var done = false;
                    try
                    {
                        done = pending.Try();
                    }
                    catch (Exception ex)
                    {
                        done = true;
                        run.Report.Problem($"{pending.What} could not be restored");
                        Log.Error($"Deferred restore of {pending.What} failed", ex);
                    }
                    if (done) run.Pending.Remove(pending);
                }

                foreach (var watcher in run.Watchers) Ui.Guard(watcher, "restore watcher")();
            }
            finally
            {
                IsRunning = false;
            }

            var settling = run.Watchers.Count > 0 && (DateTime.UtcNow - run.Started).TotalMilliseconds < SettleMs;
            if (run.Pending.Count > 0 || settling) return false;

            Finish(run, timedOut: false);
            return true;
        }

        private static void Finish(Run run, bool timedOut)
        {
            if (timedOut)
            {
                foreach (var pending in run.Pending)
                {
                    if (pending.OnTimeout != null) Ui.Guard(pending.OnTimeout, $"timeout of {pending.What}")();
                    else run.Report.Problem($"{pending.What} did not appear");
                }
                run.Pending.Clear();
            }

            // Only when something turned up late. Otherwise this would re-stack, seconds after the
            // load, windows the controller has since raised.
            if (run.PlacedLate) Placement.ApplyZOrder(run.Placed);
            if (ReferenceEquals(current, run)) current = null;
            run.Report.Flush();
        }

        private static IEnumerable<WindowEntry> Entries(Run run, string kind) => run.Layout.Windows.Where(w => w != null && w.Kind == kind);

        private static bool Same(string a, string b) => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

        private static void Place(Run run, Form form, WindowEntry entry)
        {
            Placement.Apply(form, entry, run.Report);
            run.Placed[entry] = form;
            if (run.Deferred) run.PlacedLate = true;
        }

        private static void CloseIfShowing(Form form)
        {
            if (Vatsys.IsVisible(form)) form.Close();
        }

        // ---- main window ------------------------------------------------------------------------------

        private static void RestoreMain(Run run)
        {
            var main = Vatsys.MainForm;
            var entry = Entries(run, WindowKind.Main).FirstOrDefault();
            if (!Vatsys.Alive(main) || entry == null) return;

            Place(run, main, entry);

            if (run.Layout.DynamicInfill.HasValue && run.Layout.DynamicInfill != Vatsys.DynamicInfill) Vatsys.SetDynamicInfill(run.Layout.DynamicInfill.Value);
            if (run.Layout.TerrainHeights.HasValue && run.Layout.TerrainHeights != Vatsys.TerrainHeights) Vatsys.SetTerrainHeights(run.Layout.TerrainHeights.Value);

            ApplyViewWhenReady(run, main, entry, isMain: true);
        }

        private static void ApplyViewWhenReady(Run run, Form form, WindowEntry entry, bool isMain, Action then = null)
        {
            if (entry.Asd == null)
            {
                then?.Invoke();
                return;
            }

            void ApplyNow()
            {
                Vatsys.ApplyView(form, entry.Asd, isMain, run.Report);
                // vatSys's render thread rewrites the ATC list and scale rectangles every frame, so a
                // single write can lose to a frame already in progress. Look again for a few seconds.
                run.Watchers.Add(() => { if (Vatsys.Alive(form)) Vatsys.ReassertRects(form, entry.Asd); });
                then?.Invoke();
            }

            if (Vatsys.IsAsdReady(Vatsys.AsdControl(form)))
            {
                ApplyNow();
                return;
            }

            // A display that has not drawn yet sets its own zoom on its first draw. Wait for that.
            run.Pending.Add(new Pending
            {
                What = $"the display in \"{entry.Title}\"",
                Try = () =>
                {
                    if (!Vatsys.Alive(form)) return true;
                    if (!Vatsys.IsAsdReady(Vatsys.AsdControl(form))) return false;
                    ApplyNow();
                    return true;
                },
                // It never drew (covered, or minimised). Apply anyway rather than leave it at defaults.
                OnTimeout = () =>
                {
                    if (Vatsys.Alive(form)) ApplyNow();
                }
            });
        }

        // ---- sectors --------------------------------------------------------------------------------

        private static void RestoreSectors(Run run)
        {
            var layout = run.Layout;
            if (!layout.RestoreSectors) return;

            var names = layout.ControlledSectors;
            if (names == null || names.Count == 0)
            {
                run.Report.Note("sectors left alone: the layout was saved with none");
                return;
            }

            // MMI.SetControlledSectors hands off to nobody every tracked aircraft in a sector that is
            // being removed. That must never happen as a side effect of moving windows about.
            if (Vatsys.IsConnected())
            {
                run.Report.Note("sectors left alone: connected to the network");
                return;
            }

            var wanted = new List<SectorsVolumes.Sector>();
            var unknown = new List<string>();
            foreach (var name in names)
            {
                var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s != null && !s.IsDummy && Same(s.Name, name));
                if (sector == null) unknown.Add(name);
                else if (!wanted.Contains(sector)) wanted.Add(sector);
            }

            if (unknown.Count > 0) run.Report.Problem($"saved sector(s) not found: {string.Join(", ", unknown)}");
            if (wanted.Count == 0) return;

            var current = MMI.SectorsControlled.Where(s => s != null && !s.IsDummy).ToList();
            if (current.Count == wanted.Count && wanted.All(current.Contains)) return;

            MMI.SetControlledSectors(wanted);
        }

        // ---- strip windows ----------------------------------------------------------------------------

        private static void RestoreStrips(Run run)
        {
            var stateEntries = Entries(run, WindowKind.StripState).ToList();
            var beaconEntries = Entries(run, WindowKind.StripBeacon)
                .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.StripType))
                .GroupBy(e => (e.StripType + "|" + e.Key).ToUpperInvariant()).Select(g => g.First()).ToList();
            var otherEntry = Entries(run, WindowKind.StripOther).FirstOrDefault();
            var setup = run.Layout.Strips;

            if (setup == null && stateEntries.Count == 0 && beaconEntries.Count == 0 && otherEntry == null) return;

            if (!Vatsys.StripsAvailable)
            {
                run.Report.Problem("strip windows could not be restored: this version of vatSys keeps them differently");
                return;
            }

            if (!string.IsNullOrWhiteSpace(setup?.Mode)) Vatsys.SetStripMode(setup.Mode);

            // Beacon, ADEP and ADES windows. vatSys only shows the ones that are already defined, so
            // the definitions come first: the same windows in the same order as when it was saved.
            var current = Vatsys.BeaconWindows();
            bool Matches(StripWindowInfo info, WindowEntry entry) => Same(info.Type, entry.StripType) && Same(info.Point, entry.Key);

            if (setup != null && setup.BeaconsComplete)
            {
                var sameSequence = current.Count == beaconEntries.Count && !current.Where((info, i) => !Matches(info, beaconEntries[i])).Any();
                if (!sameSequence)
                {
                    if (!Vatsys.CanCreateBeacons)
                    {
                        run.Report.Problem("beacon strip windows could not be defined in this version of vatSys");
                    }
                    else
                    {
                        foreach (var info in current)
                        {
                            // vatSys only hides the window on delete if it counts as showing, and a
                            // minimised one does not. Hide it first so none is left behind.
                            CloseIfShowing(info.Form);
                            Vatsys.DeleteBeacon(info.Form);
                        }
                        for (var i = 0; i < beaconEntries.Count; i++)
                        {
                            // vatSys names a new one after how many exist, and briefly shows it where that name was last placed.
                            Placement.Seed($"beacon{i}StripWindow", beaconEntries[i]);
                            Vatsys.CreateBeacon(beaconEntries[i].StripType, beaconEntries[i].Key);
                        }
                        current = Vatsys.BeaconWindows();
                    }
                }
            }
            else if (Vatsys.CanCreateBeacons)
            {
                // A version 1 layout only knew the beacon windows that were showing. Add what is
                // missing and leave the definition of anything else alone.
                foreach (var entry in beaconEntries.Where(e => !current.Any(info => Matches(info, e))))
                {
                    Placement.Seed($"beacon{Vatsys.BeaconWindows().Count}StripWindow", entry);
                    Vatsys.CreateBeacon(entry.StripType, entry.Key);
                }
                current = Vatsys.BeaconWindows();
            }

            foreach (var info in current)
            {
                var entry = beaconEntries.FirstOrDefault(e => Matches(info, e));
                if (entry == null) CloseIfShowing(info.Form);
                else ShowOrHide(run, info.Form, entry);
            }

            foreach (var entry in beaconEntries.Where(e => !current.Any(info => Matches(info, e))))
            {
                run.Report.Problem($"the {entry.StripType} {entry.Key} strip window could not be created");
            }

            // The five state windows always exist. One with no entry was not showing when the layout was saved.
            foreach (var name in Vatsys.StateWindowNames)
            {
                var window = Vatsys.StateStripWindow(name);
                if (!Vatsys.Alive(window)) continue;
                var entry = stateEntries.FirstOrDefault(e => Same(e.Key, name));
                if (entry == null) CloseIfShowing(window);
                else ShowOrHide(run, window, entry);
            }

            // vatSys shows and hides the Other window as strips need it. Only its place is the layout's.
            var other = Vatsys.OtherStripWindow;
            if (otherEntry != null && Vatsys.Alive(other)) Place(run, other, otherEntry);

            if (!string.IsNullOrWhiteSpace(setup?.SortMode)) Vatsys.SetStripSortMode(setup.SortMode);

            // Setting the mode again makes vatSys hand every strip to the windows now showing.
            Vatsys.SetStripMode(Vatsys.StripMode);
        }

        private static void ShowOrHide(Run run, Form window, WindowEntry entry)
        {
            if (!entry.Visible)
            {
                CloseIfShowing(window);
                Place(run, window, entry); // still remember where it goes
                return;
            }

            if (!Vatsys.IsVisible(window))
            {
                Placement.Apply(window, entry, run.Report); // put it in place before it appears
                Vatsys.ShowStripWindow(window);
            }
            Place(run, window, entry);
        }

        // ---- ATIS windows --------------------------------------------------------------------------------

        private static void RestoreAtis(Run run)
        {
            var wanted = Entries(run, WindowKind.Atis).Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .GroupBy(e => e.Key.Trim().ToUpperInvariant()).Select(g => g.First()).ToList();

            if (!Vatsys.AtisAvailable)
            {
                if (wanted.Count > 0) run.Report.Problem("ATIS windows could not be restored: this version of vatSys keeps them differently");
                return;
            }

            foreach (var open in Vatsys.AtisWindows())
            {
                if (!wanted.Any(w => Same(w.Key, open.Value))) open.Key.Close();
            }

            foreach (var entry in wanted)
            {
                var open = Vatsys.AtisWindows();
                if (open.Any(o => Same(o.Value, entry.Key))) continue;
                try
                {
                    // vatSys names the window after how many are open and shows it where that name was last placed.
                    Placement.Seed($"atis{open.Count}Window", entry);
                    MMI.OpenATISWindow(entry.Key.Trim());
                }
                catch (Exception ex)
                {
                    run.Report.Problem($"the {entry.Key} ATIS window could not be opened ({ex.GetBaseException().Message})");
                }
            }

            var now = Vatsys.AtisWindows();
            foreach (var entry in wanted)
            {
                var window = now.FirstOrDefault(o => Same(o.Value, entry.Key)).Key;
                if (window != null) Place(run, window, entry);
            }
        }

        // ---- arrival lists ---------------------------------------------------------------------------------

        private static void RestoreArrivals(Run run)
        {
            var wanted = Entries(run, WindowKind.Arrival).Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .GroupBy(e => e.Key.Trim().ToUpperInvariant()).Select(g => g.First()).ToList();

            if (!Vatsys.ArrivalsAvailable)
            {
                if (wanted.Count > 0) run.Report.Problem("arrival lists could not be restored: this version of vatSys keeps them differently");
                return;
            }

            foreach (var open in Vatsys.ArrivalWindows())
            {
                if (!wanted.Any(w => Same(w.Key, open.Airport))) open.Form.Close();
            }

            foreach (var entry in wanted)
            {
                var airport = entry.Key.Trim().ToUpperInvariant();
                var open = Vatsys.ArrivalWindows();
                if (!open.Any(o => o.Airport == airport))
                {
                    Placement.Seed($"sequence{open.Count(o => o.IsVatsys)}Window", entry);
                    MMI.OpenArrivalListWindow(airport);
                }

                // If the list was saved from another plugin's stand-in, that window is shown under its
                // own name. Seed that too, for when the swap happens after this load has finished.
                if (!string.IsNullOrWhiteSpace(entry.FormName) && entry.TypeName != "vatsys.SequenceWindow") Placement.Seed(entry.FormName, entry);

                // Another plugin may swap vatSys's list for its own a moment after it opens. Each
                // window that turns up for this airport is placed once, whichever it is.
                var handled = new HashSet<Form>();
                bool PlaceAny()
                {
                    var any = false;
                    foreach (var info in Vatsys.ArrivalWindows().Where(o => o.Airport == airport))
                    {
                        if (!handled.Add(info.Form)) continue;
                        Place(run, info.Form, entry);
                        any = true;
                    }
                    return any;
                }

                if (PlaceAny())
                {
                    run.Watchers.Add(() => PlaceAny());
                }
                else
                {
                    run.Pending.Add(new Pending
                    {
                        What = $"the {airport} arrival list",
                        Try = () =>
                        {
                            if (!PlaceAny()) return false;
                            run.Watchers.Add(() => PlaceAny());
                            return true;
                        }
                    });
                }
            }
        }

        // ---- vatSys's single-instance windows -------------------------------------------------------------------

        private static void RestoreSingles(Run run)
        {
            foreach (var single in Vatsys.Singles)
            {
                if (single.Field == null) continue;
                var entry = Entries(run, WindowKind.Single).FirstOrDefault(e => string.Equals(e.Key, single.Key, StringComparison.Ordinal));
                var window = Vatsys.SingleForm(single);

                if (single.Automatic)
                {
                    if (entry != null && Vatsys.Alive(window)) Place(run, window, entry);
                    continue;
                }

                if (entry != null && entry.Visible)
                {
                    if (!Vatsys.IsVisible(window))
                    {
                        Placement.Seed(string.IsNullOrWhiteSpace(entry.FormName) ? window?.Name : entry.FormName, entry);
                        if (!Vatsys.OpenSingle(single)) run.Report.Problem($"the {entry.Title ?? single.Key} window could not be opened");
                        window = Vatsys.SingleForm(single);
                    }
                    if (Vatsys.Alive(window)) Place(run, window, entry);
                }
                else
                {
                    CloseIfShowing(window);
                    window = Vatsys.SingleForm(single);
                    if (entry != null && Vatsys.Alive(window)) Place(run, window, entry);
                }
            }
        }

        // ---- second ASD and Ground windows ----------------------------------------------------------------------

        private static void RestoreDisplays(Run run, bool ground)
        {
            var kind = ground ? WindowKind.Ground : WindowKind.Asd;
            var type = ground ? Vatsys.TGroundWindow : Vatsys.TAsdWindow;
            var wanted = Entries(run, kind).ToList();

            if (type == null)
            {
                if (wanted.Count > 0) run.Report.Problem($"{(ground ? "Ground" : "ASD")} windows could not be restored: this version of vatSys keeps them differently");
                return;
            }

            // Reuse the windows already open: vatSys never releases a closed window, and each new
            // radar window costs it threads it does not give back.
            var open = Vatsys.AllForms().Where(f => f.GetType() == type && Vatsys.IsVisible(f)).ToList();
            var assigned = new Dictionary<WindowEntry, Form>();

            foreach (var entry in wanted)
            {
                var match = open.FirstOrDefault(f => !assigned.ContainsValue(f) && Same(Vatsys.DisplayPositionName(f), entry.Asd?.Position));
                if (match != null) assigned[entry] = match;
            }
            foreach (var entry in wanted.Where(e => !assigned.ContainsKey(e)))
            {
                var spare = open.FirstOrDefault(f => !assigned.ContainsValue(f));
                if (spare != null) assigned[entry] = spare;
            }

            foreach (var extra in open.Where(f => !assigned.ContainsValue(f))) extra.Close();

            foreach (var entry in wanted)
            {
                var created = false;
                if (!assigned.TryGetValue(entry, out var window))
                {
                    window = Vatsys.CreateDisplayWindow(ground, entry.Actual ?? entry.Normal);
                    if (window == null)
                    {
                        run.Report.Problem($"a {(ground ? "Ground" : "ASD")} window could not be opened");
                        continue;
                    }
                    created = true;
                }

                if (created && entry.State == WindowStateName.Minimized && entry.Asd != null)
                {
                    // A display sets its default zoom on its first draw, and a minimised one never
                    // draws. Let a new one draw in its normal state, apply the view, then minimise it.
                    var shown = new WindowEntry { Kind = entry.Kind, Title = entry.Title, Visible = true, State = WindowStateName.Normal, Normal = entry.Normal, Actual = entry.Actual, Z = entry.Z };
                    Placement.Apply(window, shown, run.Report);
                    run.Placed[entry] = window;
                    var target = window;
                    ApplyViewWhenReady(run, window, entry, isMain: false, then: () => { if (Vatsys.Alive(target)) Place(run, target, entry); });
                    continue;
                }

                Place(run, window, entry);
                ApplyViewWhenReady(run, window, entry, isMain: false);
            }
        }

        // ---- other plugins' windows ---------------------------------------------------------------------------------

        private static Form MatchPluginWindow(WindowEntry entry, IEnumerable<Form> candidates, HashSet<Form> used)
        {
            var sameType = candidates.Where(f => !used.Contains(f) && string.Equals(f.GetType().FullName, entry.TypeName, StringComparison.Ordinal)).ToList();
            return sameType.FirstOrDefault(f => f.Name == entry.FormName && f.Text == entry.Title)
                   ?? sameType.FirstOrDefault(f => f.Name == entry.FormName)
                   ?? sameType.FirstOrDefault();
        }

        private static void RestorePluginWindows(Run run)
        {
            var wanted = Entries(run, WindowKind.Plugin).Where(e => !string.IsNullOrWhiteSpace(e.TypeName)).ToList();
            var all = PluginWindows.AllWindows();
            var used = new HashSet<Form>();

            // First, pair each saved window with one that exists (showing or hidden).
            var existing = new Dictionary<WindowEntry, Form>();
            foreach (var entry in wanted.Where(e => e.Visible))
            {
                var match = MatchPluginWindow(entry, all.Where(Vatsys.IsVisible), used) ?? MatchPluginWindow(entry, all, used);
                if (match == null) continue;
                existing[entry] = match;
                used.Add(match);
            }

            // A showing window the layout does not have is closed only when all of these hold: the
            // controller could close it themselves; its menu item is known, so another layout can
            // bring it back; and that is known for certain (it was seen being opened) or the window
            // is one the controller has saved in a layout. A plugin's pop-up notice is none of those.
            HashSet<string> savedTypes = null;
            foreach (var extra in all.Where(f => Vatsys.IsVisible(f) && !used.Contains(f)).ToList())
            {
                if (!PluginWindows.CanUserClose(extra)) continue;
                var opener = PluginWindows.OpenerFor(extra);
                if (opener == null) continue;
                var typeName = extra.GetType().FullName;
                if (!PluginWindows.HasLearnedOpener(typeName))
                {
                    savedTypes = savedTypes ?? LayoutStore.PluginWindowTypes();
                    if (!savedTypes.Contains(typeName)) continue;
                }
                run.Report.Note($"closing \"{extra.Text}\" (not in this layout; reopened by '{opener}')");
                extra.Close();
            }

            foreach (var entry in wanted.Where(e => e.Visible))
            {
                var label = string.IsNullOrWhiteSpace(entry.Title) ? entry.TypeName : entry.Title;
                existing.TryGetValue(entry, out var window);

                if (Vatsys.IsVisible(window))
                {
                    Place(run, window, entry);
                    continue;
                }

                // Not on screen. Only its own plugin can open it: click that plugin's menu item.
                Placement.Seed(entry.FormName, entry);
                // The pairing known now comes first; the one saved with the layout is the fallback.
                var openers = new List<MenuOpener>();
                foreach (var candidate in new[] { PluginWindows.OpenerFor(entry.TypeName, window?.GetType().Assembly ?? AssemblyDefining(entry.TypeName), entry.Title), entry.Opener })
                {
                    if (candidate != null && openers.All(o => o.ToString() != candidate.ToString())) openers.Add(candidate);
                }
                var asked = openers.Any(PluginWindows.Click);

                if (!asked && Vatsys.Alive(window))
                {
                    // It exists but its plugin hid it, and no menu item is known. Show it as it is.
                    try
                    {
                        if (window is BaseForm baseForm) baseForm.Show(Vatsys.MainForm);
                        else window.Show();
                        asked = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Showing '{label}' failed", ex);
                    }
                }

                if (window != null) used.Remove(window);
                var target = entry;
                var appearedAt = DateTime.MinValue;
                Form placedWindow = null;

                bool TryPlace()
                {
                    if (placedWindow == null)
                    {
                        var found = MatchPluginWindow(target, PluginWindows.AllWindows().Where(Vatsys.IsVisible), used);
                        if (found == null) return false;
                        used.Add(found);
                        placedWindow = found;
                        appearedAt = DateTime.UtcNow;
                        Place(run, found, target);
                    }
                    return true;
                }

                // Some plugins move their window themselves just after showing it. For a short while
                // after it appears, put it back if it has moved; then leave it to the controller.
                void Settle()
                {
                    if (placedWindow == null || !Vatsys.IsVisible(placedWindow)) return;
                    if ((DateTime.UtcNow - appearedAt).TotalMilliseconds > SettleMs) return;
                    var want = target.Actual ?? target.Normal;
                    if (want == null || target.State != WindowStateName.Normal) return;
                    if (User32.GetWindowRect(placedWindow.Handle, out var now) &&
                        (now.left != want.Left || now.top != want.Top || now.right != want.Right || now.bottom != want.Bottom))
                    {
                        Placement.Apply(placedWindow, target, null);
                    }
                }

                if (!asked)
                {
                    run.Report.Problem($"\"{label}\" belongs to another plugin and is not open. Open it from its menu once; after that it is reopened with the layout");
                    // If the controller opens it in the next few seconds, still put it in place.
                    run.Watchers.Add(() => { if (TryPlace()) Settle(); });
                    continue;
                }

                if (!TryPlace())
                {
                    run.Pending.Add(new Pending { What = $"\"{label}\"", Try = TryPlace });
                }
                run.Watchers.Add(Settle);
            }
        }

        private static System.Reflection.Assembly AssemblyDefining(string typeName)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic) continue;
                    if (assembly.GetType(typeName, false) != null) return assembly;
                }
            }
            catch
            {
                // not found
            }
            return null;
        }

        // ---- VSCS ---------------------------------------------------------------------------------

        private static void RestoreVscs(Run run)
        {
            var entry = Entries(run, WindowKind.Vscs).FirstOrDefault();
            var vscs = Vatsys.Vscs;
            // VSCS has no close button and is always there; only its place and state are restored.
            if (entry != null && Vatsys.Alive(vscs)) Place(run, vscs, entry);
        }
    }
}
