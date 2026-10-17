using System.Globalization;
using System.Numerics;
using System.Text;

namespace AlchemyStars.Engine;

/// <param name="Groups">Attachment groups (same root bone): token + root bone; the first entry is the default.</param>
/// <param name="Sequences">Compiled sequence name, frame count at 30 fps, and the 9031 mag-in frame if any.</param>
/// <param name="Bodygroups">Single-file layout: viewmodel bodygroup name per group (attachments switch it instead of loading a model).</param>
/// <param name="MagazineRounds">Per magazine token: j_ammo bones (top round first) of the gun's magazine (j_mag1) and the offhand one (j_mag2).</param>
public sealed record MwBaseWeapon(
    string Name,
    string ViewModel,
    string WorldModel,
    string AttachmentModels,
    IReadOnlyList<(string Token, string Root)[]> Groups,
    IReadOnlyList<(string Name, int Frames, int? MagFrame)> Sequences,
    IReadOnlyList<string> GripLayers,
    string PrintName = "",
    IReadOnlyList<string>? Bodygroups = null,
    IReadOnlyDictionary<string, (string[] Current, string[] Reserve)>? MagazineRounds = null,
    IReadOnlyList<bool>? EmptyDefaults = null,
    IReadOnlySet<string>? Suppressed = null,
    CodFireSet? Fire = null,
    IReadOnlySet<string>? Mounts = null,
    IReadOnlyDictionary<string, HashSet<string>>? BarrelMounts = null,
    bool LaserRail = false,
    IReadOnlyDictionary<string, Dictionary<string, (Vector3 P, Quaternion Q)>>? BarrelTags = null);

/// <summary>
/// Writes a drop-in MW Base (mg_base) SWEP: lua/weapons/mg_&lt;name&gt;/{shared,animations,customization}.lua plus one
/// ATTACHMENT file per part under lua/weapons/mg_base/modules/attachmentss/&lt;name&gt;/. Layout and keys follow the
/// decompiled mg_akilo47 / mg_mw2_m13se packs. Gameplay numbers are placeholders to tune.
/// </summary>
public static class MwBaseLua
{
    private const float Fps = 30;

    // MW Base animation key -> candidate sequences (first present wins; several = random pick).
    private static readonly (string Key, string[] Sequences)[] AnimationKeys =
    [
        ("Draw", ["draw"]), ("Holster", ["holster"]), ("Equip", ["draw_first", "draw"]),
        ("Reload", ["reload"]), ("Reload_Empty", ["reload_empty"]),
        ("Reload_Fast", ["reload_fast"]), ("Reload_Empty_Fast", ["reload_empty_fast"]),
        ("Fire", ["fire"]), ("Fire_Last", ["fire_last"]), ("Ads_In", ["ads_in"]), ("Ads_Out", ["ads_out"]),
        // Sprint_In holds its last frame (the sprint pose) while sprint_loop plays as a pose-parameter layer.
        ("Sprint_In", ["sprint_in"]), ("Sprint_Out", ["sprint_out"]),
        ("Super_Sprint_In", ["super_sprint_in"]), ("Super_Sprint_Out", ["super_sprint_out"]),
        ("Firemode_Auto", ["semi_off"]), ("Firemode_Semi", ["semi_on"]),
        // No inspect fallback for Inspect_Empty: task_inspect plays Inspect when it is nil, and a copy of the plain clip
        // would bypass the magazine's Inspect = Inspect_Xmag swap (empty gun with a 50 mag inspected the 30 mag).
        ("Inspect", ["inspect"]), ("Inspect_Empty", ["inspect_empty"]),
        ("Jog_Out", ["jog_out"]), ("Land", ["jog_out"]),
        ("Melee", ["melee_miss_01", "melee_miss_02", "melee_miss_03"]),
        ("Melee_Hit", ["melee_hit_01", "melee_hit_02", "melee_hit_03"]),
    ];

    // Magazine variants CoD ships per reload/inspect clip (reload_smag, inspect_xmag): MW Base key suffix per variant.
    private static readonly (string Variant, string Suffix)[] MagVariants = [("smag", "_Smag"), ("xmag", "_Xmag")];

