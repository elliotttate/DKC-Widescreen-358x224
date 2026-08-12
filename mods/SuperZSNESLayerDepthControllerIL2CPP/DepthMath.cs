using System;
using System.Globalization;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    internal sealed class DepthProfile
    {
        internal float BackdropZ;
        internal float BackdropScale;
        internal float[] PlaneZ = new float[13];
        internal float[] PlaneScale = new float[13];
    }

    internal static class DepthMath
    {
        internal const int GapCount = 13;
        internal const int ScaleCount = 14;

        internal static bool TryParseCsv(string value, int count, float minimum,
            float maximum, out float[] parsed, out string error)
        {
            parsed = null;
            error = string.Empty;
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length != count)
            {
                error = "expected " + count + " comma-separated values, got " + parts.Length;
                return false;
            }
            float[] result = new float[count];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float number) ||
                    float.IsNaN(number) || float.IsInfinity(number))
                {
                    error = "value " + i + " is not a finite number";
                    return false;
                }
                if (number < minimum || number > maximum)
                {
                    error = "value " + i + " is outside " + minimum + ".." + maximum;
                    return false;
                }
                result[i] = number;
            }
            parsed = result;
            return true;
        }

        internal static DepthProfile Build(float[] gaps, float separation,
            int neutralBoundary, float[] scales)
        {
            if (gaps == null || gaps.Length != GapCount)
                throw new ArgumentException("Exactly 13 gaps are required.", nameof(gaps));
            if (scales == null || scales.Length != ScaleCount)
                throw new ArgumentException("Exactly 14 scales are required.", nameof(scales));
            if (neutralBoundary < 0 || neutralBoundary > GapCount)
                throw new ArgumentOutOfRangeException(nameof(neutralBoundary));

            float neutralOffset = 0f;
            for (int i = 0; i < neutralBoundary; i++)
                neutralOffset += gaps[i] * separation;

            DepthProfile profile = new DepthProfile
            {
                BackdropZ = neutralOffset,
                BackdropScale = scales[0]
            };
            float accumulated = 0f;
            for (int i = 0; i < GapCount; i++)
            {
                accumulated += gaps[i] * separation;
                profile.PlaneZ[i] = neutralOffset - accumulated;
                profile.PlaneScale[i] = scales[i + 1];
            }
            return profile;
        }

        internal static string ToCsv(float[] values)
        {
            string[] text = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                text[i] = values[i].ToString("0.###", CultureInfo.InvariantCulture);
            return string.Join(",", text);
        }

        internal static float PerspectiveCompensation(float planeZ, float cameraDistance)
        {
            if (cameraDistance <= 0.001f)
                throw new ArgumentOutOfRangeException(nameof(cameraDistance));
            return Math.Max(0.01f, cameraDistance / (cameraDistance - planeZ));
        }

        internal static float SublayerCompensation(float baseZ, float offset,
            float cameraDistance)
        {
            float denominator = cameraDistance + baseZ;
            if (denominator <= 0.001f)
                return 1f;
            return Math.Max(0.01f, (cameraDistance + baseZ + offset) / denominator);
        }
    }
}
