using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace vatSysWindowManager
{
    /// <summary>
    /// The few Win32 calls the plugin needs that PInvoke.User32 either does not expose or exposes in
    /// a shape that is awkward here. Window placement itself goes through PInvoke.User32, because
    /// vatSys's own placement store (MMI.BaseFormPlacements) is typed with its WINDOWPLACEMENT.
    /// </summary>
    internal static class Native
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        /// <summary>The thread this is called on. Called once from the UI thread and remembered by <see cref="Ui"/>.</summary>
        internal static uint CurrentThreadId() => GetCurrentThreadId();

        /// <summary>
        /// Every WinForms form that has a window on the given thread, shown or hidden. This is the
        /// inventory the plugin trusts: Application.OpenForms leaves out forms that vatSys shows
        /// natively through BaseForm.ShowWithPlacement.
        /// </summary>
        internal static List<Form> FormsOnThread(uint threadId)
        {
            var result = new List<Form>();
            var seen = new HashSet<Form>();
            EnumThreadWindows(threadId, (hWnd, _) =>
            {
                if (Control.FromHandle(hWnd) is Form form && !form.IsDisposed && seen.Add(form))
                {
                    result.Add(form);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// This process's top-level windows from the top of the z-order down. EnumWindows walks the
        /// z-order, which is the only order Windows keeps.
        /// </summary>
        internal static List<IntPtr> ZOrderTopDown()
        {
            var result = new List<IntPtr>();
            var me = GetCurrentProcessId();
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == me) result.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>Moves and sizes a window without activating it or changing its place in the z-order.</summary>
        internal static bool MoveWindowQuietly(IntPtr hWnd, int x, int y, int width, int height)
        {
            return SetWindowPos(hWnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        /// <summary>Raises a window to the top of its z-order group without activating, moving or sizing it.</summary>
        internal static bool RaiseQuietly(IntPtr hWnd)
        {
            return SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }
}
