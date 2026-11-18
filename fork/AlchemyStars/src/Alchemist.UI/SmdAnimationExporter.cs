using RedFox.Graphics3D.Skeletal;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace Alchemist.UI;

internal static class SmdAnimationExporter
{
    private const float TwoPi = MathF.PI * 2;

    /// <param name="scale">Translation scale (1/2.54 converts CAST centimetres to Source inches).</param>
    public static void Save(string outputPath, SkeletonAnimation animation, float scale = 1)
    {
        var skeleton = animation.Skeleton
            ?? throw new InvalidDataException("SMD animation export requires a skeleton.");
        var targets = animation.Targets.ToDictionary(
            target => target.BoneName,
            StringComparer.OrdinalIgnoreCase);
        var frameCount = GetFrameCount(animation);
        var previousEuler = new Vector3[skeleton.Bones.Count];
        var hasPreviousEuler = new bool[skeleton.Bones.Count];

        using var stream = File.Create(outputPath);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("version 1");
        writer.WriteLine("nodes");
        foreach (var bone in skeleton.Bones)
        {
            var parentIndex = bone.Parent is null ? -1 : skeleton.Bones.IndexOf(bone.Parent);
            if (bone.Parent is not null && parentIndex < 0)
                throw new InvalidDataException($"SMD parent bone for '{bone.Name}' is missing from the skeleton.");
            writer.WriteLine($"{bone.Index} \"{SanitizeName(bone.Name ?? $"bone_{bone.Index}")}\" {parentIndex}");
        }
        writer.WriteLine("end");
        writer.WriteLine("skeleton");

        for (var frame = 0; frame < frameCount; frame++)
        {
            writer.WriteLine($"time {frame}");
            for (var boneIndex = 0; boneIndex < skeleton.Bones.Count; boneIndex++)
            {
                var bone = skeleton.Bones[boneIndex];
                targets.TryGetValue(bone.Name ?? string.Empty, out var target);
                var translation = target?.TranslationFrameCount > 0
                    ? target.SampleTranslation(frame)
                    : bone.BaseLocalTranslation;
                var rotation = target?.RotationFrameCount > 0
                    ? target.SampleRotation(frame)
                    : bone.BaseLocalRotation;
                var euler = ToEulerXyz(rotation);
                if (hasPreviousEuler[boneIndex])
                    euler = Unwrap(euler, previousEuler[boneIndex]);
                previousEuler[boneIndex] = euler;
                hasPreviousEuler[boneIndex] = true;

                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{bone.Index} {Number(translation.X * scale)} {Number(translation.Y * scale)} {Number(translation.Z * scale)} {Number(euler.X)} {Number(euler.Y)} {Number(euler.Z)}"));
            }
        }

        writer.WriteLine("end");
    }

    internal static Vector3 ToEulerXyz(Quaternion rotation)
    {
        // Double precision avoids amplified float cancellation near a 90-degree
        // pitch, which otherwise moves distant descendants after SMD import.
        double qx = rotation.X, qy = rotation.Y, qz = rotation.Z, qw = rotation.W;
        var norm = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        qx /= norm; qy /= norm; qz /= norm; qw /= norm;
        var sinY = Math.Clamp(2 * (qw * qy - qz * qx), -1, 1);
        // Gimbal lock (pitch ±90°, e.g. CoD j_mainroot): both atan2 terms are 0/0, so only x - z is defined.
        // Put it all in x with z = 0, read from the R = Rz*Ry*Rx matrix: m11 = cos x, m12 = -sin x.
        if (Math.Abs(sinY) > 1 - 1e-6)
        {
            var lockedX = Math.Atan2(-2 * (qy * qz - qw * qx), 1 - 2 * (qx * qx + qz * qz));
            return new Vector3((float)lockedX, (float)(Math.Sign(sinY) * Math.PI / 2), 0);
        }
        var x = Math.Atan2(2 * (qw * qx + qy * qz), 1 - 2 * (qx * qx + qy * qy));
        var y = Math.Asin(sinY);
        var z = Math.Atan2(2 * (qw * qz + qx * qy), 1 - 2 * (qy * qy + qz * qz));
        return new Vector3((float)x, (float)y, (float)z);
    }

    private static int GetFrameCount(SkeletonAnimation animation)
    {
        var maximum = animation.Targets
            .SelectMany(target =>
                (target.TranslationFrames?.Select(frame => frame.Frame) ?? [])
                .Concat(target.RotationFrames?.Select(frame => frame.Frame) ?? []))
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(1, checked((int)MathF.Floor(maximum) + 1));
    }

    private static Vector3 Unwrap(Vector3 value, Vector3 previous) => new(
        Unwrap(value.X, previous.X),
        Unwrap(value.Y, previous.Y),
        Unwrap(value.Z, previous.Z));

    private static float Unwrap(float value, float previous)
    {
        while (value - previous > MathF.PI)
            value -= TwoPi;
        while (value - previous < -MathF.PI)
            value += TwoPi;
        return value;
    }

    private static string Number(float value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string SanitizeName(string name) => name
        .Replace('"', '\'')
        .Replace('\r', ' ')
        .Replace('\n', ' ');
}
