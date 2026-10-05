using System.Text.Json;
using Cast.NET;
using Cast.NET.Nodes;

namespace AlchemyStars.Avalonia;

/// <summary>
/// CoD magazine CASTs ship with empty j_ammo_* slots; the rounds are a separate CAST (ammo_unspent_9p).
/// Before QC generation every part with empty ammunition bones is filled through the ModelMerger engine
/// (fill_ammunition). Filled copies keep the original file names, so tokens and bodygroups do not change.
/// </summary>
internal static class QcAmmoFiller
{
    /// <summary>
    /// Finds the round for a magazine by calibre: mag_30_9p → ammo_unspent_9p, looked up next to the part and in the
    /// saluki models folder (models/&lt;stem&gt;/&lt;stem&gt;_LOD0.cast). The longest calibre token wins (9p over a variant miss).
    /// </summary>
    public static string? Guess(IEnumerable<string> parts)
    {
        foreach (var part in parts.Where(part => Path.GetFileName(part).Contains("_mag", StringComparison.OrdinalIgnoreCase)))
        {
            var padded = "_" + Path.GetFileNameWithoutExtension(part).ToLowerInvariant() + "_";
            var folder = Path.GetDirectoryName(Path.GetFullPath(part))!;
            var roots = new[] { folder, Path.GetDirectoryName(folder) }.Where(root => root is not null && Directory.Exists(root)).Cast<string>();
            var match = roots
                .SelectMany(root => Directory.EnumerateFiles(root, "ammo_unspent_*_LOD0.cast")
                    .Concat(Directory.EnumerateDirectories(root, "ammo_unspent_*").Select(dir => Path.Combine(dir, Path.GetFileName(dir) + "_LOD0.cast"))))
                .Where(File.Exists)
                .Select(file => (File: file, Calibre: Path.GetFileNameWithoutExtension(file)["ammo_unspent_".Length..^"_LOD0".Length].ToLowerInvariant()))
                .Where(item => item.Calibre.Length > 0 && padded.Contains("_" + item.Calibre + "_", StringComparison.Ordinal))
                .OrderByDescending(item => item.Calibre.Length)
                .FirstOrDefault();
            if (match.File is not null) return match.File;
        }
        return null;
    }

    /// <summary>Viewmodel magazine CASTs of a saluki blueprint export (vm_..._mag_30_9mm_2516_vm_..._LOD0.cast).</summary>
    public static IReadOnlyList<ModelPartSpec> BlueprintMagazines(string folder) => !Directory.Exists(folder) ? []
        : [.. Directory.GetFiles(folder, "vm_*_mag*_LOD0.cast", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => new ModelPartSpec(file, ModelPartKind.Attachment))];

    /// <summary>Fills the blueprint magazines like the weapon's own: original CAST → filled copy, only for changed ones.</summary>
    public static async Task<(Dictionary<string, string> Filled, IReadOnlyList<string> Log)> FillBlueprintsAsync(
        ModelMergerService service, string folder, string ammo, string staging)
    {
        var magazines = BlueprintMagazines(folder);
        if (magazines.Count == 0) return ([], []);
        var (filled, log) = await FillAsync(service, magazines, ammo, staging);
        return (magazines.Zip(filled).Where(pair => pair.First.FilePath != pair.Second.FilePath)
            .ToDictionary(pair => pair.First.FilePath, pair => pair.Second.FilePath, StringComparer.OrdinalIgnoreCase), log);
    }

    public static bool IsAmmo(string path) => Path.GetFileName(path).StartsWith("ammo_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fills the empty slots of every magazine (j_mag1 and the j_mag2 reload copy). Loose slots outside a magazine
    /// (j_ammo_01 on tag_mag_attach, the chambered round) are left empty; the magazine merger tool can add them.
    /// </summary>
    public static async Task<(IReadOnlyList<ModelPartSpec> Parts, IReadOnlyList<string> Log)> FillAsync(
        ModelMergerService service, IReadOnlyList<ModelPartSpec> parts, string ammo, string staging, CancellationToken token = default)
    {
        Directory.CreateDirectory(staging);
        var round = Normalize(ammo, Path.Combine(staging, "ammo.cast"));
        var log = new List<string>();
        var result = new List<ModelPartSpec>();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Kind == ModelPartKind.ViewHands) { result.Add(part); continue; }
            var name = Path.GetFileName(part.FilePath);
            try
            {
                var analysis = await service.RunAsync(ModelMergerService.Json(w =>
                {
                    w.WriteString("command", "inspect_ammunition"); w.WriteString("file_path", part.FilePath);
                }), null, null, token);
                var magazines = analysis.GetProperty("magazines").EnumerateArray()
                    .Where(magazine => magazine.GetProperty("slots").GetArrayLength() > magazine.GetProperty("occupied").GetArrayLength())
                    .Select(magazine => magazine.GetProperty("name").GetString()!).ToArray();
                if (magazines.Length == 0) { result.Add(part); continue; }
                var output = Path.Combine(staging, index.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var filled = await service.RunAsync(ModelMergerService.Json(w =>
                {
                    w.WriteString("command", "fill_ammunition"); w.WriteString("weapon", part.FilePath);
                    w.WriteString("ammunition", round); w.WriteString("output", output);
                    ModelMergerService.Strings(w, "magazines", magazines);
                    w.WriteStartArray("replicas"); w.WriteEndArray();
                }), null, null, token, output);
                log.Add($"ok  {name}: +{filled.GetProperty("inserted")} ({string.Join(", ", magazines)})");
                result.Add(part with { FilePath = output });
            }
            catch (InvalidOperationException ex)
            {
                // e.g. every slot already holds a round: keep the original part.
                log.Add($"--  {name}: {ex.Message}");
                result.Add(part);
            }
        }
        return (result, log);
    }

    /// <summary>
    /// Saluki exports the round's single bone with a hashed name (bone_788fd52a); the engine requires tag_ammo.
    /// The copy lives in the staging folder, so texture paths are made absolute against the original CAST.
    /// </summary>
    private static IEnumerable<CastNode> DescendantsAndSelf(CastNode node) => node.Children.SelectMany(DescendantsAndSelf).Prepend(node);

    private static string Normalize(string ammo, string destination)
    {
        var cast = CastReader.Load(ammo);
        var folder = Path.GetDirectoryName(Path.GetFullPath(ammo))!;
        var nodes = cast.RootNodes.SelectMany(DescendantsAndSelf).ToArray();
        var bones = nodes.OfType<BoneNode>().ToArray();
        if (bones.Length == 1 && bones[0].Name != "tag_ammo") bones[0].AddString("n", "tag_ammo");
        // Material "m\att_ammo_9p" would become an SMD material subpath; keep the last segment.
        foreach (var material in nodes.OfType<MaterialNode>().Where(material => material.Name.Contains('\\') || material.Name.Contains('/')))
            material.AddString("n", material.Name.Split('\\', '/')[^1]);
        foreach (var file in nodes.OfType<FileNode>().Where(file => file.Path.Length > 0 && !Path.IsPathRooted(file.Path)))
            file.AddString("p", Path.GetFullPath(Path.Combine(folder, file.Path.Replace('\\', Path.DirectorySeparatorChar))));
        CastWriter.Save(destination, cast);
        return destination;
    }
}
