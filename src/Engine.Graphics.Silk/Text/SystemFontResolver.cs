namespace Engine.Graphics;

/// <summary>Locates an ordered set of operating-system UI and fallback font files.</summary>
internal static class SystemFontResolver
{
    /// <summary>Returns existing system font files in preferred fallback order.</summary>
    /// <returns>System font files and collection face indices.</returns>
    internal static SystemFontSource[] Resolve()
    {
        var sources = new List<SystemFontSource>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
            AddWindowsFonts(sources, paths);
        else if (OperatingSystem.IsMacOS())
            AddMacFonts(sources, paths);
        else
            AddLinuxFonts(sources, paths);
        return sources.ToArray();
    }

    /// <summary>Adds the Windows UI, symbols, emoji, and international fallback faces.</summary>
    /// <param name="sources">Destination font sources.</param>
    /// <param name="paths">Canonical paths already added.</param>
    private static void AddWindowsFonts(List<SystemFontSource> sources, HashSet<string> paths)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            return;
        var fonts = Path.Combine(windows, "Fonts");
        Add(sources, paths, Path.Combine(fonts, "segoeui.ttf"));
        Add(sources, paths, Path.Combine(fonts, "seguiemj.ttf"));
        Add(sources, paths, Path.Combine(fonts, "seguisym.ttf"));
        AddFirstExisting(sources, paths, fonts,
            "msyh.ttc", "Deng.ttf", "simhei.ttf", "simsun.ttc");
        Add(sources, paths, Path.Combine(fonts, "Nirmala.ttf"));
        Add(sources, paths, Path.Combine(fonts, "Nirmala.ttc"));
        Add(sources, paths, Path.Combine(fonts, "arial.ttf"));
    }

    /// <summary>Adds the macOS UI, Unicode, symbols, and emoji fallback faces.</summary>
    /// <param name="sources">Destination font sources.</param>
    /// <param name="paths">Canonical paths already added.</param>
    private static void AddMacFonts(List<SystemFontSource> sources, HashSet<string> paths)
    {
        Add(sources, paths, "/System/Library/Fonts/SFNS.ttf");
        Add(sources, paths, "/System/Library/Fonts/SFNSRounded.ttf");
        Add(sources, paths, "/System/Library/Fonts/PingFang.ttc");
        Add(sources, paths, "/System/Library/Fonts/Apple Symbols.ttf");
        Add(sources, paths, "/System/Library/Fonts/Apple Color Emoji.ttc");
        Add(sources, paths, "/System/Library/Fonts/Supplemental/Arial Unicode.ttf");
        Add(sources, paths, "/System/Library/Fonts/Supplemental/Arial.ttf");
    }

    /// <summary>Adds common Fontconfig-installed UI and international fallback faces.</summary>
    /// <param name="sources">Destination font sources.</param>
    /// <param name="paths">Canonical paths already added.</param>
    private static void AddLinuxFonts(List<SystemFontSource> sources, HashSet<string> paths)
    {
        var roots = new List<string>
        {
            "/usr/share/fonts",
            "/usr/local/share/fonts"
        };
        var userFonts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "fonts");
        if (!string.IsNullOrWhiteSpace(userFonts))
            roots.Add(userFonts);
        var names = new[]
        {
            "NotoSans-Regular.ttf",
            "DejaVuSans.ttf",
            "LiberationSans-Regular.ttf",
            "NotoSansArabic-Regular.ttf",
            "NotoSansHebrew-Regular.ttf",
            "NotoSansCJK-Regular.ttc",
            "NotoColorEmoji.ttf",
            "Symbola.ttf"
        };
        for (var nameIndex = 0; nameIndex < names.Length; nameIndex++)
        {
            for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                var root = roots[rootIndex];
                if (!Directory.Exists(root))
                    continue;
                try
                {
                    var matches = Directory.GetFiles(root, names[nameIndex], SearchOption.AllDirectories);
                    Array.Sort(matches, StringComparer.Ordinal);
                    for (var matchIndex = 0; matchIndex < matches.Length; matchIndex++)
                        Add(sources, paths, matches[matchIndex]);
                }
                catch (UnauthorizedAccessException)
                {
                    // Font directories can contain package-managed subtrees unavailable to the process.
                }
                catch (IOException)
                {
                    // A concurrently changed font cache must not prevent renderer startup.
                }
            }
        }
    }

    /// <summary>Adds one existing, previously unseen font file.</summary>
    /// <param name="sources">Destination font sources.</param>
    /// <param name="paths">Canonical paths already added.</param>
    /// <param name="path">Candidate font file.</param>
    private static void Add(
        List<SystemFontSource> sources,
        HashSet<string> paths,
        string path)
    {
        if (!File.Exists(path))
            return;
        var fullPath = Path.GetFullPath(path);
        if (paths.Add(fullPath))
            sources.Add(new SystemFontSource(fullPath, 0));
    }

    /// <summary>Adds the first installed font from an ordered platform fallback family.</summary>
    /// <param name="sources">Destination font sources.</param>
    /// <param name="paths">Canonical paths already added.</param>
    /// <param name="directory">System font directory.</param>
    /// <param name="fileNames">Preferred font file names.</param>
    private static void AddFirstExisting(
        List<SystemFontSource> sources,
        HashSet<string> paths,
        string directory,
        params string[] fileNames)
    {
        for (var index = 0; index < fileNames.Length; index++)
        {
            var path = Path.Combine(directory, fileNames[index]);
            if (!File.Exists(path))
                continue;
            Add(sources, paths, path);
            return;
        }
    }
}

/// <summary>Identifies one face in a system font file or collection.</summary>
/// <param name="Path">Absolute system font path.</param>
/// <param name="FaceIndex">Zero-based collection face index.</param>
internal readonly record struct SystemFontSource(string Path, int FaceIndex);
