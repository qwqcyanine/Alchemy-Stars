using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Alchemist.UI;
using Cast.NET;
using Cast.NET.Nodes;
using RedFox.Graphics3D;
using RedFox.Graphics3D.Skeletal;
using static Alchemist.UI.CastNodeTraversal;

namespace AlchemyStars.Engine;

/// <param name="Parts">View hands, weapon receiver and attachments. Attachments sharing a root bone form one bodygroup; the first is the default.</param>
/// <param name="AnimationDirectory">Weapon CAST clips (idle, raise, reload…).</param>
/// <param name="LocomotionDirectory">Optional shared folder searched first for jog/walk/sprint/ADS layers.</param>
/// <param name="ModelPath">Model path without prefix/extension, e.g. "weapons/mw3/pmike" → weapons/mw3/v_pmike.mdl + w_pmike.mdl. Blank = automatic.</param>
public sealed record QcProjectRequest(
    IReadOnlyList<ModelPartSpec> Parts,
    string AnimationDirectory,
    string LocomotionDirectory,
    string OutputDirectory,
    string ModelPath = "",
    string MaterialsPath = "",
    QcTarget Target = QcTarget.Both,
    QcLocomotionScale? Scale = null,
    bool ConvertMaterials = false,
    int MaterialMaxSize = 2048,
    bool IncludeHands = false);

/// <summary>Amplitude (0..1.5) of shared-locomotion weapon/camera motion per loop family.</summary>
public sealed record QcLocomotionScale(float Walk = 1, float Jog = 1, float Sprint = 0.5f, float SuperSprint = 0.5f)
{
    public float For(string layer) => layer switch
    {
        _ when layer.StartsWith("walk", StringComparison.Ordinal) => Walk,
        _ when layer.StartsWith("jog", StringComparison.Ordinal) => Jog,
        _ when layer.StartsWith("super_sprint", StringComparison.Ordinal) => SuperSprint,
        _ when layer.StartsWith("sprint", StringComparison.Ordinal) => Sprint,
        _ => 1,
    };
}

/// <summary>Which weapon base the QC targets. Both = MW Base layers/events plus ARC9 alias sequences.</summary>
public enum QcTarget { Both, MwBase, Arc9 }

public sealed record QcProjectResult(
    string ViewModelQc,
    string WorldModelQc,
    IReadOnlyList<string> Sequences,
    IReadOnlyList<string> Layers,
    IReadOnlyList<string> MissingLayers,
    IReadOnlyList<string>? AttachmentQcs = null,
    string? LuaDirectory = null,
    IReadOnlyList<string>? MaterialLog = null);

/// <summary>
/// Writes a Source viewmodel + worldmodel QC project (Source-Files-MW-Public layout) with SMDs from CAST assets.
/// Names follow MW Base (attachments muzzle/shell_eject/camera, pose-parameter layers, 5004/9011/9021/9031 events);
/// attachment order (1 muzzle, 2 shell, 4 camera) and alias sequences (ready, enter_sights…) match ARC9 defaults.
/// </summary>
public static partial class QcProjectGenerator
{
    private const float Inches = 1 / 2.54f;
    private const float Fps = WorkspacePaths.StandardAnimationFramerate;
    private const string HandBone = "ValveBiped.Bip01_R_Hand";
    private static readonly IkChainSpec LeftIk = new("j_shoulder_le", "j_elbow_le", "j_wrist_le", "tag_ik_loc_le");
    private static readonly IkChainSpec RightIk = new("j_shoulder_ri", "j_elbow_ri", "j_wrist_ri", "tag_ik_loc_ri");

    // MW Base pose-parameter layers. Loops subtract their own first frame; poses are layered over idle and subtract idle.
    private static readonly (string Layer, string Source, bool Loop)[] LayerSources =
    [
        ("aim_offset", "ads_additive", false),
        ("jog_loop", "jog_loop", true),
        ("jog_offset", "jog_offset_additive", false),
        ("walk_loop", "walk_loop", true),
        ("walk_offset", "walk_offset_additive", false),
        ("sprint_loop", "sprint_loop", true),
        ("sprint_offset", "sprint_offset_additive", false),
        ("super_sprint_loop", "super_sprint_loop", true),
        ("grip_ang_offset", "pose_l_gripang", false),
        ("grip_vert_offset", "pose_l_gripvert", false),
        ("empty_offset", "idle_empty", false),
    ];

    // MW Base plays these by name (Sprint_In/Sprint_Out are required: GetAnimLength errors without them).
    // Shared clips go from rest to the sprint pose, so they bake as weapon-only deltas over idle.
    private static readonly (string Sequence, string Source)[] Transitions =
    [
        ("sprint_in", "walk_to_sprint"), ("sprint_out", "sprint_to_walk"), ("jog_out", "jog_to_walk"),
        ("super_sprint_in", "walk_to_super_sprint"), ("super_sprint_out", "super_sprint_to_walk"),
    ];

    // MW Base layer -> pose parameter ARC9-COD2019 drives (CustomPoseParamsHandler: blend_move jog, blend_walk walk, empty).
    private static readonly Dictionary<string, string> Arc9Params = new()
    {
        ["jog_loop"] = "blend_move", ["jog_offset"] = "blend_move", ["walk_loop"] = "blend_walk", ["walk_offset"] = "blend_walk",
        ["empty_offset"] = "empty",
    };

    private static void LayerSequence(StringBuilder text, string layer, string name, string param) =>
        text.AppendLine($"$sequence \"{name}\" {{").AppendLine($"\ta_{layer}_zero").AppendLine($"\ta_{layer}")
            .AppendLine("\tblendwidth 2").AppendLine($"\tblend {param} 0 1").AppendLine("\tdelta").AppendLine("\trealtime").AppendLine("}");

    // ARC9 picks sequences by name automatically; these duplicate MW Base names under ARC9's.
    // No enter_sights/exit_sights: CoD's ads_up only moves tag_ads, so ARC9 would snap back to hip idle after it.
    // ARC9 ADS is procedural instead (IronSights solved by SolveSight).
    private static readonly (string Arc9, string Source)[] Arc9Aliases =
    [
        ("ready", "draw_first"),
        ("fire_empty", "fire_last"), ("bash", "melee_hit_01"), ("enter_sprint", "sprint_in"), ("exit_sprint", "sprint_out"),
    ];

    // Attachments in ARC9 QCA order (1 muzzle, 2 shell, 3 mag, 4 camera); MW Base looks them up by name.
    private static readonly (string Name, string[] Bones)[] AttachmentSlots =
    [
        ("muzzle", ["tag_flash"]), ("shell_eject", ["tag_brass"]), ("mag", ["j_mag1", "tag_mag_attach"]),
        ("camera", ["tag_camera", "tag_playerhelmet"]), ("reflex", ["tag_reflex"]), ("holo", ["tag_holo"]),
        ("scope", ["tag_scope"]), ("acog", ["tag_acog_2"]), ("hybrid", ["tag_hybrid"]), ("align", ["tag_align_gun"]),
    ];

