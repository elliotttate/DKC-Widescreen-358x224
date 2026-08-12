using System;
using SuperZSNESLayerDepthControllerIL2CPP;

internal static class Program
{
    private static int Main()
    {
        try
        {
            Require(DepthMath.TryParseCsv("1,1,1,1,1,1,1,1,1,1,1,1,1",
                13, 0f, 100f, out float[] gaps, out _), "valid 13-gap parse");
            Require(!DepthMath.TryParseCsv("1,2", 13, 0f, 100f, out _, out _),
                "short gap list rejected");
            Require(!DepthMath.TryParseCsv("1,1,1,1,1,1,1,1,1,1,1,1,-1",
                13, 0f, 100f, out _, out _), "negative gap rejected");
            Require(DepthMath.TryParseCsv("1,1,1,1,1,1,1,1,1,1,1,1,1,1",
                14, 0.01f, 10f, out float[] scales, out _), "valid 14-scale parse");

            DepthProfile profile = DepthMath.Build(gaps, 0.5f, 6, scales);
            Near(profile.BackdropZ, 3f, "backdrop position");
            Near(profile.PlaneZ[0], 2.5f, "P0 position");
            Near(profile.PlaneZ[5], 0f, "neutral-adjacent P5 position");
            Near(profile.PlaneZ[12], -3.5f, "P12 position");
            Near(profile.BackdropScale, 1f, "backdrop scale");
            Near(profile.PlaneScale[12], 1f, "P12 scale");

            float[] custom = new float[14];
            for (int i = 0; i < custom.Length; i++) custom[i] = 0.5f + i * 0.1f;
            profile = DepthMath.Build(gaps, 1f, 0, custom);
            Near(profile.BackdropZ, 0f, "neutral backdrop");
            Near(profile.PlaneZ[0], -1f, "P0 after backdrop neutral");
            Near(profile.BackdropScale, 0.5f, "custom backdrop scale");
            Near(profile.PlaneScale[12], 1.8f, "custom P12 scale");

            Near(DepthMath.PerspectiveCompensation(3f, 30f), 1.111111f,
                "far-plane perspective compensation");
            Near(DepthMath.PerspectiveCompensation(-3f, 30f), 0.909091f,
                "near-plane perspective compensation");
            Near(DepthMath.SublayerCompensation(3f, 0.33f, 30f), 1.01f,
                "palette sublayer compensation");

            int baseIndex = NativeTileDepthPatcher.CalculatePaletteIndex(0x1234, 1);
            Require(baseIndex == NativeTileDepthPatcher.CalculatePaletteIndex(0xD234, 1),
                "tile flips do not change native palette index");
            Require(baseIndex == NativeTileDepthPatcher.CalculatePaletteIndex(0x1235, 1),
                "tile number does not change native palette index");
            Require(baseIndex != NativeTileDepthPatcher.CalculatePaletteIndex(0x1634, 1),
                "palette number changes native palette index");
            float[] paletteOffsets = new float[NativeTileDepthPatcher.PaletteOffsetCount];
            for (int i = 0; i < paletteOffsets.Length; i++) paletteOffsets[i] = i / 100f;
            Near(NativeTileDepthPatcher.CalculateOffset(0x1234, 1, paletteOffsets),
                paletteOffsets[baseIndex], "native palette offset table lookup");
            byte[] scaleStub = NativeTileDepthPatcher.BuildScaleStub(
                0x200000, 0x100006, new IntPtr(0x300000));
            byte[] zStub = NativeTileDepthPatcher.BuildZStub(
                0x200100, 0x100009, new IntPtr(0x300010));
            Require(scaleStub.Length < 192 && scaleStub[0] == 0xF3,
                "scale stub shape");
            Require(ContainsSequence(scaleStub,
                new byte[] { 0xC1, 0xE8, 0x0A, 0x83, 0xE0, 0x07 }),
                "scale stub extracts SNES palette bits");
            Require(ContainsSequence(scaleStub,
                new byte[] { 0x8B, 0x55, 0x10, 0xC1, 0xE2, 0x03, 0x01, 0xD0 }),
                "scale stub indexes BG palette table");
            Require(ContainsSequence(scaleStub,
                new byte[] { 0xF3, 0x0F, 0x10, 0x04, 0x85 }),
                "scale stub loads direct configured offset");
            Require(!ContainsSequence(scaleStub, new byte[] { 0xF7, 0x35 }),
                "scale stub has no modulo divide");
            Require(zStub.Length == 13 && zStub[0] == 0xF3 && zStub[8] == 0xE9,
                "Z stub shape");

            string roundTrip = DepthMath.ToCsv(new[] { 1f, 0.25f, 2.5f });
            Require(roundTrip == "1,0.25,2.5", "invariant CSV formatting");
            Console.WriteLine("PASS: profile parsing, plane geometry, perspective compensation, palette sublayers, native stubs, and CSV persistence.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }

    private static void Near(float actual, float expected, string name)
    {
        if (Math.Abs(actual - expected) > 0.0001f)
            throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) break;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
