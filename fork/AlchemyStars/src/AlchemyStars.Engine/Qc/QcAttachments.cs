using System.Text.RegularExpressions;

namespace AlchemyStars.Engine;

/// <summary>
/// Attachment slot rules derived from CoD part names only (no guessed gameplay numbers):
/// default part per slot, magazine capacity, the smag/xmag animation variant and same-weapon part discovery.
/// </summary>
public static partial class QcAttachments
{
    /// <summary>mag_20_9p → 20; null when the token carries no capacity.</summary>
    public static int? Capacity(string token) =>
        MagCapacity().Match(token) is { Success: true } match ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>Slot name shared by a group's tokens: bar / bar_long / barsil → bar.</summary>
    public static string SlotName(IEnumerable<string> tokens) => tokens.MinBy(token => token.Length)!.Split('_')[0];

    /// <summary>
    /// Puts the default part first. The default is the token equal to the slot name (bar, stock); for magazines
    /// without one, the median capacity (20/30/50 → 30). Otherwise nothing is the default (CoD grip tapes):
    /// the parts keep their order and <paramref name="emptyDefault"/> is set, so the slot starts empty.
    /// </summary>
    public static T[] DefaultFirst<T>(T[] parts, Func<T, string> token, out bool emptyDefault)
    {
        emptyDefault = false;
        if (parts.Length == 1) return parts; // a lone part is what the user wants on the gun
        var name = SlotName(parts.Select(token));
        if (Array.FindIndex(parts, part => token(part) == name) is var exact and >= 0)
            return [parts[exact], .. parts.Where((_, index) => index != exact)];
        if (parts.All(part => Capacity(token(part)) is not null))
        {
            var byCapacity = parts.OrderBy(part => Capacity(token(part))).ToArray();
            var median = (byCapacity.Length - 1) / 2;
            return [byCapacity[median], .. byCapacity.Where((_, index) => index != median)];
        }
        emptyDefault = true;
        return parts;
    }

    /// <summary>CoD reload/inspect variant for a magazine relative to the default one: smaller → smag, larger → xmag.</summary>
    public static string? MagVariant(int? capacity, int? defaultCapacity) =>
        capacity is { } rounds && defaultCapacity is { } standard && rounds != standard ? (rounds < standard ? "smag" : "xmag") : null;

    /// <summary>The variant a sequence or foley alias belongs to: reload_empty_smag / wfoly_..._inspect_xmag_03 → xmag; "" for the default.</summary>
    public static string VariantOf(string name) => Variant().Match(name) is { Success: true } match ? match.Groups[1].Value : "";

    /// <summary>
    /// Other parts of the receiver's weapon next to it: vm_jup_jp03_sm_pmike_rec_000_LOD0.cast → every
    /// vm_jup_jp03_sm_pmike_*_LOD0.cast in sibling folders (Saluki/Greyhound export one folder per part) or the same folder.
    /// </summary>
    public static IReadOnlyList<string> Siblings(string weaponFile)
    {
        var stem = LodSuffix().Replace(Path.GetFileNameWithoutExtension(weaponFile), "");
        var family = stem.LastIndexOf('_', Math.Max(0, PartIndex().Replace(stem, "").Length - 1)) is var cut and > 0
            ? stem[..(cut + 1)] : "";
        var folder = Path.GetDirectoryName(Path.GetFullPath(weaponFile));
        // A family must still name the weapon (vm_jup_jp03_sm_pmike_), not just a prefix every export shares.
        if (family.Count(character => character == '_') < 3 || folder is null) return [];
        // Same folder, plus one folder per part next to it (models/<part>/<part>_LOD0.cast); never a recursive drive scan.
        var folders = Path.GetDirectoryName(folder) is { } parent ? Directory.EnumerateDirectories(parent).Prepend(folder) : [folder];
        return folders.SelectMany(path => Directory.EnumerateFiles(path, family + "*_LOD0.cast"))
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(weaponFile), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [GeneratedRegex(@"^mag_(\d+)(?:_|$)")] private static partial Regex MagCapacity();
    [GeneratedRegex(@"_(smag|xmag)(?:_|$)")] private static partial Regex Variant();
    [GeneratedRegex("_LOD\\d+$", RegexOptions.IgnoreCase)] private static partial Regex LodSuffix();
    [GeneratedRegex("_\\d+$")] private static partial Regex PartIndex();
}
