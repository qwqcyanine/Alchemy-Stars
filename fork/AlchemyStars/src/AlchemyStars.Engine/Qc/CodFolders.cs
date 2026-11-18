namespace AlchemyStars.Engine;

/// <summary>
/// Folder lookups that do not assume one exporter layout. Every folder the user picks is searched with its subfolders,
/// so saluki's images/{icon_attachment,reticle_composed,...} work as exported, merged into one folder, or moved elsewhere.
/// </summary>
internal static class CodFolders
{
    /// <summary>Asset prefixes of the IW/Treyarch titles (jup = MW3 2023, iw9 = MW2022, iw8 = MW2019, t9 = BOCW, t10 = BO6, s4 = Vanguard).</summary>
    public static readonly string[] GamePrefixes = ["jup", "iw9", "iw8", "t10", "t9", "s4"];

    private static readonly Dictionary<string, Dictionary<string, string>> Indexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>First file named one of <paramref name="stems"/> + <paramref name="extension"/> anywhere under the folders, or null.</summary>
    public static string? Find(IEnumerable<string> folders, IEnumerable<string> stems, string extension)
    {
        var indexes = folders.Select(f => Index(f, extension)).ToArray();
        foreach (var stem in stems)
            foreach (var index in indexes)
                if (index.TryGetValue(stem + extension, out var path)) return path;
        return null;
    }

    /// <summary>Strips a known game prefix (jup_reflex02 -> reflex02); the name itself when it has none.</summary>
    public static string WithoutGame(string name) =>
        GamePrefixes.FirstOrDefault(p => name.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase)) is { } p ? name[(p.Length + 1)..] : name;

    /// <summary>Game prefix of an asset name (jup_jp03_sm_pmike_mp -> jup), or null.</summary>
    public static string? Game(string name) =>
        GamePrefixes.FirstOrDefault(p => name.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder itself plus each existing sibling or child named <paramref name="names"/> (sounds -> ../sndbanks).</summary>
    public static IEnumerable<string> WithNeighbours(string folder, params string[] names)
    {
        folder = Path.TrimEndingDirectorySeparator(folder.Trim());
        if (folder.Length == 0) yield break;
        if (Directory.Exists(folder)) yield return folder;
        var parent = Path.GetDirectoryName(folder);
        foreach (var name in names)
        {
            var child = Path.Combine(folder, name);
            if (Directory.Exists(child)) yield return child;
            if (parent is not null && Path.Combine(parent, name) is var sibling && Directory.Exists(sibling)) yield return sibling;
        }
    }

    /// <summary>Drops the file indexes; called at the start of each generation so files moved since the last run are seen.</summary>
    public static void Reset()
    {
        lock (Indexes) Indexes.Clear();
    }

    private static Dictionary<string, string> Index(string folder, string extension)
    {
        var key = folder + "|" + extension;
        lock (Indexes)
        {
            if (Indexes.TryGetValue(key, out var known)) return known;
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories))
                    index.TryAdd(Path.GetFileName(file), file);
            return Indexes[key] = index;
        }
    }
}
