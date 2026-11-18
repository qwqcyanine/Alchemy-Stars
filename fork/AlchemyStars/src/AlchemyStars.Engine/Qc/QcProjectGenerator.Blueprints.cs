using System.Text;
using System.Text.RegularExpressions;

namespace AlchemyStars.Engine;

public static partial class QcProjectGenerator
{
    /// <summary>
    /// A CoD blueprint part export: saluki names the folder/CAST "&lt;model&gt;_&lt;model it derives from&gt;"
    /// (vm_jup_jp03_sm_pmike_bar_long_2516_vm_jup_jp03_sm_pmike_bar_long_000); new meshes carry only their own name.
    /// </summary>
    private sealed record BlueprintCast(string File, string Model, string Base);
    private sealed record BlueprintEntry(int Slot, string Folder, string Class, BlueprintCast Cast, string Lua);

    private static List<BlueprintCast> BlueprintCasts(string folder, IReadOnlyDictionary<string, string>? filled) =>
        Directory.GetFiles(folder.Trim(), "*.cast", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => (File: file, Stem: LodSuffix().Replace(Path.GetFileNameWithoutExtension(file), "")))
            // vm_ only (wm_ worldmodels are not used by MW Base attachments); LOD0 sorts first.
            .Where(item => item.Stem.StartsWith("vm_", StringComparison.OrdinalIgnoreCase)).DistinctBy(item => item.Stem, StringComparer.OrdinalIgnoreCase)
            .Select(item => (File: filled?.GetValueOrDefault(item.File) ?? item.File, item.Stem))
            .Select(item => BlueprintStem().Match(item.Stem) is { Success: true } match
                ? new BlueprintCast(item.File, match.Groups[1].Value, match.Groups[2].Value) : new BlueprintCast(item.File, item.Stem, item.Stem))
            .ToList();

