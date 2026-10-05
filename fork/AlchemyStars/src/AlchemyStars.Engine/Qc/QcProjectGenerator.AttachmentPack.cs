using System.Text;
using System.Text.RegularExpressions;
using Alchemist.UI;
using Cast.NET;
using Cast.NET.Nodes;
using RedFox.Graphics3D.Skeletal;
using static Alchemist.UI.CastNodeTraversal;

namespace AlchemyStars.Engine;

/// <param name="Casts">Extracted CoD attachment CASTs; a stem outside ^(att_)?vm_ is skipped (a worldmodel cannot bonemerge).</param>
/// <param name="ReticleDirectory">saluki reticle PNGs; a reticle without a PNG references the MW Base material instead.</param>
/// <param name="StatsFile">Optional cod20_weapons.json: muzzles and grips CoD lists get its modifiers instead of the generic MW Base ones.</param>
/// <param name="IconDirectory">Optional saluki images/icon_attachment: each part's CoD UI icon becomes its ATTACHMENT.Icon.</param>
public sealed record MwAttachmentPackRequest(IReadOnlyList<string> Casts, string ReticleDirectory, string OutputDirectory, int MaterialMaxSize = 2048,
    string StatsFile = "", string IconDirectory = "", IReadOnlyDictionary<string, (string Class, string Token)>? Overrides = null);

/// <param name="Qcs">QC projects written; an optic also writes its _hide QC.</param>
/// <param name="LuaFiles">One MW Base ATTACHMENT lua per part; its first line names the bone it mounts on.</param>
public sealed record MwAttachmentPackResult(IReadOnlyList<string> Qcs, IReadOnlyList<string> LuaFiles, IReadOnlyList<string> Log);

public static partial class QcProjectGenerator
{
    private const string AttachmentPackModels = "cod_attachments/";
    private const string AttachmentPackMaterials = "cod_attachments";

    /// <summary>
    /// CoD attachment pockets: a stem minus its export prefix, the BOM's _LOD suffix and a trailing _v0 / _000.
    /// </summary>
    private static string AttachmentToken(string stem) =>
        AttachmentTokenSuffix().Replace(ViewModelAttachmentPrefix().Replace(LodSuffix().Replace(stem, ""), ""), "");

    /// <summary>att_cod_&lt;token&gt;: model, QC, lua and material names of one converted CoD attachment.</summary>
    public static string AttachmentPackClass(string castPath) =>
        "att_cod_" + MwBaseLua.Identifier(AttachmentToken(Path.GetFileNameWithoutExtension(castPath)));

    /// <summary>Only viewmodel parts convert: the same attachment's worldmodel bonemerges nowhere.</summary>
    public static bool IsAttachmentPackCast(string castPath) =>
        ViewModelAttachmentPrefix().IsMatch(LodSuffix().Replace(Path.GetFileNameWithoutExtension(castPath), ""));

