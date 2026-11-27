using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PInvoke;

namespace vatSysWindowManager
{
    /// <summary>Pure rectangle arithmetic, kept apart from Win32 so it can be tested.</summary>
    internal static class Geometry
    {
        /// <summary>How much of a window must be on a screen for it to count as reachable.</summary>
        internal const int MinVisibleWidth = 60;
        internal const int MinVisibleHeight = 16;

        /// <summary>The strip at the top of a window that holds its title bar.</summary>
        internal const int TitleStripHeight = 40;

        /// <summary>
        /// Returns <paramref name="rect"/> unchanged when enough of it is on one of the screens, and
        /// otherwise the same size moved onto the nearest screen. A layout saved with a second
        /// monitor must not put windows where there is now no screen.
        /// </summary>
        internal static IntRect EnsureOnScreen(IntRect rect, IReadOnlyList<IntRect> screens, out bool moved)
        {
            moved = false;
            if (rect == null || screens == null || screens.Count == 0 || rect.Width <= 0 || rect.Height <= 0) return rect;

            foreach (var screen in screens)
            {
                var w = Math.Min(rect.Right, screen.Right) - Math.Max(rect.Left, screen.Left);
                // Only the top strip matters: that is where the title bar is. A maximised window
                // overhangs its screen by a few pixels on every side, so not all of the strip need show.
                var h = Math.Min(Math.Min(rect.Top + TitleStripHeight, rect.Bottom), screen.Bottom) - Math.Max(rect.Top, screen.Top);
                if (w >= Math.Min(MinVisibleWidth, rect.Width) && h >= Math.Min(MinVisibleHeight, rect.Height)) return rect;
            }

            var nearest = screens.OrderBy(s => DistanceSquared(rect, s)).First();
            var width = Math.Min(rect.Width, nearest.Width);
            var height = Math.Min(rect.Height, nearest.Height);
            var left = Math.Max(nearest.Left, Math.Min(rect.Left, nearest.Right - width));
            var top = Math.Max(nearest.Top, Math.Min(rect.Top, nearest.Bottom - height));
            moved = true;
            return new IntRect(left, top, left + width, top + height);
        }

        private static long DistanceSquared(IntRect rect, IntRect screen)
        {
            long cx = (rect.Left + rect.Right) / 2, cy = (rect.Top + rect.Bottom) / 2;
            long dx = cx < screen.Left ? screen.Left - cx : cx > screen.Right ? cx - screen.Right : 0;
            long dy = cy < screen.Top ? screen.Top - cy : cy > screen.Bottom ? cy - screen.Bottom : 0;
            return dx * dx + dy * dy;
        }
    }

    /// <summary>Reading and applying where a window is: rectangle, state, visibility and z-order.</summary>
    internal static class Placement
    {
        private const int SW_HIDE = 0;
        private const int SW_SHOWNORMAL = 1;
        private const int SW_SHOWMAXIMIZED = 3;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_SHOWMINNOACTIVE = 7;

        private static List<IntRect> ScreenWorkAreas()
        {
            return Screen.AllScreens.Select(s => new IntRect(s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Right, s.WorkingArea.Bottom)).ToList();
        }

        // ---- capture ------------------------------------------------------------------------------

        /// <summary>
        /// Fills in the placement half of an entry. A form that has never had a window is recorded as
        /// hidden with no rectangle: asking for its handle here would create it.
        /// </summary>
        internal static void Capture(Form form, WindowEntry entry, IList<IntPtr> zOrderTopDown)
        {
            entry.TypeName = form.GetType().FullName;
            entry.FormName = form.Name;
            entry.Title = form.Text;
            entry.Z = -1;

            if (!form.IsHandleCreated)
            {
                entry.Visible = false;
                return;
            }

            var handle = form.Handle;
            entry.Visible = Native.IsWindowVisible(handle);
            entry.State = Native.IsIconic(handle) ? WindowStateName.Minimized
                : Native.IsZoomed(handle) ? WindowStateName.Maximized
                : WindowStateName.Normal;

            var placement = User32.GetWindowPlacement(handle);
            var normal = new IntRect(placement.rcNormalPosition.left, placement.rcNormalPosition.top, placement.rcNormalPosition.right, placement.rcNormalPosition.bottom);
            entry.Normal = normal.IsUsable ? normal : null;

            // The rectangle really on screen. It is only meaningful for a shown, normal-state window:
            // a minimised one reports (-32000,-32000) and a maximised one its monitor.
            if (entry.Visible && entry.State == WindowStateName.Normal && User32.GetWindowRect(handle, out var rect))
            {
                var actual = new IntRect(rect.left, rect.top, rect.right, rect.bottom);
                if (actual.IsUsable) entry.Actual = actual;
            }

            if (entry.Visible && zOrderTopDown != null) entry.Z = zOrderTopDown.IndexOf(handle);
        }

        // ---- apply ----------------------------------------------------------------------------------

        private static User32.WINDOWPLACEMENT Build(IntRect normal, int showCmd)
        {
            return new User32.WINDOWPLACEMENT
            {
                length = System.Runtime.InteropServices.Marshal.SizeOf<User32.WINDOWPLACEMENT>(),
                showCmd = (User32.WindowShowStyle)showCmd,
                ptMinPosition = new POINT { x = -1, y = -1 },
                ptMaxPosition = new POINT { x = -1, y = -1 },
                rcNormalPosition = new RECT { left = normal.Left, top = normal.Top, right = normal.Right, bottom = normal.Bottom }
            };
        }

