using System.Text;
using System.Text.RegularExpressions;

namespace AlchemyStars.Engine;

/// <summary>Gun-butt melee sounds registered for the weapon (sound.Add names); null members fall back to MW Base's MW_Melee.*.</summary>
public sealed record CodMeleeSet(string? Swing, string? Flesh, string? World);

/// <summary>Sounds the QC events need: alias -> Gmod sound paths (sound/ relative) and lines describing what is missing.</summary>
public sealed record CodSoundSet(IReadOnlyDictionary<string, string[]> Aliases, IReadOnlyList<string> Missing, string? SoundScript);

/// <summary>
/// Resolves CoD foley aliases from animation notetracks (wfoly_plr_sm_pmike_reload_01) to exported sound files.
/// Saluki writes alias tables to sndbanks/csv (name,secondary,file) next to sounds/; each alias row is one random variant.
/// Used files are copied to &lt;output&gt;/sound/weapons/&lt;weapon&gt;/ (resampled to 44.1 kHz: Source rejects 48 kHz wav)
/// and registered with sound.Add, so QC 5004 events and ARC9 EventTables play them by alias name.
/// </summary>
public static partial class CodSoundResolver
{
    private static readonly int[] SourceRates = [11025, 22050, 44100];

    /// <summary>Foley aliases fired by 5004 events in the given QC sequence bodies.</summary>
    public static IReadOnlyList<string> Aliases(IEnumerable<string> bodies) =>
        bodies.SelectMany(body => SoundEvent().Matches(body).Select(match => match.Groups[1].Value))
            .Where(name => name.StartsWith("wfoly", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    /// <param name="script">Sound script the sound.Add calls are appended to (shared with <see cref="CodFireSound"/>).</param>
    public static CodSoundSet Resolve(IReadOnlyList<string> aliases, string soundDirectory, string output, string weaponName, string script)
    {
        var table = AliasTable(soundDirectory);
        var missing = new List<string>();
        if (table.Count == 0) missing.Add("(no sndbanks/csv alias table found next to the sounds folder)");
        var resolved = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        var levels = new Dictionary<string, int>();
        var target = Path.Combine(output, "sound", "weapons", weaponName);
        foreach (var alias in aliases)
        {
            if (!table.TryGetValue(alias, out var files)) { missing.Add($"{alias}  (not in alias table)"); continue; }
            var paths = new List<string>();
            foreach (var file in files)
            {
                var source = Find(soundDirectory, file);
                if (source is null) { missing.Add($"{alias}  → {file}"); continue; }
                var name = Path.GetFileName(file).Split('.')[0] + Path.GetExtension(source).ToLowerInvariant();
                Directory.CreateDirectory(target);
                Copy(source, Path.Combine(target, name));
                paths.Add($"weapons/{weaponName}/{name}");
                if (Level().Match(file) is { Success: true } level) levels[alias] = int.Parse(level.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (paths.Count > 0) resolved[alias] = [.. paths];
        }
        if (resolved.Count == 0) return new(resolved, missing, null);
        var lua = new StringBuilder("\n-- Foley: CoD aliases the viewmodel's QC events / ARC9 EventTable play\n");
        foreach (var (alias, paths) in resolved)
        {
            lua.AppendLine().AppendLine("sound.Add({");
            lua.AppendLine($"    name = \"{alias}\",");
            lua.AppendLine("    channel = CHAN_AUTO,");
            lua.AppendLine("    volume = 1,");
            lua.AppendLine($"    level = {levels.GetValueOrDefault(alias, 75)},");
            lua.AppendLine("    pitch = 100,");
            lua.AppendLine("    sound = {");
            lua.AppendLine(string.Join(",\n", paths.Select(path => $"        \"{path}\"")));
            lua.AppendLine("    }").AppendLine("})");
        }
        LuaFile.Append(script, lua.ToString());
        return new(resolved, missing, script);
    }

    /// <summary>
    /// CoD's gun-butt melee set from saluki's iw9/wpn/melee export (CoD melee clips carry no sound notetracks, and the
    /// weapon's own sndbank has none), shared by every generated weapon the way MW Base ships viper/shared/melee + MW_Melee.*:
    /// sound/cod/shared/melee/*.wav and COD_Melee.{Swing,Flesh,World}_{Small,Medium,Large} in mw_sounds_cod_melee.lua
    /// (same files and script from every weapon, so one copy once installed together). Swing = attack/plr, body hit =
    /// character/plr steel, wall hit = world/plr steel on cement. Returns this weapon's size: pistols Small, the rest Large.
    /// </summary>
    public static CodMeleeSet? Melee(string soundDirectory, string output, string codToken)
    {
        var folder = Path.Combine(soundDirectory, "iw9", "wpn", "melee");
        if (!Directory.Exists(folder)) return null;
        var target = Path.Combine(output, "sound", "cod", "shared", "melee");
        var lua = new StringBuilder("-- Generated by Alchemy Stars: CoD gun-butt melee sounds shared by every generated weapon\n");
        string? Register(string alias, string sub, params string[] stems)
        {
            var dir = Path.Combine(folder, sub, "plr");
            var files = Directory.Exists(dir) ? stems.Select(stem => Directory.GetFiles(dir, stem + "_*.wav").Order(StringComparer.Ordinal).ToArray())
                .FirstOrDefault(found => found.Length > 0) : null;
            if (files is null) return null;
            Directory.CreateDirectory(target);
            var paths = files.Select(file =>
            {
                var name = Path.GetFileName(file).Split('.')[0] + ".wav";
                Copy(file, Path.Combine(target, name));
                return $"cod/shared/melee/{name}";
            }).ToArray();
            lua.AppendLine().AppendLine("sound.Add({").AppendLine($"    name = \"{alias}\",").AppendLine("    channel = CHAN_AUTO,")
                .AppendLine("    volume = 1,").AppendLine($"    level = {(Level().Match(Path.GetFileName(files[0])) is { Success: true } level ? level.Groups[1].Value : "75")},")
                .AppendLine("    pitch = 100,").AppendLine("    sound = {").AppendLine(string.Join(",\n", paths.Select(path => $"        \"{path}\""))).AppendLine("    }").AppendLine("})");
            return alias;
        }
        var sets = new Dictionary<string, CodMeleeSet>();
        foreach (var (size, name) in new[] { ("sml", "Small"), ("med", "Medium"), ("lrg", "Large") })
            sets[size] = new(Register($"COD_Melee.Swing_{name}", "attack", $"iw9_melee_attack_gun_{size}_plr", $"melee_attack_gun_{size}_plr"),
                Register($"COD_Melee.Flesh_{name}", "character", $"melee_character_gun_{size}_steel_plr", $"melee_character_gun_{size}_polymer_plr"),
                Register($"COD_Melee.World_{name}", "world", $"melee_world_gun_{size}_steel_cement_plr", $"melee_world_gun_{size}_polymer_cement_plr"));
        if (sets.Values.All(set => set is { Swing: null, Flesh: null, World: null })) return null;
        var script = Path.Combine(output, "lua", "weapons", "mg_base", "modules", "sounds", "mw_sounds_cod_melee.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        LuaFile.Write(script, lua.ToString());
        // shortcut: Large for every long gun, Small for pistols; CoD's per-weapon choice is not in the dump.
        return sets[codToken.StartsWith("pi_", StringComparison.Ordinal) ? "sml" : "lrg"];
    }

    /// <summary>alias -> files (no extension, relative to sounds/) from every name,secondary,file CSV beside or inside the sounds folder.</summary>
    private static Dictionary<string, List<string>> AliasTable(string soundDirectory)
    {
        var table = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var roots = new[] { soundDirectory, Path.Combine(soundDirectory, "..", "sndbanks") }.Where(Directory.Exists);
        foreach (var csv in roots.SelectMany(root => Directory.EnumerateFiles(root, "*.csv", SearchOption.AllDirectories)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(csv);
            if (reader.ReadLine()?.Trim() != "name,secondary,file") continue;
            while (reader.ReadLine() is { } line)
            {
                var cells = line.Split(',');
                if (cells.Length < 3 || cells[2].Length == 0) continue;
                if (!table.TryGetValue(cells[0], out var files)) table[cells[0]] = files = [];
                if (!files.Contains(cells[2], StringComparer.OrdinalIgnoreCase)) files.Add(cells[2]);
            }
        }
        return table;
    }

    private static string? Find(string soundDirectory, string file)
    {
        var stem = Path.Combine(soundDirectory, file.Replace('\\', Path.DirectorySeparatorChar));
        return new[] { ".wav", ".ogg", ".mp3" }.Select(extension => stem + extension).FirstOrDefault(File.Exists);
    }

    /// <summary>Copies a sound; 16-bit PCM wav at a rate Source cannot play is linearly resampled to 44.1 kHz.</summary>
    private static void Copy(string source, string destination)
    {
        var bytes = File.ReadAllBytes(source);
        if (!source.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || Resample(bytes) is not { } wav) { File.WriteAllBytes(destination, bytes); return; }
        File.WriteAllBytes(destination, wav);
    }

    /// <summary>Returns a 44.1 kHz copy of a 16-bit PCM wav, or null when the file is already playable or not plain PCM16.</summary>
    public static byte[]? Resample(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        int channels = 0, rate = 0, bits = 0, format = 0, dataOffset = -1, dataLength = 0;
        for (var offset = 12; offset + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(wav, offset + 8); channels = BitConverter.ToUInt16(wav, offset + 10);
                rate = BitConverter.ToInt32(wav, offset + 12); bits = BitConverter.ToUInt16(wav, offset + 22);
            }
            else if (id == "data") { dataOffset = offset + 8; dataLength = Math.Min(size, wav.Length - dataOffset); }
            offset += 8 + size + (size & 1);
        }
        if (format != 1 || bits != 16 || channels == 0 || dataOffset < 0 || SourceRates.Contains(rate)) return null;
        var frames = dataLength / (2 * channels);
        var outFrames = (int)((long)frames * 44100 / rate);
        var samples = new short[outFrames * channels];
        for (var i = 0; i < outFrames; i++)
        {
            var position = i * (double)rate / 44100;
            var a = Math.Min((int)position, frames - 1);
            var b = Math.Min(a + 1, frames - 1);
            var t = position - a;
            for (var c = 0; c < channels; c++)
            {
                var sa = BitConverter.ToInt16(wav, dataOffset + (a * channels + c) * 2);
                var sb = BitConverter.ToInt16(wav, dataOffset + (b * channels + c) * 2);
                samples[i * channels + c] = (short)Math.Round(sa + (sb - sa) * t);
            }
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write(44100);
            writer.Write(44100 * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(samples.Length * 2);
            foreach (var sample in samples) writer.Write(sample);
        }
        return stream.ToArray();
    }

    [GeneratedRegex("event 5004 \\d+ \"([^\"]+)\"")] private static partial Regex SoundEvent();
    [GeneratedRegex("\\.[a-z]{3}\\.(\\d+)\\.\\d+\\.", RegexOptions.IgnoreCase)] private static partial Regex Level();
}
