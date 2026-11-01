using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlchemyStars.Engine;

/// <summary>
/// MW3 2023 (COD20) gameplay numbers from cod20_weapons.json (D:\cod_tool\cod20_dump, tools/export.py): weapon damage,
/// ranges, fire rate, ADS time, hip spread, recoil, and per-attachment modifier scales (only values != 1).
/// </summary>
public sealed partial class Cod20Stats
{
    [GeneratedRegex("^\\s*\"#hash_([0-9a-f]+)\": \"(.*)\",?\\s*$", RegexOptions.Multiline)] private static partial Regex LocalizeLine();
    /// <param name="Name">CoD weapon asset, e.g. jup_jp03_sm_pmike_mp.</param>
    /// <param name="Damage">Near to far, 4 values (0 = unused band).</param>
    /// <param name="RangeM">Damage band ends in meters (0 = unused).</param>
    /// <param name="RecoilVertical">Mean of the 12 vertical recoil stages (CoD units).</param>
    public sealed record Weapon(string Name, int[] Damage, float[] RangeM, float Rpm, int AdsMs, float HipSpreadMax,
        float GunKick, float RecoilVertical, float Head, float Leg)
    {
        /// <summary>Attachment keys of this weapon: jup_jp03_sm_pmike_mp -> jup_jp03_sm_pmike_.</summary>
        public string Prefix => Name.EndsWith("_mp", StringComparison.Ordinal) ? Name[..^2] : Name + "_";
    }

    // MW Base reference gun: official mg_mike4 (Cone.Hip 0.45, Recoil.Shake 1.1, Vertical {0.5, 0.75}) against
    // iw9_ar_mike4_mp (hip 7.5, gun kick 24, vertical 51.5). CoD spread/kick units have no MW Base equivalent, only ratios.
    private const float HipPerCod = 0.45f / 7.5f, ShakePerKick = 1.1f / 24f, VerticalPerCod = 0.5f / 51.5f;