    /// <summary>
    /// Compiles every CoD attachment as its own MW Base model — one bodygroup plus an idle, bonemerged onto the viewmodel by
    /// bone name — with converted materials and one ATTACHMENT lua that weapons generated beside the pack list in their slots. Optic
    /// sights also get a _hide model (the lens shape MW Base stencils around) and a "lens" bodygroup.
    /// </summary>
    public static MwAttachmentPackResult GenerateAttachmentPack(MwAttachmentPackRequest request)
    {
        // Trimmed: a folder picker's trailing separator would break the stale-injector cleanup paths.
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
        var stats = Cod20Stats.Load(request.StatsFile);
        var materials = Path.Combine(output, "materials", AttachmentPackMaterials);
        Directory.CreateDirectory(Path.Combine(output, "meshes"));
        var staging = Path.Combine(Path.GetTempPath(), "AlchemyStarsAttPack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var qcs = new List<string>();
        var luas = new List<string>();
        var log = new List<string>();
        var staged = new List<string>();
        var glass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lenses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reticles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var cast in request.Casts.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var stem = LodSuffix().Replace(Path.GetFileNameWithoutExtension(cast), "");
                if (!ViewModelAttachmentPrefix().IsMatch(stem)) { log.Add($"skip {stem}: not a viewmodel attachment"); continue; }
                // Blueprint variants (WriteBlueprints) compile under their own class but decide sight/muzzle layout by their base part.
                var (className, token) = request.Overrides?.GetValueOrDefault(cast) is { Class: not null } given
                    ? given : (AttachmentPackClass(cast), AttachmentPackClass(cast)["att_cod_".Length..]);
                var stagedCast = Stage(cast, Path.Combine(staging, className + ".cast"));
                var mount = RootBone(stagedCast);
                if (AttachmentCategory(mount) is not { } category)
                {
                    log.Add($"skip {className}: {mount} is not a sight, muzzle or grip mount");
                    continue;
                }
                staged.Add(stagedCast);
                var plan = Plan([new ModelPartSpec(stagedCast, ModelPartKind.Attachment)]);
                var merged = Path.Combine(staging, className + ".merged.cast");
                MayaCastPackage.SaveModel(merged, plan, AnimationConverter.TranslatorFactory);
                var (parts, meshMaterials) = AttachmentMeshes(merged);
                var names = plan.Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<int> Keep(params AttachmentMesh[] kinds) =>
                    [.. parts.Select((part, index) => (part, index)).Where(item => kinds.Contains(item.part)).Select(item => item.index)];

                var body = Keep(AttachmentMesh.Body);
                var lens = Keep(AttachmentMesh.Lens);
                var hide = Keep(AttachmentMesh.Hide);
                var all = Keep(AttachmentMesh.Body, AttachmentMesh.Lens, AttachmentMesh.Hide);
                var mesh = $"meshes/{className}.smd";
                var bodygroups = "";
                var attachmentLines = new List<string>();
                string lua;
                string arc9;
                if (category == "sights")
                {
                    if (names.Contains("tag_reticle_attach")) attachmentLines.Add(AttachmentLine("reticle", "tag_reticle_attach"));
                    else log.Add($"-- {className}: no tag_reticle_attach, the sight shows no reticle");
                    if (names.Contains("tag_reticle_attach2")) attachmentLines.Add(AttachmentLine("reticle2", "tag_reticle_attach2"));
                    var sight = AttachmentSight(token);
                    var optic = sight.Base != "att_sight_1x";
                    if (optic && hide.Count == 0)
                    {
                        log.Add($"-- {className}: no lens-hide mesh, packaged as a 1x sight");
                        sight = ("att_sight_1x", sight.Reticle, sight.Size, null);
                        optic = false;
                    }
                    // A red dot keeps its lens in the optic bodygroup; an optic moves it to the "lens" bodygroup att_optic swaps.
                    SmdModelExporter.Save(Path.Combine(output, mesh), merged, plan.Skeleton, Inches,
                        keepMeshes: optic ? body : Keep(AttachmentMesh.Body, AttachmentMesh.Lens));
                    bodygroups = Bodygroup("optic", Studio(mesh));
                    if (optic)
                    {
                        var lensMesh = $"meshes/{className}_lens.smd";
                        SmdModelExporter.Save(Path.Combine(output, lensMesh), merged, plan.Skeleton, Inches, keepMeshes: lens);
                        bodygroups += Bodygroup("lens", "blank", Studio(lensMesh));
                        lenses.Add(className);
                        // The hide model is stencilled out around the reticle, so its surface is the render-target lens material.
                        var hideMaterials = hide.Select(index => meshMaterials[index]).Where(name => name.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(name => name, _ => className + "_lens", StringComparer.OrdinalIgnoreCase);
                        var hideCast = Stage(cast, Path.Combine(staging, className + "_hide.cast"), hideMaterials);
                        var hidePlan = Plan([new ModelPartSpec(hideCast, ModelPartKind.Attachment)]);
                        var hideMerged = Path.Combine(staging, className + "_hide.merged.cast");
                        MayaCastPackage.SaveModel(hideMerged, hidePlan, AnimationConverter.TranslatorFactory);
                        var hideMesh = $"meshes/{className}_hide.smd";
                        SmdModelExporter.Save(Path.Combine(output, hideMesh), hideMerged, hidePlan.Skeleton, Inches, keepMeshes: hide);
                        var hideQc = Path.Combine(output, className + "_hide.qc");
                        File.WriteAllText(hideQc, AttachmentQc(className + "_hide", Bodygroup("hide", Studio(hideMesh)), hidePlan.Skeleton, [], hideMesh, hide: true));
                        qcs.Add(hideQc);
                    }
                    var thermal = token.Contains("therm", StringComparison.Ordinal);
                    // MW Base's hybrid reticle needs the second lens tag; without it the CoD part is a plain optic.
                    var hybridReticle = sight.Base == "att_hybrid_3x" && names.Contains("tag_reticle_attach2")
                        ? ReticleMaterial(request, materials, reticles, "reticle_hybrid_default", log) : "";
                    var reticle = PreviewReticle(request, materials, reticles, stats?.ReticlePreview(token), log)
                        ?? ReticleMaterial(request, materials, reticles, sight.Reticle, log);
                    lua = SightLua(mount, className, AttachmentName(token, stats), sight, optic ? 2 : 1, thermal,
                        reticle, hybridReticle, optic && HideDepth(merged, hide) < 0.25f);
                    // studiomdl numbers materials by first use: the optic bodygroup's, then the lens bodygroup's.
                    var modelMaterials = body.Order().Concat(optic ? lens.Order() : []).Select(index => meshMaterials[index])
                        .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    // ads_scope_lens* is the visible front glass (checked in game); reticle_ui_stencil sits behind it.
                    var rtIndex = optic ? modelMaterials.FindIndex(name => ScopeLensMaterial().IsMatch(name)) : -1;
                    if (optic && rtIndex < 0)
                        rtIndex = modelMaterials.FindIndex(name => !name.Contains("reticle_ui_stencil", StringComparison.OrdinalIgnoreCase)
                            && lens.Any(index => meshMaterials[index] == name));
                    arc9 = Arc9SightLua(sight.Base, sight.Size, reticle, BarrelTags(plan.Skeleton).GetValueOrDefault("tag_reticle_attach").P, rtIndex);
                }
                else
                {
                    SmdModelExporter.Save(Path.Combine(output, mesh), merged, plan.Skeleton, Inches, keepMeshes: all);
                    bodygroups = Bodygroup(category == "muzzles" ? "muzzle" : "grip", Studio(mesh));
                    if (category == "muzzles")
                    {
                        // CoD lights the flash on a tag of its own; a muzzle without one still needs the attachment for the flash.
                        var flash = new[] { "tag_flash_silenced", "tag_flash_muzzle" }.FirstOrDefault(names.Contains);
                        if (flash is null) log.Add($"-- {className}: no flash bone, the muzzle attachment sits on {mount}");
                        attachmentLines.Add(AttachmentLine("muzzle", flash ?? mount));
                    }
                    lua = category == "muzzles" ? MuzzleLua(mount, className, token, stats) : GripLua(mount, className, token, stats);
                    arc9 = category != "muzzles" ? ""
                        : "ATT.MuzzleDevice = true\n" + (token.Contains("silencer", StringComparison.Ordinal) ? "ATT.Silencer = true\nATT.MuzzleParticleOverride = \"muzzleflash_suppressed\"\n" : "");
                }
                // Body and lens materials the converter skips are CoD lens glass: MW Base shades them like attachment_reflex_02_glass.
                foreach (var material in parts.Select((part, index) => (part, index))
                    .Where(item => item.part is AttachmentMesh.Body or AttachmentMesh.Lens).Select(item => meshMaterials[item.index]))
                    if (material.Length > 0 && !ConvertedMaterial(material) && !MwBaseAttachmentMaterial(material)) glass.Add(material);

                var icon = AttachmentIcon(request.IconDirectory, cast, Path.Combine(output, "materials"), $"{AttachmentPackMaterials}/icons/{className}");
                if (icon is not null)
                    lua = IconLine().Replace(lua, $"$0ATTACHMENT.Icon = Material(\"{icon}\")\n", 1);
                else if (request.IconDirectory.Trim().Length > 0) log.Add($"-- {className}: no icon PNG");
                var qc = Path.Combine(output, className + ".qc");
                File.WriteAllText(qc, AttachmentQc(className, bodygroups, plan.Skeleton, attachmentLines, mesh));
                qcs.Add(qc);
                var luaFile = Path.Combine(output, "lua", "weapons", "mg_base", "modules", "attachmentss", "cod", category, className + ".lua");
                Directory.CreateDirectory(Path.GetDirectoryName(luaFile)!);
                LuaFile.Write(luaFile, lua);
                luas.Add(luaFile);
                // ARC9 weapons list the same parts by slot category cod_<mount> (Arc9Lua); the file name is the ATT shortname.
                var arc9File = Path.Combine(output, "lua", "arc9", "common", "attachments", className + ".lua");
                Directory.CreateDirectory(Path.GetDirectoryName(arc9File)!);
                LuaFile.Write(arc9File, Arc9AttachmentLua(mount, className, AttachmentName(token, stats), icon, arc9,
                    stats is not null && stats.Knows(token) ? stats.Mods(token) : null));
                luas.Add(arc9File);
            }
            log.AddRange(CodMaterialConverter.Convert(staged, materials, AttachmentPackMaterials + "/", VmtEnvmapStyle.MwBase,
                request.MaterialMaxSize, ConvertedMaterial));
            foreach (var material in glass) File.WriteAllText(Path.Combine(materials, material + ".vmt"), GlassVmt);
            File.WriteAllText(Path.Combine(materials, "arc9_lens_clear.vmt"), Arc9ClearLensVmt);
            foreach (var lensMaterial in lenses) File.WriteAllText(Path.Combine(materials, lensMaterial + "_lens.vmt"), ScopeLensVmt);
            // Weapons generated next to the pack list its parts in their own customization.lua (AttachmentPackParts).
            RemoveOldInjectors(output);
            log.Add("weapons: regenerate the mg_ weapons next to this pack to add its parts to their slots");
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new(qcs, luas, log);
    }

    private enum AttachmentMesh { Body, Lens, Hide, Drop }

    /// <summary>Bone -> MW Base category folder. Anything else (bipod, grenade launcher…) has no MW Base equivalent.</summary>
    private static string? AttachmentCategory(string root) => root.ToLowerInvariant() switch
    {
        "tag_silencer" => "muzzles",
        "tag_grip_attach" or "tag_folding_grip" => "grips",
        "tag_reflex" or "tag_holo" or "tag_hybrid" or "tag_acog_2" or "tag_scope" or "tag_thermal" => "sights",
        _ => null,
    };

    /// <summary>
    /// CoD splits a sight into the housing, the lens MW Base draws the reticle on (weighted to tag_lense) and the lens shape it
    /// stencils out (tag_lense_hide). Reflections are CoD's screen-space mirrors and have no Source equivalent.
    /// </summary>
    private static (AttachmentMesh[] Parts, string[] Materials) AttachmentMeshes(string merged)
    {
        var model = CastReader.Load(merged).RootNodes.SelectMany(DescendantsAndSelf).OfType<ModelNode>().Single();
        var bones = (model.Skeleton?.Bones ?? []).Select(bone => bone.Name ?? "").ToArray();
        var parts = new AttachmentMesh[model.Meshes.Length];
        var materials = new string[model.Meshes.Length];
        for (var index = 0; index < model.Meshes.Length; index++)
        {
            var mesh = model.Meshes[index];
            materials[index] = mesh.Material?.Name ?? "";
            if (materials[index].Contains("reflection", StringComparison.OrdinalIgnoreCase)) { parts[index] = AttachmentMesh.Drop; continue; }
            var weighted = WeightedBoneNames(mesh, bones);
            parts[index] = weighted.Contains("tag_lense_hide") ? AttachmentMesh.Hide
                : weighted.Contains("tag_lense") ? AttachmentMesh.Lens : AttachmentMesh.Body;
        }
        return (parts, materials);
    }

    /// <summary>Front-to-back extent (CAST units, along x) of the lens-hide meshes: a scope tube, or ~0 for a flat glass.</summary>
    private static float HideDepth(string merged, HashSet<int> hide)
    {
        var model = CastReader.Load(merged).RootNodes.SelectMany(DescendantsAndSelf).OfType<ModelNode>().Single();
        var xs = hide.SelectMany(index => model.Meshes[index].VertexPositionBuffer.Values.Select(position => position.X)).ToArray();
        return xs.Length == 0 ? 0 : xs.Max() - xs.Min();
    }

    private static HashSet<string> WeightedBoneNames(MeshNode mesh, string[] bones)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indices = mesh.VertexWeightBoneBuffer switch
        {
            CastArrayProperty<byte> values => values.Values.Select(value => (int)value).ToArray(),
            CastArrayProperty<ushort> values => values.Values.Select(value => (int)value).ToArray(),
            CastArrayProperty<uint> values => values.Values.Select(value => checked((int)value)).ToArray(),
            _ => [],
        };
        var weights = mesh.VertexWeightValueBuffer?.Values;
        for (var index = 0; index < indices.Length; index++)
            if ((weights is null || index < weights.Count && weights[index] > 0) && (uint)indices[index] < (uint)bones.Length)
                names.Add(bones[indices[index]]);
        return names;
    }

