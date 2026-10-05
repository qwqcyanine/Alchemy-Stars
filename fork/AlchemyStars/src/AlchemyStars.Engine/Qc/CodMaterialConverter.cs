using System.Globalization;
using System.Numerics;
using System.Text;
using Cast.NET;
using Cast.NET.Nodes;
using SkiaSharp;
using static Alchemist.UI.CastNodeTraversal;

namespace AlchemyStars.Engine;

/// <summary>Which envmap/proxy the VMTs reference: MW Base (viper envmaps + MwEnvMapTint), ARC9 (arc9 envmap + Arc9EnvMapTint) or plain env_cubemap.</summary>
public enum VmtEnvmapStyle { Cubemap, MwBase, Arc9 }

/// <summary>
/// Converts Call of Duty (IW/MW engine) PBR materials referenced by CAST models into Source VertexLitGeneric phong
/// materials: &lt;name&gt;_rgb (albedo, A = metal mask for $blendtintbybasealpha), &lt;name&gt;_n (normal, A = phong mask)
/// and &lt;name&gt;_e (phong exponent: R exponent, G albedo tint, A rim mask).
/// - Inputs follow saluki/Greyhound exports: colour "_c&amp;..._s~" (RGB albedo, A fused specular) and NOG "_n&amp;..._g~"
///   (R gloss, G/A hemi-octahedral normal, B occlusion). Decode math from Scobalula's GameImageUtil (GPL-3.0).
/// - The phong mapping (exponent = gloss^4, mask = gloss^2.5, envmap by average gloss) re-implements the approach of
///   9lbw/mwb-materials, the converter MW Base materials are made with.
/// </summary>
public static class CodMaterialConverter
{
    private const int Bgra8888 = 12, Dxt5 = 15;
    private const uint EightBitAlpha = 0x2000, NormalFlag = 0x80;

