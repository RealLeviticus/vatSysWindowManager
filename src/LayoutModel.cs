using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace vatSysWindowManager
{
    /// <summary>What each saved window is. The kind decides how it is found, opened and closed.</summary>
    internal static class WindowKind
    {
        public const string Main = "Main";
        public const string Asd = "Asd";
        public const string Ground = "Ground";
        public const string StripState = "StripState";
        public const string StripBeacon = "StripBeacon";
        public const string StripOther = "StripOther";
        public const string Atis = "Atis";
        public const string Arrival = "Arrival";
        public const string Vscs = "Vscs";
        public const string Single = "Single";
        public const string Plugin = "Plugin";
    }

    internal static class WindowStateName
    {
        public const string Normal = "Normal";
        public const string Minimized = "Minimized";
        public const string Maximized = "Maximized";
    }

    /// <summary>One saved layout. Version 2 is the first with a version number; files without one are version 1.</summary>
    internal sealed class Layout
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;
        public string Position { get; set; }
        public string LayoutName { get; set; }
        public DateTime SavedUtc { get; set; }

        public StripSetup Strips { get; set; }
        public List<WindowEntry> Windows { get; set; } = new List<WindowEntry>();

        public bool? DynamicInfill { get; set; }
        public bool? TerrainHeights { get; set; }

        /// <summary>The sectors held when the layout was saved. Only applied when <see cref="RestoreSectors"/> is on.</summary>
        public List<string> ControlledSectors { get; set; }

        /// <summary>
        /// Off by default. Even when on, sectors are only applied while not connected and never
        /// from an empty list: vatSys drops the aircraft tracked in any sector that is removed.
        /// </summary>
        public bool RestoreSectors { get; set; }

        /// <summary>True when the file was written by an older version and upgraded on load.</summary>
        [JsonIgnore]
        public bool Upgraded { get; set; }
    }

    internal sealed class StripSetup
    {
        /// <summary>State or Beacon.</summary>
        public string Mode { get; set; }

        /// <summary>Alpha, Time or Level. Null leaves it alone.</summary>
        public string SortMode { get; set; }

        /// <summary>
        /// True when the layout lists every beacon window that existed, shown or hidden, in order.
        /// Version 1 layouts only listed the visible ones, so for those the plugin adds what is
        /// missing and hides the rest rather than deleting anything.
        /// </summary>
        public bool BeaconsComplete { get; set; }
    }

    internal sealed class WindowEntry
    {
        public string Kind { get; set; }

        /// <summary>
        /// What identifies the window within its kind: the HMI state for a state strip window, the
        /// point for a beacon window, the callsign for ATIS, the airport for an arrival list, the
        /// MMI field name for a single-instance window.
        /// </summary>
        public string Key { get; set; }

        /// <summary>Beacon, ADEP or ADES. Beacon strip windows only.</summary>
        public string StripType { get; set; }

        public string TypeName { get; set; }
        public string FormName { get; set; }
        public string Title { get; set; }

        public bool Visible { get; set; } = true;
        public string State { get; set; } = WindowStateName.Normal;

        /// <summary>The restored (not minimised, not maximised) rectangle, in workspace coordinates.</summary>
        public IntRect Normal { get; set; }

        /// <summary>
        /// The rectangle actually on screen when the window was visible and in the normal state. It
        /// differs from <see cref="Normal"/> for a window docked with Windows Snap.
        /// </summary>
        public IntRect Actual { get; set; }

        /// <summary>Position in the z-order, 0 being the top. -1 when unknown.</summary>
        public int Z { get; set; } = -1;

        public AsdView Asd { get; set; }

        /// <summary>Another plugin's window: the menu item that opens it, when known.</summary>
        public MenuOpener Opener { get; set; }
    }

    internal sealed class AsdView
    {
        /// <summary>The display position's name. Null or empty means Free.</summary>
        public string Position { get; set; }
        public double? CentreLat { get; set; }
        public double? CentreLon { get; set; }
        public double? Range { get; set; }

        /// <summary>Visible maps as "Folder|Name" (version 1 stored the bare name). Null leaves maps alone.</summary>
        public List<string> Maps { get; set; }

        public FloatRect AtcList { get; set; }
        public FloatRect Scale { get; set; }

        /// <summary>The view kept by the Store key. Left alone when <see cref="HasStoredView"/> is false.</summary>
        public bool HasStoredView { get; set; }
        public double? StoredLat { get; set; }
        public double? StoredLon { get; set; }
        public int? StoredRange { get; set; }
    }

    /// <summary>A plugin menu item, identified by where vatSys put it and the text on the way down to it.</summary>
    internal sealed class MenuOpener
    {
        public string Category { get; set; }
        public string CustomCategory { get; set; }
        public List<string> Path { get; set; } = new List<string>();

        public MenuOpener Clone() => new MenuOpener
        {
            Category = Category,
            CustomCategory = CustomCategory,
            Path = new List<string>(Path ?? new List<string>())
        };

        public override string ToString() =>
            (string.IsNullOrEmpty(CustomCategory) ? Category : CustomCategory) + " > " + string.Join(" > ", Path ?? new List<string>());
    }

    internal sealed class IntRect
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        [JsonIgnore] public int Width => Right - Left;
        [JsonIgnore] public int Height => Bottom - Top;
        [JsonIgnore] public bool IsUsable => Width > 0 && Height > 0 && Left > -30000 && Top > -30000;

        public IntRect() { }

        public IntRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public bool SameAs(IntRect other) =>
            other != null && Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    internal sealed class FloatRect
    {
        public float Left { get; set; }
        public float Top { get; set; }
        public float Right { get; set; }
        public float Bottom { get; set; }
    }
}