    private static string Bodygroup(string name, params string[] entries) =>
        $"$bodygroup \"{name}\"\n{{\n" + string.Concat(entries.Select(entry => $"\t{entry}\n")) + "}\n";

    private static string Studio(string mesh) => $"studio \"{mesh}\"";

    private static string AttachmentLine(string name, string bone) => $"$attachment \"{name}\" \"{bone}\" 0 0 0 rotate 0 0 0";

    private static string AttachmentQc(string className, string bodygroups, Skeleton skeleton, IReadOnlyList<string> attachments, string mesh,
        bool hide = false)
    {
        var text = new StringBuilder("// CoD attachment: compiled standalone, bonemerged onto the MW Base viewmodel by bone name\n\n");
        text.Append($"$modelname \"{AttachmentPackModels}{className}.mdl\"\n\n// Meshes\n\n").Append(bodygroups);
        text.Append("\n$surfaceprop \"weapon\"\n\n$contents \"solid\"\n\n$illumposition 0 0 0\n\n$mostlyopaque\n\n// Materials\n\n")
            .Append($"$cdmaterials \"{AttachmentPackModels}\"\n")
            // MW Base ships reticle_ui_stencil, stencil_outline and ads_scope_lens_1 there; they are not converted.
            .Append("$cdmaterials \"viper/mw/attachments/\"\n");
        text.Append("\n// Weapon Bones\n\n").Append(DefineBones(skeleton, null));
        // Unweighted tags (tag_lense, tag_reticle_attach…) must survive, or MW Base reads an unset matrix for them.
        text.Append("\n// Bonemerge targets\n\n").Append(Bonemerges(skeleton.Bones.Select(bone => bone.Name!).Order(StringComparer.Ordinal)));
        if (attachments.Count > 0) text.Append("\n// Attachments\n\n").Append(string.Concat(attachments.Select(line => line + "\n")));
        // Unmerged copies need an identity root: att_optic draws the _hide model at the optic's bone 0, and ARC9's muzzle proxy
        // (flash source) sits at the slot. "rotate -90" cancels studiomdl's default +90° sequence yaw; bonemerged parts ignore it.
        text.Append($"\n// Sequences\n\n$sequence \"idle\" \"{mesh}\" fps 30 rotate -90\n");
        return text.ToString();
    }