    /// <summary>Blueprint addon written beside the weapon output: &lt;output&gt;_blueprints.</summary>
    public static string BlueprintOutput(string output) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)) + "_blueprints";

    /// <summary>
    /// Writes the weapon's CoD blueprint parts as a separate addon like modern_warfare_2019_sweps_assault_rifles_blueprints:
    /// one model + cosmetic ATTACHMENT per part (Base = the part it reskins, so stats, reticle, grip pose and mounts carry
    /// over) and one MW Base injector per customization slot. Weapon parts (barrel, mag, stock, pistol grip) compile like the
    /// weapon's own att_ models; a receiver hides the viewmodel's "receiver" bodygroup; sights / muzzles / grips compile like
    /// the attachment pack and reskin its att_cod_ part. Parts whose base is in no slot (lasers, parts missing from the pack)
    /// are skipped with a note.
    /// </summary>
    private static List<string> WriteBlueprints(QcProjectRequest request, string weaponOutput, string staging, string weaponStem, string receiver,
        IReadOnlyList<(ModelPartSpec Part, string Token)[]> groups, string attachmentDirectory, string common, string materials,
        Dictionary<string, string> clips, string weaponName, Cod20Stats? stats, List<string> notes)
    {
        var blueprints = BlueprintCasts(request.BlueprintDirectory, request.BlueprintFilled);
        var output = BlueprintOutput(weaponOutput);
        foreach (var folder in new[] { "", "anims", "meshes" }) Directory.CreateDirectory(Path.Combine(output, folder));
        var swep = "mg_" + MwBaseLua.Identifier(weaponName);
        var customization = File.ReadAllText(Path.Combine(weaponOutput, "lua", "weapons", swep, "customization.lua"));
        // SWEP.Customization tables hold no nested braces: each {...} is one slot, its 1-based position the injector Slot.
        var slots = CustomizationSlot().Matches(customization[customization.IndexOf("SWEP.Customization", StringComparison.Ordinal)..])
            .Select(slot => QuotedClass().Matches(slot.Value).Select(item => item.Groups[1].Value).ToHashSet(StringComparer.Ordinal)).ToList();
        int? SlotOf(string baseClass) => slots.FindIndex(slot => slot.Contains(baseClass)) is >= 0 and var index ? index + 1 : null;

        // vm_jup_jp03_sm_pmike_rec_000 -> vm_jup_jp03_sm_pmike_: blueprint parts of this gun start with it.
        var prefix = weaponStem[..(weaponStem.LastIndexOf("_" + receiver, StringComparison.OrdinalIgnoreCase) + 1)];
        var weaponTokens = groups.SelectMany(group => group).Select(item => item.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = new List<(ModelPartSpec Part, string Token)>();
        var overrides = new Dictionary<string, (string Class, string Token)>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<BlueprintEntry>();
        foreach (var cast in blueprints)
        {
            var name = MwBaseLua.LuaString(stats?.BlueprintName(cast.Model) ?? cast.Model);
            var header = $"-- Blueprint part {cast.Model}\n";
            // Token() would cut the blueprint number (_2516) as a variant suffix: strip the gun's prefix instead.
            var token = cast.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? cast.Model[prefix.Length..] : cast.Model;
            if (prefix.Length > 0 && cast.Base.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var baseToken = VariantSuffix().Replace(cast.Base[prefix.Length..], "");
                // New meshes (mag_30_9p_v4017) name no base: the weapon part whose token starts theirs.
                if (cast.Base == cast.Model)
                    baseToken = weaponTokens.Where(item => token.StartsWith(item + "_", StringComparison.OrdinalIgnoreCase)).MaxBy(item => item.Length) ?? baseToken;
                var isReceiver = baseToken.Equals(receiver, StringComparison.OrdinalIgnoreCase);
                var baseClass = isReceiver ? "att_receiver" : $"{swep}_{MwBaseLua.Identifier(baseToken)}";
                if (SlotOf(baseClass) is not { } slot) { notes.Add($"blueprint {cast.Model}: {baseClass} is in no slot, skipped"); continue; }
                parts.Add((new ModelPartSpec(cast.File, ModelPartKind.Attachment), token));
                var model = $"Model(\"models/{attachmentDirectory}att_{token}.mdl\")";
                // shortcut: the blueprint receiver keeps its laser rail cover in its main mesh; split it like WriteMeshes if a laser clips it.
                entries.Add(new(slot, isReceiver ? "receivers" : FolderOf(baseClass, weaponOutput, swep), $"{swep}_{MwBaseLua.Identifier(token)}", cast,
                    header + $"ATTACHMENT.Base = \"{baseClass}\"\nATTACHMENT.Name = \"{name}\"\nATTACHMENT.Model = {model}\n"
                    + (isReceiver ? "ATTACHMENT.Bodygroups = {\n    [\"receiver\"] = 1,\n    [\"tag_sight\"] = 1\n}\n" : "")
                    + "ATTACHMENT.CosmeticChange = true\n"));
                continue;
            }
            // Shared CoD part: reskins the pack's att_cod_ class, or the weapon's own copy of it (grips: pose + barrel bonemerge).
            var packClass = AttachmentPackClass(cast.Base);
            var parent = new[] { $"{swep}_{packClass}", packClass }.FirstOrDefault(item => SlotOf(item) is not null);
            if (parent is null) { notes.Add($"blueprint {cast.Model}: {packClass} is in no slot (not in the attachment pack?), skipped"); continue; }
            var variant = "att_cod_bp_" + MwBaseLua.Identifier(ViewModelAttachmentPrefix().Replace(cast.Model, ""));
            overrides[cast.File] = (variant, packClass["att_cod_".Length..]);
            entries.Add(new(SlotOf(parent)!.Value, FolderOf(parent, weaponOutput, swep), variant, cast,
                header + $"ATTACHMENT.Base = \"{parent}\"\nATTACHMENT.Name = \"{name}\"\nATTACHMENT.Model = Model(\"models/{AttachmentPackModels}{variant}.mdl\")\n"
                + "ATTACHMENT.CosmeticChange = true\n"));
        }

        var qcs = WriteAttachmentModels(output, staging, parts.Count == 0 ? [] : [[.. parts]], attachmentDirectory, common, clips);
        if (request.ConvertMaterials && parts.Count > 0)
            notes.AddRange(CodMaterialConverter.Convert(parts.Select(item => item.Part.FilePath), Path.Combine(output, "materials", materials.Replace('/', Path.DirectorySeparatorChar)),
                materials, VmtEnvmapStyle.MwBase, request.MaterialMaxSize).Select(line => "blueprint material: " + line));
        if (overrides.Count > 0)
        {
            var pack = GenerateAttachmentPack(new(overrides.Keys.ToArray(), "", output, request.MaterialMaxSize, Overrides: overrides), modelsOnly: true);
            qcs.AddRange(pack.Qcs);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                // Optics stencil their own lens shape: point the inherited Optic table at the variant's hide model and lens.
                if (pack.Qcs.Any(qc => Path.GetFileNameWithoutExtension(qc) == entry.Class + "_hide"))
                    entries[i] = entry with { Lua = entry.Lua + $"ATTACHMENT.Optic = {{\n    HideModel = Model(\"models/{AttachmentPackModels}{entry.Class}_hide.mdl\"),\n"
                        + $"    LensHideMaterial = Material(\"{AttachmentPackMaterials}/{entry.Class}_lens\")\n}}\n" };
                else if (overrides.Values.Any(item => item.Class == entry.Class) && !pack.Qcs.Any(qc => Path.GetFileNameWithoutExtension(qc) == entry.Class))
                {
                    notes.Add($"blueprint {entry.Class}: the pack could not convert it, skipped");
                    entries[i] = entry with { Slot = 0 };
                }
            }
        }

        var modules = Path.Combine(output, "lua", "weapons", "mg_base", "modules");
        var weaponId = MwBaseLua.Identifier(weaponName);
        // Remove only our old flat injectors, never third-party Lua in the same addon.
        var injectorRoot = Path.Combine(modules, "injectors");
        if (Directory.Exists(injectorRoot))
            foreach (var old in Directory.GetFiles(injectorRoot, weaponId + "_*.lua"))
                if (File.ReadAllText(old).StartsWith($"-- Generated by Alchemy Stars: CoD blueprint parts for {swep}\n", StringComparison.Ordinal)) File.Delete(old);
        var injectors = Path.Combine(injectorRoot, weaponId);
        Directory.CreateDirectory(injectors);
        var valid = entries.Where(item => item.Slot > 0).ToArray();
        foreach (var entry in valid)
        {
            var file = Path.Combine(modules, "attachmentss", MwBaseLua.Identifier(weaponName), entry.Folder, entry.Class + ".lua");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var material = $"{materials.TrimEnd('/')}/icons/blueprints/{entry.Class}";
            var icon = AttachmentIcon(request.IconDirectory, entry.Cast.Model, Path.Combine(output, "materials"), material)
                ?? AttachmentIcon(request.IconDirectory, entry.Cast.Base, Path.Combine(output, "materials"), material);
            // shortcut: the dump has no rarity; blue distinguishes blueprints without claiming a CoD rarity tier.
            LuaFile.Write(file, entry.Lua + "ATTACHMENT.UIColor = CUSTOMIZATION_COLOR_RARE\n"
                + (icon is null ? "" : $"ATTACHMENT.Icon = Material(\"{icon}\")\n"));
            if (icon is null) notes.Add($"blueprint {entry.Cast.Model}: no icon PNG; using the base attachment icon");
        }
        foreach (var slot in valid.GroupBy(item => (item.Slot, item.Folder)))
        {
            var list = string.Join(", ", slot.Select(item => $"\"{item.Class}\""));
            LuaFile.Write(Path.Combine(injectors, $"{weaponId}_{slot.Key.Folder}.lua"),
                $"-- Generated by Alchemy Stars: CoD blueprint parts for {swep}\n\nINJECTOR.SWEPs = {{\"{swep}\"}}\nINJECTOR.Slot = {slot.Key.Slot}\n"
                + $"INJECTOR.Attachment = {{{WrapList(list)}}}\n");
        }
        var presets = Path.Combine(modules, "presets", weaponId);
        foreach (var blueprint in valid.GroupBy(entry => stats?.BlueprintKey(entry.Cast.Model)
            ?? (BlueprintVariant().Match(entry.Cast.Model) is { Success: true } match ? "v" + match.Groups[1].Value : entry.Cast.Model)))
        {
            // A preset can select only one part per slot: do not silently mix ambiguous exports.
            if (blueprint.GroupBy(entry => entry.Slot).Any(slot => slot.Count() > 1))
            {
                notes.Add($"blueprint preset {blueprint.Key}: multiple parts in one slot, skipped");
                continue;
            }
            Directory.CreateDirectory(presets);
            var title = stats?.BlueprintName(blueprint.First().Cast.Model) ?? $"{weaponName} {blueprint.Key}";
            var list = string.Join(", ", blueprint.OrderBy(entry => entry.Slot).Select(entry => $"\"{entry.Class}\""));
            LuaFile.Write(Path.Combine(presets, $"{swep}_bp_{MwBaseLua.Identifier(blueprint.Key)}.lua"),
                $"-- Generated by Alchemy Stars: CoD blueprint preset for {swep}\n\nPRESET.Name = \"{MwBaseLua.LuaString(title)}\"\n"
                + $"PRESET.SWEP = \"{swep}\"\nPRESET.Attachments = {{{WrapList(list)}}}\n");
        }
        // Older versions wrote and then deleted pack Lua, leaving an empty ARC9 directory tree.
        var arc9 = Path.Combine(output, "lua", "arc9");
        var arc9Attachments = Path.Combine(arc9, "common", "attachments");
        foreach (var variant in overrides.Values.Select(item => item.Class))
        {
            var old = Path.Combine(arc9Attachments, variant + ".lua");
            if (File.Exists(old) && File.ReadAllText(old) is var text && text.StartsWith("-- mount: ", StringComparison.Ordinal)
                && text.Contains($"ATT.Model = \"models/{AttachmentPackModels}{variant}.mdl\"", StringComparison.Ordinal)) File.Delete(old);
        }
        foreach (var folder in new[] { arc9Attachments, Path.Combine(arc9, "common"), arc9 })
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        notes.Add($"blueprints: {valid.Length} parts -> {output}");
        return qcs;
    }

    /// <summary>The attachmentss folder MwBaseLua put a class in (barrels, mags, sights...), "misc" when unknown.</summary>
    private static string FolderOf(string baseClass, string weaponOutput, string swep)
    {
        var own = Path.Combine(weaponOutput, "lua", "weapons", "mg_base", "modules", "attachmentss", swep[3..]);
        var file = Directory.Exists(own) ? Directory.GetFiles(own, baseClass + ".lua", SearchOption.AllDirectories).FirstOrDefault() : null;
        return file is not null ? Path.GetFileName(Path.GetDirectoryName(file)!)
            : baseClass.StartsWith("att_cod_", StringComparison.Ordinal) ? (baseClass.Contains("grip", StringComparison.Ordinal) ? "grips" : "sights") : "misc";
    }

    /// <summary>Wraps a comma list at ~120 columns like the official injectors (akilo47_receivers.lua).</summary>
    private static string WrapList(string list)
    {
        var text = new StringBuilder();
        var line = 0;
        foreach (var item in list.Split(", "))
        {
            if (line > 0 && line + item.Length > 110) { text.Append('\n'); line = 0; }
            else if (line > 0) { text.Append(' '); line++; }
            text.Append(item).Append(',');
            line += item.Length + 1;
        }
        return text.ToString().TrimEnd(',');
    }

    [GeneratedRegex(@"_(?:v)?(\d+)$", RegexOptions.IgnoreCase)] private static partial Regex BlueprintVariant();
    [GeneratedRegex(@"^(vm_.+?)_((?:att_)?vm_.+)$", RegexOptions.IgnoreCase)] private static partial Regex BlueprintStem();
    [GeneratedRegex(@"\{[^{}]*\}")] private static partial Regex CustomizationSlot();
    [GeneratedRegex("\"([^\"]+)\"")] private static partial Regex QuotedClass();
}
