namespace AlchemyStars.Avalonia;

/// <summary>
/// --att-pack-smoke &lt;attachments-dir&gt; &lt;reticles-dir&gt; &lt;output-dir&gt;: converts every CoD viewmodel attachment CAST into an
/// MW Base pack (QCs, SMDs, materials, lua) and checks the result.
/// </summary>
internal static class AttachmentPackSmoke
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 3) throw new ArgumentException("usage: --att-pack-smoke <attachments> <reticles> <output>");
            Check();
            var output = Path.GetFullPath(args[2]);
            var casts = Directory.GetFiles(args[0], "*_LOD0.cast", SearchOption.AllDirectories)
                .Where(QcProjectGenerator.IsAttachmentPackCast).ToArray();
            var result = QcProjectGenerator.GenerateAttachmentPack(new(casts, args[1], output, StatsFile: args.SkipWhile(arg => arg != "--stats").Skip(1).FirstOrDefault() ?? "",
                IconDirectory: args.SkipWhile(arg => arg != "--icons").Skip(1).FirstOrDefault() ?? ""));
            foreach (var line in result.Log) Console.WriteLine(line);
            foreach (var qc in result.Qcs) Console.WriteLine("qc: " + qc);
            foreach (var lua in result.LuaFiles) Console.WriteLine("lua: " + lua);

            static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("attachment pack: " + message); }
            static string Category(string lua) => Path.GetFileName(Path.GetDirectoryName(lua)!);
            static string Class(string path) => Path.GetFileNameWithoutExtension(path);
            var sights = result.LuaFiles.Where(lua => Category(lua) == "sights").ToArray();
            var muzzles = result.LuaFiles.Where(lua => Category(lua) == "muzzles").ToArray();
            var grips = result.LuaFiles.Where(lua => Category(lua) == "grips").ToArray();
            Require(sights.Length > 0 && muzzles.Length > 0 && grips.Length > 0,
                $"no sight / muzzle / grip lua ({sights.Length} / {muzzles.Length} / {grips.Length})");
            Require(result.LuaFiles.All(lua => !Path.GetFileName(lua).Contains("wm_", StringComparison.Ordinal)), "a worldmodel stem reached the pack");
            foreach (var lua in sights)
                Require(File.ReadAllText(Path.Combine(output, Class(lua) + ".qc")).Contains("$attachment \"reticle\"", StringComparison.Ordinal),
                    "sight QC without a reticle attachment: " + Class(lua));
            // MW Base can only stencil an optic's lens away with its own hide model.
            var optics = sights.Where(lua => File.ReadAllText(lua).Contains("ATTACHMENT.Optic", StringComparison.Ordinal)).ToArray();
            foreach (var optic in optics) Require(File.Exists(Path.Combine(output, Class(optic) + "_hide.qc")), "optic without a hide QC: " + Class(optic));
            // With COD20 stats and saluki's reticle_preview beside the reticle folder, a sight gets its own CoD reticle.
            var reflex02 = sights.FirstOrDefault(lua => Class(lua) == "att_cod_jup_reflex02");
            if (reflex02 is not null && result.Log.All(line => !line.Contains("jup_ui_reticle_reflex02: no preview", StringComparison.Ordinal))
                && File.Exists(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(args[1]))!, "reticle_preview", "jup_ui_reticle_reflex02.png"))
                && args.Contains("--stats"))
                Require(File.ReadAllText(reflex02).Contains("reticles/jup_ui_reticle_reflex02", StringComparison.Ordinal), "jup_reflex02 lost its CoD reticle");
            foreach (var qc in result.Qcs)
            {
                var text = File.ReadAllText(qc);
                Require(text.Count(character => character == '{') == text.Count(character => character == '}'), "unbalanced braces: " + Path.GetFileName(qc));
            }
            // Weapons list the pack parts in their own customization.lua; the pack must not leave MW Base injectors behind.
            var injectors = Path.Combine(output, "lua", "weapons", "mg_base", "modules", "injectors");
            Require(!Directory.Exists(injectors) || Directory.GetFiles(injectors, "cod_*.lua").Length == 0, "stale injectors left");
            Console.WriteLine($"sights {sights.Length} ({optics.Length} optics), muzzles {muzzles.Length}, grips {grips.Length}, qcs {result.Qcs.Count}, "
                + $"skipped {result.Log.Count(line => line.StartsWith("skip ", StringComparison.Ordinal))}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check()
    {
        void Is(string actual, string expected) { if (actual != expected) throw new InvalidOperationException($"attachment pack class: '{actual}' != '{expected}'"); }
        Is(QcProjectGenerator.AttachmentPackClass("att_vm_holo01_v0_LOD0.cast"), "att_cod_holo01");
        Is(QcProjectGenerator.AttachmentPackClass("vm_jup_reflex02_000_LOD0.cast"), "att_cod_jup_reflex02");
    }
}