    // MW Base magnification per CoD token. att_sight_1x is a red dot (reticle only); att_optic_* adds the lens bodygroup
    // and the _hide model. Hybrids without tag_reticle_attach2 stay plain optics.
    private static (string Base, string Reticle, int Size, int? Parallax) AttachmentSight(string token)
    {
        if (token.Contains("hybrid", StringComparison.Ordinal)) return ("att_hybrid_3x", "reticle_acog_default", 600, 600);
        if (SniperSightTokens.Any(word => token.Contains(word, StringComparison.Ordinal))) return ("att_optic_10x", "reticle_vzscope_default", 750, 600);
        if (token.Contains("fourx", StringComparison.Ordinal)) return ("att_optic_4x", "reticle_acog_default2", 700, 700);
        if (token.Contains("twofivex", StringComparison.Ordinal)) return ("att_optic_2x", "reticle_holo_default", 1000, 800);
        if (token.Contains("holo", StringComparison.Ordinal)) return ("att_sight_1x", "reticle_holo_default2", 400, null);
        return ("att_sight_1x", "reticle_reflex_default2", 950, null);
    }

    private static readonly string[] SniperSightTokens = ["scope_vz", "scope_rf", "scopenvg", "scopetherm", "xl"];

    private static string SightLua(string mount, string className, string display, (string Base, string Reticle, int Size, int? Parallax) sight,
        int index, bool thermal, string reticle, string hybridReticle, bool flatLens = false)
    {
        var text = new StringBuilder($"-- mount: {mount}\n-- shortcut: reticle/magnification picked from the CoD file name, edit per sight\n");
        text.Append($"ATTACHMENT.Base = \"{sight.Base}\"\nATTACHMENT.Name = \"{display}\"\n");
        text.Append($"ATTACHMENT.Model = Model(\"models/{AttachmentPackModels}{className}.mdl\")\n");
        text.Append($"ATTACHMENT.Bodygroups = {{\n    [\"tag_sight\"] = {index},\n    [\"tag_rail\"] = 1,\n    [\"sight\"] = {index}\n}}\n");
        text.Append($"ATTACHMENT.AttachmentBodygroups = {{\n    [\"tag_sight\"] = {index},\n    [\"tag_rail\"] = 1,\n    [\"sight\"] = {index}\n}}\n");
        if (sight.Parallax is { } parallax)
            text.Append($"ATTACHMENT.Optic = {{\n    LensHideMaterial = Material(\"{AttachmentPackMaterials}/{className}_lens\"),\n"
                + $"    HideModel = Model(\"models/{AttachmentPackModels}{className}_hide.mdl\"),\n"
                + $"    LensBodygroup = \"lens\",\n    FOV = 7,\n    ParallaxSize = {parallax},\n    Thermal = {(thermal ? "true" : "false")}"
                + (thermal ? ",\n    ThermalBackgroundColor = Color(50, 100, 75, 223),\n    ThermalBodiesColor = Color(250, 250, 0, 150)" : "")
                + "\n}\n");
        text.Append($"ATTACHMENT.Reticle = {{\n    Material = Material(\"{reticle}\"),\n    Size = {sight.Size},\n"
            + "    Color = Color(255, 255, 255, 255),\n    Attachment = \"reticle\"\n}\n");
        // att_optic's parallax ring imitates a scope tube; a flat projected glass (lens-hide 0 deep) has none, so the ring
        // only blacks out the edge of the window.
        if (flatLens) text.Append("\nfunction ATTACHMENT:DrawParallax() end\n");
        if (hybridReticle.Length > 0)
            text.Append($"ATTACHMENT.ReticleHybrid = {{\n    Material = Material(\"{hybridReticle}\"),\n    Size = 110,\n"
                + "    Color = Color(255, 255, 255, 255),\n    Attachment = \"reticle2\"\n}\n");
        return text.ToString();
    }