    private static readonly string[] SkippedClips = ["branched", "transition", "additive", "pose", "settle", "non_loop", "override", "retracted", "hybrid", "nvg"];
    private static readonly string[] SequenceClips = ["idle", "raise", "drop", "fire", "reload", "inspect", "melee", "selectsemi", "ads_up", "ads_down"];

    public static QcProjectResult Generate(QcProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hands = request.Parts.SingleOrDefault(part => part.Kind == ModelPartKind.ViewHands)
            ?? throw new InvalidDataException("需要一个手臂模型 / Exactly one view-hands model is required.");
        var weapon = request.Parts.FirstOrDefault(part => part.Kind == ModelPartKind.Weapon)
            ?? throw new InvalidDataException("需要武器主体模型 / A weapon receiver model is required.");
        if (!Directory.Exists(request.AnimationDirectory))
            throw new DirectoryNotFoundException(request.AnimationDirectory);
        var weaponStem = PartStem(weapon.FilePath);
        var groups = request.Parts.Where(part => part.Kind == ModelPartKind.Attachment)
            .GroupBy(part => RootBone(part.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(part => (Part: part, Token: Token(PartStem(part.FilePath), weaponStem))).ToArray())
            .ToArray();

        var output = Path.GetFullPath(request.OutputDirectory);
        foreach (var folder in new[] { "", "anims", "meshes", "additives", "macros" })
            Directory.CreateDirectory(Path.Combine(output, folder));
        var staging = Path.Combine(Path.GetTempPath(), "AlchemyStarsQc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var defaults = new[] { hands, weapon }.Concat(groups.Select(group => group[0].Part)).ToArray();
            var plan = Plan(defaults);
            var bones = plan.Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var receiver = Token(weaponStem, weaponStem);
            // MW Base: v_/w_ carry only the receiver; attachments become separate bonemerged models.
            var separate = request.Target == QcTarget.MwBase;
            var useMw = request.Target != QcTarget.Arc9;
            var useArc9 = request.Target != QcTarget.MwBase;
            // ARC9-COD2019 drives aim_blend (0 hip, 1 ADS); MW Base drives aim_offset. A blend can follow only one.
            var aimParam = request.Target == QcTarget.Arc9 ? "aim_blend" : "aim_offset";
            var (viewMeshes, worldMeshes, worldPlan) = WriteMeshes(output, staging, hands, weapon, receiver, groups, separate, request.IncludeHands);

            // Animations
            var clips = Clips(request.AnimationDirectory);
            var idle = clips.GetValueOrDefault("idle") ?? throw new InvalidDataException("动画目录缺少 idle / Animation folder has no idle clip.");
            var lengths = new Dictionary<string, int> { ["idle"] = Bake(plan, idle, Path.Combine(output, "anims", "idle.smd")) };
            // IronSights only trims the CoD ADS hold pose (aim_offset) onto screen centre.
            var checkpoints = new Dictionary<string, int?>();
            var basic = new StringBuilder();
            var sequences = new List<string>();
            var sequenceSources = new Dictionary<string, string>();
            foreach (var group in clips.Where(clip => SequenceName(clip.Key) is not null)
                .GroupBy(clip => clip.Key.StartsWith("ads_", StringComparison.Ordinal) ? clip.Key : string.Join('_', clip.Key.Split('_').Where(token => token != "ads")))
                .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var hip = group.FirstOrDefault(clip => clip.Key == group.Key);
                if (hip.Value is null) continue;
                var ads = group.FirstOrDefault(clip => clip.Key != hip.Key);
                var name = SequenceName(group.Key)!;
                if (hip.Key != "idle") lengths[name] = Bake(plan, hip.Value, Path.Combine(output, "anims", hip.Key + ".smd"), basePose: idle);
                if (LastCheckpoint(hip.Value) is { } checkpoint) checkpoints[name] = checkpoint;
                if (ads.Value is not null) Bake(plan, ads.Value, Path.Combine(output, "anims", ads.Key + ".smd"), basePose: idle);
                var body = new StringBuilder();
                body.AppendLine($"\t\"anims/{hip.Key}.smd\"");
                if (ads.Value is not null)
                {
                    body.AppendLine($"\t\"anims/{ads.Key}.smd\"");
                    body.AppendLine("\t$include \"macros/aim_blend.qc\"");
                }
                foreach (var line in Events(hip.Value, name)) body.AppendLine("\t" + line);
                body.AppendLine("\t$include \"macros/layers.qc\"");
                body.AppendLine("\t$include \"macros/ik.qc\"");
                body.AppendLine($"\tfps {Number(Fps)}");
                body.AppendLine(name == "idle" ? "\tloop" : "\tsnap");
                basic.AppendLine($"$sequence \"{name}\" {{").Append(body).AppendLine("}").AppendLine();
                sequences.Add(name);
                sequenceSources[name] = body.ToString();
            }

            // Pose-parameter layers (MW Base)
            var locomotion = string.IsNullOrWhiteSpace(request.LocomotionDirectory) ? [] : Clips(request.LocomotionDirectory);
            File.WriteAllText(Path.Combine(output, "additives", "_reference.qc"),
                "// Idle reference subtracted by every pose layer\n$animation a_idle_ref \"anims/idle.smd\"\n");
            var layers = new List<string>();
            var missing = new List<string>();
            foreach (var (layer, source, loop) in LayerSources)
            {
                // Weapon clips first; shared locomotion (another gun's rig) only fills gaps.
                var own = Find(clips, source);
                var file = own ?? Find(locomotion, source);
                if (file is null) { missing.Add(layer); continue; }
                var smd = Path.Combine(output, "anims", layer + ".smd");
                var text = new StringBuilder();
                if (own is null)
                {
                    // A shared clip's arms grip a different gun. Keep only its weapon/camera motion as a delta,
                    // layer it on this weapon's idle and let the baked IK keep the hands on tag_ik_loc_*.
                    var delta = WeaponOnlyDelta(file, loop, staging, Math.Clamp((request.Scale ?? new()).For(layer), 0, 1.5f));
                    var frames = Bake(plan, idle, smd, [new AnimationLayerSpec(delta, AnimationLayerKind.Additive)]);
                    if (loop)
                    {
                        text.AppendLine($"$animation a_{layer}_ref \"anims/{layer}.smd\"");
                        text.AppendLine($"$animation a_{layer}_zero \"anims/{HeldIdle(output, layer, frames)}\" fps {Number(Fps)} subtract a_idle_ref 0");
                        text.AppendLine($"$animation a_{layer} \"anims/{layer}.smd\" fps {Number(Fps)} subtract a_{layer}_ref 0");
                    }
                    else
                    {
                        text.AppendLine($"$animation a_{layer}_zero \"anims/idle.smd\" subtract a_idle_ref 0");
                        text.AppendLine($"$animation a_{layer} \"anims/{layer}.smd\" subtract a_idle_ref 0");
                    }
                }
                else if (loop)
                {
                    var frames = Bake(plan, file, smd);
                    text.AppendLine($"$animation a_{layer}_ref \"anims/{layer}.smd\"");
                    text.AppendLine($"$animation a_{layer}_zero \"anims/{HeldIdle(output, layer, frames)}\" fps {Number(Fps)} subtract a_idle_ref 0");
                    text.AppendLine($"$animation a_{layer} \"anims/{layer}.smd\" fps {Number(Fps)} subtract a_{layer}_ref 0");
                }
                else
                {
                    // pose_l_* is CoD's left-hand pose layer: bake it like the converter's LeftHandPoseFile (additive over idle, IK on).
                    var grip = layer.StartsWith("grip_", StringComparison.Ordinal);
                    if (grip) Bake(plan, idle, smd, [GripTarget(plan, idle, file, staging)], leftHandPose: file);
                    else Bake(plan, idle, smd, [new AnimationLayerSpec(file, AnimationLayerKind.Normal)]);
                    text.AppendLine($"$animation a_{layer}_zero \"anims/idle.smd\" subtract a_idle_ref 0");
                    text.AppendLine($"$animation a_{layer} \"anims/{layer}.smd\" subtract a_idle_ref 0");
                }
                text.AppendLine();
                if (useMw) LayerSequence(text, layer, layer, layer);
                if (useArc9 && Arc9Params.TryGetValue(layer, out var arc9Param)) LayerSequence(text, layer, "a9_" + layer, arc9Param);
                File.WriteAllText(Path.Combine(output, "additives", layer + ".qc"), text.ToString());
                layers.Add(layer);
            }

            // sprint_loop and super_sprint_loop are both driven by MW Base's sprint delta: never stack them.
            var commonLayers = layers.Where(layer => !layer.StartsWith("super_sprint", StringComparison.Ordinal)).ToArray();
            foreach (var (sequence, source) in Transitions)
            {
                var own = Find(clips, source);
                var file = own ?? Find(locomotion, source);
                if (file is null) continue;
                var smd = Path.Combine(output, "anims", sequence + ".smd");
                // Transitions carry the full sprint pose (MW Base drives no sprint_offset); the locomotion scale only tames loops.
                lengths[sequence] = own is not null ? Bake(plan, own, smd, basePose: idle)
                    : Bake(plan, idle, smd, [new AnimationLayerSpec(WeaponOnlyDelta(file, false, staging, 1), AnimationLayerKind.Additive)]);
                var body = new StringBuilder($"\t\"anims/{sequence}.smd\"\n");
                body.AppendLine(sequence.StartsWith("super_sprint", StringComparison.Ordinal) ? "\t$include \"macros/layers_super.qc\"" : "\t$include \"macros/layers.qc\"");
                body.AppendLine("\t$include \"macros/ik.qc\"").AppendLine($"\tfps {Number(Fps)}").AppendLine("\tfadein 0.2\n\tfadeout 0.2");
                basic.AppendLine($"$sequence \"{sequence}\" {{").Append(body).AppendLine("}").AppendLine();
                sequences.Add(sequence);
                sequenceSources[sequence] = body.ToString();
            }

            // ARC9 aliases. idle_sprint holds idle (as long as the loop) with the sprint offset and loop at full weight:
            // ARC9 drives no sprint pose parameter (ARC9-COD2019's mp5 does the same with sprint/sprint_loop/sprint_offset).
            var arc9 = new StringBuilder("// ARC9 sequence names (MW Base ignores these)\n");
            foreach (var (alias, source) in Arc9Aliases)
                if (sequenceSources.TryGetValue(source, out var body))
                    arc9.AppendLine($"$sequence \"{alias}\" {{").Append(body).AppendLine("}").AppendLine();
            if (layers.Contains("sprint_loop"))
            {
                arc9.AppendLine("$sequence \"a9_sprint_loop_full\" {\n\ta_sprint_loop\n\tdelta\n\tloop\n}\n");
                if (layers.Contains("sprint_offset")) arc9.AppendLine("$sequence \"a9_sprint_offset_full\" {\n\ta_sprint_offset\n\tdelta\n}\n");
                arc9.AppendLine($"$sequence \"idle_sprint\" {{\n\t\"anims/sprint_loop_idle.smd\"\n"
                    + (layers.Contains("sprint_offset") ? "\taddlayer \"a9_sprint_offset_full\"\n" : "")
                    + $"\taddlayer \"a9_sprint_loop_full\"\n\t$include \"macros/ik.qc\"\n\tfps {Number(Fps)}\n\tloop\n}}");
            }
            if (useArc9) File.WriteAllText(Path.Combine(output, "animations_arc9.qc"), arc9.ToString());

            File.WriteAllText(Path.Combine(output, "animations_basic.qc"), basic.ToString());
            var arc9Layers = useArc9 ? layers.Where(Arc9Params.ContainsKey).Select(layer => "a9_" + layer).ToArray() : [];
            string AddLayers(IEnumerable<string> names) => string.Concat(names.Select(layer => $"addlayer \"{layer}\"\n"));
            File.WriteAllText(Path.Combine(output, "macros", "layers.qc"), AddLayers((useMw ? commonLayers : []).Concat(arc9Layers)));
            File.WriteAllText(Path.Combine(output, "macros", "layers_super.qc"), AddLayers((useMw
                ? commonLayers.Where(layer => layer != "sprint_loop").Append("super_sprint_loop").Where(layers.Contains) : []).Concat(arc9Layers)));
            File.WriteAllText(Path.Combine(output, "macros", "aim_blend.qc"), $"blend {aimParam} 0 1\nblendwidth 2\n");
            File.WriteAllText(Path.Combine(output, "macros", "ik.qc"), string.Concat(new[] { RightIk, LeftIk }
                .Where(chain => bones.Contains(chain.TargetBone))
                .Select(chain => $"ikrule {(chain == RightIk ? "rhand" : "lhand")} touch \"{chain.TargetBone}\" usesource\n")));
            File.WriteAllText(Path.Combine(output, "meshes.qc"), viewMeshes);
            File.WriteAllText(Path.Combine(output, "meshes_worldmodel.qc"), worldMeshes);
            File.WriteAllText(Path.Combine(output, "bones.qci"), DefineBones(plan.Skeleton, null));
            File.WriteAllText(Path.Combine(output, "bones_worldmodel.qci"), DefineBones(worldPlan.Skeleton, HandBone));
            File.WriteAllText(Path.Combine(output, "bones_chands.qci"), ValveBipedBones());
            File.WriteAllText(Path.Combine(output, "attachments.qc"), Attachments(plan.Skeleton));
            File.WriteAllText(Path.Combine(output, "attachments_worldmodel.qc"), Attachments(worldPlan.Skeleton));

            var (modelDirectory, weaponName) = ModelPath(request.ModelPath, weaponStem, groups);
            var materials = string.IsNullOrWhiteSpace(request.MaterialsPath) ? $"models/weapons/{weaponName}/" : request.MaterialsPath.Trim();
            var common = $"$surfaceprop \"weapon\"\n$contents \"solid\"\n$illumposition 0 0 0\n$mostlyopaque\n$cdmaterials \"{materials}\"\n";
            // VMT/VTF land in <output>/materials/<cdmaterials>, ready to copy into the addon next to models/ and lua/.
            var materialLog = request.ConvertMaterials
                ? CodMaterialConverter.Convert(request.Parts.Select(part => part.FilePath), Path.Combine(output, "materials", materials.Replace('/', Path.DirectorySeparatorChar)),
                    materials, request.Target == QcTarget.MwBase ? VmtEnvmapStyle.MwBase : request.Target == QcTarget.Arc9 ? VmtEnvmapStyle.Arc9 : VmtEnvmapStyle.Cubemap,
                    request.MaterialMaxSize)
                : null;

            var view = new StringBuilder($"// Generated by Alchemy Stars from CAST assets (target: {request.Target})\n");
            view.AppendLine($"$modelname \"{modelDirectory}v_{weaponName}.mdl\"");
            view.AppendLine("$origin 0 0 0 -90");
            view.AppendLine("$include \"meshes.qc\"\n$include \"bones.qci\"\n$include \"bones_chands.qci\"\n$include \"attachments.qc\"");
            view.Append(common);
            view.AppendLine("$ikchain rhand \"j_wrist_ri\" knee 0.707 0.707 0");
            view.AppendLine("$ikchain lhand \"j_wrist_le\" knee 0.707 0.707 0");
            // aim param is always declared: hip/ADS sequences blend on it.
            foreach (var param in (useMw ? layers : []).Concat(arc9Layers.Select(layer => Arc9Params[layer[3..]])).Append(aimParam).Distinct())
                view.AppendLine($"$poseparameter \"{param}\" 0 1 loop 0");
            view.AppendLine("$include \"animations_basic.qc\"");
            view.AppendLine("$include \"additives/_reference.qc\"");
            foreach (var layer in layers) view.AppendLine($"$include \"additives/{layer}.qc\"");
            if (useArc9) view.AppendLine("$include \"animations_arc9.qc\"");
            var viewQc = Path.Combine(output, "weapon_viewmodel.qc");
            File.WriteAllText(viewQc, view.ToString());

            var world = new StringBuilder("// Generated by Alchemy Stars. The weapon root sits on ValveBiped.Bip01_R_Hand;\n// align it with SWEP.WorldModelOffset (ARC9) or by editing the root $definebone.\n");
            world.AppendLine($"$modelname \"{modelDirectory}w_{weaponName}.mdl\"");
            world.AppendLine("$include \"meshes_worldmodel.qc\"\n$include \"bones_worldmodel.qci\"\n$include \"attachments_worldmodel.qc\"");
            world.Append(common);
            world.AppendLine($"$sequence \"idle\" \"meshes/w_{receiver}.smd\" fps 30");
            world.AppendLine($"$collisionmodel \"meshes/w_{receiver}.smd\" {{\n\t$mass 5\n}}");
            var worldQc = Path.Combine(output, "weapon_worldmodel.qc");
            File.WriteAllText(worldQc, world.ToString());

            if (!separate)
            {
                // ARC9 / Both: one combined model, bodygroup attachments driven by AttachmentElements.
                var arc9Sequences = sequences.ToDictionary(name => name, name => (lengths.GetValueOrDefault(name, 1), checkpoints.GetValueOrDefault(name), sequenceSources[name]));
                if (useArc9 && layers.Contains("sprint_loop")) arc9Sequences["idle_sprint"] = (lengths["idle"], null, "");
                var adsUp = Path.Combine(output, "anims", "ads_up.smd");
                var sight = File.Exists(adsUp) ? SolveSight(Path.Combine(output, "anims", "idle.smd"), adsUp) : null;
                var arc9Lua = Arc9Lua.Write(output, new(weaponName, $"models/{modelDirectory}v_{weaponName}.mdl", $"models/{modelDirectory}w_{weaponName}.mdl",
                    groups.Select(group => group.Select(item => (item.Token, RootBone(item.Part.FilePath))).ToArray()).ToArray(), arc9Sequences, sight));
                return new(viewQc, worldQc, sequences, layers, missing, LuaDirectory: arc9Lua, MaterialLog: materialLog);
            }
            var attachmentDirectory = $"{modelDirectory}attachments/{weaponName}/";
            var attachmentQcs = WriteAttachmentModels(output, staging, groups, attachmentDirectory, common);
            var lua = MwBaseLua.Write(output, new(weaponName, $"models/{modelDirectory}v_{weaponName}.mdl", $"models/{modelDirectory}w_{weaponName}.mdl",
                $"models/{attachmentDirectory}", groups.Select(group => group.Select(item => (item.Token, RootBone(item.Part.FilePath))).ToArray()).ToArray(),
                sequences.Select(name => (name, lengths.GetValueOrDefault(name, 1), checkpoints.GetValueOrDefault(name))).ToArray(),
                layers.Where(layer => layer.StartsWith("grip_", StringComparison.Ordinal)).ToArray()));
            return new(viewQc, worldQc, sequences, layers, missing, attachmentQcs, lua, materialLog);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Maps a COD clip suffix to a Source sequence name, or null when it is not a playable viewmodel sequence.</summary>
    public static string? SequenceName(string clip)
    {
        if (SkippedClips.Any(clip.Contains) || !SequenceClips.Any(prefix => clip.StartsWith(prefix, StringComparison.Ordinal)))
            return null;
        return StartAlias().Replace(clip, match => match.Value switch
        {
            "raise" => "draw",
            "drop" => "holster",
            "ads_up" => "ads_in",
            "ads_down" => "ads_out",
            _ => "semi",
        });
    }

    /// <summary>Converts one CAST notetrack to a MW Base QC event line, or null when Source has no equivalent.</summary>
    public static string? EventLine(string note, int frame, string sequence, ref bool checkpointSeen)
    {
        note = note.Trim();
        if (note.StartsWith("ps_", StringComparison.OrdinalIgnoreCase)) note = note[3..];
        if (note.Contains("wfoly", StringComparison.OrdinalIgnoreCase))
            return $"{{ event 5004 {frame} \"{note}\" }}";
        var hand = IkHand().Match(note);
        if (hand.Success)
            return $"{{ event {(hand.Groups[2].Value == "left" ? 9011 : 9021)} {frame} \"{(hand.Groups[1].Value == "in" ? 1 : 0)}\" }}";
        if (Rattle().Match(note) is { Success: true } rattle)
            return $"{{ event 5004 {frame} \"Viewmodel.{char.ToUpperInvariant(rattle.Groups[1].Value[0])}{rattle.Groups[1].Value[1..]}\" }}";
        if (note == "reload checkpoint" && sequence.StartsWith("reload", StringComparison.Ordinal) && !checkpointSeen)
        {
            checkpointSeen = true;
            return $"{{ event 9031 {frame} \"ResetBullets\" }}";
        }
        return null;
    }

    /// <summary>vm_jup_jp03_sm_pmike_mag_30_9p_000 relative to vm_jup_jp03_sm_pmike_rec_000 → mag_30_9p.</summary>
    public static string Token(string stem, string weaponStem)
    {
        var common = stem.Zip(weaponStem).TakeWhile(pair => pair.First == pair.Second).Count();
        var cut = stem.LastIndexOf('_', Math.Max(0, Math.Min(common, stem.Length - 1))) + 1;
        if (stem == weaponStem) cut = stem.LastIndexOf('_', stem.LastIndexOf('_') - 1) + 1;
        var token = VariantSuffix().Replace(stem[cut..], "");
        return string.IsNullOrEmpty(token) ? stem : token;
    }

    private static IEnumerable<string> Events(string file, string sequence)
    {
        var animation = CastReader.Load(file).RootNodes.SelectMany(DescendantsAndSelf).OfType<AnimationNode>().FirstOrDefault();
        var lines = new List<(int Frame, string Line)>();
        if (sequence.StartsWith("fire", StringComparison.Ordinal))
        {
            lines.Add((0, "{ event 9001 0 \"MuzzleFlash muzzle\" }"));
            lines.Add((0, "{ event 6001 0 \"shell_eject\" }"));
        }
        if (animation is not null)
        {
            var scale = animation.Framerate > 0 ? Fps / animation.Framerate : 1;
            var checkpoint = false;
            foreach (var (note, frame) in animation.NotificationTracks
                .SelectMany(track => Frames(track.KeyFrameBuffer).Select(value => (track.Name, Frame: (int)MathF.Round(value * scale))))
                .OrderBy(item => item.Frame))
                if (EventLine(note, frame, sequence, ref checkpoint) is { } line) lines.Add((frame, line));
        }
        return lines.OrderBy(item => item.Frame).Select(item => item.Line);
    }

    private static IEnumerable<float> Frames(CastProperty property) => property switch
    {
        CastArrayProperty<byte> values => values.Values.Select(value => (float)value),
        CastArrayProperty<ushort> values => values.Values.Select(value => (float)value),
        CastArrayProperty<uint> values => values.Values.Select(value => (float)value),
        CastArrayProperty<float> values => values.Values,
        _ => [],
    };

    private static SkeletonMergePlan Plan(IEnumerable<ModelPartSpec> parts) =>
        SkeletonMergePlan.Build(parts.Select(AnimationExportEngine.ToCompatibilityPart), false);

    /// <summary>
    /// ARC9 IronSights reproducing CoD's ADS pose: the rigid move of tag_sight_on (in tag_view space) from the hip idle to
    /// the last frame of ads_up. ARC9 adds Pos along (right, forward, up) and then rotates about the fixed up/right/forward
    /// axes by Ang (p, y, r), i.e. Q = Rfwd(r) * Rright(y) * Rup(p); in view space (x fwd, y left, z up) that is Rx(r) Ry(-y) Rz(p).
    /// </summary>
    private static (Vector3 Pos, Vector3 Ang)? SolveSight(string idleSmd, string adsSmd)
    {
        if (SightPose(idleSmd) is not { } hip || SightPose(adsSmd) is not { } ads) return null;
        var (t, q) = Mul(ads, Inverse(hip));
        Vector3 Column(Vector3 axis) => Vector3.Transform(axis, q);
        var (x, y, z) = (Column(Vector3.UnitX), Column(Vector3.UnitY), Column(Vector3.UnitZ));
        const float Degrees = 180 / MathF.PI;
        var pitchAboutRight = -MathF.Asin(Math.Clamp(z.X, -1, 1));
        var yawAboutUp = MathF.Atan2(-y.X, x.X);
        var rollAboutForward = MathF.Atan2(-z.Y, z.Z);
        return (new Vector3(-t.Y, t.X, t.Z), new Vector3(yawAboutUp, pitchAboutRight, rollAboutForward) * Degrees);
    }

    /// <summary>tag_sight_on in tag_view space at the SMD's last frame.</summary>
    private static (Vector3 P, Quaternion Q)? SightPose(string smd)
    {
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<int, int>();
        var pose = new Dictionary<int, float[]>();
        string? mode = null;
        foreach (var line in File.ReadLines(smd))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0] is "nodes" or "skeleton") { mode = parts[0]; continue; }
            if (parts[0] == "end") { if (mode == "skeleton") break; mode = null; continue; }
            if (mode == "nodes")
            {
                var quoted = line.Split('"');
                var index = int.Parse(parts[0], CultureInfo.InvariantCulture);
                names[quoted[1]] = index;
                parents[index] = int.Parse(quoted[2], CultureInfo.InvariantCulture);
            }
            // Later frames overwrite earlier ones: the last frame wins.
            else if (mode == "skeleton" && parts[0] != "time")
                pose[int.Parse(parts[0], CultureInfo.InvariantCulture)] = parts[1..7].Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        }
        if (!names.TryGetValue("tag_view", out var view) || !names.TryGetValue("tag_sight_on", out var sight)) return null;
        var cache = new Dictionary<int, (Vector3 P, Quaternion Q)>();
        (Vector3 P, Quaternion Q) World(int bone)
        {
            if (cache.TryGetValue(bone, out var hit)) return hit;
            var v = pose[bone];
            // SMD euler: R = Rz * Ry * Rx.
            var local = (new Vector3(v[0], v[1], v[2]), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, v[5])
                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, v[4]) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, v[3]));
            return cache[bone] = parents[bone] < 0 ? local : Mul(World(parents[bone]), local);
        }
        return Mul(Inverse(World(view)), World(sight));
    }

    /// <summary>
    /// Writes anims/&lt;layer&gt;_idle.smd: the idle's first pose held for the loop's frame count. Both blend
    /// endpoints of a delta sequence need equal length, otherwise studiomdl resamples them and the layer shakes.
    /// </summary>
    private static string HeldIdle(string output, string layer, int frames)
    {
        var lines = File.ReadAllLines(Path.Combine(output, "anims", "idle.smd"));
        var start = Array.IndexOf(lines, "time 0");
        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("time ", StringComparison.Ordinal) || line == "end");
        var pose = lines[(start + 1)..end];
        var text = new StringBuilder();
        foreach (var line in lines[..start]) text.AppendLine(line);
        for (var frame = 0; frame < Math.Max(1, frames); frame++)
        {
            text.AppendLine($"time {frame}");
            foreach (var line in pose) text.AppendLine(line);
        }
        text.AppendLine("end");
        var name = layer + "_idle.smd";
        File.WriteAllText(Path.Combine(output, "anims", name), text.ToString());
        return name;
    }

    private static readonly string[] WeaponMotionBones = ["tag_weapon", "tag_camera"];

    /// <summary>
    /// Copies a shared locomotion clip keeping only tag_weapon/tag_camera as additive deltas, scaled. Loops are taken
    /// relative to their first key; offset and transition clips (*_additive, *_to_*) are already deltas from rest
    /// (mpapa5 jog_offset_additive is -8.8°, akilo47's decompiled jog_offset -8.6°), so they are kept as-is.
    /// </summary>
    private static string WeaponOnlyDelta(string file, bool loop, string staging, float scale)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var cast = CastReader.Load(file);
        foreach (var animation in cast.RootNodes.SelectMany(DescendantsAndSelf).OfType<AnimationNode>().ToArray())
            foreach (var curve in animation.Children.OfType<CurveNode>().ToArray())
            {
                if (!WeaponMotionBones.Contains(curve.NodeName, StringComparer.OrdinalIgnoreCase)) { animation.Children.Remove(curve); continue; }
                switch (curve.KeyValueBuffer)
                {
                    case CastArrayProperty<Vector4> { Values.Count: > 0 } values:
                        var inverse = loop ? Quaternion.Inverse(Quaternion.Normalize(ToQuaternion(values.Values[0]))) : Quaternion.Identity;
                        values.Values = values.Values.Select(value => { var q = Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(inverse * ToQuaternion(value)), scale); return new Vector4(q.X, q.Y, q.Z, q.W); }).ToList();
                        break;
                    case CastArrayProperty<float> { Values.Count: > 0 } values:
                        var first = loop ? values.Values[0] : 0;
                        values.Values = values.Values.Select(value => (value - first) * scale).ToList();
                        break;
                    default: animation.Children.Remove(curve); continue;
                }
                curve.Mode = "additive";
            }
        var output = Path.Combine(staging, $"{name}_weapon_delta.cast");
        CastWriter.Save(output, cast);
        return output;
    }

    private static Quaternion ToQuaternion(Vector4 value) => new(value.X, value.Y, value.Z, value.W);

    private static int Bake(SkeletonMergePlan plan, string file, string output, IReadOnlyList<AnimationLayerSpec>? layers = null, string? basePose = null,
        string leftHandPose = "")
    {
        // Camera-only clips (ads_up/down, idle_empty: just tag_ads) leave the arms in bind pose;
        // play them as a Normal layer over idle and keep only the clip's own length.
        int? trim = null;
        if (layers is null && basePose is not null && file != basePose && !AnimatesBone(file, RightIk.EndBone))
        {
            trim = (int)MathF.Ceiling(AnimationClipMetadataReader.Read(file, Fps).FrameCount);
            layers = [new AnimationLayerSpec(file, AnimationLayerKind.Normal)];
            file = basePose;
        }
        var job = new AnimationExportJob(file, Path.GetFileNameWithoutExtension(output), Path.GetDirectoryName(output)!, Fps, LeftHandPoseFile: leftHandPose, Layers: layers);
        var baked = AnimationConverter.Bake(plan, AnimationExportEngine.ToCompatibilityAnimation(job),
            AnimationExportEngine.ToIkSettings(LeftIk, ""), AnimationExportEngine.ToIkSettings(RightIk, ""), false, false);
        if (trim is { } frames)
            foreach (var target in baked.Targets)
            {
                target.TranslationFrames?.RemoveAll(key => key.Frame >= frames);
                target.RotationFrames?.RemoveAll(key => key.Frame >= frames);
                target.ScaleFrames?.RemoveAll(key => key.Frame >= frames);
            }
        SmdAnimationExporter.Save(output, baked, Inches);
        return Math.Max(1, (int)baked.GetAnimationFrameCount());
    }

    private static (Vector3 P, Quaternion Q) Mul((Vector3 P, Quaternion Q) a, (Vector3 P, Quaternion Q) b) =>
        (a.P + Vector3.Transform(b.P, a.Q), Quaternion.Normalize(a.Q * b.Q));

    private static (Vector3 P, Quaternion Q) Inverse((Vector3 P, Quaternion Q) a)
    {
        var q = Quaternion.Inverse(a.Q);
        return (-Vector3.Transform(a.P, q), q);
    }

    /// <summary>
    /// The grip pose keys tag_weapon_left (gun in left-wrist space). With the pose applied, the wrist that holds the
    /// gun there is gun * inverse(tag_weapon_left). Bakes the pose once, solves that wrist, and returns a Normal layer
    /// keying tag_ik_loc_le onto it so the baked left IK moves the hand to the foregrip (MW Base grip_*_offset).
    /// </summary>
    private static AnimationLayerSpec GripTarget(SkeletonMergePlan plan, string idle, string pose, string staging)
    {
        var job = new AnimationExportJob(idle, "grip", staging, Fps, LeftHandPoseFile: pose);
        var baked = AnimationConverter.Bake(plan, AnimationExportEngine.ToCompatibilityAnimation(job),
            AnimationExportEngine.ToIkSettings(LeftIk, ""), AnimationExportEngine.ToIkSettings(RightIk, ""), false, false);
        var tracks = baked.Targets.ToDictionary(item => item.BoneName, StringComparer.OrdinalIgnoreCase);
        (Vector3 P, Quaternion Q) Local(SkeletonBone bone) => tracks.TryGetValue(bone.Name!, out var track)
            ? (track.TranslationFrameCount > 0 ? track.SampleTranslation(0) : bone.BaseLocalTranslation,
               Quaternion.Normalize(track.RotationFrameCount > 0 ? track.SampleRotation(0) : bone.BaseLocalRotation))
            : (bone.BaseLocalTranslation, bone.BaseLocalRotation);
        (Vector3 P, Quaternion Q) World(SkeletonBone? bone) => bone is null ? (Vector3.Zero, Quaternion.Identity) : Mul(World(bone.Parent), Local(bone));
        var bones = plan.Skeleton.Bones.ToDictionary(bone => bone.Name!, StringComparer.OrdinalIgnoreCase);
        var gun = bones.GetValueOrDefault("tag_weapon") ?? throw new InvalidDataException("grip pose needs tag_weapon");
        var weaponLeft = bones.GetValueOrDefault("tag_weapon_left") ?? throw new InvalidDataException("grip pose needs tag_weapon_left");
        var target = bones[LeftIk.TargetBone];
        var wrist = Mul(World(gun), Inverse(Local(weaponLeft)));
        var (position, rotation) = Mul(Inverse(World(target.Parent)), wrist);

        var cast = CastReader.Load(idle);
        var animation = cast.RootNodes.SelectMany(DescendantsAndSelf).OfType<AnimationNode>().First();
        foreach (var child in animation.Children.ToArray()) animation.Children.Remove(child);
        void Add(string key, CastProperty value) => animation.Children.Add(new CurveNode
        {
            NodeName = target.Name!, KeyPropertyName = key, Mode = "absolute",
            KeyFrameBuffer = new CastArrayProperty<uint>([0]), KeyValueBuffer = value,
        });
        Add("rq", new CastArrayProperty<Vector4>([new(rotation.X, rotation.Y, rotation.Z, rotation.W)]));
        Add("tx", new CastArrayProperty<float>([position.X]));
        Add("ty", new CastArrayProperty<float>([position.Y]));
        Add("tz", new CastArrayProperty<float>([position.Z]));
        var output = Path.Combine(staging, Path.GetFileNameWithoutExtension(pose) + "_iktarget.cast");
        CastWriter.Save(output, cast);
        return new AnimationLayerSpec(output, AnimationLayerKind.Normal);
    }

    private static bool AnimatesBone(string file, string bone) => CastReader.Load(file).RootNodes.SelectMany(DescendantsAndSelf)
        .OfType<CurveNode>().Any(curve => string.Equals(curve.NodeName, bone, StringComparison.OrdinalIgnoreCase));

    /// <summary>Last "reload checkpoint" frame (mag seated; earlier ones mark mag-out), at 30 fps.</summary>
    private static int? LastCheckpoint(string file)
    {
        var animation = CastReader.Load(file).RootNodes.SelectMany(DescendantsAndSelf).OfType<AnimationNode>().FirstOrDefault();
        if (animation is null) return null;
        var scale = animation.Framerate > 0 ? Fps / animation.Framerate : 1;
        var frames = animation.NotificationTracks.Where(track => track.Name.Trim() == "reload checkpoint")
            .SelectMany(track => Frames(track.KeyFrameBuffer)).ToArray();
        return frames.Length == 0 ? null : (int)MathF.Round(frames.Max() * scale);
    }

    /// <summary>
    /// One studiomdl project per attachment part (MW Base bonemerges it onto the viewmodel by bone name): the part's own
    /// skeleton, its mesh, a muzzle attachment for barrels and an idle sequence.
    /// </summary>
    private static List<string> WriteAttachmentModels(string output, string staging, IReadOnlyList<(ModelPartSpec Part, string Token)[]> groups,
        string modelDirectory, string common)
    {
        var qcs = new List<string>();
        foreach (var (part, token) in groups.SelectMany(group => group))
        {
            var plan = Plan([part with { ParentBoneTag = "" }]);
            var merged = Path.Combine(staging, "att_" + token + ".cast");
            MayaCastPackage.SaveModel(merged, plan, AnimationConverter.TranslatorFactory);
            var mesh = $"meshes/att_{token}.smd";
            SmdModelExporter.Save(Path.Combine(output, mesh), merged, plan.Skeleton, Inches);
            var names = plan.Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder($"// MW Base attachment, bonemerged onto the viewmodel by bone name\n$modelname \"{modelDirectory}att_{token}.mdl\"\n");
            text.AppendLine($"$bodygroup \"{token}\"\n{{\n\tstudio \"{mesh}\"\n}}").Append(common);
            // Keep unweighted tags (tag_grip_attach, tag_flash…): grips/muzzles bonemerge onto them.
            text.Append(DefineBones(plan.Skeleton, null));
            if (new[] { "tag_flash", "tag_flash_silenced" }.FirstOrDefault(names.Contains) is { } flash)
                text.AppendLine($"$attachment \"muzzle\" \"{flash}\" 0 0 0 rotate 0 0 0");
            text.AppendLine($"$sequence \"idle\" \"{mesh}\" fps 30");
            var qc = Path.Combine(output, $"att_{token}.qc");
            File.WriteAllText(qc, text.ToString());
            qcs.Add(qc);
        }
        return qcs;
    }

    /// <summary>Writes one SMD per part for the viewmodel (with hands) and the worldmodel (weapon only), returning both meshes.qc texts.</summary>
    private static (string View, string World, SkeletonMergePlan WorldPlan) WriteMeshes(string output, string staging,
        ModelPartSpec hands, ModelPartSpec weapon, string receiver, IReadOnlyList<(ModelPartSpec Part, string Token)[]> groups, bool receiverOnly, bool includeHands)
    {
        var standalone = weapon with { ParentBoneTag = "" };
        void Export(string name, ModelPartSpec part, params ModelPartSpec[] context)
        {
            var parts = context.Append(part).ToArray();
            var plan = Plan(parts);
            var merged = Path.Combine(staging, name + ".cast");
            MayaCastPackage.SaveModel(merged, plan, AnimationConverter.TranslatorFactory);
            var skip = context.Sum(item => MeshCount(item.FilePath));
            SmdModelExporter.Save(Path.Combine(output, "meshes", name + ".smd"), merged, plan.Skeleton, Inches, skip, MeshCount(part.FilePath));
        }

        Export(receiver, weapon, hands);
        Export("w_" + receiver, standalone);
        var view = new StringBuilder($"$bodygroup \"receiver\"\n{{\n\tstudio \"meshes/{receiver}.smd\"\n}}\n");
        var world = new StringBuilder($"$bodygroup \"receiver\"\n{{\n\tstudio \"meshes/w_{receiver}.smd\"\n}}\n");
        if (includeHands)
        {
            // Preview only: games draw c_hands bonemerged on ValveBiped, so ship with this off (or set the bodygroup to blank).
            // Taken from the same hands+weapon merge as the receiver: a hands-only plan places the arms in another frame.
            var plan = Plan([hands, weapon]);
            var merged = Path.Combine(staging, "hands.cast");
            MayaCastPackage.SaveModel(merged, plan, AnimationConverter.TranslatorFactory);
            SmdModelExporter.Save(Path.Combine(output, "meshes", "hands.smd"), merged, plan.Skeleton, Inches, 0, MeshCount(hands.FilePath));
            view.Append("$bodygroup \"hands\"\n{\n\tstudio \"meshes/hands.smd\"\n\tblank\n}\n");
        }
        foreach (var group in receiverOnly ? [] : groups)
        {
            var header = $"$bodygroup \"{group.MinBy(item => item.Token.Length).Token.Split('_')[0]}\"\n{{\n";
            view.Append(header);
            world.Append(header);
            foreach (var (part, token) in group)
            {
                Export(token, part, hands, weapon);
                Export("w_" + token, part, standalone);
                view.AppendLine($"\tstudio \"meshes/{token}.smd\"");
                world.AppendLine($"\tstudio \"meshes/w_{token}.smd\"");
            }
            view.AppendLine("\tblank\n}");
            world.AppendLine("\tblank\n}");
        }
        var worldPlan = Plan(new[] { standalone }.Concat(groups.Select(group => group[0].Part)));
        return (view.ToString(), world.ToString(), worldPlan);
    }

    /// <summary>CAST bind pose as $definebone (inches, QAngle pitch=eulerY yaw=eulerZ roll=eulerX); an optional parent receives the root.</summary>
    private static string DefineBones(Skeleton skeleton, string? rootParent)
    {
        var text = new StringBuilder("// CAST bind pose in inches; rotation is QAngle pitch yaw roll\n");
        if (rootParent is not null)
            text.AppendLine($"$definebone \"{rootParent}\" \"\" 0 0 0 0 0 0 0 0 0 0 0 0");
        foreach (var bone in skeleton.Bones)
        {
            var p = bone.BaseLocalTranslation * Inches;
            var e = SmdAnimationExporter.ToEulerXyz(bone.BaseLocalRotation) * (180 / MathF.PI);
            var parent = bone.Parent?.Name ?? rootParent ?? "";
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"$definebone \"{bone.Name}\" \"{parent}\" {Number(p.X)} {Number(p.Y)} {Number(p.Z)} {Number(e.Y)} {Number(e.Z)} {Number(e.X)} 0 0 0 0 0 0"));
        }
        return text.ToString();
    }

    private static string ValveBipedBones()
    {
        using var stream = typeof(QcProjectGenerator).Assembly.GetManifestResourceStream("ValveBipedBones.qci")
            ?? throw new InvalidOperationException("ValveBipedBones.qci resource is missing.");
        var template = new StreamReader(stream).ReadToEnd();
        var names = DefineBoneName().Matches(template).Select(match => match.Groups[1].Value);
        return template.TrimEnd() + "\n\n" + string.Concat(names.Select(name => $"$bonemerge \"{name}\"\n"));
    }

    /// <summary>The first four slots always exist so ARC9 QCA indices stay 1..4; missing bones fall back to the root.</summary>
    private static string Attachments(Skeleton skeleton)
    {
        var names = skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = skeleton.Bones[0].Name;
        var text = new StringBuilder();
        for (var index = 0; index < AttachmentSlots.Length; index++)
        {
            var (name, candidates) = AttachmentSlots[index];
            var bone = candidates.FirstOrDefault(names.Contains) ?? (index < 4 ? root : null);
            if (bone is not null) text.AppendLine($"$attachment \"{name}\" \"{bone}\" 0 0 0 rotate 0 0 0");
        }
        return text.ToString();
    }

    private static (string Directory, string Name) ModelPath(string requested, string weaponStem, IReadOnlyList<(ModelPartSpec Part, string Token)[]> groups)
    {
        requested = requested.Trim().Replace('\\', '/');
        if (requested.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) requested = requested[..^4];
        if (requested.Length > 0)
        {
            var slash = requested.LastIndexOf('/');
            var name = ViewWorldPrefix().Replace(requested[(slash + 1)..], "");
            return (slash < 0 ? "" : requested[..(slash + 1)], name);
        }
        // Shared prefix of receiver and attachments: vm_jup_jp03_sm_pmike_rec_000 + _bar_000 -> jup_jp03_sm_pmike.
        var family = groups.Count == 0 ? weaponStem : PartStem(groups[0][0].Part.FilePath);
        var shared = weaponStem[..weaponStem.Zip(family).TakeWhile(pair => pair.First == pair.Second).Count()];
        var automatic = Regex.Replace(shared.Length > 0 && groups.Count > 0 ? shared[..(shared.LastIndexOf('_') + 1)] : weaponStem, "^vm_|_+$", "");
        return ("weapons/", automatic);
    }

    /// <summary>Indexes CAST clips by the suffix that follows the folder's shared file-name prefix.</summary>
    private static Dictionary<string, string> Clips(string directory)
    {
        var files = Directory.GetFiles(directory, "*.cast");
        var stems = files.Select(file => Path.GetFileNameWithoutExtension(file)).ToArray();
        var prefix = stems.Length < 2 ? "" : stems.Aggregate((a, b) => a[..a.Zip(b).TakeWhile(pair => pair.First == pair.Second).Count()]);
        prefix = prefix[..(prefix.LastIndexOf('_') + 1)];
        return files.ToDictionary(file => Path.GetFileNameWithoutExtension(file)[prefix.Length..], file => file, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Exact clip key, else the shortest key ending in _source (mixed-prefix shared folders; ubgl_jog_loop loses to jog_loop).</summary>
    private static string? Find(Dictionary<string, string> clips, string source) => clips.GetValueOrDefault(source)
        ?? clips.Where(clip => clip.Key.EndsWith("_" + source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(clip => clip.Key.Length).Select(clip => clip.Value).FirstOrDefault();

    private static string PartStem(string path) => LodSuffix().Replace(Path.GetFileNameWithoutExtension(path), "");

    private static string RootBone(string path) => CastReader.Load(path).RootNodes.SelectMany(DescendantsAndSelf).OfType<ModelNode>()
        .SelectMany(model => model.Skeleton?.Bones ?? []).FirstOrDefault(bone => bone.ParentIndex < 0)?.Name ?? "";

    private static int MeshCount(string path) => CastReader.Load(path).RootNodes.SelectMany(DescendantsAndSelf).OfType<ModelNode>()
        .Sum(model => model.Meshes.Length);

    private static string Number(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    [GeneratedRegex("_LOD\\d+$", RegexOptions.IgnoreCase)] private static partial Regex LodSuffix();
    [GeneratedRegex("_\\d+$")] private static partial Regex VariantSuffix();
    [GeneratedRegex("^[vwc]_")] private static partial Regex ViewWorldPrefix();
    [GeneratedRegex("^(raise|drop|selectsemi|ads_up|ads_down)")] private static partial Regex StartAlias();
    [GeneratedRegex("^ik_(in|out)_start_(left|right)_hand$")] private static partial Regex IkHand();
    [GeneratedRegex("^viewmodel_(small|medium|large)$")] private static partial Regex Rattle();
    [GeneratedRegex("\\$definebone \"([^\"]+)\"")] private static partial Regex DefineBoneName();
}