    // Root bone of an attachment part -> MW Base base class (category).
    private static readonly Dictionary<string, string> Bases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tag_barrel_attach"] = "att_barrel", ["tag_mag_attach"] = "att_magazine", ["tag_stock_attach"] = "att_stock",
        ["tag_pistolgrip_attach"] = "att_pistolgrip", ["tag_grip_attach"] = "att_grip", ["tag_scope"] = "att_sight",
        ["tag_silencer"] = "att_muzzle", ["tag_flash_attachment"] = "att_muzzle", ["tag_laser_attach"] = "att_laser",
    };

    /// <summary>Returns the lua/ root that was written.</summary>
    public static string Write(string output, MwBaseWeapon weapon)
    {
        var root = Path.Combine(output, "lua");
        var className = "mg_" + Identifier(weapon.Name);
        var swep = Path.Combine(root, "weapons", className);
        var attachments = Path.Combine(root, "weapons", "mg_base", "modules", "attachmentss", Identifier(weapon.Name));
        Directory.CreateDirectory(swep);
        Directory.CreateDirectory(attachments);
        var names = weapon.Sequences.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

        LuaFile.Write(Path.Combine(swep, "shared.lua"), Shared(weapon, className));
        LuaFile.Write(Path.Combine(swep, "animations.lua"), Animations(names));

        // Magazine capacity and smag/xmag reloads come from the part names (mag_20_9p vs the default mag_30_9p).
        var defaultMag = weapon.Groups.Select(group => QcAttachments.Capacity(group[0].Token)).FirstOrDefault(rounds => rounds is not null);
        var slots = new List<string>();
        foreach (var (group, index) in weapon.Groups.Select((group, index) => (group, index)))
        {
            var classes = new List<string>();
            if (weapon.EmptyDefaults?[index] == true)
            {
                // Optional extras (grip tapes): a model-less first entry keeps the slot empty by default.
                var none = $"{className}_no_{Identifier(QcAttachments.SlotName(group.Select(item => item.Token)))}";
                classes.Add(none);
                // Single-file: the group's blank bodygroup comes after its parts.
                var blank = weapon.Bodygroups is { } groups ? $"ATTACHMENT.Bodygroups = {{[\"{groups[index]}\"] = {group.Length}}}\n" : "";
                LuaFile.Write(AttachmentFile(attachments, Bases.GetValueOrDefault(group[0].Root, "att_base"), none),
                    $"ATTACHMENT.Base = \"{Bases.GetValueOrDefault(group[0].Root, "att_base")}\"\nATTACHMENT.Name = \"None\"\n" + blank);
            }
            foreach (var ((token, rootBone), value) in group.Select((item, value) => (item, value)))
            {
                var attClass = $"{className}_{Identifier(token)}";
                classes.Add(attClass);
                var baseClass = Bases.GetValueOrDefault(rootBone, "att_base");
                // Bodygroup value = position in the group (meshes list each part in order, blank last).
                var visual = weapon.Bodygroups is { } bodygroups
                    ? $"ATTACHMENT.Bodygroups = {{[\"{bodygroups[index]}\"] = {value}}}\n"
                    : $"ATTACHMENT.Model = Model(\"{weapon.AttachmentModels}att_{token}.mdl\")\n";
                var rounds = weapon.MagazineRounds is { } lists && lists.TryGetValue(token, out var list) ? list : ((string[], string[])?)null;
                LuaFile.Write(AttachmentFile(attachments, baseClass, attClass),
                    $"ATTACHMENT.Base = \"{baseClass}\"\nATTACHMENT.Name = \"{token}\"\n" + visual + Excluded(weapon, token, baseClass) + RoundLists(rounds)
                    + Stats(token, defaultMag, weapon.Suppressed?.Contains(token) == true, names));
            }
            slots.Add("    {" + string.Join(", ", classes.Select(item => $"\"{item}\"")) + "},");
        }
        slots.Insert(0, PerkSlot(attachments, className, names));
        slots.AddRange(SharedSlots(weapon, attachments, className));
        LuaFile.Write(Path.Combine(swep, "customization.lua"),
            "AddCSLuaFile()\n\n" + SuppressorStats(weapon.Fire) + "\n-- One table per slot; the first entry is equipped by default.\nSWEP.Customization = {\n"
            + string.Join('\n', slots) + "\n}\n\n-- NECESSARY: loads custom attachments from other authors\nrequire(\"mw_utils\")\nmw_utils.LoadInjectors(SWEP)\n");
        return root;
    }

    // MW Base suppressors (att_vm_silencer*) and integrally suppressed barrels call weapon:doSuppressorStats(); mg_base has
    // no default, so a weapon without it errors on equip. akilo47 swaps Primary.Sound to its .suppressed alias here.
    private static string SuppressorStats(CodFireSet? fire) => $$"""
        function SWEP:doSuppressorStats()
        {{(fire?.Suppressed is { } sup ? $"    self.Primary.Sound = Sound(\"{sup}\")\n" : "")}}{{(fire?.SuppressedAds is { } supAds ? $"    self.Primary.AdsSound = Sound(\"{supAds}\")\n" : "")}}    self.Reverb = {
                RoomScale = 50000,
                Sounds = {
                    Outside = {
                        Layer = Sound("Atmo_SMG_Sup.Outside"),
                        Reflection = Sound("Reflection_ARSUP.Outside")
                    },

                    Inside = {
                        Layer = Sound("Atmo_AR_Sup.Inside"),
                        Reflection = Sound("Reflection_ARSUP.Inside")
                    }
                }
            }
            self.ParticleEffects.MuzzleFlash = self.ParticleEffects.MuzzleFlash_Suppressed
        end

        """;

    /// <summary>
    /// att_magazine round lists: BulletList keeps the old count until the 9031 ResetBullets event (where j_mag1 turns into
    /// the new magazine), ReserveBulletList always shows the incoming magazine. Without them every round stays visible.
    /// </summary>
    private static string RoundLists((string[] Current, string[] Reserve)? rounds)
    {
        if (rounds is not { } lists) return "";
        static string List(string name, string[] bones) => $"ATTACHMENT.{name} = {{\n"
            + string.Concat(bones.Select((bone, index) => $"    [{index + 1}] = {{\"{bone}\"}},\n")) + "}\n";
        return "\n-- round, bone name (top round first)\n" + List("BulletList", lists.Current) + "\n" + List("ReserveBulletList", lists.Reserve);
    }

    /// <summary>
    /// Stats that follow from the part itself: magazine capacity and its smag/xmag reload/inspect animations, and the
    /// suppressed sound/flash of an integrally suppressed barrel. Handling numbers (ADS speed, range) are left to tune.
    /// </summary>
    private static string Stats(string token, int? defaultMag, bool suppressed, IReadOnlyDictionary<string, (string Name, int Frames, int? MagFrame)> sequences)
    {
        var lines = new List<string>();
        if (QcAttachments.Capacity(token) is { } rounds)
        {
            lines.Add($"weapon.Primary.ClipSize = {rounds}");
            if (QcAttachments.MagVariant(rounds, defaultMag) is { } variant)
            {
                var suffix = MagVariants.First(item => item.Variant == variant).Suffix;
                foreach (var key in MagVariantKeys(sequences, variant))
                    lines.Add($"weapon.Animations.{key} = weapon.Animations.{key}{suffix}");
            }
        }
        if (suppressed) lines.Add("weapon:doSuppressorStats()");
        if (lines.Count == 0) return "";
        return "\nlocal BaseClass = GetAttachmentBaseClass(ATTACHMENT.Base)\nfunction ATTACHMENT:Stats(weapon)\n    BaseClass.Stats(self, weapon)\n"
            + string.Concat(lines.Select(line => $"    {line}\n")) + "end\n";
    }

    /// <summary>
    /// Perk slot like the official automatic rifles (mg_mike4 / mg_kilo433 customization.lua): "att_perk" (none) first,
    /// then MW Base's shared perks. Sleight of Hand needs this weapon's fast reloads, so it gets its own attachment
    /// (attachment_vm_ar_akilo47_perk_soh). The perk slot comes first: Stats run in slot order, so a magazine's
    /// Reload = Reload_Xmag afterwards picks up the fast variant soh put there.
    /// </summary>
    private static string PerkSlot(string attachments, string className, IReadOnlyDictionary<string, (string Name, int Frames, int? MagFrame)> sequences)
    {
        var perks = new List<string> { "att_perk" };
        var swaps = new List<string>();
        foreach (var suffix in MagVariants.Select(variant => variant.Suffix).Prepend(""))
        foreach (var (slow, fast) in new[] { ("reload", "reload_fast"), ("reload_empty", "reload_empty_fast") })
        {
            var variant = suffix.Length == 0 ? "" : "_" + suffix[1..].ToLowerInvariant();
            if (!sequences.ContainsKey(slow + variant) || !sequences.ContainsKey(fast + variant)) continue;
            var key = slow == "reload" ? "Reload" : "Reload_Empty";
            swaps.Add($"    weapon.Animations.{key}{suffix} = weapon.Animations.{key}_Fast{suffix}");
        }
        if (swaps.Count > 0)
        {
            var soh = $"{className}_perk_soh";
            perks.Add(soh);
            LuaFile.Write(AttachmentFile(attachments, "att_perk", soh),
                "ATTACHMENT.Base = \"att_perk_soh\"\n\nlocal BaseClass = GetAttachmentBaseClass(ATTACHMENT.Base)\nfunction ATTACHMENT:Stats(weapon)\n    BaseClass.Stats(self, weapon)\n"
                + string.Join('\n', swaps) + "\nend\n");
        }
        // Burst rewrites Firemodes[1], which is Full Auto here (as on mike4); the rest only touch Bullet stats.
        perks.AddRange(["att_perk_mw_burst", "att_perk_fmj", "att_perk_ricochet", "att_perk_headshot"]);
        return "    {" + string.Join(", ", perks.Select(item => $"\"{item}\"")) + "},";
    }

    /// <summary>
    /// Categories a barrel cannot carry (akilo47 smgbarcust: ExcludedCategories = {"Grips"}): a shared muzzle/laser/grip
    /// slot exists, but this barrel lacks its tag (or, barsil, is suppressed and has no muzzle thread).
    /// </summary>
    private static string Excluded(MwBaseWeapon weapon, string token, string baseClass)
    {
        if (baseClass != "att_barrel" || weapon.BarrelMounts?.GetValueOrDefault(token) is not { } tags) return "";
        var mounts = weapon.Mounts ?? new HashSet<string>();
        var excluded = SharedAttachments.Where(set => set.Bones.Length > 0 && set.Entries.Any(entry => mounts.Contains(entry.Bone)))
            .Where(set => !set.Entries.Any(entry => tags.Contains(entry.Bone)) || set.Category == "muzzles" && weapon.Suppressed?.Contains(token) == true)
            .Select(set => set.Category switch { "muzzles" => "Muzzle Devices", "lasers" => "Lasers", _ => "Grips" }).ToArray();
        return excluded.Length == 0 ? "" : "ATTACHMENT.ExcludedCategories = {" + string.Join(", ", excluded.Select(item => $"\"{item}\"")) + "}\n";
    }

    /// <summary>
    /// MW Base's own sights, muzzles, lasers and grips (lua/weapons/mg_base/modules/attachmentss/*) for every mount the gun
    /// has, as on the official ARs. Each set is gated on the bone the shared models are rooted on (att_vm_holo_west01 is
    /// rooted on tag_holo...), which the receiver or a barrel must also carry, or the part would bonemerge to nothing.
    /// </summary>
    private static IEnumerable<string> SharedSlots(MwBaseWeapon weapon, string attachments, string className)
    {
        var mounts = weapon.Mounts ?? new HashSet<string>();
        var barrels = weapon.Groups.Where(group => Bases.GetValueOrDefault(group[0].Root) == "att_barrel").SelectMany(group => group).ToArray();
        foreach (var (category, bones, entries) in SharedAttachments)
        {
            var present = entries.Where(entry => mounts.Contains(entry.Bone)).Select(entry => entry.Class).ToArray();
            if (present.Length == 0) continue;
            // Muzzles/lasers/grips sit on barrel tags: parent them to the barrel (akilo47 attachments set this too) so a
            // longer barrel carries them forward. Only when every barrel that allows the category has the tags (barsil excludes
            // muzzles, so it does not count), or the default barrel would drop them. Single-file barrels are bodygroups of
            // the viewmodel itself (no model to parent to), so shared parts bonemerge onto the viewmodel directly.
            var carriers = barrels.Where(item => !(category == "muzzles" && weapon.Suppressed?.Contains(item.Token) == true)).ToArray();
            var onBarrel = category != "sights" && weapon.Bodygroups is null && carriers.Length > 0 && carriers.All(item => weapon.BarrelMounts?.GetValueOrDefault(item.Token)?.IsSupersetOf(bones) == true);
            var classes = present.Select(shared => onBarrel ? BarrelChild(attachments, className, shared, weapon, carriers.Select(item => item.Token).ToArray()) : shared);
            // Integrally suppressed barrels exclude Muzzle Devices themselves (Excluded), like smgbarcust excludes Grips.
            yield return "    {" + string.Join(", ", classes.Prepend(EmptyClass(category)).Select(item => $"\"{item}\"")) + "},";
        }
    }

    private static string EmptyClass(string category) => category switch
    {
        "sights" => "att_sight", "muzzles" => "att_muzzle", "lasers" => "att_laser", _ => "att_grip",
    };

    /// <summary>
    /// A copy of a shared attachment placed on the weapon's barrel. Like the official akilo47 grips/lasers it also sets
    /// what the shared class cannot know: the left-hand grip pose, and the receiver's laser rail cover ("laser" bodygroup,
    /// hidden without a laser). Grips and lasers are placed by an ATTACHMENT.VElement on tag_barrel_attach (MW Base's own
    /// per-attachment offset, see mcxs_tac): the default barrel's mount tag goes in Position/Angles, every other barrel whose
    /// tag differs gets an Offsets entry. Unlike bonemerging onto the shared mount tag, each grip/laser can be nudged on its
    /// own, as CoD16-era shared models never sit right on every CoD20 handguard. Muzzles keep BonemergeToCategory Barrels.
    /// </summary>
    private static string BarrelChild(string attachments, string className, string shared, MwBaseWeapon weapon, string[] barrels)
    {
        var child = $"{className}_{shared["att_vm_".Length..]}";
        var mount = SharedAttachments.SelectMany(set => set.Entries).First(entry => entry.Class == shared).Bone;
        var text = new StringBuilder($"ATTACHMENT.Base = \"{shared}\"\n");
        // The rail cover is CoD's cylinder-laser ring mount: box lasers (laser01-03) clamp on the handguard without it.
        if (BaseOf(shared) == "att_laser" && weapon.LaserRail && shared.Contains("cylinder", StringComparison.Ordinal))
            text.Append("ATTACHMENT.Bodygroups = {\n    [\"laser\"] = 1\n}\n");
        var tags = barrels.Select(token => (token, Tag: weapon.BarrelTags?.GetValueOrDefault(token)?.GetValueOrDefault(mount))).ToArray();
        // Muzzles stay bonemerged children of the barrel: mg_viewmodel:FindAttachment("muzzle") takes the deepest child
        // with that attachment, so a VElement muzzle (a sibling of the barrel) lost to the barrel's bare-tip muzzle.
        if (BaseOf(shared) is "att_grip" or "att_laser" && tags.FirstOrDefault().Tag is { } home)
        {
            var (position, angles) = VElementOf(home);
            text.Append($"-- Placed on the barrel tag (MW Base VElement): tune Position/Angles per attachment, then re-equip.\n"
                + $"ATTACHMENT.VElement = {{\n    Bone = \"tag_barrel_attach\",\n    Position = {position},\n    Angles = {angles}");
            var offsets = tags.Skip(1).Where(item => item.Tag is { } tag && (Vector3.Distance(tag.P, home.P) > 1e-3f || MathF.Abs(Quaternion.Dot(tag.Q, home.Q)) < 0.99999f))
                .Select(item => (Class: $"{className}_{Identifier(item.token)}", Delta: VElementOf(item.Tag!.Value, home))).ToArray();
            if (offsets.Length > 0)
                text.Append(",\n    Offsets = {\n" + string.Join(",\n", offsets.Select(item => $"        [\"{item.Class}\"] = {{{item.Delta.Position}, {item.Delta.Angles}}}")) + "\n    }");
            text.Append("\n}\n");
        }
        else text.Append("ATTACHMENT.BonemergeToCategory = {\"Barrels\"}\n");
        if (GripPose(shared, weapon.GripLayers) is { } pose)
            text.Append($"\nlocal BaseClass = GetAttachmentBaseClass(ATTACHMENT.Base)\n\nfunction ATTACHMENT:PostProcess(weapon)\n    BaseClass.PostProcess(self, weapon)\n    weapon:SetGripPoseParameter(\"{pose}\")\nend\n");
        LuaFile.Write(AttachmentFile(attachments, BaseOf(shared), child), text.ToString());
        return child;
    }

    /// <summary>
    /// MW Base VElement fields for a tag in tag_barrel_attach space (inches). SWEP:MoveVElement turns Angle() by roll about
    /// forward, then by Angles.y about the new right, then by Angles.p about the new up, so the frame is
    /// Rx(r) * Ry(-y) * Rz(p) (checked in Gmod 2026-10-17: y = 30 gives QAngle pitch -30, p = 30 gives yaw 30), and Position
    /// is x along the final right, y along forward, z along up.
    /// </summary>
    internal static (string Position, string Angles) VElementOf((Vector3 P, Quaternion Q) tag, (Vector3 P, Quaternion Q)? relativeTo = null)
    {
        var (p, q) = tag;
        if (relativeTo is { } baseTag)
        {
            // Offsets add to Position/Angles component-wise; report the difference of the two VElements.
            var (bp, ba) = VElementParts(baseTag);
            var (tp, ta) = VElementParts(tag);
            return (Vec(tp - bp), Ang(ta - ba));
        }
        var (position, angles) = VElementParts((p, q));
        return (Vec(position), Ang(angles));
        static string Vec(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"Vector({v.X:0.####}, {v.Y:0.####}, {v.Z:0.####})");
        static string Ang(Vector3 a) => string.Create(CultureInfo.InvariantCulture, $"Angle({a.X:0.##}, {a.Y:0.##}, {a.Z:0.##})");
    }

    /// <summary>VElement Position (x right, y forward, z up, in the rotated frame) and Angles (p, y, r) for a barrel-space tag.</summary>
    private static (Vector3 Position, Vector3 Angles) VElementParts((Vector3 P, Quaternion Q) tag)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(tag.Q));
        // Rows are the tag's forward / left / up in barrel space; as columns they form R = Rx(a) * Ry(b) * Rz(c).
        Vector3 forward = new(m.M11, m.M12, m.M13), left = new(m.M21, m.M22, m.M23), up = new(m.M31, m.M32, m.M33);
        var b = MathF.Asin(Math.Clamp(up.X, -1, 1));
        var a = MathF.Atan2(-up.Y, up.Z);
        var c = MathF.Atan2(-left.X, forward.X);
        const float Deg = 180 / MathF.PI;
        var position = new Vector3(-Vector3.Dot(tag.P, left), Vector3.Dot(tag.P, forward), Vector3.Dot(tag.P, up));
        return (position, new Vector3(c * Deg, -b * Deg, a * Deg));
    }

    /// <summary>
    /// Left-hand pose per shared grip, as the official akilo47 grips set it (angled -> grip_ang_offset, vertical/bipod ->
    /// grip_vert_offset, stubby -> grip_vert_pro_offset); a pose the weapon did not compile falls back to grip_vert_offset.
    /// </summary>
    private static string? GripPose(string shared, IReadOnlyList<string> layers)
    {
        if (BaseOf(shared) != "att_grip") return null;
        var pose = shared.Contains("angledgrip", StringComparison.Ordinal) ? "grip_ang_offset"
            : shared.Contains("stubbygrip", StringComparison.Ordinal) ? "grip_vert_pro_offset" : "grip_vert_offset";
        return layers.Contains(pose) ? pose : layers.Contains("grip_vert_offset") ? "grip_vert_offset" : null;
    }

    private static string BaseOf(string shared) => SharedAttachments.First(set => set.Entries.Any(entry => entry.Class == shared)).Category switch
    {
        "muzzles" => "att_muzzle", "lasers" => "att_laser", _ => "att_grip",
    };

    // Root bone of each shared model, read from its .mdl in modern_wokefare_base (attachments/*.mdl, bone 0).
    private static readonly (string Category, string[] Bones, (string Class, string Bone)[] Entries)[] SharedAttachments =
    [
        ("sights", [], [
            ("att_vm_reflex_east01", "tag_reflex"), ("att_vm_reflex_west03", "tag_reflex"), ("att_vm_minireddot01_tall", "tag_reflex"),
            ("att_vm_minireddot02_tall", "tag_reflex"), ("att_vm_holo_west01", "tag_holo"), ("att_vm_holo_east01", "tag_holo"),
            ("att_vm_reflex_02", "tag_holo"), ("att_vm_2x_west02_holo", "tag_holo"), ("att_vm_hybrid_west01", "tag_hybrid"),
            ("att_vm_thermal_east01_hybrid", "tag_hybrid"), ("att_vm_4x_west01_tall", "tag_acog_2"), ("att_vm_4x_east01_tall", "tag_acog_2"),
            ("att_vm_thermal_west01", "tag_thermal"), ("att_vm_scope_mike14", "tag_scope"),
        ]),
        ("muzzles", ["tag_silencer"], [
            ("att_vm_flashhider01", "tag_silencer"), ("att_vm_flashhider02", "tag_silencer"), ("att_vm_compensator01", "tag_silencer"),
            ("att_vm_compensator02", "tag_silencer"), ("att_vm_muzzlebrake01", "tag_silencer"), ("att_vm_muzzlebrake02", "tag_silencer"),
            ("att_vm_breacher01", "tag_silencer"), ("att_vm_silencer01", "tag_silencer"), ("att_vm_silencer02", "tag_silencer"),
            ("att_vm_silencer03", "tag_silencer"), ("att_vm_silencer04", "tag_silencer"),
        ]),
        ("lasers", ["tag_laser_attach"], [
            ("att_vm_laser01", "tag_laser_attach"), ("att_vm_laser01_cylinder", "tag_laser_attach"), ("att_vm_laser02", "tag_laser_attach"),
            ("att_vm_laser02_cylinder", "tag_laser_attach"), ("att_vm_laser03", "tag_laser_attach"),
        ]),
        ("grips", ["tag_grip_attach", "tag_folding_grip"], [
            ("att_vm_angledgrip01", "tag_grip_attach"), ("att_vm_angledgrip02", "tag_grip_attach"), ("att_vm_vertgrip01", "tag_folding_grip"),
            ("att_vm_vertgrip02", "tag_folding_grip"), ("att_vm_vertgrip03", "tag_folding_grip"), ("att_vm_stubbygrip01", "tag_folding_grip"),
            ("att_vm_stubbygrip02", "tag_folding_grip"), ("att_vm_bipodgrip", "tag_folding_grip"),
        ]),
    ];

    // Base class -> subfolder, as in the official akilo47 pack (attachmentss/akilo47/barrels, mags, stock...).
    // mw_assets/attachments.lua loads attachmentss recursively and names classes by file, so folders are only for people.
    private static readonly Dictionary<string, string> Folders = new(StringComparer.Ordinal)
    {
        ["att_barrel"] = "barrels", ["att_magazine"] = "mags", ["att_stock"] = "stock", ["att_pistolgrip"] = "pistolgrips",
        ["att_grip"] = "grips", ["att_sight"] = "sights", ["att_muzzle"] = "muzzles", ["att_laser"] = "lasers", ["att_perk"] = "perks",
    };

    private static string AttachmentFile(string attachments, string baseClass, string attClass)
    {
        var folder = Path.Combine(attachments, Folders.GetValueOrDefault(baseClass, "misc"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, attClass + ".lua");
    }

    /// <summary>Base keys (Reload, Inspect...) whose sequence has a compiled _&lt;variant&gt; version.</summary>
    private static IEnumerable<string> MagVariantKeys(IReadOnlyDictionary<string, (string Name, int Frames, int? MagFrame)> sequences, string variant) =>
        AnimationKeys.Where(entry => entry.Key.StartsWith("Reload", StringComparison.Ordinal) || entry.Key.StartsWith("Inspect", StringComparison.Ordinal))
            .Where(entry => entry.Sequences.Length > 0 && sequences.ContainsKey($"{entry.Sequences[0]}_{variant}"))
            .Select(entry => entry.Key);

    private static string Animations(IReadOnlyDictionary<string, (string Name, int Frames, int? MagFrame)> sequences)
    {
        var text = new StringBuilder("AddCSLuaFile()\n\n-- Generated by Alchemy Stars. Lengths are seconds at Fps 30; tune per feel.\nSWEP.Animations = {\n");
        text.AppendLine("    [\"Idle\"] = {\n        Sequences = {\"idle\"},\n        Fps = 30,\n        Events = {\n            {Time = 0, Callback = function(self) self:EnableGrip() end},\n        }\n    },\n");
        // Each base key, then its _Smag/_Xmag copies (Reload_Xmag -> reload_xmag) that magazine attachments swap in.
        var entries = AnimationKeys.Concat(MagVariants.SelectMany(variant => MagVariantKeys(sequences, variant.Variant)
            .Select(key => (Key: key + variant.Suffix, Sequences: new[] { $"{AnimationKeys.First(entry => entry.Key == key).Sequences[0]}_{variant.Variant}" }))));
        foreach (var (key, candidates) in entries)
        {
            var present = candidates.Where(sequences.ContainsKey).ToArray();
            if (present.Length == 0) continue;
            if (key is not ("Melee" or "Melee_Hit")) present = present[..1];
            var first = sequences[present[0]];
            text.AppendLine($"    [\"{key}\"] = {{");
            text.AppendLine($"        Sequences = {{{string.Join(", ", present.Select(name => $"\"{name}\""))}}},");
            text.AppendLine("        Fps = 30,");
            text.AppendLine($"        Length = {Seconds(first.Frames)},");
            if (key.StartsWith("Reload", StringComparison.Ordinal))
                text.AppendLine($"        MagLength = {Seconds(first.MagFrame ?? (int)(first.Frames * 0.6f))},");
            if (key == "Melee") text.AppendLine("        Size = 15,\n        Range = 40,");
            if (key == "Melee_Hit") text.AppendLine("        Damage = 50,");
            if (key is not ("Sprint_In" or "Super_Sprint_In" or "Holster")) text.AppendLine("        NextSequence = \"Idle\",");
            var events = new List<string>();
            if (key is "Melee" or "Melee_Hit")
            {
                events.Add("{Time = 0, Callback = function(self) self:DisableGrip() end}");
                events.Add($"{{Time = 0, Callback = function(self) self:DoSound(Sound(\"MW_Melee.{(key == "Melee" ? "Miss" : "Flesh")}_Medium\")) end}}");
                events.Add($"{{Time = {Seconds((int)(first.Frames * 0.8f))}, Callback = function(self) self:EnableGrip() end}}");
            }
            else if (key is "Equip" or "Sprint_In" or "Sprint_Out" or "Jog_Out" or "Land")
                events.Add("{Time = 0, Callback = function(self) self:EnableGrip() end}");
            if (events.Count > 0)
                text.AppendLine("        Events = {\n" + string.Concat(events.Select(item => $"            {item},\n")) + "        }");
            text.AppendLine("    },\n");
        }
        return text.AppendLine("}").ToString();
    }

    private static string Shared(MwBaseWeapon weapon, string className) => $$$"""
        AddCSLuaFile()

        include("animations.lua")
        include("customization.lua")

        -- Generated by Alchemy Stars from CAST assets. Gameplay values below are placeholders.
        SWEP.Base = "mg_base"
        SWEP.GripPoseParameters = {{{{string.Join(", ", weapon.GripLayers.Select(item => $"\"{item}\""))}}}}

        SWEP.PrintName = "{{{LuaString(weapon.PrintName.Length > 0 ? weapon.PrintName : weapon.Name)}}}"
        SWEP.Category = "Modern Warfare"
        SWEP.SubCategory = "Generated"
        SWEP.Spawnable = true
        SWEP.VModel = Model("{{{weapon.ViewModel}}}")
        SWEP.WorldModel = Model("{{{weapon.WorldModel}}}")

        SWEP.Slot = 2
        SWEP.HoldType = "Rifle"

        SWEP.Primary.Sound = Sound("{{{weapon.Fire?.Fire ?? "Weapon_SMG1.Single"}}}"){{{(weapon.Fire?.FireAds is { } ads ? $"\nSWEP.Primary.AdsSound = Sound(\"{ads}\")" : "")}}}
        SWEP.Primary.Ammo = "SMG1"
        SWEP.Primary.ClipSize = {{{weapon.Groups.Select(group => QcAttachments.Capacity(group[0].Token)).FirstOrDefault(rounds => rounds is not null) ?? 30}}}
        SWEP.Primary.Automatic = true
        SWEP.Primary.BurstRounds = 1
        SWEP.Primary.BurstDelay = 0
        SWEP.Primary.RPM = 800
        SWEP.CanChamberRound = true

        SWEP.ParticleEffects = {
            ["MuzzleFlash"] = "mwb_muzzle_smg_0",
            ["MuzzleFlash_Suppressed"] = "mwb_suppressor_0",
            ["Ejection"] = "mwb_shelleject",
        }

        SWEP.Reverb = {
            RoomScale = 50000,
            Sounds = {
                Outside = {
                    Layer = Sound("Atmo_SMG.Outside"),
                    Reflection = Sound("Reflection_AR.Outside")
                },

                Inside = {
                    Layer = Sound("Atmo_SMG.Inside"),
                    Reflection = Sound("Reflection_AR.Inside")
                },
            }
        }

        SWEP.Firemodes = {
            [1] = {
                Name = "Full Auto",
                OnSet = function()
                    return "Firemode_Auto"
                end
            },

            [2] = {
                Name = "Semi Auto",
                OnSet = function(self)
                    self.Primary.Automatic = false

                    return "Firemode_Semi"
                end
            },
        }

        SWEP.Cone = {
            Hip = 0.4,
            TacStance = 0.15,
            Ads = 0,
            Increase = 0.08,
            TacStanceMultiplier = 0.8,
            AdsMultiplier = 0,
            Max = 1.5
        }

        SWEP.Recoil = {
            Vertical = {0.8, 1.0},
            Horizontal = {-0.6, 0.6},
            Shake = 1.2,
            AdsMultiplier = 0.5,
            Seed = {{{Seed(className)}}}
        }

        SWEP.Bullet = {
            Damage = {28, 15},
            DropOffStartRange = 15,
            EffectiveRange = 35,
            Range = 150,
            Tracer = false,
            NumBullets = 1,
            PhysicsMultiplier = 1,
            HeadshotMultiplier = 1,
            Penetration = {
                DamageMultiplier = 0.8,
                MaxCount = 3,
                Thickness = 10
            },
        }

        SWEP.Zoom = {
            IdleSway = 0.1,
            FovMultiplier = 0.95,
            ViewModelFovMultiplier = 1,
            Blur = {
                EyeFocusDistance = 8
            }
        }

        -- w_ is rigged like the official w_mcharlie (hand-rooted, same tag_sling pose); these are its offsets.
        SWEP.WorldModelOffsets = {
            Bone = "tag_sling",
            Angles = Angle(0, 95, -90),
            Pos = Vector(3, -6, -3.5)
        }

        SWEP.ViewModelOffsets = {
            Aim = {
                Angles = Angle(0, 0, 0),
                Pos = Vector(0, 0, 0)
            },

            Idle = {
                Angles = Angle(0, 0, 0),
                Pos = Vector(0, 0, 0)
            },
        }

        SWEP.Shell = "mwb_shelleject_9mm"
        """ + "\n";

    private static string Seconds(int frames) => (Math.Max(1, frames) / Fps).ToString("0.###", CultureInfo.InvariantCulture);

    private static int Seed(string text) => (int)(text.Aggregate(17u, (hash, c) => hash * 31 + c) % 100000);

    /// <summary>Lua/file-safe lowercase identifier.</summary>
    public static string Identifier(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

    /// <summary>Escapes text for a double-quoted Lua string (display names like 9mm "PM").</summary>
    public static string LuaString(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
