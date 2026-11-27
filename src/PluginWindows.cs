using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace vatSysWindowManager
{
    /// <summary>
    /// Windows that belong to other plugins.
    ///
    /// A layout can save where another plugin's window was and put it back, but only that plugin can
    /// open it. So this class works out which of the plugin menu items vatSys shows opens which
    /// window, and reopens a window by clicking that item, exactly as the controller would.
    ///
    /// It learns in two ways. While vatSys runs it watches: when a plugin menu item is clicked and a
    /// new window belonging to that same plugin appears straight afterwards, that pairing is
    /// remembered (and kept between sessions). For a window it has never seen opened, it will use a
    /// menu item only if it belongs to the same plugin and its text names the window.
    ///
    /// It never constructs another plugin's window itself and never clicks one of vatSys's own menu
    /// items.
    /// </summary>
    internal static class PluginWindows
    {
        private const string WatchJob = "plugin-window-watch";
        private const int LearnWindowMs = 2500;

        private static Dictionary<string, MenuOpener> learned;
        private static readonly HashSet<ToolStripItem> Hooked = new HashSet<ToolStripItem>();
        private static HashSet<Form> knownVisible;
        private static MenuOpener lastClicked;
        private static ToolStripItem lastClickedItem;
        private static DateTime lastClickedAt;
        private static bool replaying;

        /// <summary>This plugin's own menu, which is never an opener for anything.</summary>
        internal static ToolStripItem OwnMenu { get; set; }

        internal static void Start()
        {
            learned = LayoutStore.LoadOpeners();
            Ui.Poll(WatchJob, 500, 0, () =>
            {
                HookMenuItems();
                NoticeNewWindows();
                return false;
            });
        }

        // ---- which windows count ---------------------------------------------------------------------

        /// <summary>True for a window a layout can own on behalf of another plugin.</summary>
        internal static bool IsPluginWindow(Form form)
        {
            if (!Vatsys.Alive(form) || !form.IsHandleCreated || !form.TopLevel || form.Modal) return false;
            var type = form.GetType();
            if (Vatsys.IsVatsysType(type) || type.Assembly == typeof(PluginWindows).Assembly) return false;
            if (type.Assembly.GlobalAssemblyCache) return false;               // framework dialogs
            if (Vatsys.ForeignArrivalAirport(form) != null) return false;      // handled as an arrival list
            return form.Width >= 60 && form.Height >= 40;
        }

        internal static List<Form> AllWindows() => Vatsys.AllForms().Where(IsPluginWindow).ToList();

        internal static List<Form> VisibleWindows() => AllWindows().Where(Vatsys.IsVisible).ToList();

        /// <summary>True when the controller could close the window themselves, so closing it here is no more than that.</summary>
        internal static bool CanUserClose(Form form)
        {
            if (form is BaseForm baseForm) return baseForm.HasCloseButton;
            return form.ControlBox;
        }

        // ---- learning -----------------------------------------------------------------------------------

        private static string Normalize(string text) => (text ?? string.Empty).Replace("&", string.Empty).Trim();

        private static IEnumerable<CustomToolStripMenuItem> MainMenuItems()
        {
            return Vatsys.PluginMenuItems().Where(c => c.Window == CustomToolStripMenuItemWindowType.Main && !ReferenceEquals(c.Item, OwnMenu));
        }

        private static void HookMenuItems()
        {
            foreach (var custom in MainMenuItems()) Hook(custom.Item, custom);
        }

        private static void Hook(ToolStripItem item, CustomToolStripMenuItem root)
        {
            if (item == null) return;
            if (Hooked.Add(item))
            {
                item.Click += (s, e) => Ui.Guard(() => OnClicked(item, root))();
                if (item is ToolStripMenuItem parent)
                {
                    // Some plugins fill a submenu only when it opens.
                    parent.DropDownOpened += (s, e) => Ui.Guard(() =>
                    {
                        foreach (ToolStripItem child in parent.DropDownItems) Hook(child, root);
                    })();
                }
            }

            if (item is ToolStripMenuItem menu)
            {
                foreach (ToolStripItem child in menu.DropDownItems) Hook(child, root);
            }
        }

        private static void OnClicked(ToolStripItem item, CustomToolStripMenuItem root)
        {
            if (replaying || Restore.IsRunning) return;
            if (item is ToolStripMenuItem menu && menu.HasDropDownItems) return; // only opened a submenu

            var opener = Describe(item, root);
            if (opener == null) return;
            lastClicked = opener;
            lastClickedItem = item;
            lastClickedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// True when two assemblies are parts of one plugin: the same DLL, or DLLs in the same folder
        /// (a plugin often keeps its windows in a second DLL beside the one vatSys loads).
        /// </summary>
        private static bool SamePlugin(Assembly a, Assembly b)
        {
            if (a == null || b == null) return false;
            if (a == b) return true;
            try
            {
                if (a.IsDynamic || b.IsDynamic || string.IsNullOrEmpty(a.Location) || string.IsNullOrEmpty(b.Location)) return false;
                return string.Equals(System.IO.Path.GetDirectoryName(a.Location), System.IO.Path.GetDirectoryName(b.Location), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HandledBy(ToolStripItem item, Assembly windowAssembly) =>
            ClickHandlerAssemblies(item).Any(a => SamePlugin(a, windowAssembly));

        private static MenuOpener Describe(ToolStripItem item, CustomToolStripMenuItem root)
        {
            var path = new List<string>();
            var current = item;
            while (current != null)
            {
                path.Insert(0, Normalize(current.Text));
                if (ReferenceEquals(current, root.Item)) break;
                current = current.OwnerItem;
            }
            if (current == null || path.Any(string.IsNullOrEmpty)) return null;

            return new MenuOpener
            {
                Category = root.Category.ToString(),
                CustomCategory = root.CustomCategoryName,
                Path = path
            };
        }

        private static void NoticeNewWindows()
        {
            var visible = new HashSet<Form>(VisibleWindows());
            if (knownVisible == null)
            {
                knownVisible = visible;
                return;
            }

            var appeared = visible.Where(f => !knownVisible.Contains(f)).ToList();
            knownVisible = visible;
            if (appeared.Count == 0 || lastClicked == null) return;
            if ((DateTime.UtcNow - lastClickedAt).TotalMilliseconds > LearnWindowMs)
            {
                lastClicked = null;
                return;
            }

            var changed = false;
            foreach (var form in appeared)
            {
                var key = form.GetType().FullName;
                if (string.IsNullOrEmpty(key)) continue;

                // A window from some other plugin that happened to open at the same moment (on
                // connect, by a hotkey) is not this menu item's doing.
                if (lastClickedItem == null || !HandledBy(lastClickedItem, form.GetType().Assembly)) continue;

                if (!learned.TryGetValue(key, out var existing) || existing.ToString() != lastClicked.ToString())
                {
                    learned[key] = lastClicked.Clone();
                    changed = true;
                    Log.Info($"Learned that '{lastClicked}' opens {key}");
                }
            }
            lastClicked = null;
            if (changed) LayoutStore.SaveOpeners(learned);
        }

        // ---- finding the menu item for a window ---------------------------------------------------------------

        /// <summary>The menu item that opens a window of this type, or null when it cannot be worked out.</summary>
        internal static MenuOpener OpenerFor(string typeName, Assembly assembly, string title)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (learned != null && learned.TryGetValue(typeName, out var known)) return known.Clone();

            // Never seen it opened. This is a guess, and a wrong guess means clicking something the
            // controller did not ask for, so it has to pass two tests: the menu item belongs to the
            // same plugin as the window, and the item's text names the window (it is in the window's
            // title or type name). Exactly one item must pass. "Settings..." on a plugin whose
            // window is called something else does not.
            if (assembly == null) return null;

            var matches = new List<KeyValuePair<ToolStripItem, CustomToolStripMenuItem>>();
            foreach (var custom in MainMenuItems())
            {
                foreach (var leaf in Leaves(custom.Item))
                {
                    var text = Normalize(leaf.Text);
                    if (text.Length < 3) continue;
                    var named = (title ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                typeName.IndexOf(text.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase) >= 0;
                    if (named && HandledBy(leaf, assembly)) matches.Add(new KeyValuePair<ToolStripItem, CustomToolStripMenuItem>(leaf, custom));
                }
            }
            return matches.Count == 1 ? Describe(matches[0].Key, matches[0].Value) : null;
        }

        /// <summary>True when this plugin has itself seen a menu item open a window of this type.</summary>
        internal static bool HasLearnedOpener(string typeName) =>
            !string.IsNullOrEmpty(typeName) && learned != null && learned.ContainsKey(typeName);

        internal static MenuOpener OpenerFor(Form form) =>
            form == null ? null : OpenerFor(form.GetType().FullName, form.GetType().Assembly, form.Text);

        private static IEnumerable<ToolStripItem> Leaves(ToolStripItem item)
        {
            if (item is ToolStripMenuItem menu && menu.HasDropDownItems)
            {
                foreach (ToolStripItem child in menu.DropDownItems)
                {
                    foreach (var leaf in Leaves(child)) yield return leaf;
                }
            }
            else if (item != null && !(item is ToolStripSeparator))
            {
                yield return item;
            }
        }

        // The assemblies whose code runs when the item is clicked. Read from the component's event
        // list; if WinForms ever stores it differently this returns nothing and the text match is used.
        private static HashSet<Assembly> ClickHandlerAssemblies(ToolStripItem item)
        {
            var result = new HashSet<Assembly>();
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var events = typeof(Component).GetProperty("Events", flags)?.GetValue(item) as EventHandlerList;
                var key = typeof(ToolStripItem).GetField("EventClick", flags)?.GetValue(null);
                if (events == null || key == null) return result;

                var handlers = events[key];
                if (handlers == null) return result;
                foreach (var handler in handlers.GetInvocationList())
                {
                    var assembly = handler.Method.Module.Assembly;
                    if (assembly != typeof(PluginWindows).Assembly) result.Add(assembly);
                }
            }
            catch
            {
                // fall back to matching by text
            }
            return result;
        }

        // ---- clicking -----------------------------------------------------------------------------------

        internal static ToolStripItem Resolve(MenuOpener opener)
        {
            if (opener?.Path == null || opener.Path.Count == 0) return null;
            foreach (var custom in MainMenuItems())
            {
                if (!string.Equals(custom.Category.ToString(), opener.Category, StringComparison.Ordinal)) continue;
                if (!string.Equals(custom.CustomCategoryName ?? string.Empty, opener.CustomCategory ?? string.Empty, StringComparison.Ordinal)) continue;
                if (!string.Equals(Normalize(custom.Item.Text), opener.Path[0], StringComparison.OrdinalIgnoreCase)) continue;

                var current = custom.Item;
                var ok = true;
                for (var i = 1; i < opener.Path.Count && ok; i++)
                {
                    var next = (current as ToolStripMenuItem)?.DropDownItems.Cast<ToolStripItem>()
                        .FirstOrDefault(c => string.Equals(Normalize(c.Text), opener.Path[i], StringComparison.OrdinalIgnoreCase));
                    if (next == null) ok = false;
                    else current = next;
                }
                if (ok) return current;
            }
            return null;
        }

        /// <summary>Clicks the plugin's menu item. Returns false when the item is gone or disabled.</summary>
        internal static bool Click(MenuOpener opener)
        {
            var item = Resolve(opener);
            if (item == null || !item.Enabled) return false;

            replaying = true;
            try
            {
                item.PerformClick();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Clicking plugin menu item '{opener}' failed", ex);
                return false;
            }
            finally
            {
                replaying = false;
            }
        }
    }
}