        /// <summary>
        /// Records where vatSys should put a window the next time it shows it by name, without
        /// needing the window to exist yet.
        /// </summary>
        internal static void Seed(string formName, WindowEntry entry)
        {
            var normal = entry?.Normal ?? entry?.Actual;
            if (string.IsNullOrWhiteSpace(formName) || normal == null || !normal.IsUsable) return;
            normal = Geometry.EnsureOnScreen(normal, ScreenWorkAreas(), out _);
            Vatsys.SeedPlacement(formName, Build(normal, entry.State == WindowStateName.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL));
        }

        /// <summary>
        /// Puts a window where the entry says, in the state the entry says. It does not show a hidden
        /// window or hide a shown one: opening and closing is done first, through vatSys's own
        /// methods, and this then sets the place and state for whichever it is.
        /// </summary>
        internal static void Apply(Form form, WindowEntry entry, RestoreReport report)
        {
            if (!Vatsys.Alive(form) || entry == null) return;

            try
            {
                var screens = ScreenWorkAreas();
                var normal = entry.Normal ?? entry.Actual;
                var movedOnScreen = false;
                if (normal != null) normal = Geometry.EnsureOnScreen(normal, screens, out movedOnScreen);

                // A form with no window yet: record its place for when vatSys shows it, and stop.
                if (!form.IsHandleCreated)
                {
                    if (normal != null) Vatsys.SeedPlacement(form.Name, Build(normal, SW_SHOWNORMAL));
                    return;
                }

                var handle = form.Handle;
                var visible = Native.IsWindowVisible(handle);
                var current = User32.GetWindowPlacement(handle);
                if (normal == null)
                {
                    normal = new IntRect(current.rcNormalPosition.left, current.rcNormalPosition.top, current.rcNormalPosition.right, current.rcNormalPosition.bottom);
                }

                int showCmd;
                if (!visible) showCmd = SW_HIDE;
                else if (entry.State == WindowStateName.Minimized) showCmd = SW_SHOWMINNOACTIVE;
                else if (entry.State == WindowStateName.Maximized) showCmd = SW_SHOWMAXIMIZED;
                else showCmd = SW_SHOWNOACTIVATE;

                User32.SetWindowPlacement(handle, Build(normal, showCmd));

                // A window docked with Windows Snap sits somewhere other than its restore rectangle.
                if (visible && entry.State == WindowStateName.Normal && entry.Actual != null && entry.Actual.IsUsable && !movedOnScreen)
                {
                    var actual = Geometry.EnsureOnScreen(entry.Actual, screens, out var actualMoved);
                    if (!actualMoved && User32.GetWindowRect(handle, out var now) &&
                        (now.left != actual.Left || now.top != actual.Top || now.right != actual.Right || now.bottom != actual.Bottom))
                    {
                        Native.MoveWindowQuietly(handle, actual.Left, actual.Top, actual.Width, actual.Height);
                    }
                }

                if (User32.GetWindowRect(handle, out var after))
                {
                    Log.Debug($"placed \"{Label(form, entry)}\" [{entry.Kind} {entry.Key}] visible={visible} state={entry.State} normal={normal} actual={entry.Actual} -> ({after.left},{after.top})-({after.right},{after.bottom})");
                }

                if (movedOnScreen)
                {
                    report?.Problem($"\"{Label(form, entry)}\" was saved on a screen that is not connected and was moved onto this one");
                }

                // Keep vatSys's own record in step, so closing and reopening the window keeps the place.
                if (form is vatsys.BaseForm)
                {
                    Vatsys.SeedPlacement(form.Name, Build(normal, entry.State == WindowStateName.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL));
                }
            }
            catch (Exception ex)
            {
                report?.Problem($"\"{Label(form, entry)}\" could not be moved into place");
                Log.Error($"Placing '{Label(form, entry)}' failed", ex);
            }
        }

        private static string Label(Form form, WindowEntry entry)
        {
            var text = form?.Text;
            if (string.IsNullOrWhiteSpace(text)) text = entry?.Title;
            if (string.IsNullOrWhiteSpace(text)) text = form?.Name ?? entry?.Kind;
            return text;
        }

        /// <summary>
        /// Stacks the restored windows in the saved order. Each is raised in turn from the bottom of
        /// the saved order to the top, without activating any of them. The main form is left out:
        /// every vatSys window is owned by it and so always sits above it.
        /// </summary>
        internal static void ApplyZOrder(IEnumerable<KeyValuePair<WindowEntry, Form>> placed)
        {
            var ordered = placed
                .Where(p => p.Key != null && p.Key.Kind != WindowKind.Main && p.Key.Z >= 0 && Vatsys.IsVisible(p.Value))
                .OrderByDescending(p => p.Key.Z)
                .ToList();

            foreach (var pair in ordered)
            {
                try
                {
                    Native.RaiseQuietly(pair.Value.Handle);
                }
                catch
                {
                    // one window refusing to move must not stop the rest
                }
            }
        }
    }
}
