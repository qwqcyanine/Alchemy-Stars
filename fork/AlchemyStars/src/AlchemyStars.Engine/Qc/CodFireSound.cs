using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlchemyStars.Engine;

/// <summary>Mixed player fire sounds: alias -> sound/ relative wav paths, plus what could not be built.</summary>
/// <param name="Tail">Outdoor report tail (fire_plr_atmo_ext), a separate layer so ARC9 can fade it out indoors.</param>
public sealed record CodFireSet(string? Fire, string? FireAds, string? Suppressed, string? SuppressedAds, string? Tail, IReadOnlyList<string> Log);

/// <summary>
/// CoD fires a shot as a chain of aliases (wpn_&lt;w&gt;_fire_plr_transient -> _shot -> _mech -> _lfe; _mech_ads while aimed;
/// sup_plr_shot when suppressed). Source plays one wav per shot, so each variant is pre-mixed here, the way
/// GunMix (github.com/ez4cywa/gunmix, MIT) bakes its single-shot export: layers balanced by their first-250 ms RMS
/// relative to the shot (mech -10 dB, transient -10 dB), then the whole mix peak-normalised.
/// _lfe is left out: it is the subwoofer send (pure &lt;150 Hz), and summed into the main mix it only adds boom.
/// The layer gains are a mixing suggestion, not CoD's runtime values (the alias tables carry no gain data).
/// </summary>
public static partial class CodFireSound
{
    private const int Rate = 48000;
    private const double MechRelDb = -10, TransientRelDb = -10, TailRelDb = -10, PeakDbfs = -1;

    /// <param name="codToken">CoD weapon token (sm_pmike), from <see cref="Token"/>.</param>
    /// <param name="script">Sound script the sound.Add calls are appended to (shared with <see cref="CodSoundResolver"/>).</param>
    public static CodFireSet Build(string soundDirectory, string output, string weaponName, string codToken, string script)
    {
        var log = new List<string>();
        // The alias tables (sndbanks/csv) list exact files; scanning the whole sounds tree would be slow and ambiguous.
        var files = BankFiles(soundDirectory, codToken);
        var shots = Layer(files, "fire_plr_shot");
        if (shots.Length == 0) return new(null, null, null, null, null, [$"no wpn_{codToken}_fire_plr_shot in the sndbanks/csv alias tables: fire sound stays Weapon_SMG1.Single"]);
        var mech = Layer(files, "fire_plr_mech");
        var mechAds = Layer(files, "fire_plr_mech_ads");
        var transient = Layer(files, "fire_plr_transient");
        var suppressed = Layer(files, "sup_plr_shot");
        var tail = Layer(files, "fire_plr_atmo_ext");
        log.Add($"fire layers: shot {shots.Length}, mech {mech.Length}, mech_ads {mechAds.Length}, transient {transient.Length}, sup_shot {suppressed.Length}, atmo_ext {tail.Length}");

        var target = Path.Combine(output, "sound", "weapons", weaponName);
        Directory.CreateDirectory(target);
        double fireRms = 0;
        string? Write(string name, string[] main, string[] mechanism, string[] head)
        {
            if (main.Length == 0) return null;
            var paths = new List<string>();
            for (var index = 0; index < main.Length; index++)
            {
                // Variant i takes mech i (CoD rolls each alias independently; pairing keeps every file used).
                var layers = new List<(string File, double RelDb)> { (main[index], 0) };
                if (mechanism.Length > 0) layers.Add((mechanism[index % mechanism.Length], MechRelDb));
                if (head.Length > 0) layers.Add((head[index % head.Length], TransientRelDb));
                var file = $"{name}_{index + 1:00}.wav";
                var mix = Mix(layers);
                if (fireRms == 0) fireRms = EarlyRms(mix);
                File.WriteAllBytes(Path.Combine(target, file), Wav(Resample(mix)));
                paths.Add($"weapons/{weaponName}/{file}");
            }
            return paths.Count == 0 ? null : $"{weaponName}.{name}";
        }
        var set = new CodFireSet(
            Write("fire", shots, mech, transient),
            Write("fire_ads", shots, mechAds.Length > 0 ? mechAds : mech, transient),
            // The transient is the unsuppressed muzzle crack, so the suppressed shots skip it.
            Write("fire_sup", suppressed, mech, []),
            Write("fire_sup_ads", suppressed, mechAds.Length > 0 ? mechAds : mech, []),
            WriteTail(target, weaponName, tail, fireRms * Math.Pow(10, TailRelDb / 20)),
            log);
        if (set.Suppressed is null) log.Add("no wpn_*_sup_plr_shot_NN.wav: suppressed barrels keep the normal fire sound");
        WriteScript(script, weaponName, set, target);
        return set;
    }

