using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace vatSysWindowManager
{
    /// <summary>
    /// The only way this plugin gets work onto vatSys's UI thread.
    ///
    /// vatSys runs with UnhandledExceptionMode.ThrowException and does not guard Application.Run, so
    /// an exception that escapes a posted delegate, a timer tick or an event handler ends the whole
    /// process. Everything here is wrapped by <see cref="Guard"/>.
    ///
    /// Waiting is done by one WinForms timer that re-checks short jobs ("is that window up yet?"),
    /// never by sleeping the UI thread: a window created on the UI thread cannot appear while that
    /// thread sleeps.
    /// </summary>
    internal static class Ui
    {
        private sealed class Job
        {
            public string Name;
            public DateTime Deadline;
            public DateTime NextRun;
            public int IntervalMs;
            public Func<bool> Step;
            public Action OnTimeout;
        }

        private const int TickMs = 100;

        private static SynchronizationContext context;
        private static System.Windows.Forms.Timer timer;
        private static readonly List<Job> Jobs = new List<Job>();
        private static uint uiThreadId;

        internal static uint ThreadId => uiThreadId;

        internal static bool Ready => timer != null;

        /// <summary>
        /// Call on the UI thread (the plugin's constructor runs there: vatSys composes its plugins
        /// from the main form's constructor).
        /// </summary>
        internal static bool Init()
        {
            if (timer != null) return true;

            context = SynchronizationContext.Current as WindowsFormsSynchronizationContext;
            if (context == null) return false;

            uiThreadId = Native.CurrentThreadId();
            timer = new System.Windows.Forms.Timer { Interval = TickMs };
            timer.Tick += (s, e) => Guard(Tick)();
            return true;
        }

        /// <summary>Wraps an action so nothing it throws reaches vatSys.</summary>
        internal static Action Guard(Action action, string what = null)
        {
            return () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error(what ?? "Unhandled error on the UI thread", ex);
                }
            };
        }

        /// <summary>Runs the action on the UI thread after the current message has finished.</summary>
        internal static void Post(Action action, string what = null)
        {
            var guarded = Guard(action, what);
            var ctx = context;
            if (ctx == null)
            {
                Log.Warn("UI thread not available; skipped: " + (what ?? "action"));
                return;
            }
            ctx.Post(_ => guarded(), null);
        }

        /// <summary>
        /// Re-runs <paramref name="step"/> on the UI thread every <paramref name="intervalMs"/> until it
        /// returns true or the time runs out. Pass 0 for no time limit.
        /// </summary>
        internal static void Poll(string name, int intervalMs, int timeoutMs, Func<bool> step, Action onTimeout = null)
        {
            if (timer == null) return;
            var now = DateTime.UtcNow;
            Jobs.Add(new Job
            {
                Name = name,
                IntervalMs = Math.Max(TickMs, intervalMs),
                NextRun = now,
                Deadline = timeoutMs <= 0 ? DateTime.MaxValue : now.AddMilliseconds(timeoutMs),
                Step = step,
                OnTimeout = onTimeout
            });
            if (!timer.Enabled) timer.Start();
        }

        /// <summary>Drops every job with the given name without running its timeout action.</summary>
        internal static void Cancel(string name)
        {
            Jobs.RemoveAll(j => j.Name == name);
        }

        private static bool ticking;

        private static void Tick()
        {
            // A step can end up pumping messages (another plugin's menu handler opening a dialog, for
            // one). The timer would then tick again inside the step and run every job a second time.
            if (ticking) return;
            ticking = true;
            try
            {
                RunDueJobs();
            }
            finally
            {
                ticking = false;
            }

            if (Jobs.Count == 0) timer.Stop();
        }

        private static void RunDueJobs()
        {
            var now = DateTime.UtcNow;
            foreach (var job in Jobs.ToList())
            {
                if (!Jobs.Contains(job) || now < job.NextRun) continue;

                bool done;
                try
                {
                    job.NextRun = DateTime.UtcNow.AddMilliseconds(job.IntervalMs);
                    done = job.Step();
                }
                catch (Exception ex)
                {
                    Log.Error($"Job '{job.Name}' failed and was dropped", ex);
                    Jobs.Remove(job);
                    continue;
                }

                if (done)
                {
                    Jobs.Remove(job);
                }
                else if (DateTime.UtcNow >= job.Deadline)
                {
                    Jobs.Remove(job);
                    if (job.OnTimeout != null) Guard(job.OnTimeout, $"Timeout handler of '{job.Name}'")();
                }
                else
                {
                    job.NextRun = DateTime.UtcNow.AddMilliseconds(job.IntervalMs);
                }
            }
        }
    }
}