    /// <summary>ARC9 attachment of a pack part: the att_cod_*.mdl bonemerged onto the weapon's mount tag (both share the CoD bone names).</summary>
    private static string Arc9AttachmentLua(string mount, string className, string display, string? icon, string extra,
        IReadOnlyDictionary<string, float>? mods) =>
        $"-- mount: {mount}\n" +
        $"ATT.PrintName = \"{display}\"\n" +
        $"ATT.CompactName = \"{display}\"\n" +
        (icon is null ? "" : $"ATT.Icon = Material(\"{icon}\")\n") +
        "ATT.Description = [[]]\n" +
        "ATT.AutoStats = true\n" +
        "ATT.Free = true\n" +
        "ATT.MenuCategory = \"ARC9 - COD20\"\n" +
        $"ATT.Category = \"cod_{mount}\"\n" +
        $"ATT.Model = \"models/{AttachmentPackModels}{className}.mdl\"\n" +
        "ATT.BoneMerge = true\n" +
        extra + Arc9Lua.StatLines(mods);

    /// <summary>
    /// ARC9 sight of a pack part. The eye lines up with tag_reticle_attach (mount space, inches: X forward, Y left, Z up);
    /// GenerateAutoSight subtracts Pos along the tag's right/forward/up, hence (Y, distance, -Z).
    /// Red dots draw a holosight; optics (rtIndex >= 0) paint the scope render target on that model material, like COD2019.
    /// </summary>
    private static string Arc9SightLua(string baseClass, int size, string reticle, System.Numerics.Vector3 eye, int rtIndex)
    {
        static string N(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var magnification = baseClass switch { "att_optic_2x" => 2, "att_hybrid_3x" => 3, "att_optic_4x" => 4, "att_optic_10x" => 10, _ => 1.15f };
        // shortcut: eye distance 6 is a guess; HoloSightSize = 0.4 x MW Base reticle size was tuned in game on reflex03 only.
        var sights = $$"""

            ATT.Sights = {
                {
                    Pos = Vector({{N(eye.Y)}}, 6, {{N(-eye.Z)}}),
                    Ang = Angle(0, 0, 0),
                    Magnification = 1.15,
                    ViewModelFOV = 54,
                },
            }

            """;
        var scope = rtIndex < 0
            ? $$"""
                ATT.HoloSight = true
                ATT.HoloSightReticle = Material("{{reticle}}")
                ATT.HoloSightSize = {{(int)Math.Round(size * 0.4)}}
                ATT.HoloSightColorable = false

                """
            : $$"""
                ATT.RTScope = true
                ATT.RTScopeSubmatIndex = {{rtIndex}}
                ATT.RTScopeMagnification = {{N(magnification)}}
                ATT.RTScopeReticle = Material("{{reticle}}", "mips smooth")
                ATT.RTScopeReticleScale = 1
                ATT.RTScopeColorable = false
                ATT.ScopeScreenRatio = 0.5

                -- The "lens" bodygroup (blank by default for MW Base's att_optic) carries the scope glass.
                ATT.ModelBodygroups = "01"

                """;
        // The other lens surfaces use MW Base materials (env_cubemap glass) that ARC9 cannot shade: draw them clear.
        return sights + scope + $$"""
            ATT.DrawFunc = function(swep, model, wm)
                if model.CodLensClear then return end

                model.CodLensClear = true

                for index, material in ipairs(model:GetMaterials()) do
                    local isLens = string.find(material, "lens", 1, true)
                        or string.find(material, "glas", 1, true)
                        or string.find(material, "reticle_ui_stencil", 1, true)

                    if isLens{{(rtIndex < 0 ? "" : $" and index - 1 ~= {rtIndex}")}} then
                        model:SetSubMaterial(index - 1, "{{AttachmentPackMaterials}}/arc9_lens_clear")
                    end
                end
            end

            """;
    }

    /// <summary>COD20 stat lines of a shared part (jup_grip_vert_01), or null when the data has no entry for it.</summary>
    private static string? CodStatLines(Cod20Stats? cod, string token) =>
        cod is not null && cod.Knows(token) ? string.Join('\n', Cod20Stats.StatLines(cod.Mods(token)).Select(line => "    " + line)) : null;

    private static string MuzzleLua(string mount, string className, string token, Cod20Stats? cod)
    {
        var (baseClass, stats) = MuzzleStats(token);
        // Suppressors still need doSuppressorStats for the sound/flash swap.
        if (CodStatLines(cod, token) is { } codLines)
            stats = string.Join('\n', new[] { codLines, baseClass == "att_suppressor" ? "    weapon:doSuppressorStats()" : "" }.Where(line => line.Length > 0));
        var text = new StringBuilder($"-- mount: {mount}\nATTACHMENT.Base = \"{baseClass}\"\nATTACHMENT.Name = \"{AttachmentName(token, cod)}\"\n");
        text.Append($"ATTACHMENT.Model = Model(\"models/{AttachmentPackModels}{className}.mdl\")\n");
        // Like the official muzzle devices: attach to whichever barrel is fitted, flash follows the muzzle attachment.
        text.Append("ATTACHMENT.BonemergeToCategory = {\"Barrels\"}\n").Append(MuzzleBodygroups);
        if (stats.Length == 0) return text.ToString();
        return text.Append(StatsPrefix).Append(stats).Append("\nend\n").ToString();
    }

    private static string GripLua(string mount, string className, string token, Cod20Stats? cod)
    {
        var text = new StringBuilder($"-- mount: {mount}\nATTACHMENT.Base = \"att_grip\"\nATTACHMENT.Name = \"{AttachmentName(token, cod)}\"\n");
        text.Append($"ATTACHMENT.Model = Model(\"models/{AttachmentPackModels}{className}.mdl\")\n");
        var angled = token.Contains("angled", StringComparison.Ordinal);
        var stats = CodStatLines(cod, token) is { } codLines
            ? codLines + (angled ? "" : "\n    weapon.HoldType = \"RifleWithVerticalGrip\"")
            : angled ? AngledGripStats : VerticalGripStats;
        text.Append(StatsPrefix).Append(stats).Append("\nend\n");
        return text.ToString();
    }

    // Stats numbers are copied from the official MW Base parts (muzzles/*, grips/*), picked by the CoD device name.
    private static (string Base, string Stats) MuzzleStats(string token)
    {
        if (token.Contains("silencer", StringComparison.Ordinal)) return ("att_suppressor", SilencerStats);
        if (token.Contains("comp", StringComparison.Ordinal)) return ("att_muzzle", CompensatorStats);
        if (token.Contains("brake", StringComparison.Ordinal)) return ("att_muzzle", BrakeStats);
        if (token.Contains("breacher", StringComparison.Ordinal)) return ("att_muzzle", BreacherStats);
        if (token.Contains("flash", StringComparison.Ordinal)) return ("att_muzzle", FlashHiderStats);
        return ("att_muzzle", "");
    }

    /// <summary>CoD's display name ("Caucasus Reflex Sight") when the COD20 strings know it, else the token's words (JUP REFLEX02).</summary>
    private static string AttachmentName(string token, Cod20Stats? cod = null) =>
        cod?.Name(token)?.Replace("\\", "\\\\").Replace("\"", "\\\"")
        ?? string.Join(' ', token.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToUpperInvariant()));