    private readonly Dictionary<string, Weapon> weapons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, float>> mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, int Clip)> magazines = [];
    private readonly Dictionary<ulong, string> localize = [];
    private readonly Dictionary<string, string> reticlePreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> blueprintNames = new(StringComparer.OrdinalIgnoreCase);
    private string folder = "";

    /// <summary>Reads cod20_weapons.json (the file or the folder holding it); null when the path is empty or missing.</summary>
    public static Cod20Stats? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var file = Directory.Exists(path) ? Path.Combine(path, "cod20_weapons.json") : path;
        if (!File.Exists(file)) return null;
        using var json = JsonDocument.Parse(File.ReadAllBytes(file));
        var stats = new Cod20Stats { folder = Path.GetDirectoryName(file)! };
        var root = json.RootElement;
        foreach (var item in root.GetProperty("weapons").EnumerateArray())
        {
            if (item.GetProperty("rpm").ValueKind != JsonValueKind.Number) continue;
            static float F(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0;
            var stages = item.TryGetProperty("recoil_v_stages", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(v => v.GetSingle()).ToArray() : [];
            var loc = item.TryGetProperty("locdmg", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
            float Loc(string key) => loc.ValueKind == JsonValueKind.Object ? F(loc, key) : 1;
            var name = item.GetProperty("name").GetString()!;
            stats.weapons[name] = new(name,
                [.. item.GetProperty("damage").EnumerateArray().Select(v => v.GetInt32())],
                [.. item.GetProperty("range_m").EnumerateArray().Select(v => v.GetSingle())],
                F(item, "rpm"), (int)F(item, "ads_ms"), F(item, "hip_spread_max"), F(item, "gun_kick"),
                stages.Length > 0 ? stages.Average() : 0, Loc("head"), Loc("r_leg_upper"));
        }
        foreach (var item in root.GetProperty("magazines").EnumerateArray())
            stats.magazines.Add((item.GetProperty("name").GetString()!, item.GetProperty("clip").GetInt32()));
        foreach (var item in root.GetProperty("attachment_mods").EnumerateObject())
            stats.mods[item.Name] = item.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetSingle());
        if (root.TryGetProperty("attachment_reticles", out var reticles))
            foreach (var item in reticles.EnumerateObject())
                if (item.Value.GetProperty("preview").GetString() is { Length: > 0 } preview) stats.reticlePreviews[item.Name] = preview;
        if (root.TryGetProperty("blueprints", out var blueprints))
            foreach (var item in blueprints.EnumerateObject())
                if (item.Value.GetProperty("name").GetString() is { Length: > 0 } title)
                    foreach (var part in item.Value.GetProperty("parts").EnumerateArray())
                        stats.blueprintNames.TryAdd(part.GetProperty("model").GetString()!, title);
        // The dump's English strings ({"#hash_<fnv>": text}), for attachment display names.
        var strings = Path.Combine(Path.GetDirectoryName(file)!, "dump", "localize.json");
        if (File.Exists(strings))
        {
            // Not strict JSON (raw control characters in some strings): read it line by line like the dump's tools/loc.py.
            foreach (Match line in LocalizeLine().Matches(File.ReadAllText(strings)))
                if (ulong.TryParse(line.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
                    stats.localize[hash] = Regex.Unescape(line.Groups[2].Value).Trim();
        }
        return stats;
    }

    /// <summary>Display name of the blueprint a part model belongs to (vm_jup_jp03_sm_pmike_bar_long_2516 -> its V2516 name), or null.</summary>
    public string? BlueprintName(string model) => blueprintNames.GetValueOrDefault(model);

    /// <summary>The MP weapon of a CoD token (sm_pmike -> jup_jp03_sm_pmike_mp).</summary>
    public Weapon? Find(string token) =>
        weapons.Values.Where(w => w.Name.EndsWith($"_{token}_mp", StringComparison.OrdinalIgnoreCase)).OrderBy(w => w.Name.Length).FirstOrDefault();

    /// <summary>
    /// Modifiers of one of the weapon's own parts. Barrels/stocks/grips use the CoD part name (bar_long); magazines are
    /// matched by capacity, since the model token (mag_50_9p) and the attachment (drum_50_9mm) are named differently.
    /// </summary>
    public IReadOnlyDictionary<string, float> PartMods(Weapon weapon, string token, int? capacity) => Mods(PartAttachment(weapon, token, capacity));

    /// <summary>The CoD attachment of one of the weapon's own parts (see PartMods).</summary>
    public string PartAttachment(Weapon weapon, string token, int? capacity) =>
        capacity is { } clip && magazines.FirstOrDefault(m => m.Clip == clip && m.Name.StartsWith(weapon.Prefix, StringComparison.OrdinalIgnoreCase)).Name is { } mag
            ? mag : weapon.Prefix + token;

    /// <summary>
    /// English display name of a CoD attachment ("Thorn-90 Barrel"), from localize key JUP_ATTACHMENTS/&lt;NAME&gt; (MW3) or
    /// ATTACHMENTS/&lt;NAME&gt; (MW2019 parts: fourx01 -> "VLK 4.0 Optic"); null when unknown. Keys are FNV-1a 64 of the
    /// lowercased key with IW's offset basis (dump tools/loc.py), stored with or without the top bit.
    /// </summary>
    public string? Name(string attachment) => Lookup("jup_attachments/" + attachment) ?? Lookup("attachments/" + attachment);

    /// <summary>CoD description of the weapon (JUP_WEAPON/JUP_JP03_SM_PMIKE_DESC), or null.</summary>
    public string? Description(Weapon weapon) => Lookup($"jup_weapon/{weapon.Prefix}desc");

    // shortcut: magazine-name ammo tokens only; unknown tokens give no caliber, add entries as weapons need them.
    private static readonly Dictionary<string, string> Calibers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["9mm"] = "9x19mm", ["9p"] = "9x19mm", ["45acp"] = ".45 ACP", ["556n"] = "5.56x45mm NATO", ["762n"] = "7.62x51mm NATO",
        ["762s"] = "7.62x39mm", ["762r"] = "7.62x54mmR", ["545"] = "5.45x39mm", ["12g"] = "12 Gauge", ["410g"] = ".410 Bore",
        ["450b"] = ".450 Bushmaster", ["338lmp"] = ".338 Lapua Magnum", ["50b"] = ".50 BMG", ["58"] = "5.8x42mm",
    };

    /// <summary>Caliber from the weapon's magazine names (jup_jp03_sm_pmike_mag_20_9mm -> 9x19mm), or null.</summary>
    public string? Caliber(Weapon weapon) => magazines.Where(m => m.Name.StartsWith(weapon.Prefix, StringComparison.OrdinalIgnoreCase))
        .Select(m => Calibers.GetValueOrDefault(m.Name[(m.Name.LastIndexOf('_') + 1)..])).FirstOrDefault(c => c is not null);

    /// <summary>
    /// Manufacturer from data/weapon/&lt;name&gt;.json ($attachment_manufacturer_gerhardwolf). The dump has no key to its display
    /// string, so it only matches an English string equal to the token ignoring case and punctuation ("Gerhard &amp; Wolff");
    /// null otherwise rather than a guess.
    /// </summary>
    public string? Manufacturer(Weapon weapon)
    {
        var file = Path.Combine(folder, "data", "weapon", weapon.Name + ".json");
        if (!File.Exists(file) || ManufacturerRef().Match(File.ReadAllText(file)) is not { Success: true } match) return null;
        var token = match.Groups[1].Value.Replace("_", "", StringComparison.Ordinal);
        static string Plain(string text) => new([.. text.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
        return localize.Values.Where(v => v.Length < 40 && Plain(v) is var plain && plain.StartsWith(token, StringComparison.Ordinal) && plain.Length - token.Length <= 1)
            .OrderBy(v => v.Length).FirstOrDefault();
    }

    [GeneratedRegex("\\$(?:weapon|attachment)_manufacturer_([a-z0-9_]+)\"")] private static partial Regex ManufacturerRef();

    private string? Lookup(string key)
    {
        var hash = 0x47F5817A5EF961BAUL;
        foreach (var c in Encoding.UTF8.GetBytes(key.ToLowerInvariant())) hash = (hash ^ c) * 0x100000001B3UL;
        return localize.TryGetValue(hash, out var name) || localize.TryGetValue(hash & 0x7FFFFFFFFFFFFFFFUL, out name)
            ? name.Length > 0 ? name : null : null;
    }

    /// <summary>
    /// Menu preview image of a sight's ADS reticle (jup_reflex02 -> jup_ui_reticle_reflex02), or null. Model tokens differ
    /// from attachment names by mount variants (_tall, _pstl, _le) and the MW2022 iw9_ prefix.
    /// </summary>
    public string? ReticlePreview(string token)
    {
        var bare = MountVariant().Replace(token, "");
        foreach (var name in new[] { token, bare, "iw9_" + token, "iw9_" + bare, bare + "_tall", "jup_" + bare })
            if (reticlePreviews.TryGetValue(name, out var preview)) return preview;
        return null;
    }

    [GeneratedRegex("_(le_tall|le|pstl|tall)$")] private static partial Regex MountVariant();

    /// <summary>Modifiers of a shared CoD attachment (jup_grip_vert_01); empty when CoD changes nothing.</summary>
    public IReadOnlyDictionary<string, float> Mods(string attachment) =>
        mods.TryGetValue(attachment, out var values) ? values : new Dictionary<string, float>();

    /// <summary>Whether CoD lists the attachment at all (a shared part without an entry keeps its hand-written stats).</summary>
    public bool Knows(string attachment) => mods.ContainsKey(attachment);

    /// <summary>
    /// MW Base ATTACHMENT:Stats lines for CoD modifier scales. Speeds are Fps (MW Base time = Length*30/Fps): ADS speed
    /// multiplies, sprint-to-fire is a time scale and divides. Move/sprint speed and bullet velocity have no MW Base stat.
    /// </summary>
    public static IEnumerable<string> StatLines(IReadOnlyDictionary<string, float> scales)
    {
        string N(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        if (scales.TryGetValue("ads_speed_scale", out var ads))
        {
            yield return $"weapon.Animations.Ads_In.Fps = weapon.Animations.Ads_In.Fps * {N(ads)}";
            yield return $"weapon.Animations.Ads_Out.Fps = weapon.Animations.Ads_Out.Fps * {N(ads)}";
        }
        if (scales.TryGetValue("sprint_to_fire_scale", out var stf))
            yield return $"weapon.Animations.Sprint_Out.Fps = weapon.Animations.Sprint_Out.Fps / {N(stf)}";
        foreach (var (key, field) in new[] { ("recoil_vertical_scale", "Vertical"), ("recoil_horizontal_scale", "Horizontal") })
            if (scales.TryGetValue(key, out var recoil))
                for (var index = 1; index <= 2; index++)
                    yield return $"weapon.Recoil.{field}[{index}] = weapon.Recoil.{field}[{index}] * {N(recoil)}";
        if (scales.TryGetValue("gun_kick_scale", out var kick)) yield return $"weapon.Recoil.Shake = weapon.Recoil.Shake * {N(kick)}";
        if (scales.TryGetValue("hip_spread_max_scale", out var hip)) yield return $"weapon.Cone.Hip = weapon.Cone.Hip * {N(hip)}";
        if (scales.TryGetValue("range_scale", out var range))
        {
            yield return $"weapon.Bullet.DropOffStartRange = weapon.Bullet.DropOffStartRange * {N(range)}";
            yield return $"weapon.Bullet.EffectiveRange = weapon.Bullet.EffectiveRange * {N(range)}";
        }
    }

    /// <summary>MW Base base stats of a weapon (shared.lua values), see the reference-gun note for spread and recoil.</summary>
    public static (int Near, int Far, float DropOff, float Effective, float Rpm, float Hip, float VerticalMin, float VerticalMax,
        float Shake, float Headshot, float Limb) Base(Weapon weapon)
    {
        var bands = weapon.Damage.Where(d => d > 0).ToArray();
        var ends = weapon.RangeM.Where(r => r > 0).ToArray();
        var dropOff = ends.Length > 0 ? ends[0] : 0;
        // MW Base lerps over EffectiveRange after DropOffStartRange and divides by it: keep at least 1 m.
        var effective = Math.Max(1, ends.Length > 0 ? ends[^1] - dropOff : 1);
        var vertical = weapon.RecoilVertical * VerticalPerCod;
        // MW Base: head = dmg * 0.5 * (HeadshotMultiplier * 2), legs = dmg * 4 * 0.75 * LimbMultiplier; then GMod's default
        // Scale*Damage doubles the head and quarters limbs. So in game head = 2 * HeadshotMultiplier, legs = 0.75 * LimbMultiplier.
        return (bands.Length > 0 ? bands[0] : 0, bands.Length > 0 ? bands[^1] : 0, dropOff, effective, weapon.Rpm,
            weapon.HipSpreadMax * HipPerCod, vertical, vertical * 1.5f, weapon.GunKick * ShakePerKick,
            weapon.Head / 2, weapon.Leg / 0.75f);
    }
}
