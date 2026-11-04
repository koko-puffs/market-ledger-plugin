using System;
using System.Collections.Generic;

namespace MarketLedger.UI
{
    /// <summary>The Lucide icons the window uses (the site's own icon set).</summary>
    public enum Icon
    {
        Play,
        Pause,
        Square,
        Eye,
        List,
        Sliders,
        X,
        ChevronRight,
        ChevronDown,
        MapPin,
        Search,
        Clipboard,
        Droplet,
        Package,
        Move,
        House,
        Upload,
        Info,
        Alert,
        Refresh,
        Check,
        ArrowLeft,
        Shelf,
        Minus,
        Plus,
        Backpack,
        Bell,
        Store,
        Scale,
        Hammer,
        Flask,
        Brick,
        Sofa,
        Undo,
        Send,
        Save,
        File,
        Link,
        AlertTriangle,
        Camera,
        Heart,
        Bookmark,
        Stamp,
        ExternalLink,
        Inbox,
        ChevronLeft,
        Swords,
        Trash,
        Folder,
        Users,
        Clock,
        FileDown,
        Map,
        Sparkles,
        Lock,
        LockOpen,
        Coins,
        SkipForward,
        ShoppingCart,
        Pickaxe,
        Leaf,
        Plug,
        ArrowDownToLine,
        ArrowUpFromLine,
        Wallet,
        MessageSquare,
    }
}

namespace MarketLedger.UI.Model
{
    /// <summary>
    /// Each icon's Lucide path data (circles and rects written as paths), game-free so a test parses
    /// every one: a path <see cref="SvgPath"/> can't read would throw the first time its icon is drawn.
    /// Lucide (https://lucide.dev) is under the ISC License: copyright (c) for portions of Lucide are
    /// held by Cole Bemis 2013-2022 as part of Feather (MIT); all other copyright (c) for Lucide are held
    /// by Lucide Contributors 2022.
    /// </summary>
    public static class IconPaths
    {
        /// <summary>The most points one stroke of an icon may have (the draw buffer's size).</summary>
        public const int MaxPoints = 128;