    /// <summary>
    /// MW Base ships these under viper/mw/attachments, so they are neither converted nor copied: reticle_ui_stencil and
    /// ads_scope_lens_1 are the lens the optic renders through, stencil_outline its shadow.
    /// </summary>
    private static bool MwBaseAttachmentMaterial(string name) =>
        name.Equals("reticle_ui_stencil", StringComparison.OrdinalIgnoreCase)
        || name.Equals("stencil_outline", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ads_scope_lens_1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Materials the PBR converter turns into phong VTFs; CoD lens glass gets the hand-written glass VMT instead.</summary>
    private static bool ConvertedMaterial(string name) =>
        !MwBaseAttachmentMaterial(name) && !name.StartsWith("att_lens_", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("_lens", StringComparison.OrdinalIgnoreCase) && !name.Contains("glass", StringComparison.OrdinalIgnoreCase);

    /// <summary>MW Base reticle materials that exist as VMTs under viper/mw/reticles.</summary>
    private static readonly HashSet<string> MwBaseReticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "reticle_acog_default", "reticle_acog_default2", "reticle_holo_default", "reticle_holo_default2",
        "reticle_reflex_default2", "reticle_thermal_default", "reticle_vzscope_default",
    };

    /// <summary>
    /// CoD UI icon of an attachment CAST — jup_icon_attachment_&lt;token&gt;_000 (MW3) or icon_attachment_&lt;token&gt;_v0 (MW2019),
    /// token also tried without jup_ and _tall/_pstl — written as an MW Base icon VMT+VTF. Null when the folder has none.
    /// </summary>
    /// <summary>
    /// The weapon's CoD icon (saluki images/icon_weapon/icon_weapon_&lt;weapon&gt;_mp.png, beside the attachment icon folder) as
    /// MW Base's two VGUI icons: vgui/entities/mg_&lt;weapon&gt; (spawn menu, 512 square) and vgui/spawnicons/icon_cac_mg_&lt;weapon&gt;
    /// (weapon selection, 1024x512). False when there is no icon folder or PNG.
    /// </summary>
    private static bool WeaponIcon(string iconDirectory, string weaponName, string codName, string materialsRoot, List<string> notes, bool arc9 = false)
    {
        if (iconDirectory.Trim().Length == 0) return false;
        var folder = Path.TrimEndingDirectorySeparator(iconDirectory.Trim());
        var png = new[] { folder, Path.Combine(Path.GetDirectoryName(folder) ?? folder, "icon_weapon") }
            .SelectMany(directory => new[] { weaponName, codName }.SelectMany(name => new[] { $"icon_weapon_{name}_mp", $"icon_weapon_{name}" })
                .Select(name => Path.Combine(directory, name + ".png")))
            .FirstOrDefault(File.Exists);
        notes.Add(png is null ? $"weapon icon: no icon_weapon_{codName}_mp.png in {folder} or its sibling icon_weapon" : "weapon icon: " + Path.GetFileName(png));
        if (png is null) return false;
        if (arc9)
        {
            // ARC9 draws Material("entities/<class>.png") square for both the spawn menu and weapon selection.
            var entity = Path.Combine(materialsRoot, "entities", "arc9_" + MwBaseLua.Identifier(weaponName) + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(entity)!);
            CodMaterialConverter.WriteIconPng(png, entity, 512, 512);
            return true;
        }
        var swep = "mg_" + MwBaseLua.Identifier(weaponName);
        foreach (var (material, width, height) in new[] { ($"vgui/entities/{swep}", 512, 512), ($"vgui/spawnicons/icon_cac_{swep}", 1024, 512) })
        {
            var vtf = Path.Combine(materialsRoot, material.Replace('/', Path.DirectorySeparatorChar) + ".vtf");
            Directory.CreateDirectory(Path.GetDirectoryName(vtf)!);
            CodMaterialConverter.WriteIconVtf(png, vtf, width, height);
            File.WriteAllText(Path.ChangeExtension(vtf, ".vmt"),
                $"\"UnlitGeneric\"\n{{\n\t\"$basetexture\" \"{material}\"\n\t\"$nolod\" 1\n\t\"$vertexalpha\" 1\n\t\"$vertexcolor\" 1\n\t\"$translucent\" \"1\"\n}}\n");
        }
        return true;
    }

    internal static string? AttachmentIcon(string iconDirectory, string castPath, string materialsRoot, string materialPath)
    {
        if (iconDirectory.Trim().Length == 0) return null;
        var token = AttachmentToken(Path.GetFileNameWithoutExtension(castPath)).ToLowerInvariant();
        var png = new[] { token, MountVariantSuffix().Replace(token, "") }
            .SelectMany(name => name.StartsWith("jup_", StringComparison.Ordinal) ? new[] { name[4..], name } : [name])
            .SelectMany(name => new[] { "_000", "_v0", "" }.SelectMany(suffix => new[] { $"jup_icon_attachment_{name}{suffix}", $"icon_attachment_{name}{suffix}" }))
            .Select(name => Path.Combine(iconDirectory.Trim(), name + ".png")).FirstOrDefault(File.Exists);
        if (png is null) return null;
        var vtf = Path.Combine(materialsRoot, materialPath.Replace('/', Path.DirectorySeparatorChar) + ".vtf");
        Directory.CreateDirectory(Path.GetDirectoryName(vtf)!);
        CodMaterialConverter.WriteImageVtf(png, vtf, 256);
        // Same shader keys as MW Base's own icons (viper/mw/attachments/icons/*).
        File.WriteAllText(Path.ChangeExtension(vtf, ".vmt"),
            $"\"UnlitGeneric\"\n{{\n\t\"$basetexture\" \"{materialPath}\"\n\t\"$nolod\" 1\n\t\"$vertexalpha\" 1\n\t\"$vertexcolor\" 1\n\t\"$translucent\" \"1\"\n}}\n");
        return materialPath;
    }

    /// <summary>
    /// The sight's own CoD reticle: its menu preview PNG (cod20_weapons.json attachment_reticles), looked up in the reticle
    /// folder, then saluki's sibling images/reticle_composed (crisp, built from the LUI widgets by
    /// cod20_dump/tools/lj/compose_reticles.py), then images/reticle_preview. Null when the data or the PNG is missing.
    /// </summary>
    private static string? PreviewReticle(MwAttachmentPackRequest request, string materials, Dictionary<string, string> written, string? preview, List<string> log)
    {
        if (preview is null || request.ReticleDirectory.Trim().Length == 0) return null;
        if (written.TryGetValue(preview, out var known)) return known;
        var folder = Path.TrimEndingDirectorySeparator(request.ReticleDirectory.Trim());
        var images = Path.GetDirectoryName(folder) ?? folder;
        var png = new[] { folder, Path.Combine(images, "reticle_composed"), Path.Combine(images, "reticle_preview") }
            .Select(directory => Path.Combine(directory, preview + ".png")).FirstOrDefault(File.Exists);
        if (png is null)
        {
            log.Add($"-- reticle {preview}: no preview PNG, falling back to a generic reticle");
            return null;
        }
        var vtf = Path.Combine(materials, "reticles", preview + ".vtf");
        Directory.CreateDirectory(Path.GetDirectoryName(vtf)!);
        CodMaterialConverter.WriteReticlePreviewVtf(png, vtf, request.MaterialMaxSize);
        // Not additive: scope marks are black.
        File.WriteAllText(Path.ChangeExtension(vtf, ".vmt"),
            $"\"UnlitGeneric\"\n{{\n\t\"$basetexture\"\t\"{AttachmentPackMaterials}/reticles/{preview}\"\n\t\"$translucent\"\t\"1\"\n\t\"$ignorez\"\t\"1\"\n}}\n");
        return written[preview] = $"{AttachmentPackMaterials}/reticles/{preview}";
    }

    /// <summary>A reticle PNG from the saluki export when there is one, else the MW Base material for that name.</summary>
    private static string ReticleMaterial(MwAttachmentPackRequest request, string materials, Dictionary<string, string> written, string name, List<string> log)
    {
        if (written.TryGetValue(name, out var known)) return known;
        var png = request.ReticleDirectory.Trim().Length == 0 ? "" : Path.Combine(request.ReticleDirectory, name + ".png");
        string path;
        if (png.Length > 0 && File.Exists(png))
        {
            var vtf = Path.Combine(materials, "reticles", name + ".vtf");
            Directory.CreateDirectory(Path.GetDirectoryName(vtf)!);
            CodMaterialConverter.WriteImageVtf(png, vtf, request.MaterialMaxSize);
            File.WriteAllText(Path.ChangeExtension(vtf, ".vmt"),
                $"\"UnlitGeneric\"\n{{\n\t\"$basetexture\"\t\"{AttachmentPackMaterials}/reticles/{name}\"\n\t\"$translucent\"\t\"1\"\n\t\"$additive\"\t\"1\"\n\t\"$ignorez\"\t\"1\"\n}}\n");
            path = $"{AttachmentPackMaterials}/reticles/{name}";
        }
        else
        {
            path = MwBaseReticles.Contains(name) ? $"viper/mw/reticles/{name}" : "viper/shared/reticles/aimpoint_reticle";
            log.Add($"-- reticle {name}: no PNG in the reticle folder, using {path}");
        }
        written[name] = path;
        return path;
    }

    /// <summary>
    /// Copy of the CAST the engine can compile: the material name loses CoD's "m\" folder, the scope lens keeps one name MW Base
    /// ships, and texture paths are made absolute so the copy can live in the staging folder.
    /// </summary>
    private static string Stage(string cast, string destination, IReadOnlyDictionary<string, string>? rename = null)
    {
        var package = CastReader.Load(cast);
        var folder = Path.GetDirectoryName(Path.GetFullPath(cast))!;
        var nodes = package.RootNodes.SelectMany(DescendantsAndSelf).ToArray();
        foreach (var material in nodes.OfType<MaterialNode>().Where(material => !string.IsNullOrWhiteSpace(material.Name)))
        {
            var name = material.Name.Split('\\', '/')[^1];
            if (ScopeLensMaterial().IsMatch(name) && !name.Contains("reflection", StringComparison.OrdinalIgnoreCase)) name = "ads_scope_lens_1";
            material.AddString("n", rename?.GetValueOrDefault(name) ?? name);
        }
        foreach (var file in nodes.OfType<FileNode>().Where(file => file.Path.Length > 0 && !Path.IsPathRooted(file.Path)))
            file.AddString("p", Path.GetFullPath(Path.Combine(folder, file.Path.Replace('\\', Path.DirectorySeparatorChar))));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        CastWriter.Save(destination, package);
        return destination;
    }

    private const string StatsPrefix = "local BaseClass = GetAttachmentBaseClass(ATTACHMENT.Base)\nfunction ATTACHMENT:Stats(weapon)\n    BaseClass.Stats(self, weapon)\n";

    private const string MuzzleBodygroups =
        "ATTACHMENT.AttachmentBodygroups = {\n    [\"tag_tip\"] = 1,\n    [\"muzzle\"] = 1,\n    [\"tip\"] = 1\n}\n"
        + "ATTACHMENT.Bodygroups = {\n    [\"tag_tip\"] = 1,\n    [\"muzzle\"] = 1,\n    [\"tip\"] = 1\n}\n";

    private const string SilencerStats = """
            weapon.Animations.Ads_In.Fps = weapon.Animations.Ads_In.Fps * 0.95
            weapon.Animations.Ads_Out.Fps = weapon.Animations.Ads_Out.Fps * 0.95
            weapon.Animations.Holster.Fps = weapon.Animations.Holster.Fps * 0.94
            weapon.Animations.Draw.Fps = weapon.Animations.Draw.Fps * 0.94
            weapon.Bullet.EffectiveRange = weapon.Bullet.EffectiveRange * 1.07
            weapon.Bullet.DropOffStartRange = weapon.Bullet.DropOffStartRange * 1.07
            weapon:doSuppressorStats()
        """;

    private const string CompensatorStats = """
            weapon.Cone.Ads = weapon.Cone.Ads * 0.9
            weapon.Cone.Hip = weapon.Cone.Hip * 0.9
            if weapon.Cone.TacStance then
                weapon.Cone.TacStance = weapon.Cone.TacStance * 0.9
            end
            weapon.ParticleEffects.MuzzleFlash = "mwb_compensator_2"
        """;

    private const string BrakeStats = """
            weapon.Recoil.Horizontal[1] = weapon.Recoil.Horizontal[1] * 0.98
            weapon.Recoil.Horizontal[2] = weapon.Recoil.Horizontal[2] * 0.98
            weapon.Recoil.Vertical[1] = weapon.Recoil.Vertical[1] * 0.98
            weapon.Recoil.Vertical[2] = weapon.Recoil.Vertical[2] * 0.98
            weapon.ParticleEffects.MuzzleFlash = "mwb_muzzlebrake_2"
        """;

    private const string BreacherStats = """
            weapon.Animations.Melee_Hit.Damage = weapon.Animations.Melee_Hit.Damage * 1.25
        """;

    private const string FlashHiderStats = """
            weapon.ParticleEffects.MuzzleFlash = "mwb_flashhider_3"
        """;

    private const string AngledGripStats = """
            weapon.Animations.Ads_In.Fps = weapon.Animations.Ads_In.Fps * 1.08
            weapon.Animations.Ads_Out.Fps = weapon.Animations.Ads_Out.Fps * 1.08
            weapon.Animations.Holster.Fps = weapon.Animations.Holster.Fps * 1.08
            weapon.Animations.Draw.Fps = weapon.Animations.Draw.Fps * 1.08
            weapon.Recoil.Horizontal[1] = self:ChangeRecoil(weapon.Recoil.Horizontal[1], 1.02)
            weapon.Recoil.Horizontal[2] = self:ChangeRecoil(weapon.Recoil.Horizontal[2], 1.02)
            weapon.Recoil.Vertical[1] = self:ChangeRecoil(weapon.Recoil.Vertical[1], 1.02)
            weapon.Recoil.Vertical[2] = self:ChangeRecoil(weapon.Recoil.Vertical[2], 1.02)
            weapon.Zoom.IdleSway = weapon.Zoom.IdleSway * 0.8
        """;

    private const string VerticalGripStats = """
            weapon.Recoil.Vertical[1] = self:ChangeRecoil(weapon.Recoil.Vertical[1], 0.95)
            weapon.Recoil.Vertical[2] = self:ChangeRecoil(weapon.Recoil.Vertical[2], 0.95)
            weapon.Zoom.IdleSway = weapon.Zoom.IdleSway * 0.9
            weapon.HoldType = "RifleWithVerticalGrip"
        """;

    // Copy of MW Base attachment_reflex_02_glass: the sight glass reads the gun's envmap and is shaded like an optic window.
    private const string GlassVmt = """
        VertexLitGeneric
        {
        	"$basetexture"			 	"viper/shared/optics/kobra_lense"
        	"$bumpmap" 				"viper/shared/optics/kobra_lense_nm"
        	"$translucent"				"1"
        	"$additive"				"1"

        	"$phong"					"1"
        	"$phongfresnelranges"			 "[0.76 0.921 1.25]"
        	"$phongboost"				"1"
        	"$phongexponent"				"24"

        	"$envmap" 				"env_cubemap"
        	"$basemapalphaenvmapmask"		"1"
        	"$envmapfresnel"				"-1"

        	"Proxies"
        	{
        		"MwEnvMapTint"
        		{
        			"min" "0"
        			"max" ".25"
        		}
        		"MwSight" {}
        	}
        }
        """;

    // att_optic sets this texture to a render target at runtime, so it is unlit and untinted.
    private const string ScopeLensVmt = """
        "UnlitGeneric"
        {
        	"$basetexture"			"scope_lens"
        	"$translucent" 1
        }
        """;

    // Faint blue-tinted glass without env_cubemap; still rasterises, so ARC9's holosight stencil keeps working.
    private const string Arc9ClearLensVmt = """
        "UnlitGeneric"
        {
        	"$basetexture"	"vgui/white"
        	"$color2"	"[0.04 0.06 0.07]"
        	"$additive"	1
        }
        """;

    [GeneratedRegex("^(att_)?vm_", RegexOptions.IgnoreCase)] private static partial Regex ViewModelAttachmentPrefix();
    [GeneratedRegex("_(v\\d+|\\d{3})$")] private static partial Regex AttachmentTokenSuffix();
    [GeneratedRegex("^ads_scope_lens")] private static partial Regex ScopeLensMaterial();
    [GeneratedRegex("_(tall|pstl)$")] private static partial Regex MountVariantSuffix();
    [GeneratedRegex("^ATTACHMENT\\.Name = .*\n", RegexOptions.Multiline)] private static partial Regex IconLine();
}
