namespace AlchemyStars.Engine;

public static partial class QcProjectGenerator
{
    /// <summary>
    /// The CoD attachment pack's sights / muzzles / grips (category, class, mount bone from each part's "-- mount:" first
    /// line) in any addon next to the weapon output, read from the selected base's Lua directory.
    /// Empty when no matching pack has been generated beside it.
    /// </summary>
    private static List<(string Category, string Class, string Mount)> AttachmentPackParts(string output, QcTarget target)
    {
        var addons = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)));
        if (addons is null || !Directory.Exists(addons)) return [];
        return Directory.GetDirectories(addons)
            .Select(addon => target == QcTarget.MwBase
                ? Path.Combine(addon, "lua", "weapons", "mg_base", "modules", "attachmentss", "cod")
                : Path.Combine(addon, "lua", "arc9", "common", "attachments"))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "att_cod_*.lua", SearchOption.AllDirectories))
            .Select(file => (Category: Path.GetFileName(Path.GetDirectoryName(file)!), Class: Path.GetFileNameWithoutExtension(file),
                Mount: (File.ReadLines(file).FirstOrDefault() ?? "").Replace("-- mount:", "").Trim()))
            .Select(part => (Category: target == QcTarget.MwBase ? part.Category : AttachmentCategory(part.Mount) ?? "", part.Class, part.Mount))
            .Where(part => part.Category is "sights" or "muzzles" or "grips" && part.Mount.Length > 0)
            .DistinctBy(part => part.Class).OrderBy(part => part.Class, StringComparer.Ordinal).ToList();
    }

    /// <summary>Earlier packs added themselves to weapons through MW Base injectors; weapons now list the parts themselves.</summary>
    private static void RemoveOldInjectors(string pack)
    {
        var modules = Path.Combine(pack, "lua", "weapons", "mg_base", "modules");
        var children = Path.Combine(modules, "attachmentss", "cod", "injected");
        if (Directory.Exists(children)) Directory.Delete(children, recursive: true);
        var injectors = Path.Combine(modules, "injectors");
        if (Directory.Exists(injectors)) foreach (var stale in Directory.GetFiles(injectors, "cod_*.lua")) File.Delete(stale);
    }
}