        /// <summary>Each icon's SVG elements as path data (circles, rects and lines written as paths).</summary>
        public static readonly IReadOnlyDictionary<Icon, (string[] Paths, bool Filled)> Source = new Dictionary<Icon, (string[] Paths, bool Filled)> 
        {
            [Icon.Play] = (new[] { "M7 4 20 12 7 20 7 4Z" }, true),
            [Icon.Pause] = (new[] { Rect(14, 4, 4, 16, 1), Rect(6, 4, 4, 16, 1) }, false),
            [Icon.Square] = (new[] { Rect(5, 5, 14, 14, 2) }, false),
            [Icon.Eye] = (new[] { "M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7Z", Circle(12, 12, 3) }, false),
            [Icon.List] = (new[] { "M8 6H21", "M8 12H21", "M8 18H21", "M3 6H3.01", "M3 12H3.01", "M3 18H3.01" }, false),
            [Icon.Sliders] = (new[]
            {
                "M21 4H14", "M10 4H3", "M21 12H12", "M8 12H3", "M21 20H16", "M12 20H3", "M14 2V6", "M8 10V14", "M16 18V22",
            }, false),
            [Icon.X] = (new[] { "M18 6 6 18", "m6 6 12 12" }, false),
            [Icon.ChevronRight] = (new[] { "m9 18 6-6-6-6" }, false),
            [Icon.ChevronDown] = (new[] { "m6 9 6 6 6-6" }, false),
            [Icon.MapPin] = (new[] { "M20 10c0 4.993-5.539 10.193-7.399 11.799a1 1 0 0 1-1.202 0C9.539 20.193 4 14.993 4 10a8 8 0 0 1 16 0", Circle(12, 10, 3) }, false),
            [Icon.Search] = (new[] { Circle(11, 11, 8), "m21 21-4.3-4.3" }, false),
            [Icon.Clipboard] = (new[] { Rect(8, 2, 8, 4, 1), "M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2" }, false),
            [Icon.Droplet] = (new[] { "M12 22a7 7 0 0 0 7-7c0-2-1-3.9-3-5.5s-3.5-4-4-6.5c-.5 2.5-2 4.9-4 6.5C6 11.1 5 13 5 15a7 7 0 0 0 7 7z" }, false),
            [Icon.Package] = (new[]
            {
                "m7.5 4.27 9 5.15",
                "M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z",
                "m3.3 7 8.7 5 8.7-5",
                "M12 22V12",
            }, false),
            [Icon.Move] = (new[] { "M5 9l-3 3 3 3", "M9 5l3-3 3 3", "M15 19l-3 3-3-3", "M19 9l3 3-3 3", "M2 12h20", "M12 2v20" }, false),
            [Icon.House] = (new[]
            {
                "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8",
                "M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
            }, false),
            [Icon.Upload] = (new[] { "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "M17 8 12 3 7 8", "M12 3V15" }, false),
            [Icon.Info] = (new[] { Circle(12, 12, 10), "M12 16v-4", "M12 8h.01" }, false),
            [Icon.Alert] = (new[] { Circle(12, 12, 10), "M12 8V12", "M12 16H12.01" }, false),
            [Icon.Refresh] = (new[] { "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8", "M3 3v5h5" }, false),
            [Icon.Check] = (new[] { "M20 6 9 17l-5-5" }, false),
            [Icon.ArrowLeft] = (new[] { "m12 19-7-7 7-7", "M19 12H5" }, false),
            [Icon.Shelf] = (new[] { Rect(4, 3, 16, 18, 1), "M4 9h16", "M4 15h16" }, false),
            [Icon.Minus] = (new[] { "M5 12h14" }, false),
            [Icon.Plus] = (new[] { "M5 12h14", "M12 5v14" }, false),
            [Icon.Backpack] = (new[]
            {
                "M4 10a4 4 0 0 1 4-4h8a4 4 0 0 1 4 4v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2Z",
                "M8 10h8", "M8 18h8", "M8 22v-6a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v6", "M9 6V4a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2",
            }, false),
            // The 0.6 flow mock-up's (docs/mockups/flow.html): the Get stage's sources, Finish's fixtures, the journey.
            [Icon.Bell] = (new[] { "M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9", "M10.3 21a1.9 1.9 0 0 0 3.4 0" }, false),
            [Icon.Store] = (new[] { "M3 9 4.5 4h15L21 9", "M3 9h18v2a3 3 0 0 1-6 0 3 3 0 0 1-6 0 3 3 0 0 1-6 0z", "M5 13v7h14v-7", "M10 20v-5h4v5" }, false),
            [Icon.Scale] = (new[] { "M12 3v18", "M5 21h14", "M3 7h18", "M6 7l-3 7a3 3 0 0 0 6 0z", "M18 7l-3 7a3 3 0 0 0 6 0z" }, false),
            [Icon.Hammer] = (new[] { "m15 12-8.5 8.5a2.1 2.1 0 0 1-3-3L12 9", "M17.6 15 22 10.6", "M20.9 11.7 12.3 3.1a2 2 0 0 0-2.8 0L7 5.6l7.4 7.4" }, false),
            [Icon.Flask] = (new[] { "M9 3h6", "M10 3v6L4.5 19a1.5 1.5 0 0 0 1.3 2h12.4a1.5 1.5 0 0 0 1.3-2L14 9V3", "M7 15h10" }, false),
            [Icon.Brick] = (new[] { "M3 3h18v18H3z", "M3 9h18", "M3 15h18", "M9 3v6", "M15 9v6", "M9 15v6" }, false),
            [Icon.Sofa] = (new[] { "M20 9V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v3", "M2 16a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5a2 2 0 0 0-4 0v1.5H6V11a2 2 0 0 0-4 0z", "M4 18v2", "M20 18v2" }, false),
            [Icon.Undo] = (new[] { "M9 14 4 9l5-5", "M4 9h10.5a5.5 5.5 0 0 1 0 11H11" }, false),
            [Icon.Send] = (new[] { "m22 2-7 20-4-9-9-4z", "M22 2 11 13" }, false),
            [Icon.Save] = (new[] { "M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z", "M17 21v-8H7v8", "M7 3v5h8" }, false),
            [Icon.File] = (new[] { "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z", "M14 2v6h6" }, false),
            [Icon.Link] = (new[] { "M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7", "M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7" }, false),
            [Icon.AlertTriangle] = (new[] { "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z", "M12 9v4", "M12 17h.01" }, false),
            // 0.7's site link: Take a shot, the visitor panel's like, save and stamp, Open on the site, From the site.
            [Icon.Camera] = (new[] { "M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3l-2.5-3z", Circle(12, 13, 3) }, false),
            [Icon.Heart] = (new[] { "M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z" }, false),
            [Icon.Bookmark] = (new[] { "m19 21-7-4-7 4V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v16z" }, false),
            [Icon.Stamp] = (new[]
            {
                "M5 22h14",
                "M19.27 13.73A2.5 2.5 0 0 0 17.5 13h-11A2.5 2.5 0 0 0 4 15.5V17a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-1.5c0-.66-.26-1.3-.73-1.77Z",
                "M14 13V8.5C14 7 15 7 15 5a3 3 0 0 0-6 0c0 2 1 2 1 3.5V13",
            }, false),
            [Icon.ExternalLink] = (new[] { "M15 3h6v6", "M10 14 21 3", "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" }, false),
            [Icon.ChevronLeft] = (new[] { "m15 18-6-6 6-6" }, false),
            // The Raid Planner's swords, kept for the kit's own sake.
            [Icon.Swords] = (new[]
            {
                "m13 19 6-6", "M14.5 17.5 3.586 6.586A2 2 0 0 1 3 5.172V3h2.172a2 2 0 0 1 1.414.586L17.5 14.5",
                "m14.828 6.172 2.586-2.586A2 2 0 0 1 18.828 3H21v2.172a2 2 0 0 1-.586 1.414l-2.586 2.586",
                "m16 16 4 4", "m19 21 2-2", "m5 14 4 4", "m5 21-2-2", "M7.5 16.5 4 20",
            }, false),
            [Icon.Trash] = (new[] { "M3 6h18", "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6", "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2", "M10 11v6", "M14 11v6" }, false),
            [Icon.Folder] = (new[] { "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z" }, false),
            [Icon.Users] = (new[] { "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2", Circle(9, 7, 4), "M22 21v-2a4 4 0 0 0-3-3.87", "M16 3.13a4 4 0 0 1 0 7.75" }, false),
            [Icon.Clock] = (new[] { Circle(12, 12, 10), "M12 6v6l4 2" }, false),
            [Icon.FileDown] = (new[] { "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z", "M14 2v6h6", "M12 18v-6", "m9 15 3 3 3-3" }, false),
            [Icon.Map] = (new[] { "M14.106 5.553a2 2 0 0 0 1.788 0l3.659-1.83A1 1 0 0 1 21 4.619v12.764a1 1 0 0 1-.553.894l-4.553 2.277a2 2 0 0 1-1.788 0l-4.212-2.106a2 2 0 0 0-1.788 0l-3.659 1.83A1 1 0 0 1 3 19.381V6.618a1 1 0 0 1 .553-.894l4.553-2.277a2 2 0 0 1 1.788 0z", "M15 5.764v15", "M9 3.236v15" }, false),
            [Icon.Sparkles] = (new[] { "M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z", "M20 3v4", "M22 5h-4" }, false),
            [Icon.Lock] = (new[] { Rect(3, 11, 18, 11, 2), "M7 11V7a5 5 0 0 1 10 0v4" }, false),
            [Icon.LockOpen] = (new[] { Rect(3, 11, 18, 11, 2), "M7 11V7a5 5 0 0 1 9.9-1" }, false),
            // Market Ledger's: its coins (the favicon), the steps' kinds, the helpers, the run's buttons.
            [Icon.Coins] = (new[] { Circle(8, 8, 6), "M18.09 10.37A6 6 0 1 1 10.34 18", "M7 6h1v4", "m16.71 13.88.7.71-2.82 2.82" }, false),
            [Icon.SkipForward] = (new[] { "M5 4 15 12 5 20Z", "M19 5v14" }, false),
            [Icon.ShoppingCart] = (new[] { Circle(8, 21, 1), Circle(19, 21, 1), "M2.05 2.05h2l2.66 12.42a2 2 0 0 0 2 1.58h9.78a2 2 0 0 0 1.95-1.57l1.65-7.43H5.12" }, false),
            [Icon.Pickaxe] = (new[]
            {
                "M14.531 12.469 6.619 20.38a1 1 0 1 1-3-3l7.912-7.912",
                "M15.686 4.314A12.5 12.5 0 0 0 5.461 2.958 1 1 0 0 0 5.58 4.71a22 22 0 0 1 6.318 3.393",
                "M17.7 3.7a1 1 0 0 0-1.4 0l-4.6 4.6a1 1 0 0 0 0 1.4l2.6 2.6a1 1 0 0 0 1.4 0l4.6-4.6a1 1 0 0 0 0-1.4z",
                "M19.686 8.314a12.501 12.501 0 0 1 1.356 10.225 1 1 0 0 1-1.751-.119 22 22 0 0 0-3.393-6.319",
            }, false),
            [Icon.Leaf] = (new[] { "M11 20A7 7 0 0 1 9.8 6.1C15.5 5 17 4.48 19 2c1 2 2 4.18 2 8 0 5.5-4.78 10-10 10Z", "M2 21c0-3 1.85-5.36 5.08-6C9.5 14.52 12 13 13 12" }, false),
            [Icon.Plug] = (new[] { "M12 22v-5", "M9 8V2", "M15 8V2", "M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z" }, false),
            [Icon.ArrowDownToLine] = (new[] { "M12 17V3", "m6 11 6 6 6-6", "M19 21H5" }, false),
            [Icon.ArrowUpFromLine] = (new[] { "m18 9-6-6-6 6", "M12 3v14", "M5 21h14" }, false),
            [Icon.Wallet] = (new[]
            {
                "M19 7V4a1 1 0 0 0-1-1H5a2 2 0 0 0 0 4h15a1 1 0 0 1 1 1v4h-3a2 2 0 0 0 0 4h3a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1",
                "M3 5v14a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-4",
            }, false),
            [Icon.MessageSquare] = (new[] { "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" }, false),
            [Icon.Inbox] = (new[] { "M22 12h-6l-2 3h-4l-2-3H2", "M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z" }, false),
        };

        public static string Circle(float cx, float cy, float r) => FormattableString.Invariant($"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0Z");

        public static string Rect(float x, float y, float w, float h, float rx) => FormattableString.Invariant(
            $"M{x + rx} {y}h{w - 2 * rx}a{rx} {rx} 0 0 1 {rx} {rx}v{h - 2 * rx}a{rx} {rx} 0 0 1 {-rx} {rx}h{-(w - 2 * rx)}a{rx} {rx} 0 0 1 {-rx} {-rx}v{-(h - 2 * rx)}a{rx} {rx} 0 0 1 {rx} {-rx}Z");
    }
}
