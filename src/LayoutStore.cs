using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace vatSysWindowManager
{
    internal sealed class LayoutInfo
    {
        public string Position { get; set; }
        public string LayoutName { get; set; }
        public string Path { get; set; }
    }

    /// <summary>Layout files, the auto-load map and the learned plugin menu items, all in one folder.</summary>
    internal static class LayoutStore
    {
        private const string LayoutPattern = "*.layout.json";
        private const string AutoLoadFile = "autoload.json";
        private const string OpenersFile = "openers.json";

        private static string root;

        /// <summary>For tests: point the store at another folder.</summary>
        internal static string RootOverride { get; set; }

        /// <summary>
        /// Documents\vatSys Window Manager, or a Layouts folder beside the plugin when Documents is
        /// not available.
        /// </summary>
        internal static string Root
        {
            get
            {
                if (!string.IsNullOrEmpty(RootOverride))
                {
                    Directory.CreateDirectory(RootOverride);
                    return RootOverride;
                }

                if (root != null && Directory.Exists(root)) return root;

                // For trying a build against a copy of the layouts without touching the real ones.
                try
                {
                    var overridden = Environment.GetEnvironmentVariable("VATSYS_WINDOWMANAGER_LAYOUTS");
                    if (!string.IsNullOrWhiteSpace(overridden))
                    {
                        Directory.CreateDirectory(overridden);
                        return root = overridden;
                    }
                }
                catch
                {
                    // fall through to the normal folder
                }

                try
                {
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    if (!string.IsNullOrWhiteSpace(docs))
                    {
                        var candidate = Path.Combine(docs, "vatSys Window Manager");
                        Directory.CreateDirectory(candidate);
                        return root = candidate;
                    }
                }
                catch
                {
                    // fall through to the plugin folder
                }

                var fallback = Path.Combine(PluginDirectory(), "Layouts");
                Directory.CreateDirectory(fallback);
                return root = fallback;
            }
        }

        internal static string PluginDirectory()
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrWhiteSpace(dir)) return dir;
            }
            catch
            {
                // fall through
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        // ---- file names ---------------------------------------------------------------------

        internal static string Sanitize(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var safe = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
            return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
        }

        internal static string FileNameFor(string position, string layoutName)
        {
            var safePosition = Sanitize(position, "DEFAULT");
            return string.IsNullOrWhiteSpace(layoutName)
                ? $"{safePosition}.layout.json"
                : $"{safePosition}__{Sanitize(layoutName, safePosition)}.layout.json";
        }

        internal static string PathFor(string position, string layoutName) => Path.Combine(Root, FileNameFor(position, layoutName));

        internal static string InferLayoutName(string file, string position)
        {
            var name = Path.GetFileName(file) ?? string.Empty;
            const string extension = ".layout.json";
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - extension.Length);

            var safePosition = Sanitize(position, "DEFAULT");
            var prefix = safePosition + "__";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name.Substring(prefix.Length);
            return string.Equals(name, safePosition, StringComparison.OrdinalIgnoreCase) ? position : name;
        }

        // ---- layouts ------------------------------------------------------------------------

        internal static List<LayoutInfo> List()
        {
            var list = new List<LayoutInfo>();
            string[] files;
            try
            {
                files = Directory.GetFiles(Root, LayoutPattern);
            }
            catch (Exception ex)
            {
                Log.Error("Could not list layouts", ex);
                return list;
            }

            foreach (var file in files)
            {
                try
                {
                    var (position, layoutName) = LayoutUpgrade.ReadHeader(File.ReadAllText(file));
                    if (string.IsNullOrWhiteSpace(position)) position = "DEFAULT";
                    if (string.IsNullOrWhiteSpace(layoutName)) layoutName = InferLayoutName(file, position);
                    list.Add(new LayoutInfo { Position = position, LayoutName = layoutName, Path = file });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipped unreadable layout file '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            return list;
        }

        internal static List<LayoutInfo> ListFor(string position)
        {
            return List()
                .Where(l => string.Equals(l.Position, position, StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.LayoutName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The types of other plugins' windows that appear in any saved layout: the windows the
        /// controller has made part of their layouts, as opposed to ones that just happen to be open.
        /// </summary>
        internal static HashSet<string> PluginWindowTypes()
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in List())
            {
                try
                {
                    foreach (var window in Load(info.Path).Windows)
                    {
                        if (window.Kind == WindowKind.Plugin && !string.IsNullOrEmpty(window.TypeName)) types.Add(window.TypeName);
                    }
                }
                catch
                {
                    // an unreadable layout contributes nothing
                }
            }
            return types;
        }

        internal static Layout Load(string path)
        {
            var layout = LayoutUpgrade.Parse(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(layout.Position)) layout.Position = "DEFAULT";
            if (string.IsNullOrWhiteSpace(layout.LayoutName)) layout.LayoutName = InferLayoutName(path, layout.Position);
            return layout;
        }

        internal static void Save(Layout layout, string path)
        {
            WriteAtomically(path, LayoutUpgrade.Serialize(layout));
        }

        /// <summary>
        /// Writes beside the target and swaps it in, so a crash or a full disk mid-write cannot leave
        /// a half-written layout where a good one was.
        /// </summary>
        internal static void WriteAtomically(string path, string contents)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        // ---- auto-load map --------------------------------------------------------------------

        internal static Dictionary<string, string> LoadAutoLoadMap()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(Root, AutoLoadFile);
                if (!File.Exists(path)) return result;
                var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (map == null) return result;
                foreach (var kv in map)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)) result[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the auto-load map", ex);
            }
            return result;
        }

        internal static void SaveAutoLoadMap(Dictionary<string, string> map)
        {
            try
            {
                WriteAtomically(Path.Combine(Root, AutoLoadFile), JsonConvert.SerializeObject(map, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Error("Could not save the auto-load map", ex);
            }
        }

        // ---- learned plugin menu items ----------------------------------------------------------

        internal static Dictionary<string, MenuOpener> LoadOpeners()
        {
            try
            {
                var path = Path.Combine(Root, OpenersFile);
                if (File.Exists(path))
                {
                    var map = JsonConvert.DeserializeObject<Dictionary<string, MenuOpener>>(File.ReadAllText(path));
                    if (map != null) return new Dictionary<string, MenuOpener>(map, StringComparer.Ordinal);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the learned plugin menu items", ex);
            }
            return new Dictionary<string, MenuOpener>(StringComparer.Ordinal);
        }

        internal static void SaveOpeners(Dictionary<string, MenuOpener> openers)
        {
            try
            {
                WriteAtomically(Path.Combine(Root, OpenersFile), JsonConvert.SerializeObject(openers, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Error("Could not save the learned plugin menu items", ex);
            }
        }

        // ---- one-off move from older locations ----------------------------------------------------

        /// <summary>Moves layouts from where older versions kept them. Existing files are never overwritten.</summary>
        internal static void MigrateLegacy()
        {
            try
            {
                var destination = Root;
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var legacyRoots = new[]
                {
                    Path.Combine(PluginDirectory(), "Layouts"),
                    string.IsNullOrWhiteSpace(docs) ? null : Path.Combine(docs, "vatSysWindowManager"),
                    string.IsNullOrWhiteSpace(docs) ? null : Path.Combine(docs, "vatSysWindowManager", "Layouts")
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var legacy in legacyRoots)
                {
                    if (string.Equals(legacy, destination, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy)) continue;

                    var files = Directory.GetFiles(legacy, LayoutPattern, SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(legacy, AutoLoadFile, SearchOption.TopDirectoryOnly));
                    foreach (var file in files)
                    {
                        var target = Path.Combine(destination, Path.GetFileName(file));
                        if (File.Exists(target)) continue;
                        try
                        {
                            File.Move(file, target);
                            Log.Info($"Moved '{file}' to '{target}'");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Could not move '{file}': {ex.Message}");
                        }
                    }

                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
                    }
                    catch
                    {
                        // leaving an empty folder behind is harmless
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Moving layouts from an older location failed", ex);
            }
        }
    }
}