    /// <summary>CoD weapon token in a part name: vm_jup_jp03_sm_pmike_rec_000 -> sm_pmike (class prefix + codename).</summary>
    public static string? Token(string partName) =>
        WeaponToken().Match(Path.GetFileNameWithoutExtension(partName)) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>Existing wav files the token's alias tables reference (weapon_jup_sm_pmike_plr.all.csv, ..._plr_sup.all.csv).</summary>
    private static string[] BankFiles(string soundDirectory, string token)
    {
        var banks = Path.GetFullPath(Path.Combine(soundDirectory, "..", "sndbanks"));
        if (!Directory.Exists(banks)) return [];
        var stem = $"wpn_{token}_";
        return Directory.EnumerateFiles(banks, $"*{token}*.csv", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines)
            .Select(line => line.Split(',') is { Length: >= 3 } cells ? cells[2] : "")
            .Where(file => Path.GetFileName(file.Replace('\\', '/')).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.Combine(soundDirectory, file.Replace('\\', Path.DirectorySeparatorChar)) + ".wav")
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Files of one layer, sorted by variant number: wpn_sm_pmike_fire_plr_mech_03 ∈ "fire_plr_mech", not ∈ "fire_plr_mech_ads".</summary>
    private static string[] Layer(IEnumerable<string> files, string layer) =>
        files.Where(file => LayerName().Match(Path.GetFileName(file)) is { Success: true } match && match.Groups[1].Value == layer)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void WriteScript(string script, string weaponName, CodFireSet set, string target)
    {
        var lua = new StringBuilder("\n-- Fire: pre-mixed player shots (shot + mech + transient)\n");
        foreach (var alias in new[] { set.Fire, set.FireAds, set.Suppressed, set.SuppressedAds, set.Tail }.OfType<string>())
        {
            var stem = alias[(weaponName.Length + 1)..];
            var paths = Directory.EnumerateFiles(target, stem + "_??.wav").Order(StringComparer.Ordinal)
                .Select(path => $"weapons/{weaponName}/{Path.GetFileName(path)}");
            // MW Base official fire aliases: CHAN_WEAPON + 1, level 140, slight pitch spread (mw_sounds_ar_akilo47.lua).
            lua.AppendLine().AppendLine("sound.Add({");
            lua.AppendLine($"    name = \"{alias}\",");
            lua.AppendLine("    channel = CHAN_WEAPON + 1,");
            lua.AppendLine("    volume = 1,");
            lua.AppendLine("    level = 140,");
            lua.AppendLine("    pitch = {95, 105},");
            lua.AppendLine("    sound = {");
            lua.AppendLine(string.Join(",\n", paths.Select(path => $"        \"{path}\"")));
            lua.AppendLine("    }").AppendLine("})");
        }
        LuaFile.Append(script, lua.ToString());
    }

    /// <summary>
    /// Tail variants scaled to <paramref name="targetRms"/> (early RMS, set from the first fire mix) and capped at the peak
    /// ceiling. Every export is peak-normalised, so the level is a mixing choice like the other layer gains.
    /// </summary>
    private static string? WriteTail(string target, string weaponName, string[] files, double targetRms)
    {
        if (files.Length == 0 || targetRms <= 0) return null;
        for (var index = 0; index < files.Length; index++)
        {
            var samples = ReadStereo(files[index]);
            var rms = EarlyRms(samples);
            var peak = samples.Max(MathF.Abs);
            if (rms <= 0 || peak <= 0) continue;
            var gain = (float)Math.Min(targetRms / rms, Math.Pow(10, PeakDbfs / 20) / peak);
            for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
            File.WriteAllBytes(Path.Combine(target, $"fire_tail_{index + 1:00}.wav"), Wav(Resample(samples)));
        }
        return $"{weaponName}.fire_tail";
    }

    /// <summary>
    /// Sum of the layers at 48 kHz stereo, each scaled to RelDb below the first layer's early RMS. The sum is set so the
    /// shot alone peaks at the ceiling and a peak limiter catches the layer overlaps: plain peak-normalising the sum left
    /// it ~4 dB quieter than the shot alone (and the CoD2019 pack's fire wavs), which read as muffled in game.
    /// </summary>
    private static float[] Mix(IReadOnlyList<(string File, double RelDb)> layers)
    {
        var buffers = layers.Select(layer => (Samples: ReadStereo(layer.File), layer.RelDb)).ToArray();
        var reference = EarlyRms(buffers[0].Samples);
        var mix = new float[buffers.Max(buffer => buffer.Samples.Length)];
        foreach (var (samples, relDb) in buffers)
        {
            var rms = EarlyRms(samples);
            var gain = relDb == 0 || rms <= 0 ? 1 : reference * Math.Pow(10, relDb / 20) / rms;
            for (var i = 0; i < samples.Length; i++) mix[i] += (float)(samples[i] * gain);
        }
        var peak = buffers[0].Samples.Max(MathF.Abs);
        if (peak <= 0) return mix;
        var ceiling = (float)Math.Pow(10, PeakDbfs / 20);
        var scale = ceiling / peak;
        // Linked-stereo limiter: instant attack, 40 ms release.
        var release = (float)Math.Exp(-1 / (Rate * 0.040));
        var reduction = 1f;
        for (var i = 0; i + 1 < mix.Length; i += 2)
        {
            var level = Math.Max(Math.Abs(mix[i]), Math.Abs(mix[i + 1])) * scale;
            var needed = level > ceiling ? ceiling / level : 1f;
            reduction = needed < reduction ? needed : needed + (reduction - needed) * release;
            mix[i] *= scale * reduction;
            mix[i + 1] *= scale * reduction;
        }
        return mix;
    }

    /// <summary>RMS of the first 250 ms (GunMix's loudness reference for layer balance).</summary>
    private static double EarlyRms(float[] stereo)
    {
        var count = Math.Min(stereo.Length, Rate / 4 * 2);
        if (count == 0) return 0;
        double sum = 0;
        for (var i = 0; i < count; i++) sum += stereo[i] * stereo[i];
        return Math.Sqrt(sum / count);
    }

    /// <summary>16-bit PCM wav as interleaved stereo floats at 48 kHz (mono is duplicated; other rates are rejected).</summary>
    private static float[] ReadStereo(string path)
    {
        var wav = File.ReadAllBytes(path);
        int channels = 0, rate = 0, bits = 0, dataOffset = -1, dataLength = 0;
        for (var offset = 12; offset + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (id == "fmt ") { channels = BitConverter.ToUInt16(wav, offset + 10); rate = BitConverter.ToInt32(wav, offset + 12); bits = BitConverter.ToUInt16(wav, offset + 22); }
            else if (id == "data") { dataOffset = offset + 8; dataLength = Math.Min(size, wav.Length - dataOffset); }
            offset += 8 + size + (size & 1);
        }
        if (bits != 16 || rate != Rate || channels is not (1 or 2) || dataOffset < 0)
            throw new InvalidDataException($"Fire layer must be 16-bit 48 kHz mono/stereo PCM: {path}");
        var frames = dataLength / (2 * channels);
        var stereo = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var left = BitConverter.ToInt16(wav, dataOffset + frame * channels * 2) / 32768f;
            var right = channels == 2 ? BitConverter.ToInt16(wav, dataOffset + frame * 4 + 2) / 32768f : left;
            stereo[frame * 2] = left;
            stereo[frame * 2 + 1] = right;
        }
        return stereo;
    }

