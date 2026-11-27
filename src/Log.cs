using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using vatsys;

namespace vatSysWindowManager
{
    /// <summary>
    /// A plain text log beside the layouts. A release build of the old plugin logged nothing at all
    /// (Debug.WriteLine is compiled out), so a failed restore left no trace.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object Gate = new object();

        internal static string FilePath => Path.Combine(LayoutStore.Root, "WindowManager.log");

        private static readonly bool Verbose = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VATSYS_WINDOWMANAGER_DEBUG"));

        internal static void Info(string message) => Write("INFO ", message);

        /// <summary>Step-by-step detail, written only when VATSYS_WINDOWMANAGER_DEBUG is set.</summary>
        internal static void Debug(string message)
        {
            if (Verbose) Write("DEBUG", message);
        }

        internal static void Warn(string message) => Write("WARN ", message);

        internal static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + ": " + ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    var path = FilePath;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }

                    File.AppendAllText(path, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {level} {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // The log must never be the thing that breaks vatSys.
            }
        }
    }

    /// <summary>
    /// What one layout load could not do. Written to the log, and shown once in vatSys's Errors
    /// window when there is anything to say.
    /// </summary>
    internal sealed class RestoreReport
    {
        private readonly List<string> problems = new List<string>();
        private readonly string layoutName;
        private bool flushed;

        internal RestoreReport(string layoutName)
        {
            this.layoutName = layoutName;
        }

        internal IReadOnlyList<string> Problems => problems;

        internal void Problem(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || problems.Contains(message)) return;
            problems.Add(message);
            Log.Warn($"[{layoutName}] {message}");
        }

        internal void Note(string message) => Log.Info($"[{layoutName}] {message}");

        /// <summary>Tells the controller, once, what was left undone.</summary>
        internal void Flush()
        {
            if (flushed) return;
            flushed = true;
            if (problems.Count == 0)
            {
                Log.Info($"[{layoutName}] layout loaded");
                return;
            }

            var text = new StringBuilder();
            text.Append($"Layout \"{layoutName}\" loaded with {problems.Count} item(s) not restored: ");
            text.Append(string.Join("; ", problems));
            try
            {
                Errors.Add(new Exception(text.ToString()), "Window Layouts");

                // The load has just stacked the layout's windows above everything else, which can
                // leave vatSys's Errors window underneath them. Bring it up so the report is seen.
                Ui.Post(() =>
                {
                    var errors = Vatsys.SingleForm(Vatsys.FindSingle("CurrentErrorsWindow"));
                    if (Vatsys.IsVisible(errors)) Native.RaiseQuietly(errors.Handle);
                }, "Raising the Errors window");
            }
            catch (Exception ex)
            {
                Log.Error("Could not raise the restore report in vatSys's Errors window", ex);
            }
        }
    }
}