    /// <param name="include">Material names to convert; the rest are written by hand (lens glass, MW Base's own lenses).</param>
    /// <summary>Writes one VMT + VTFs per unique material into <paramref name="materialsDirectory"/>; returns log lines.</summary>
    public static IReadOnlyList<string> Convert(IEnumerable<string> castFiles, string materialsDirectory, string cdMaterials,
        VmtEnvmapStyle style, int maxSize, Func<string, bool>? include = null)
    {
        Directory.CreateDirectory(materialsDirectory);
        var materials = new Dictionary<string, (string Color, string? Nog)>(StringComparer.OrdinalIgnoreCase);
        var log = new List<string>();
        foreach (var cast in castFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var material in CastReader.Load(cast).RootNodes.SelectMany(DescendantsAndSelf).OfType<MaterialNode>())
            {
                if (string.IsNullOrWhiteSpace(material.Name) || materials.ContainsKey(material.Name)) continue;
                if (include is not null && !include(material.Name)) continue;
                var files = material.Children.OfType<FileNode>().Select(file => Path.Combine(Path.GetDirectoryName(cast)!, file.Path.Replace('\\', '/')))
                    .Where(path => !Path.GetFileName(path).StartsWith('$')).ToArray();
                var color = material.Albedo is FileNode albedo ? Path.Combine(Path.GetDirectoryName(cast)!, albedo.Path.Replace('\\', '/')) : null;
                var nog = files.FirstOrDefault(path => IsNog(Path.GetFileNameWithoutExtension(path)));
                // Hashed exports (ammo_unspent_9p: image_<hash>.png) carry no _n&_g~ name; recognise the NOG by its content.
                nog ??= files.FirstOrDefault(path => !string.Equals(path, color, StringComparison.OrdinalIgnoreCase) && File.Exists(path) && LooksLikeNog(path));
                if (color is null || Path.GetFileName(color).StartsWith('$') || !File.Exists(color)) { log.Add($"skip {material.Name}: no colour map"); continue; }
                materials[material.Name] = (color, nog is not null && File.Exists(nog) ? nog : null);
            }

        var lines = new System.Collections.Concurrent.ConcurrentBag<string>(log);
        Parallel.ForEach(materials, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, item =>
        {
            try { lines.Add(ConvertOne(item.Key, item.Value.Color, item.Value.Nog, materialsDirectory, cdMaterials, style, maxSize)); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { lines.Add($"fail {item.Key}: {error.Message}"); }
        });
        return lines.OrderBy(line => line, StringComparer.Ordinal).ToArray();
    }

    private static bool IsNog(string name) => name.Contains("_n&", StringComparison.OrdinalIgnoreCase)
        || name.Contains("_g~", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_nog", StringComparison.OrdinalIgnoreCase)
        || name.Contains("packed_n", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// NOG packs gloss / normal U / AO / normal V: both normal channels average near 0.5 and the AO channel is mostly
    /// white, while colour maps are rarely balanced that way.
    /// </summary>
    private static bool LooksLikeNog(string path)
    {
        try
        {
            var image = Load(path);
            double u = 0, v = 0, ao = 0;
            var step = Math.Max(1, image.W * image.H / 4096);
            var samples = 0;
            for (var i = 0; i < image.W * image.H; i += step, samples++)
            {
                u += image.P[i * 4 + 1]; ao += image.P[i * 4 + 2]; v += image.P[i * 4 + 3];
            }
            u /= samples; v /= samples; ao /= samples;
            return Math.Abs(u - 128) < 24 && Math.Abs(v - 128) < 24 && ao > 180;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>One DXT5 VTF from a plain image (reticle PNGs), scaled to <paramref name="maxSize"/>.</summary>
    internal static void WriteImageVtf(string png, string vtf, int maxSize) =>
        WriteVtf(vtf, Fit(Load(png), maxSize), Dxt5, EightBitAlpha);

    /// <summary>A UI icon fitted (halved until it fits) and centred on a transparent width x height canvas, like MW Base's VGUI icons.</summary>
    internal static void WriteIconVtf(string png, string vtf, int width, int height) => WriteVtf(vtf, IconCanvas(png, width, height), Dxt5, EightBitAlpha);

    /// <summary>The icon centred on a transparent canvas as a PNG (ARC9 reads materials/entities/&lt;class&gt;.png).</summary>
    internal static void WriteIconPng(string png, string output, int width, int height)
    {
        var canvas = IconCanvas(png, width, height);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(canvas.P, 0, bitmap.GetPixels(), canvas.P.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(output);
        data.SaveTo(file);
    }

    private static Image IconCanvas(string png, int width, int height)
    {
        var image = Load(png);
        while (image.W > width || image.H > height) image = Half(image);
        var canvas = new Image(width, height);
        int left = (width - image.W) / 2, top = (height - image.H) / 2;
        for (var y = 0; y < image.H; y++) Array.Copy(image.P, y * image.W * 4, canvas.P, ((top + y) * width + left) * 4, image.W * 4);
        return canvas;
    }

    /// <summary>
    /// A CoD menu reticle preview (jup_ui_reticle_*, the whole composed reticle) as a reticle VTF. Scope previews are painted
    /// over an opaque grey lens vignette: its per-radius median is taken out, bright marks keep their colour, dark marks become
    /// black with alpha, and the painted lens rim is cut. Transparent previews (red dots) stay as they are.
    /// </summary>
    internal static void WriteReticlePreviewVtf(string png, string vtf, int maxSize)
    {
        var image = Fit(Load(png), maxSize);
        var opaque = 0;
        for (var i = 3; i < image.P.Length; i += 4) if (image.P[i] > 250) opaque++;
        WriteVtf(vtf, opaque > image.W * image.H / 2 ? Unlens(image) : image, Dxt5, EightBitAlpha);
    }

    private static Image Unlens(Image source)
    {
        int w = source.W, h = source.H;
        float cx = (w - 1) / 2f, cy = (h - 1) / 2f, rim = Math.Min(w, h) / 2f * 0.93f;
        int Ring(int x, int y) => (int)MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        var rings = Ring(0, 0) + 1;
        var samples = Enumerable.Range(0, rings * 3).Select(_ => new List<byte>()).ToArray();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                if (source.P[o + 3] > 250) for (var c = 0; c < 3; c++) samples[Ring(x, y) * 3 + c].Add(source.P[o + c]);
            }
        var background = samples.Select(list => { list.Sort(); return list.Count > 0 ? (float)list[list.Count / 2] : 0f; }).ToArray();
        var result = new Image(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var ring = Ring(x, y);
                if (ring >= rim) continue;
                var o = (y * w + x) * 4;
                float bright = 0, value = 0, back = 0;
                for (var c = 0; c < 3; c++)
                {
                    var bg = background[ring * 3 + c];
                    bright = Math.Max(bright, (source.P[o + c] - bg) / Math.Max(255 - bg, 1));
                    value = Math.Max(value, source.P[o + c]);
                    back = Math.Max(back, bg);
                }
                // The lens texture's own speckle reads as up to ~8% brighter / ~25% darker; marks are well past that.
                var dark = Math.Clamp((1 - value / Math.Max(back, 1) - 0.25f) / 0.75f, 0, 1);
                var mix = Math.Max(bright, 0.08f);
                bright = Math.Clamp((bright - 0.08f) / 0.92f, 0, 1);
                var coverage = Math.Max(bright, dark);
                var alpha = coverage * source.P[o + 3] / 255f;
                if (alpha <= 0) continue;
                if (bright >= dark)
                {
                    float Colour(int c) { var bg = background[ring * 3 + c]; return (bg + (source.P[o + c] - bg) / mix) / 255f; }
                    result.Set(o, Colour(0), Colour(1), Colour(2), alpha);
                }
                else result.Set(o, 0, 0, 0, alpha);
            }
        return result;
    }

    private static string ConvertOne(string name, string colorPath, string? nogPath, string directory, string cdMaterials, VmtEnvmapStyle style, int maxSize)
    {
        var color = Fit(Load(colorPath), maxSize);
        var fused = Path.GetFileName(colorPath).Contains("_s~", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(colorPath).Contains('&')
            || HashedFused(colorPath, color);
        var nog = nogPath is null ? null : Resample(Fit(Load(nogPath), maxSize), color.W, color.H);
        var count = color.W * color.H;
        var albedo = new Image(color.W, color.H);
        var normal = new Image(color.W, color.H);
        var exponent = new Image(color.W, color.H);
        double glossSum = 0, tintR = 0, tintG = 0, tintB = 0;
        var metalPixels = 0;
        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            float r = color.P[o] / 255f, g = color.P[o + 1] / 255f, b = color.P[o + 2] / 255f;
            // Fused colour/specular: alpha above the 0.1 insulator range is the metal mask (GameImageUtil CoDFusedCS).
            var metal = fused ? Math.Clamp((color.P[o + 3] / 255f - 0.1f) / 0.9f, 0, 1) : 0;
            float gloss = 0.5f, ao = 1, nx = 0, ny = 0, nz = 1;
            if (nog is not null)
            {
                gloss = nog.P[o] / 255f;
                ao = nog.P[o + 2] / 255f;
                // Hemi-octahedron: rotate the unit square back to the centre diamond.
                float u = nog.P[o + 1] / 255f * 2 - 1, v = nog.P[o + 3] / 255f * 2 - 1;
                nx = (u + v) * 0.5f; ny = (u - v) * 0.5f; nz = 1 - MathF.Abs(nx) - MathF.Abs(ny);
                var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length > 0) { nx /= length; ny /= length; nz /= length; }
            }
            albedo.Set(o, r * ao, g * ao, b * ao, metal);
            normal.Set(o, nx * 0.5f + 0.5f, ny * 0.5f + 0.5f, nz * 0.5f + 0.5f, Math.Min(MathF.Pow(gloss, 2.5f) + 1 / 255f, 1) * ao);
            exponent.Set(o, Math.Min(MathF.Pow(gloss, 4) * (1 - 0.5f * metal) + 1 / 255f, 1), metal, 0, gloss * ao);
            glossSum += Math.Max(gloss, 0.5f);
            if (metal > 0.5f) { tintR += r * metal; tintG += g * metal; tintB += b * metal; metalPixels++; }
            else { tintR += 0.1; tintG += 0.1; tintB += 0.1; }
        }
        // $color2 [0 0 0] blacks out masked diffuse, leaving metal to phong + envmap. Fine for a mostly painted gun, but a
        // mostly-metal part (brass cartridge) turns near black wherever the cubemap is weak; keep half its diffuse.
        if (metalPixels > count / 2)
            for (var o = 3; o < albedo.P.Length; o += 4) albedo.P[o] = (byte)(albedo.P[o] / 2);

        WriteVtf(Path.Combine(directory, name + "_rgb.vtf"), albedo, Dxt5, EightBitAlpha);
        WriteVtf(Path.Combine(directory, name + "_n.vtf"), normal, Bgra8888, EightBitAlpha | NormalFlag);
        WriteVtf(Path.Combine(directory, name + "_e.vtf"), exponent, Dxt5, EightBitAlpha);

        var gloss01 = glossSum / count;
        var tint = string.Create(CultureInfo.InvariantCulture, $"[{tintR / count:0.###} {tintG / count:0.###} {tintB / count:0.###}]");
        var envmap = style switch
        {
            // mwb-materials thresholds: glossier surfaces get the sharper specularity cubemap MW Base ships.
            VmtEnvmapStyle.MwBase => "viper/shared/envmaps/specularity_" + (gloss01 >= 0.9 ? "100" : gloss01 >= 0.85 ? "75" : gloss01 >= 0.8 ? "50" : gloss01 >= 0.7 ? "25" : "00"),
            VmtEnvmapStyle.Arc9 => "arc9/shared/envmaps/specularity_50",
            _ => "env_cubemap",
        };
        var path = cdMaterials.Trim().Replace('\\', '/').TrimEnd('/') + "/" + name;
        var proxy = style switch
        {
            VmtEnvmapStyle.MwBase => $"\n    \"Proxies\"\n    {{\n        \"MwEnvMapTint\"\n        {{\n            \"min\" \"0\"\n            \"max\" \"1\"\n            \"envmap\" \"{envmap}\"\n            \"color\" \"{tint}\"\n        }}\n    }}\n",
            VmtEnvmapStyle.Arc9 => $"\n    \"Proxies\"\n    {{\n        \"Arc9EnvMapTint\"\n        {{\n            \"min\" \"0\"\n            \"max\" \"1\"\n            \"color\" \"{tint}\"\n        }}\n    }}\n",
            _ => "",
        };
        // Blueprint materials carry a folder (m/mtl_jup_jp03_sm_pmike_bar_2516).
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, name + ".vmt"))!);
        File.WriteAllText(Path.Combine(directory, name + ".vmt"), $$"""
            "VertexLitGeneric"
            {
                "$basetexture" "{{path}}_rgb"
                "$bumpmap" "{{path}}_n"
                "$phongexponenttexture" "{{path}}_e"

                "$color2" "[0 0 0]"
                "$blendtintbybasealpha" "1"

                "$phong" "1"
                "$phongboost" "1"
                "$phongalbedotint" "1"
                "$phongalbedoboost" "10"
                "$phongfresnelranges" "[1 0.1 0]"

                "$rimlight" "1"
                "$rimlightexponent" "100"
                "$rimmask" "1"
                "$rimlightboost" "1"

                "$normalmapalphaenvmapmask" "1"
                "$envmap" "{{envmap}}"
                "$envmaptint" "{{tint}}"
            """.Replace("\r\n", "\n") + proxy + "}\n");
        return $"ok   {name} {color.W}x{color.H}{(nog is null ? " (no NOG: flat normal)" : "")}";
    }

    /// <summary>
    /// Hashed exports (image_&lt;hash&gt;.png) lose the _c&amp;_s~ name, so fused colour/specular is told by its alpha:
    /// a metal mask has real insulator areas (ammo_unspent_9p: brass/copper 255, primer/paint 0), while a plain
    /// colour map stays opaque. Without this the cartridge renders as flat saturated diffuse.
    /// </summary>
    private static bool HashedFused(string path, Image color)
    {
        if (!Path.GetFileName(path).StartsWith("image_", StringComparison.OrdinalIgnoreCase)) return false;
        var low = 0;
        for (var i = 3; i < color.P.Length; i += 4) if (color.P[i] < 128) low++;
        return low > color.W * color.H / 100;
    }

    private sealed class Image(int width, int height)
    {
        public int W { get; } = width;
        public int H { get; } = height;
        public byte[] P { get; } = new byte[width * height * 4];

        public void Set(int offset, float r, float g, float b, float a)
        {
            P[offset] = Byte(r); P[offset + 1] = Byte(g); P[offset + 2] = Byte(b); P[offset + 3] = Byte(a);
        }
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)(value * 255 + 0.5f), 0, 255);

