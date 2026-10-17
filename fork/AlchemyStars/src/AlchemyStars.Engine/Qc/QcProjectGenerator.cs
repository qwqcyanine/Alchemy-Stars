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
    bool IncludeHands = false,
    string PrintName = "",
    string SoundDirectory = "",
    QcMwLayout MwLayout = QcMwLayout.Official);

/// <summary>
/// MW Base project layout. Official = Source-Files-MW-Public (weapon_viewmodel.qc + includes, attachments as separate
/// bonemerged models). SingleFile = third-party packs like mw2_dm_sr25 (one v_/w_ QC, attachments as bodygroups).
/// </summary>
public enum QcMwLayout { Official, SingleFile }

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
    IReadOnlyList<string>? MaterialLog = null,
    CodSoundSet? Sounds = null,
    IReadOnlyList<string>? Notes = null);

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

    // upper > 1 scales the layer down: the parameter tops out at 1, so a_<layer> is never reached at full weight.
    // poses: a_<layer>_00..NN spread evenly over the blend instead of the a_<layer>_zero / a_<layer> pair.
    private static void LayerSequence(StringBuilder text, string layer, string name, string param, float upper = 1, int? poses = null)
    {
        text.AppendLine($"$sequence \"{name}\" {{");
        foreach (var animation in poses is { } count ? Enumerable.Range(0, count).Select(index => $"a_{layer}_{index:00}") : [$"a_{layer}_zero", $"a_{layer}"])
            text.AppendLine($"\t{animation}");
        text.AppendLine($"\tblendwidth {poses ?? 2}").AppendLine($"\tblend {param} 0 {Number(upper)}").AppendLine("\tdelta").AppendLine("\trealtime").AppendLine("}");
    }

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
        // Default part first (QcAttachments.DefaultFirst); slots of optional extras (CoD grip tapes) start empty.
        var emptyDefaults = new List<bool>();
        var groups = request.Parts.Where(part => part.Kind == ModelPartKind.Attachment)
            .GroupBy(part => RootBone(part.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var parts = group.Select(part => (Part: part, Token: Token(PartStem(part.FilePath), weaponStem))).ToArray();
                parts = QcAttachments.DefaultFirst(parts, item => item.Token, out var emptyDefault);
                emptyDefaults.Add(emptyDefault);
                return parts;
            })
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
            var notes = new List<string>();
            var bones = plan.Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var receiver = Token(weaponStem, weaponStem);
            var mwOnly = request.Target == QcTarget.MwBase;
            // MW Base official layout: v_/w_ carry only the receiver; attachments become separate bonemerged models.
            // Single-file layout keeps them as viewmodel bodygroups (SWEP attachments set ATTACHMENT.Bodygroups).
            var separate = mwOnly && request.MwLayout == QcMwLayout.Official;
            // Official layout titles fragment sections like Source-Files-MW-Public; the other layouts stay unchanged.
            string Heading(string title) => separate ? $"// {title}\n\n" : "";
            var useMw = request.Target != QcTarget.Arc9;
            var useArc9 = request.Target != QcTarget.MwBase;
            // ARC9-COD2019 drives aim_blend (0 hip, 1 ADS); MW Base drives aim_offset. A blend can follow only one.
            var aimParam = request.Target == QcTarget.Arc9 ? "aim_blend" : "aim_offset";
            var (viewMeshes, worldMeshes, worldPlan) = WriteMeshes(output, staging, hands, weapon, receiver, groups, separate, request.IncludeHands, mwOnly);

            // Animations
            var clips = Clips(request.AnimationDirectory);
            var idle = clips.GetValueOrDefault("idle") ?? throw new InvalidDataException("动画目录缺少 idle / Animation folder has no idle clip.");
            var lengths = new Dictionary<string, int> { ["idle"] = Bake(plan, idle, Path.Combine(output, "anims", "idle.smd")) };
            // IronSights only trims the CoD ADS hold pose (aim_offset) onto screen centre.
            var checkpoints = new Dictionary<string, int?>();
            var magSwaps = new Dictionary<string, int>();
            var basic = new StringBuilder();
            var adsSequences = new StringBuilder();
            var transitionIncludes = new StringBuilder();
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
                if (MagSwapFrame(hip.Value) is { } swap) magSwaps[name] = swap;
                if (ads.Value is not null) Bake(plan, ads.Value, Path.Combine(output, "anims", ads.Key + ".smd"), basePose: idle);
                var body = new StringBuilder();
                body.AppendLine($"\t\"anims/{hip.Key}.smd\"");
                if (ads.Value is not null)
                {
                    body.AppendLine($"\t\"anims/{ads.Key}.smd\"");
                    body.AppendLine("\t$include \"macros/aim_blend.qc\"");
                }
                foreach (var line in Events(hip.Value, name)) body.AppendLine("\t" + line);
                // ads_in/ads_out carry the ADS move themselves; aim_offset on top would apply it twice (akilo47 layers_noaim).
                body.AppendLine(useMw && name is "ads_in" or "ads_out" ? "\t$include \"macros/layers_noaim.qc\"" : "\t$include \"macros/layers.qc\"");
                body.AppendLine("\t$include \"macros/ik.qc\"");
                body.AppendLine($"\tfps {Number(Fps)}");
                body.AppendLine(name == "idle" ? "\tloop" : "\tsnap");
                var sequenceFile = separate && (name is "ads_in" or "ads_out") ? adsSequences : basic;
                sequenceFile.AppendLine($"$sequence \"{name}\" {{").Append(body).AppendLine("}").AppendLine();
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
                // MW Base holds ADS only through aim_offset (Ads_In then returns to Idle). Without ads_additive use the
                // ads_up end pose; a shared ads_additive is the other rig's left arm and has no tag_ads move at all.
                var adsHold = layer == "aim_offset" && own is null && useMw && clips.ContainsKey("ads_up")
                    ? Path.Combine(output, "anims", "ads_up.smd") : null;
                // Super sprint from another rig swings this gun with that rig's pose; without its own clips MW Base just sprints.
                // MW Base target borrows it like x123 borrows mike4: the whole clip as a delta, hands kept on by ikrule.
                var file = own ?? adsHold ?? (layer.StartsWith("super_sprint", StringComparison.Ordinal) && !mwOnly ? null : Find(locomotion, source));
                if (file is null) { missing.Add(layer); continue; }
                var smd = Path.Combine(output, "anims", layer + ".smd");
                var text = new StringBuilder(Heading("Subtracted animations"));
                float upper = 1;
                IReadOnlyList<string>? poses = null;
                if (adsHold is not null)
                {
                    // MW Base swaps Ads_In for Idle + aim_offset at cycle 0.5, with aim_offset rising linearly. One pose per
                    // ads_up frame (evenly spread over the blend) makes aim_offset = t show ads_up at cycle t, so the swap
                    // lands on the same pose and keeps CoD's ease instead of a straight two-pose lerp (felt as a hitch).
                    poses = FrameSmds(adsHold, output, layer);
                    foreach (var (pose, index) in poses.Select((pose, index) => (pose, index)))
                        text.AppendLine($"$animation a_{layer}_{index:00} \"anims/{pose}\" subtract a_idle_ref 0");
                }
                else if (own is null && mwOnly && loop)
                {
                    // The other rig's loop as is, minus its own first frame: only its motion lands on this gun.
                    // Locomotion scale caps the blend instead (parameter 1 reaches scale of the way to a_<layer>).
                    var weight = Math.Clamp((request.Scale ?? new()).For(layer), 0, 1);
                    if (weight <= 0) { missing.Add(layer); continue; }
                    upper = 1 / weight;
                    var frames = Bake(plan, file, smd);
                    text.AppendLine($"$animation a_{layer}_ref \"anims/{layer}.smd\"");
                    text.AppendLine($"$animation a_{layer}_zero \"anims/{HeldIdle(output, layer, frames)}\" fps {Number(Fps)} subtract a_idle_ref 0");
                    text.AppendLine($"$animation a_{layer} \"anims/{layer}.smd\" fps {Number(Fps)} subtract a_{layer}_ref 0");
                }
                else if (own is null)
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
                text.AppendLine().Append(Heading("Delta"));
                if (useMw) LayerSequence(text, layer, layer, layer, upper, poses?.Count);
                if (useArc9 && Arc9Params.TryGetValue(layer, out var arc9Param)) LayerSequence(text, layer, "a9_" + layer, arc9Param);
                File.WriteAllText(Path.Combine(output, "additives", layer + ".qc"), text.ToString());
                layers.Add(layer);
            }

            // sprint_loop and super_sprint_loop are both driven by MW Base's sprint delta: never stack them.
            var commonLayers = layers.Where(layer => !layer.StartsWith("super_sprint", StringComparison.Ordinal)).ToArray();
            string? hold = null;
            foreach (var (sequence, source) in Transitions)
            {
                var own = Find(clips, source);
                var file = own ?? (sequence.StartsWith("super_sprint", StringComparison.Ordinal) && !mwOnly ? null : Find(locomotion, source));
                if (file is null) continue;
                var smd = Path.Combine(output, "anims", sequence + ".smd");
                // Official layout: one additives/<transition>.qc each (akilo47 sprint_in.qc); otherwise inline in animations_basic.
                var transition = separate ? new StringBuilder() : basic;
                var body = new StringBuilder();
                var super = sequence.StartsWith("super_sprint", StringComparison.Ordinal);
                var leftHand = new List<string>();
                if (mwOnly)
                {
                    // akilo47 sprint_in/sprint_out: a held idle timed to the clip plus the clip as a delta layer. In-transitions
                    // subtract frame 0 (rest -> pose), out-transitions their last frame (pose -> rest, ends on idle).
                    var frames = Bake(plan, file, smd);
                    var reference = sequence.EndsWith("_out", StringComparison.Ordinal) ? frames - 1 : 0;
                    transition.Append(Heading("To Sub")).AppendLine($"$animation a_{sequence}_ref \"anims/{sequence}.smd\"");
                    transition.Append(Heading("Delta")).AppendLine($"$sequence \"a_{sequence}\" {{\n\t\"anims/{sequence}.smd\"\n\tsubtract a_{sequence}_ref {reference}\n\tdelta\n\tfps {Number(Fps)}\n}}\n");
                    // Super sprint frees the left hand only between CoD's ik_out/ik_in notetracks. The clip's arm delta over this
                    // gun's idle does not keep it on the grip outside them (a borrowed rig's wrist sinks ~4 in through the gun),
                    // so a ranged lhand ikrule does. Ranges count frames of the main animation: hold idle for the clip's length.
                    if (super) leftHand.AddRange(LeftHandIkRanges(file, frames).Select(range => $"\tikrule lhand touch \"{LeftIk.TargetBone}\" usesource range {range}"));
                    var held = leftHand.Count > 0 ? HeldIdle(output, sequence, frames) : (hold ??= HeldIdle(output, "hold", 2));
                    var heldFps = leftHand.Count > 0 ? Fps : Fps / Math.Max(1, frames - 1);
                    body.AppendLine($"\t\"anims/{held}\"\n\tfps {Number(heldFps)}\n\taddlayer \"a_{sequence}\"");
                    lengths[sequence] = frames;
                }
                else
                {
                    // Transitions carry the full sprint pose (MW Base drives no sprint_offset); the locomotion scale only tames loops.
                    lengths[sequence] = own is not null ? Bake(plan, own, smd, basePose: idle)
                        : Bake(plan, idle, smd, [new AnimationLayerSpec(WeaponOnlyDelta(file, false, staging, 1), AnimationLayerKind.Additive)]);
                    body.AppendLine($"\t\"anims/{sequence}.smd\"");
                }
                body.AppendLine(super ? "\t$include \"macros/layers_super.qc\"" : "\t$include \"macros/layers.qc\"");
                // Super sprint is one-handed (akilo47 super_sprint_in comments out the lhand rule): the left hand only in its range.
                body.AppendLine(super && mwOnly ? string.Join('\n', leftHand.Prepend($"\tikrule rhand touch \"{RightIk.TargetBone}\" usesource")) : "\t$include \"macros/ik.qc\"").AppendLine(mwOnly ? "" : $"\tfps {Number(Fps)}").AppendLine("\tfadein 0.2\n\tfadeout 0.2");
                transition.Append(Heading("Sequence")).AppendLine($"$sequence \"{sequence}\" {{").Append(body).AppendLine("}").AppendLine();
                if (separate)
                {
                    File.WriteAllText(Path.Combine(output, "additives", sequence + ".qc"), transition.ToString());
                    transitionIncludes.AppendLine($"$include \"additives/{sequence}.qc\"");
                }
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

            File.WriteAllText(Path.Combine(output, "animations_basic.qc"), Heading("Sequences") + basic);
            if (separate)
                File.WriteAllText(Path.Combine(output, "animations_ads.qc"), Heading("ADS sequences") + adsSequences);
            var arc9Layers = useArc9 ? layers.Where(Arc9Params.ContainsKey).Select(layer => "a9_" + layer).ToArray() : [];
            string AddLayers(IEnumerable<string> names) => string.Concat(names.Select(layer => $"addlayer \"{layer}\"\n"));
            File.WriteAllText(Path.Combine(output, "macros", "layers.qc"), AddLayers((useMw ? commonLayers : []).Concat(arc9Layers)));
            File.WriteAllText(Path.Combine(output, "macros", "layers_super.qc"), AddLayers((useMw
                ? commonLayers.Where(layer => layer != "sprint_loop").Append("super_sprint_loop").Where(layers.Contains) : []).Concat(arc9Layers)));
            File.WriteAllText(Path.Combine(output, "macros", "layers_noaim.qc"), AddLayers((useMw ? commonLayers.Where(layer => layer != "aim_offset") : []).Concat(arc9Layers)));
            File.WriteAllText(Path.Combine(output, "macros", "aim_blend.qc"), $"blend {aimParam} 0 1\nblendwidth 2\n");
            File.WriteAllText(Path.Combine(output, "macros", "ik.qc"), string.Concat(new[] { RightIk, LeftIk }
                .Where(chain => bones.Contains(chain.TargetBone))
                .Select(chain => $"ikrule {(chain == RightIk ? "rhand" : "lhand")} touch \"{chain.TargetBone}\" usesource\n")));
            File.WriteAllText(Path.Combine(output, "meshes.qc"), Heading("Meshes") + viewMeshes);
            File.WriteAllText(Path.Combine(output, "meshes_worldmodel.qc"), Heading("Meshes") + worldMeshes);
            var chands = ValveBipedBones();
            // First-person views never show legs; their $definebone lines only spend the 256-bone budget (15 for the MW rig).
            // MW Base layout also drops the attachments' own sockets (see AttachmentSockets): they belong to the att_*.mdl.
            var sockets = separate ? AttachmentSockets(plan.Skeleton, groups.SelectMany(group => group).Select(item => item.Part), [hands, weapon]) : [];
            var viewBones = plan.Skeleton.Bones.Where(bone => !IsLegBone(bone) && !sockets.Contains(bone.Name!)).ToArray();
            var budget = viewBones.Length + DefineBoneName().Matches(chands.Definitions).Count;
            // Third person bonemerges the same att_*.mdl onto the worldmodel, so it drops the same sockets.
            var worldBones = worldPlan.Skeleton.Bones.Where(bone => !sockets.Contains(bone.Name!)).ToArray();
            notes.Add($"viewmodel bones: {budget} defined of {MaxStudioBones} ({plan.Skeleton.Bones.Count(IsLegBone)} leg bones dropped"
                + (sockets.Count > 0 ? $", {sockets.Count} attachment sockets left to the att_*.mdl" : "") + ")");
            if (budget > MaxStudioBones)
                notes.Add($"WARNING: {budget} bones exceed studiomdl's {MaxStudioBones}; remove attachments that add bones or split them into att_*.mdl (MW Base layout)");
            if (separate)
            {
                // akilo47 split: arm rig plus c_hands transforms, weapon bones, and c_hands bonemerge in its own file.
                var rigNames = Plan([hands]).Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isRig = viewBones.ToLookup(bone => rigNames.Contains(bone.Name!));
                File.WriteAllText(Path.Combine(output, "bones_mwrig.qci"),
                    Heading("MW Rig Bones") + DefineBoneLines(isRig[true], null) + "\n" + chands.Definitions);
                File.WriteAllText(Path.Combine(output, "bones_weapon.qc"), Heading("Weapon Bones") + DefineBoneLines(isRig[false], null));
                File.WriteAllText(Path.Combine(output, "bones_chands.qci"), Heading("C Hands Bonemerge") + chands.Bonemerge);
                File.WriteAllText(Path.Combine(output, "bones_weapon_worldmodel.qc"), HandRig(worldBones) is { } handRig
                    // w_mcharlie: hand-rooted rig, and every bone bonemerged so the att_*.mdl parts have set-up mounts to snap to.
                    ? DefineBones(handRig, null) + "\n// Bonemerge targets\n" + Bonemerges(handRig.Bones.Select(bone => bone.Name!))
                    : DefineBones(worldBones, HandBone));
            }
            else
            {
                File.WriteAllText(Path.Combine(output, "bones.qci"), DefineBones(viewBones, null));
                File.WriteAllText(Path.Combine(output, "bones_chands.qci"), chands.Definitions.TrimEnd() + "\n\n" + chands.Bonemerge);
                File.WriteAllText(Path.Combine(output, "bones_worldmodel.qci"), DefineBones(worldPlan.Skeleton, HandBone));
            }
            File.WriteAllText(Path.Combine(output, "attachments.qc"), Heading("Attachments") + Attachments(viewBones));
            File.WriteAllText(Path.Combine(output, "attachments_worldmodel.qc"), Heading("Attachments") + Attachments(worldBones));

            var (modelDirectory, weaponName) = ModelPath(request.ModelPath, weaponStem, groups);
            var materials = string.IsNullOrWhiteSpace(request.MaterialsPath) ? $"models/weapons/{weaponName}/" : request.MaterialsPath.Trim();
            var common = $"$surfaceprop \"weapon\"\n$contents \"solid\"\n$illumposition 0 0 0\n$mostlyopaque\n$cdmaterials \"{materials}\"\n";
            // VMT/VTF land in <output>/materials/<cdmaterials>, ready to copy into the addon next to models/ and lua/.
            var materialLog = request.ConvertMaterials
                ? CodMaterialConverter.Convert(request.Parts.Select(part => part.FilePath), Path.Combine(output, "materials", materials.Replace('/', Path.DirectorySeparatorChar)),
                    materials, request.Target == QcTarget.MwBase ? VmtEnvmapStyle.MwBase : request.Target == QcTarget.Arc9 ? VmtEnvmapStyle.Arc9 : VmtEnvmapStyle.Cubemap,
                    request.MaterialMaxSize)
                : null;
            // One sound script per weapon, in the folder its base auto-loads: MW Base mw_loader IncludeDir("weapons/mg_base/modules/sounds")
            // (official mw_sounds_ar_akilo47.lua); ARC9 packs keep theirs in lua/arc9/shared (arc9_modern_warfare_classic).
            var soundScript = string.IsNullOrWhiteSpace(request.SoundDirectory) ? null
                : Path.Combine(output, "lua", mwOnly ? Path.Combine("weapons", "mg_base", "modules", "sounds") : Path.Combine("arc9", "shared"),
                    (mwOnly ? "mw_sounds_" : "sh_arc9_") + weaponName + ".lua");
            if (soundScript is not null)
            {
                if (File.Exists(soundScript)) File.Delete(soundScript); // both builders append
                LuaFile.Append(soundScript, $"-- Generated by Alchemy Stars: {weaponName} sounds\n");
            }
            // Fire: CoD layers shot + mech + lfe per shot; pre-mixed into one wav per variant for Source.
            var fire = soundScript is null || CodFireSound.Token(weapon.FilePath) is not { } codToken ? null
                : CodFireSound.Build(request.SoundDirectory!.Trim(), output, weaponName, codToken, soundScript);
            if (fire is not null) notes.AddRange(fire.Log.Select(line => "fire sound: " + line));
            // Foley: aliases the 5004 events fire -> exported files copied to sound/ and registered by sound.Add.
            var sounds = soundScript is null ? null
                : CodSoundResolver.Resolve(CodSoundResolver.Aliases(sequenceSources.Values), request.SoundDirectory!.Trim(), output, weaponName, soundScript);
            // Integrally suppressed barrels (barsil) carry tag_flash_silenced instead of tag_flash.
            var suppressed = groups.SelectMany(group => group).Where(item => Plan([item.Part with { ParentBoneTag = "" }]).Skeleton.Bones
                .Any(bone => string.Equals(bone.Name, "tag_flash_silenced", StringComparison.OrdinalIgnoreCase))).Select(item => item.Token).ToHashSet();

            if (mwOnly)
            {
                // Bones that must always be set up (akilo47 bones_weapon.qc): Source skips bones nothing uses, so an
                // attachment bonemerged onto tag_sight_on2 etc. would read an unset matrix and float off the gun, and the
                // ikrule touch targets would leave the hands with nothing to reach. All weapon/attachment tags qualify.
                var keep = groups.SelectMany(group => group).SelectMany(item => Plan([item.Part with { ParentBoneTag = "" }]).Skeleton.Bones.Select(bone => bone.Name!))
                    .Append(LeftIk.TargetBone).Append(RightIk.TargetBone).Concat(SightMounts).Where(name => bones.Contains(name) && !sockets.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
                File.AppendAllText(Path.Combine(output, separate ? "bones_weapon.qc" : "bones.qci"), "\n// Bonemerge and IK targets\n" + Bonemerges(keep));
            }
            var viewQc = Path.Combine(output, "weapon_viewmodel.qc");
            var worldQc = Path.Combine(output, "weapon_worldmodel.qc");
            if (separate) WriteOfficialEntries(output, modelDirectory, weaponName, materials, receiver, layers, transitionIncludes.ToString());
            else
            {
                var arc9Params = arc9Layers.Select(layer => Arc9Params[layer[3..]]);
                WriteCombinedEntries(viewQc, worldQc, request.Target, modelDirectory, weaponName, common, receiver,
                    (useMw ? layers : []).Concat(arc9Params).Append(aimParam).Distinct(), layers, useArc9);
            }
            if (mwOnly && request.MwLayout == QcMwLayout.SingleFile)
            {
                // Third-party layout (v_sr25.qc): one self-contained QC per model, so no fragment can be compiled alone.
                viewQc = Flatten(output, viewQc, Path.Combine(output, $"v_{weaponName}.qc"));
                worldQc = Flatten(output, worldQc, Path.Combine(output, $"w_{weaponName}.qc"));
                foreach (var fragment in new[] { "weapon_viewmodel.qc", "weapon_worldmodel.qc", "meshes.qc", "meshes_worldmodel.qc", "bones.qci",
                    "bones_worldmodel.qci", "bones_chands.qci", "attachments.qc", "attachments_worldmodel.qc", "animations_basic.qc" })
                    File.Delete(Path.Combine(output, fragment));
                foreach (var folder in new[] { "additives", "macros" })
                {
                    foreach (var fragment in Directory.GetFiles(Path.Combine(output, folder), "*.qc")) File.Delete(fragment);
                    if (!Directory.EnumerateFileSystemEntries(Path.Combine(output, folder)).Any()) Directory.Delete(Path.Combine(output, folder));
                }
            }

            if (!mwOnly)
            {
                // ARC9 / Both: one combined model, bodygroup attachments driven by AttachmentElements.
                var arc9Sequences = sequences.ToDictionary(name => name, name => (lengths.GetValueOrDefault(name, 1), checkpoints.GetValueOrDefault(name), sequenceSources[name]));
                if (useArc9 && layers.Contains("sprint_loop")) arc9Sequences["idle_sprint"] = (lengths["idle"], null, "");
                var adsUp = Path.Combine(output, "anims", "ads_up.smd");
                var sight = File.Exists(adsUp) ? SolveSight(Path.Combine(output, "anims", "idle.smd"), adsUp) : null;
                var arc9Lua = Arc9Lua.Write(output, new(weaponName, $"models/{modelDirectory}v_{weaponName}.mdl", $"models/{modelDirectory}w_{weaponName}.mdl",
                    groups.Select(group => group.Select(item => (item.Token, RootBone(item.Part.FilePath))).ToArray()).ToArray(), arc9Sequences, sight, request.PrintName.Trim(), sounds?.Aliases.Keys.ToHashSet(), BulletBones(plan.Skeleton), magSwaps,
                    emptyDefaults, suppressed, fire));
                return new(viewQc, worldQc, sequences, layers, missing, LuaDirectory: arc9Lua, MaterialLog: materialLog, Sounds: sounds, Notes: notes);
            }
            var attachmentDirectory = $"{modelDirectory}attachments/{weaponName}/";
            var attachmentQcs = separate ? WriteAttachmentModels(output, staging, groups, attachmentDirectory, common) : [];
            if (separate) FormatOfficialFiles(output, new[] { viewQc, worldQc }.Concat(attachmentQcs));
            var lua = MwBaseLua.Write(output, new(weaponName, $"models/{modelDirectory}v_{weaponName}.mdl", $"models/{modelDirectory}w_{weaponName}.mdl",
                $"models/{attachmentDirectory}", groups.Select(group => group.Select(item => (item.Token, RootBone(item.Part.FilePath))).ToArray()).ToArray(),
                sequences.Select(name => (name, lengths.GetValueOrDefault(name, 1), checkpoints.GetValueOrDefault(name))).ToArray(),
                layers.Where(layer => layer.StartsWith("grip_", StringComparison.Ordinal)).ToArray(), request.PrintName.Trim(),
                separate ? null : groups.Select(GroupName).ToArray(),
                separate ? groups.SelectMany(group => group).Select(item => (item.Token, Rounds: MagazineRounds(item.Part)))
                    .Where(item => item.Rounds is not null).ToDictionary(item => item.Token, item => item.Rounds!.Value) : null,
                emptyDefaults, suppressed, fire,
                // Mount tags shared MW Base sights/muzzles/lasers/grips bonemerge onto: on the viewmodel, and per barrel.
                bones,
                groups.SelectMany(group => group).ToDictionary(item => item.Token,
                    item => Plan([item.Part with { ParentBoneTag = "" }]).Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase)),
                viewMeshes.Contains("$bodygroup \"laser\"", StringComparison.Ordinal)));
            return new(viewQc, worldQc, sequences, layers, missing, attachmentQcs, lua, materialLog, sounds, notes);
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
        // MW Base ResetBullets flips j_mag1 to the new round count: put it where j_mag1 turns into the new magazine.
        var reset = animation is not null && sequence.StartsWith("reload", StringComparison.Ordinal) ? MagCutFrame(animation) : null;
        if (sequence.StartsWith("fire", StringComparison.Ordinal))
        {
            lines.Add((0, "{ event 9001 0 \"MuzzleFlash muzzle\" }"));
            lines.Add((0, "{ event 6001 0 \"shell_eject\" }"));
        }
        if (animation is not null)
        {
            var scale = animation.Framerate > 0 ? Fps / animation.Framerate : 1;
            var checkpoint = false;
            // CoD clips carry the foley of every magazine variant (inspect has plain + smag + xmag); keep this sequence's own.
            var variant = QcAttachments.VariantOf(sequence);
            foreach (var (note, frame) in animation.NotificationTracks
                .SelectMany(track => Frames(track.KeyFrameBuffer).Select(value => (track.Name, Frame: (int)MathF.Round(value * scale))))
                .Where(item => !item.Name.Contains("wfoly", StringComparison.OrdinalIgnoreCase) || QcAttachments.VariantOf(item.Name) == variant)
                .OrderBy(item => item.Frame))
                if (EventLine(note, frame, sequence, ref checkpoint) is { } line)
                    lines.Add(reset is { } cut && line.Contains("ResetBullets", StringComparison.Ordinal) ? (cut, $"{{ event 9031 {cut} \"ResetBullets\" }}") : (frame, line));
        }
        return lines.OrderBy(item => item.Frame).Select(item => item.Line);
    }

    /// <summary>
    /// ikrule ranges (start peak tail end, 30 fps frames) while CoD's notetracks keep the left hand on the gun:
    /// ik_out_*_left_hand release it (walk_to_super_sprint), ik_in_*_left_hand bring it back (super_sprint_to_walk).
    /// shortcut: assumes one release and/or one return per clip, as in CoD transitions; split windows if a clip has more.
    /// </summary>
    private static List<string> LeftHandIkRanges(string file, int frames)
    {
        var ranges = new List<string>();
        var animation = CastReader.Load(file).RootNodes.SelectMany(DescendantsAndSelf).OfType<AnimationNode>().FirstOrDefault();
        if (animation is null) return ranges;
        var scale = animation.Framerate > 0 ? Fps / animation.Framerate : 1;
        var last = frames - 1;
        int? Note(string name) => animation.NotificationTracks.Where(track => track.Name.Trim() == name)
            .SelectMany(track => Frames(track.KeyFrameBuffer)).Select(value => (int?)Math.Clamp((int)MathF.Round(value * scale), 0, last)).Min();
        if (Note("ik_out_start_left_hand") is { } release)
            ranges.Add($"0 0 {release} {Math.Max(release, Note("ik_out_end_left_hand") ?? release)}");
        if (Note("ik_in_start_left_hand") is { } back)
            ranges.Add($"{back} {Math.Max(back, Note("ik_in_end_left_hand") ?? back)} {last} {last}");
        return ranges;
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

    /// <summary>Writes <paramref name="qc"/> with every $include replaced by the file's text (recursively, paths relative to the project).</summary>
    private static string Flatten(string root, string qc, string output)
    {
        var text = new StringBuilder();
        void Append(string file)
        {
            foreach (var line in File.ReadLines(file))
            {
                var include = IncludeLine().Match(line);
                if (include.Success) Append(Path.Combine(root, include.Groups[1].Value.Replace('\\', '/')));
                else text.AppendLine(line);
            }
        }
        Append(qc);
        File.WriteAllText(output, text.ToString());
        return output;
    }

    /// <summary>Bodygroup name of an attachment group: its shortest token's first word (bar_long + bar_short -> bar).</summary>
    private static string GroupName((ModelPartSpec Part, string Token)[] group) => group.MinBy(item => item.Token.Length).Token.Split('_')[0];

    /// <summary>Writes every frame of an animation SMD as its own one-frame SMD (held poses); returns their anims/ names.</summary>
    private static List<string> FrameSmds(string source, string output, string layer)
    {
        var lines = File.ReadAllLines(source);
        var start = Array.IndexOf(lines, "time 0");
        var end = Array.FindIndex(lines, start, line => line == "end");
        var times = Enumerable.Range(start, end - start).Where(index => lines[index].StartsWith("time ", StringComparison.Ordinal)).Append(end).ToArray();
        var names = new List<string>();
        for (var frame = 0; frame < times.Length - 1; frame++)
        {
            var name = $"{layer}_{frame:00}.smd";
            File.WriteAllLines(Path.Combine(output, "anims", name),
                lines[..start].Append("time 0").Concat(lines[(times[frame] + 1)..times[frame + 1]]).Append("end"));
            names.Add(name);
        }
        return names;
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
    /// <summary>
    /// When the shown round count flips to the new magazine: halfway between the first checkpoint (old magazine out) and
    /// the last (new one seated), where the magazine is off the gun. A single checkpoint (tactical) is used as is.
    /// </summary>
    private static int? MagSwapFrame(string file)
    {
        var animation = CastReader.Load(file).RootNodes.SelectMany(DescendantsAndSelf).OfType<AnimationNode>().FirstOrDefault();
        if (animation is null) return null;
        var scale = animation.Framerate > 0 ? Fps / animation.Framerate : 1;
        var frames = animation.NotificationTracks.Where(track => track.Name.Trim() == "reload checkpoint")
            .SelectMany(track => Frames(track.KeyFrameBuffer)).ToArray();
        return frames.Length == 0 ? null : (int)MathF.Round((frames.Min() + frames.Max()) / 2f * scale);
    }

    /// <summary>
    /// Frame (30 fps) where j_mag1 becomes the new magazine: the biggest isolated jump of j_mag1 between the first and last
    /// reload checkpoint (single checkpoint: to the end) that does not land in the far park spot. Measured on pmike: tactical
    /// reload f62 (park -> seat), reload_empty f21 (hand -> hand), fast f19. Null without checkpoints or j_mag1 curves.
    /// </summary>
    private static int? MagCutFrame(AnimationNode animation)
    {
        var checkpoints = animation.NotificationTracks.Where(track => track.Name.Trim() == "reload checkpoint")
            .SelectMany(track => Frames(track.KeyFrameBuffer)).ToArray();
        var axes = animation.Children.OfType<CurveNode>()
            .Where(curve => string.Equals(curve.NodeName, "j_mag1", StringComparison.OrdinalIgnoreCase)
                && curve.KeyPropertyName is "tx" or "ty" or "tz" && curve.KeyValueBuffer is CastArrayProperty<float> { Values.Count: > 0 })
            .ToDictionary(curve => curve.KeyPropertyName!, curve => (Keys: Frames(curve.KeyFrameBuffer).ToArray(), Values: ((CastArrayProperty<float>)curve.KeyValueBuffer!).Values));
        if (checkpoints.Length == 0 || axes.Count == 0) return null;
        static float Sample(float[] keys, IReadOnlyList<float> values, int frame)
        {
            var count = Math.Min(keys.Length, values.Count);
            if (count == 0) return 0;
            if (frame <= keys[0]) return values[0];
            for (var i = 1; i < count; i++)
                if (frame <= keys[i])
                    return keys[i] == keys[i - 1] ? values[i] : values[i - 1] + (values[i] - values[i - 1]) * (frame - keys[i - 1]) / (keys[i] - keys[i - 1]);
            return values[count - 1];
        }
        var last = (int)axes.Values.Max(axis => axis.Keys.Length > 0 ? axis.Keys[^1] : 0);
        var path = Enumerable.Range(0, last + 1).Select(frame => new Vector3(
            axes.TryGetValue("tx", out var x) ? Sample(x.Keys, x.Values, frame) : 0,
            axes.TryGetValue("ty", out var y) ? Sample(y.Keys, y.Values, frame) : 0,
            axes.TryGetValue("tz", out var z) ? Sample(z.Keys, z.Values, frame) : 0)).ToArray();
        float Step(int frame) => frame < 1 || frame > last ? 0 : Vector3.Distance(path[frame], path[frame - 1]);
        // Parked copies sit far below the gun (~61 in on pmike); hand positions stay well inside that.
        var reach = path.Max(point => Vector3.Distance(point, path[0]));
        var from = Math.Max(1, (int)checkpoints.Min());
        var to = checkpoints.Length > 1 ? Math.Min(last, (int)checkpoints.Max()) : last;
        int? best = null;
        var score = 0f;
        for (var frame = from; frame <= to; frame++)
        {
            var jump = Step(frame) - MathF.Max(Step(frame - 1), Step(frame + 1));
            if (jump <= score || Vector3.Distance(path[frame], path[0]) >= 0.8f * reach) continue;
            (best, score) = (frame, jump);
        }
        var scale = animation.Framerate > 0 ? Fps / animation.Framerate : 1;
        return best is { } cut ? (int)MathF.Round(cut * scale) : null;
    }

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
            // The idle sequence animates bones to the SMD's skeleton frame, overriding $definebone: moved mounts go in both.
            var skeleton = new Skeleton(plan.Skeleton.Name ?? token, [.. MoveMounts(plan.Skeleton)]);
            skeleton.AssignBoneIndices();
            var mesh = $"meshes/att_{token}.smd";
            SmdModelExporter.Save(Path.Combine(output, mesh), merged, skeleton, Inches, meshFilter: bones => !IsIronSight(bones));
            var sight = $"meshes/att_{token}_sight.smd";
            var hasSight = SmdModelExporter.Save(Path.Combine(output, sight), merged, skeleton, Inches, meshFilter: IsIronSight) > 0;
            if (!hasSight) File.Delete(Path.Combine(output, sight));
            var names = plan.Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder($"// MW Base attachment, bonemerged onto the viewmodel by bone name\n\n$modelname \"{modelDirectory}att_{token}.mdl\"\n\n");
            text.AppendLine($"// Meshes\n\n$bodygroup \"{token}\"\n{{\n\tstudio \"{mesh}\"\n}}\n");
            if (hasSight) text.AppendLine(IronSightBodygroup(sight));
            text.Append(common.Replace("$cdmaterials", "\n// Materials\n\n$cdmaterials", StringComparison.Ordinal));
            // Keep unweighted tags (tag_grip_attach, tag_flash…): grips/muzzles bonemerge onto them. $bonemerge makes Source
            // set them up although nothing in this model uses them; otherwise a grip parented here reads an unset matrix.
            text.AppendLine("\n// Weapon Bones\n").Append(DefineBones(skeleton, null));
            text.AppendLine("\n// Bonemerge targets\n").Append(Bonemerges(names.Order(StringComparer.Ordinal)));
            if (new[] { "tag_flash", "tag_flash_silenced" }.FirstOrDefault(names.Contains) is { } flash)
                text.AppendLine($"\n// Attachments\n\n$attachment \"muzzle\" \"{flash}\" 0 0 0 rotate 0 0 0");
            text.AppendLine($"\n// Sequences\n\n$sequence \"idle\" {{\n\t\"{mesh}\"\n\tfps 30\n}}");
            var qc = Path.Combine(output, $"att_{token}.qc");
            File.WriteAllText(qc, text.ToString());
            qcs.Add(qc);
        }
        return qcs;
    }

    /// <summary>Writes one SMD per part for the viewmodel (with hands) and the worldmodel (weapon only), returning both meshes.qc texts.</summary>
    /// <param name="splitSights">MW Base: receiver iron sights go to a "tag_sight" bodygroup that MW Base sights blank.</param>
    private static (string View, string World, SkeletonMergePlan WorldPlan) WriteMeshes(string output, string staging,
        ModelPartSpec hands, ModelPartSpec weapon, string receiver, IReadOnlyList<(ModelPartSpec Part, string Token)[]> groups, bool receiverOnly, bool includeHands,
        bool splitSights)
    {
        var standalone = weapon with { ParentBoneTag = "" };
        int Export(string name, ModelPartSpec part, ModelPartSpec[] context, Func<IReadOnlySet<string>, bool>? filter = null)
        {
            var parts = context.Append(part).ToArray();
            var plan = Plan(parts);
            var merged = Path.Combine(staging, name + ".cast");
            MayaCastPackage.SaveModel(merged, plan, AnimationConverter.TranslatorFactory);
            var skip = context.Sum(item => MeshCount(item.FilePath));
            var smd = Path.Combine(output, "meshes", name + ".smd");
            // MW Base worldmodels get the official hand-rooted skeleton (see HandRig), so CAST weight indices are remapped by name.
            var rig = splitSights && name.StartsWith("w_", StringComparison.Ordinal) ? HandRig(plan.Skeleton.Bones) : null;
            var written = SmdModelExporter.Save(smd, merged, rig ?? plan.Skeleton, Inches, skip, MeshCount(part.FilePath), filter,
                rig?.Bones.Select((bone, index) => (bone.Name!, index)).ToDictionary(StringComparer.OrdinalIgnoreCase) is { } index
                    ? plan.Skeleton.Bones.Select(bone => index[bone.Name!]).ToArray() : null);
            if (written == 0 && filter is not null) File.Delete(smd);
            return written;
        }

        // MW Base: iron sights and the laser rail cover go to their own bodygroups ("tag_sight", "laser") for optics/lasers to toggle.
        Func<IReadOnlySet<string>, bool>? body = splitSights ? bones => !IsIronSight(bones) && !IsLaserRail(bones) : null;
        Export(receiver, weapon, [hands], body);
        Export("w_" + receiver, standalone, [], body);
        var view = new StringBuilder($"$bodygroup \"receiver\"\n{{\n\tstudio \"meshes/{receiver}.smd\"\n}}\n");
        var world = new StringBuilder($"$bodygroup \"receiver\"\n{{\n\tstudio \"meshes/w_{receiver}.smd\"\n}}\n");
        if (splitSights && Export(receiver + "_sight", weapon, [hands], IsIronSight) > 0)
        {
            Export("w_" + receiver + "_sight", standalone, [], IsIronSight);
            view.AppendLine(IronSightBodygroup($"meshes/{receiver}_sight.smd"));
            world.AppendLine(IronSightBodygroup($"meshes/w_{receiver}_sight.smd"));
        }
        if (splitSights && Export(receiver + "_laser", weapon, [hands], IsLaserRail) > 0)
        {
            Export("w_" + receiver + "_laser", standalone, [], IsLaserRail);
            view.AppendLine(LaserRailBodygroup($"meshes/{receiver}_laser.smd"));
            world.AppendLine(LaserRailBodygroup($"meshes/w_{receiver}_laser.smd"));
        }
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
            var header = $"$bodygroup \"{GroupName(group)}\"\n{{\n";
            view.Append(header);
            world.Append(header);
            foreach (var (part, token) in group)
            {
                Export(token, part, [hands, weapon]);
                Export("w_" + token, part, [standalone]);
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
    private static string DefineBones(Skeleton skeleton, string? rootParent) => DefineBones(skeleton.Bones, rootParent);

    /// <summary>
    /// Official MW Base worldmodel skeleton (w_mcharlie): ValveBiped.Bip01_R_Hand is the root and its idle holds tag_sling
    /// at <see cref="SlingInHand"/>; SWEP.WorldModelOffsets (ManipulateBone on tag_sling) is tuned on top of that pose.
    /// CoD roots the gun on j_gun with tag_sling 10 in away, which turned and threw the worldmodel off the hand. The hand
    /// bone is placed so tag_sling lands on the official pose: every other bone keeps its model-space transform, so the
    /// vertices need no change and the official offsets fit. Null when there is no tag_sling.
    /// </summary>
    private static Skeleton? HandRig(IEnumerable<SkeletonBone> source)
    {
        var bones = source.ToArray();
        if (bones.FirstOrDefault(bone => string.Equals(bone.Name, "tag_sling", StringComparison.OrdinalIgnoreCase)) is not { } sling) return null;
        static (Vector3 P, Quaternion Q) World(SkeletonBone bone) => bone.Parent is null
            ? (bone.BaseLocalTranslation, bone.BaseLocalRotation)
            : Mul(World(bone.Parent), (bone.BaseLocalTranslation, bone.BaseLocalRotation));
        var anchor = Mul(World(sling), Inverse(SlingInHand));
        var hand = new SkeletonBone(HandBone) { BaseLocalTranslation = anchor.P, BaseLocalRotation = anchor.Q };
        // tag_sling must be the hand's direct child: WorldModelOffsets manipulates it in its parent's frame.
        var slingCopy = new SkeletonBone(sling.Name!) { Parent = hand, BaseLocalTranslation = SlingInHand.P, BaseLocalRotation = SlingInHand.Q };
        var copies = new Dictionary<SkeletonBone, SkeletonBone> { [sling] = slingCopy };
        foreach (var bone in bones)
        {
            if (bone == sling) continue;
            var copy = new SkeletonBone(bone.Name!) { BaseLocalTranslation = bone.BaseLocalTranslation, BaseLocalRotation = bone.BaseLocalRotation };
            if (bone.Parent is { } parent && copies.TryGetValue(parent, out var parentCopy)) copy.Parent = parentCopy;
            else
            {
                // Former roots (j_gun, CoD's parent of tag_sling) and bones whose parent was filtered out hang off
                // tag_sling at their model-space pose, like every w_mcharlie bone.
                var local = Mul(Inverse(World(sling)), World(bone));
                copy.Parent = slingCopy;
                copy.BaseLocalTranslation = local.P;
                copy.BaseLocalRotation = Quaternion.Normalize(local.Q);
            }
            copies[bone] = copy;
        }
        var skeleton = new Skeleton(HandBone, [hand, slingCopy, .. copies.Values.Where(copy => copy != slingCopy)]);
        skeleton.AssignBoneIndices();
        return skeleton;
    }

    // w_mcharlie idle, measured in Gmod: tag_sling in hand space at (-0.541, 4.594, -0.418) in, QAngle (-90, -90, 0).
    // Source angles: Rz(yaw) * Ry(pitch) * Rx(roll); bone translations are cm (Inches converts on write).
    private static readonly (Vector3 P, Quaternion Q) SlingInHand = (new Vector3(-0.541016f, 4.59375f, -0.418457f) / Inches,
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2));

    private static string DefineBones(IEnumerable<SkeletonBone> bones, string? rootParent) =>
        "// CAST bind pose in inches; rotation is QAngle pitch yaw roll\n" + DefineBoneLines(bones, rootParent);

    /// <summary>studiomdl's MAXSTUDIOBONES: compiling more fails with "Too many bones used in model".</summary>
    private const int MaxStudioBones = 256;

    // Receiver tags MW Base's shared sights are rooted on (att_vm_holo_west01 -> tag_holo...): they carry no weight, so
    // without $bonemerge Source never sets them up and a bonemerged sight reads an unset matrix.
    private static readonly string[] SightMounts = ["tag_reflex", "tag_holo", "tag_hybrid", "tag_acog_2", "tag_thermal", "tag_scope"];

    // CoD iron sights: meshes weighted only to the sight tags (receiver rear sight on tag_sight_on, barrel front on tag_sight_on2).
    private static readonly HashSet<string> IronSightBones = new(["tag_sight_on", "tag_sight_off", "tag_sight_on2", "tag_sight_off2"], StringComparer.OrdinalIgnoreCase);

    private static bool IsIronSight(IReadOnlySet<string> weightedBones) => weightedBones.Count > 0 && weightedBones.All(IronSightBones.Contains);

    // CoD laser rail cover: a receiver mesh weighted only to tag_laser_show, drawn when CoD fits a laser.
    private static bool IsLaserRail(IReadOnlySet<string> weightedBones) =>
        weightedBones.Count > 0 && weightedBones.All(bone => bone.Equals("tag_laser_show", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Hidden by default; MW Base laser subclasses show it with Bodygroups ["laser"] = 1, like the official akilo47
    /// "laser" group (its attachments use AttachmentBodygroups because the rail lives on the barrel model there).
    /// </summary>
    private static string LaserRailBodygroup(string mesh) => $"$bodygroup \"laser\"\n{{\n\tblank\n\tstudio \"{mesh}\"\n}}\n";

    /// <summary>
    /// Copy of the skeleton with a side-mounted tag_laser_attach rolled so its +Z points away from the gun. MW Base's box
    /// lasers (laser01-03) clamp on their -Z face; with CoD's identity side tag they lay flat and sank into the handguard.
    /// Grip tags stay where CoD put them: every shared grip of a slot hangs off the same tag, so per-grip fit-up is a
    /// ManipulateBonePosition in the generated attachment Lua (ATTACHMENT.MountOffset), not a tag move.
    /// </summary>
    private static IReadOnlyList<SkeletonBone> MoveMounts(Skeleton skeleton)
    {
        static bool IsBarrelChild(SkeletonBone bone) => string.Equals(bone.Parent?.Name, "tag_barrel_attach", StringComparison.OrdinalIgnoreCase);
        Quaternion? SideRoll(SkeletonBone bone)
        {
            if (!string.Equals(bone.Name, "tag_laser_attach", StringComparison.OrdinalIgnoreCase) || !IsBarrelChild(bone)) return null;
            var position = bone.BaseLocalTranslation;
            if (MathF.Abs(position.Y) * Inches < 0.3f || MathF.Abs(position.Y) <= MathF.Abs(position.Z)) return null;
            var outward = new Vector3(0, MathF.Sign(position.Y), 0);
            if (Vector3.Dot(Vector3.Transform(Vector3.UnitZ, bone.BaseLocalRotation), outward) > 0.7f) return null;
            // Roll about the tag's own X: +90 deg turns +Z to -Y (right side), -90 deg to +Y (left side).
            return bone.BaseLocalRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.Sign(position.Y) * MathF.PI / 2);
        }
        if (skeleton.Bones.All(bone => SideRoll(bone) is null)) return skeleton.Bones;
        var copies = new Dictionary<SkeletonBone, SkeletonBone>();
        foreach (var bone in skeleton.Bones)
        {
            var copy = new SkeletonBone(bone.Name!) { BaseLocalTranslation = bone.BaseLocalTranslation, BaseLocalRotation = bone.BaseLocalRotation };
            if (bone.Parent is { } parent && copies.TryGetValue(parent, out var parentCopy)) copy.Parent = parentCopy;
            if (SideRoll(bone) is { } rolled) copy.BaseLocalRotation = rolled;
            copies[bone] = copy;
        }
        return [.. copies.Values];
    }

    /// <summary>
    /// akilo47/anovember94 meshes.qc: MW Base sights set Bodygroups / AttachmentBodygroups ["tag_sight"] = 1 (2 for hybrids)
    /// on the viewmodel and every attachment model, so both indices are blank and the iron sight disappears under an optic.
    /// </summary>
    private static string IronSightBodygroup(string mesh) => $"$bodygroup \"tag_sight\"\n{{\n\tstudio \"{mesh}\"\n\tblank\n\tblank\n}}\n";

    /// <summary>
    /// MW Base layout: an attachment's own sockets (bar_hvylong tag_flash, tag_silencer, tag_tip...) must not exist on the
    /// viewmodel. EF_BONEMERGE snaps every same-named child bone onto the parent, so a longer barrel's muzzle, tip and
    /// grip/laser tags would sit at the default barrel's positions. Official akilo47 avoids the clash by naming the barrel's
    /// muzzle tag_flash_attachment; dropping the viewmodel copies does the same for CoD-named parts.
    /// shortcut: treats a part's tag_* below its root as static sockets (CoD names animated joints j_*); no HRM-9 clip
    /// animates one. Check the clips' curves if a weapon animates an attachment tag.
    /// </summary>
    private static HashSet<string> AttachmentSockets(Skeleton view, IEnumerable<ModelPartSpec> parts, IEnumerable<ModelPartSpec> owners)
    {
        var owned = Plan(owners).Skeleton.Bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = parts.SelectMany(part => Plan([part with { ParentBoneTag = "" }]).Skeleton.Bones.Skip(1))
            .Select(bone => bone.Name!).Where(name => name.StartsWith("tag_", StringComparison.OrdinalIgnoreCase) && !owned.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only whole subtrees: a kept bone must not lose its parent.
        bool Droppable(SkeletonBone bone) => candidates.Contains(bone.Name!) && view.Bones.Where(child => child.Parent == bone).All(Droppable);
        return view.Bones.Where(Droppable).Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MW rig legs: j_hip_le/_ri and everything under them (knees, ankles, tag_stowed_thigh) plus the foot IK tags.
    /// Without $definebone studiomdl drops them, as nothing in a viewmodel weights, attaches or bonemerges to them.
    /// </summary>
    private static bool IsLegBone(SkeletonBone bone)
    {
        if (bone.Name?.StartsWith("tag_ik_foot_loc", StringComparison.OrdinalIgnoreCase) == true) return true;
        for (var current = bone; current is not null; current = current.Parent)
            if (current.Name is { } name && (name.Equals("j_hip_le", StringComparison.OrdinalIgnoreCase) || name.Equals("j_hip_ri", StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    /// <summary>$definebone lines only, for a subset of a skeleton (bones keep their parents' names).</summary>
    private static string DefineBoneLines(IEnumerable<SkeletonBone> bones, string? rootParent)
    {
        var text = new StringBuilder();
        if (rootParent is not null)
            text.AppendLine($"$definebone \"{rootParent}\" \"\" 0 0 0 0 0 0 0 0 0 0 0 0");
        foreach (var bone in bones)
        {
            var p = bone.BaseLocalTranslation * Inches;
            var e = SmdAnimationExporter.ToEulerXyz(bone.BaseLocalRotation) * (180 / MathF.PI);
            var parent = bone.Parent?.Name ?? rootParent ?? "";
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"$definebone \"{bone.Name}\" \"{parent}\" {Number(p.X)} {Number(p.Y)} {Number(p.Z)} {Number(e.Y)} {Number(e.Z)} {Number(e.X)} 0 0 0 0 0 0"));
        }
        return text.ToString();
    }

    /// <summary>
    /// j_ammo_* per magazine bone (j_mag1, j_mag2), zipped by slot so each entry hides one round in both copies: CoD
    /// animates either copy as "the" magazine (inspect shows j_mag2, reload_empty pulls j_mag1), so both follow the clip.
    /// </summary>
    private static IReadOnlyList<string[]> BulletBones(Skeleton skeleton)
    {
        var mags = skeleton.Bones
            .Where(bone => bone.Name!.StartsWith("j_ammo_", StringComparison.OrdinalIgnoreCase) && bone.Parent?.Name?.StartsWith("j_mag", StringComparison.OrdinalIgnoreCase) == true)
            .GroupBy(bone => bone.Parent!.Name!, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Select(bone => bone.Name!).Order(StringComparer.Ordinal).ToArray()).ToArray();
        return mags.Length == 0 ? [] : Enumerable.Range(0, mags.Min(names => names.Length)).Select(index => mags.Select(names => names[index]).ToArray()).ToArray();
    }

    /// <summary>
    /// MW Base att_magazine round lists of one magazine part, top round first: BulletList = j_mag1 (the gun's magazine,
    /// frozen at the old count until ResetBullets), ReserveBulletList = j_mag2 (the offhand new one). Null without rounds.
    /// </summary>
    private static (string[] Current, string[] Reserve)? MagazineRounds(ModelPartSpec part)
    {
        string[] Rounds(string mag) => Plan([part with { ParentBoneTag = "" }]).Skeleton.Bones
            .Where(bone => bone.Name!.StartsWith("j_ammo_", StringComparison.OrdinalIgnoreCase) && string.Equals(bone.Parent?.Name, mag, StringComparison.OrdinalIgnoreCase))
            .Select(bone => bone.Name!).Order(StringComparer.Ordinal).ToArray();
        var current = Rounds("j_mag1");
        return current.Length == 0 ? null : (current, Rounds("j_mag2"));
    }

    /// <summary>c_hands helper bones: the embedded $definebone template and a $bonemerge line for each of them.</summary>
    private static (string Definitions, string Bonemerge) ValveBipedBones()
    {
        using var stream = typeof(QcProjectGenerator).Assembly.GetManifestResourceStream("ValveBipedBones.qci")
            ?? throw new InvalidOperationException("ValveBipedBones.qci resource is missing.");
        var template = new StreamReader(stream).ReadToEnd();
        return (template, Bonemerges(DefineBoneName().Matches(template).Select(match => match.Groups[1].Value)));
    }

    private static string Bonemerges(IEnumerable<string> names) => string.Concat(names.Select(name => $"$bonemerge \"{name}\"\n"));

    /// <summary>The first four slots always exist so ARC9 QCA indices stay 1..4; missing bones fall back to the root.</summary>
    private static string Attachments(Skeleton skeleton) => Attachments(skeleton.Bones);

    private static string Attachments(IReadOnlyList<SkeletonBone> bones)
    {
        var names = bones.Select(bone => bone.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = bones[0].Name;
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
    [GeneratedRegex("^\\s*\\$include\\s+\"([^\"]+)\"")] private static partial Regex IncludeLine();
}