    /// <summary>
    /// 48 kHz -> 44.1 kHz resample of interleaved stereo (Source rejects 48 kHz wav). Hann-windowed sinc, 32 taps,
    /// cutoff 0.9 x the 48 kHz Nyquist; linear interpolation cut 2-5 dB off everything above 10 kHz and dulled the shot.
    /// </summary>
    private static float[] Resample(float[] stereo)
    {
        const int Taps = 16;
        const double Cutoff = 0.9;
        var frames = stereo.Length / 2;
        var outFrames = (int)((long)frames * 44100 / Rate);
        var result = new float[outFrames * 2];
        for (var i = 0; i < outFrames; i++)
        {
            var position = i * (double)Rate / 44100;
            var center = (int)position;
            double left = 0, right = 0;
            for (var k = Math.Max(center - Taps + 1, 0); k <= Math.Min(center + Taps, frames - 1); k++)
            {
                var t = position - k;
                var x = Math.PI * Cutoff * t;
                var weight = Cutoff * (x == 0 ? 1 : Math.Sin(x) / x) * (0.5 + 0.5 * Math.Cos(Math.PI * t / Taps));
                left += stereo[k * 2] * weight;
                right += stereo[k * 2 + 1] * weight;
            }
            result[i * 2] = (float)left;
            result[i * 2 + 1] = (float)right;
        }
        return result;
    }

    private static byte[] Wav(float[] stereo)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + stereo.Length * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(44100);
            writer.Write(44100 * 4); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(stereo.Length * 2);
            foreach (var sample in stereo) writer.Write((short)Math.Clamp(MathF.Round(sample * 32767), -32768, 32767));
        }
        return stream.ToArray();
    }

    /// <summary>wpn_sm_pmike_fire_plr_mech_ads_03.lnn.85.48000.all.wav -> "fire_plr_mech_ads".</summary>
    [GeneratedRegex(@"^wpn_[a-z0-9]+_[a-z0-9]+_((?:fire|sup)_plr_[a-z_]+?)(?:_\d+)?\.", RegexOptions.IgnoreCase)]
    private static partial Regex LayerName();

    [GeneratedRegex(@"(?:^|_)((?:ar|sm|sh|lm|sn|dm|pi|la|me|br|mr)_[a-z0-9]+)(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WeaponToken();
}