    /// <summary>Unpremultiplied RGBA8888 without colour-space conversion: every channel is data, not colour.</summary>
    private static Image Load(string path)
    {
        // A stream, not the path: SKCodec.Create(path) fails on paths at MAX_PATH (long saluki material folders).
        using var codec = SKCodec.Create(File.OpenRead(path)) ?? throw new InvalidDataException($"unsupported image: {Path.GetFileName(path)}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var image = new Image(info.Width, info.Height);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(image.P, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"{Path.GetFileName(path)}: {result}");
        }
        finally { handle.Free(); }
        return image;
    }

    /// <summary>Box-halves until both sides fit <paramref name="maxSize"/>.</summary>
    private static Image Fit(Image image, int maxSize)
    {
        while (image.W > maxSize || image.H > maxSize) image = Half(image);
        return image;
    }

    private static Image Half(Image source)
    {
        var result = new Image(Math.Max(1, source.W / 2), Math.Max(1, source.H / 2));
        for (var y = 0; y < result.H; y++)
            for (var x = 0; x < result.W; x++)
                for (var c = 0; c < 4; c++)
                {
                    int x0 = Math.Min(x * 2, source.W - 1), x1 = Math.Min(x * 2 + 1, source.W - 1);
                    int y0 = Math.Min(y * 2, source.H - 1), y1 = Math.Min(y * 2 + 1, source.H - 1);
                    var sum = source.P[(y0 * source.W + x0) * 4 + c] + source.P[(y0 * source.W + x1) * 4 + c]
                        + source.P[(y1 * source.W + x0) * 4 + c] + source.P[(y1 * source.W + x1) * 4 + c];
                    result.P[(y * result.W + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
        return result;
    }

    // ponytail: nearest-neighbour for NOG/colour size mismatch (rare in CoD exports); bilinear if it shows.
    private static Image Resample(Image source, int width, int height)
    {
        if (source.W == width && source.H == height) return source;
        var result = new Image(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Array.Copy(source.P, ((y * source.H / height) * source.W + x * source.W / width) * 4, result.P, (y * width + x) * 4, 4);
        return result;
    }

    /// <summary>VTF 7.2, full mip chain, no low-res thumbnail.</summary>
    private static void WriteVtf(string path, Image image, int format, uint flags)
    {
        var mips = new List<Image> { image };
        while (mips[^1].W > 1 || mips[^1].H > 1) mips.Add(Half(mips[^1]));
        Vector3 reflectivity = default;
        for (var i = 0; i < image.P.Length; i += 4)
            reflectivity += new Vector3(MathF.Pow(image.P[i] / 255f, 2.2f), MathF.Pow(image.P[i + 1] / 255f, 2.2f), MathF.Pow(image.P[i + 2] / 255f, 2.2f));
        reflectivity /= image.W * image.H;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("VTF\0"u8);
        writer.Write(7u); writer.Write(2u); writer.Write(80u);
        writer.Write((ushort)image.W); writer.Write((ushort)image.H); writer.Write(flags);
        writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(reflectivity.X); writer.Write(reflectivity.Y); writer.Write(reflectivity.Z); writer.Write(0u);
        writer.Write(1f); writer.Write(format); writer.Write((byte)mips.Count);
        writer.Write(-1); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1);
        writer.Write(new byte[80 - 65]);
        for (var level = mips.Count - 1; level >= 0; level--)
            writer.Write(format == Dxt5 ? Dxt5Encode(mips[level]) : Bgra(mips[level]));
    }

    private static byte[] Bgra(Image image)
    {
        var data = new byte[image.P.Length];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = image.P[i + 2]; data[i + 1] = image.P[i + 1]; data[i + 2] = image.P[i]; data[i + 3] = image.P[i + 3];
        }
        return data;
    }

    /// <summary>BC3: bounding-box endpoints, nearest palette index. Fast and good enough for phong/albedo data.</summary>
    private static byte[] Dxt5Encode(Image image) => Dxt5Encode(image.W, image.H, image.P);

    internal static byte[] Dxt5Encode(int width, int height, byte[] rgba)
    {
        int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
        var output = new byte[blocksX * blocksY * 16];
        Span<byte> block = stackalloc byte[64];
        for (var by = 0; by < blocksY; by++)
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var p = 0; p < 16; p++)
                {
                    int x = Math.Min(bx * 4 + p % 4, width - 1), y = Math.Min(by * 4 + p / 4, height - 1);
                    rgba.AsSpan((y * width + x) * 4, 4).CopyTo(block[(p * 4)..]);
                }
                EncodeBlock(block, output.AsSpan((by * blocksX + bx) * 16, 16));
            }
        return output;
    }

    private static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> output)
    {
        // Alpha: 8-value mode (a0 > a1); equal endpoints fall through to index 0 everywhere.
        byte a0 = 0, a1 = 255;
        for (var p = 0; p < 16; p++) { a0 = Math.Max(a0, px[p * 4 + 3]); a1 = Math.Min(a1, px[p * 4 + 3]); }
        Span<int> alphas = stackalloc int[8];
        alphas[0] = a0; alphas[1] = a1;
        for (var i = 1; i < 7; i++) alphas[i + 1] = a0 > a1 ? ((7 - i) * a0 + i * a1) / 7 : a0;
        output[0] = a0; output[1] = a1;
        ulong alphaBits = 0;
        for (var p = 0; p < 16; p++) alphaBits |= (ulong)Nearest(alphas, px[p * 4 + 3]) << (3 * p);
        for (var i = 0; i < 6; i++) output[2 + i] = (byte)(alphaBits >> (8 * i));

        // Colour: bounding box inset by 1/16, quantised to 565, always 4-colour mode (c0 > c1).
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (var p = 0; p < 16; p++)
        {
            minR = Math.Min(minR, px[p * 4]); maxR = Math.Max(maxR, px[p * 4]);
            minG = Math.Min(minG, px[p * 4 + 1]); maxG = Math.Max(maxG, px[p * 4 + 1]);
            minB = Math.Min(minB, px[p * 4 + 2]); maxB = Math.Max(maxB, px[p * 4 + 2]);
        }
        int insetR = (maxR - minR) >> 4, insetG = (maxG - minG) >> 4, insetB = (maxB - minB) >> 4;
        var c0 = To565(maxR - insetR, maxG - insetG, maxB - insetB);
        var c1 = To565(minR + insetR, minG + insetG, minB + insetB);
        if (c0 < c1) (c0, c1) = (c1, c0);
        Span<int> palette = stackalloc int[12];
        From565(c0, palette[..3]); From565(c1, palette[3..6]);
        for (var c = 0; c < 3; c++)
        {
            palette[6 + c] = (2 * palette[c] + palette[3 + c]) / 3;
            palette[9 + c] = (palette[c] + 2 * palette[3 + c]) / 3;
        }
        uint colorBits = 0;
        if (c0 != c1)
            for (var p = 0; p < 16; p++)
            {
                int best = 0, bestDistance = int.MaxValue;
                for (var i = 0; i < 4; i++)
                {
                    int dr = px[p * 4] - palette[i * 3], dg = px[p * 4 + 1] - palette[i * 3 + 1], db = px[p * 4 + 2] - palette[i * 3 + 2];
                    var distance = dr * dr + dg * dg + db * db;
                    if (distance < bestDistance) { bestDistance = distance; best = i; }
                }
                colorBits |= (uint)best << (2 * p);
            }
        output[8] = (byte)c0; output[9] = (byte)(c0 >> 8); output[10] = (byte)c1; output[11] = (byte)(c1 >> 8);
        output[12] = (byte)colorBits; output[13] = (byte)(colorBits >> 8); output[14] = (byte)(colorBits >> 16); output[15] = (byte)(colorBits >> 24);
    }

    private static int Nearest(ReadOnlySpan<int> values, int value)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++) if (Math.Abs(values[i] - value) < Math.Abs(values[best] - value)) best = i;
        return best;
    }

    private static ushort To565(int r, int g, int b) => (ushort)(((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255));

    private static void From565(int c, Span<int> rgb)
    {
        rgb[0] = ((c >> 11) & 31) * 255 / 31; rgb[1] = ((c >> 5) & 63) * 255 / 63; rgb[2] = (c & 31) * 255 / 31;
    }
}
