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

            Require(NativeTileDepthPatcher.CalculateDepthIndex(1, 0x1234) == 0x11234,
                "native depth table uses BG and absolute tilemap address");
            Require(NativeTileDepthPatcher.CalculateDepthIndex(3, 0x11234) == 0x31234,
                "native tilemap address wraps to 16 bits");
            byte[] scaleStub = NativeTileDepthPatcher.BuildScaleStub(
                0x200000, 0x100006, new IntPtr(0x300000), new IntPtr(0x400000));
            byte[] zStub = NativeTileDepthPatcher.BuildZStub(
                0x200100, 0x100009, new IntPtr(0x300010));
            Require(scaleStub.Length < 192 && scaleStub[0] == 0xF3,
                "scale stub shape");
            Require(ContainsSequence(scaleStub,
                new byte[] { 0x0F, 0xB7, 0x45, 0xFC }),
                "scale stub reads the absolute tilemap descriptor address");
            Require(ContainsSequence(scaleStub,
                new byte[] { 0x8B, 0x55, 0x10, 0xC1, 0xE2, 0x10, 0x01, 0xD0 }),
                "scale stub indexes BG plus tilemap-address table");
            Require(ContainsSequence(scaleStub,
                new byte[] { 0xF3, 0x0F, 0x10, 0x04, 0x85 }),
                "scale stub loads direct configured offset");
            Require(!ContainsSequence(scaleStub, new byte[] { 0xF7, 0x35 }),
                "scale stub has no modulo divide");
            Require(zStub.Length == 13 && zStub[0] == 0xF3 && zStub[8] == 0xE9,
                "Z stub shape");

            TileShape opaqueA = new TileShape(0x1000, 1, 1, 1, 1, 1, true);
            TileShape opaqueB = new TileShape(0x1002, 2, 1, 1, 1, 1, true);
            ComponentBuildResult joined = ConnectedComponentModel.Build(
                new[] { opaqueA, opaqueB }, 2, 1, 0, 7, 0.1f, 1, wrap: false);
            Require(joined.Components.Count == 1,
                "opaque edge contact stays on one connected depth plane");
            ComponentBuildResult separated = ConnectedComponentModel.Build(
                new[] { opaqueA, new TileShape(0x1002, 2, 0, 0, 0, 0, true) },
                2, 1, 0, 7, 0.1f, 1, wrap: false);
            Require(separated.Components.Count == 2,
                "transparent boundary permits separate depth planes");
            Require(ConnectedComponentModel.Touches(0x0004, 0x0002),
                "one-pixel diagonal edge contact is conservatively joined");
            ComponentBuildResult animationA = ConnectedComponentModel.Build(
                new[] { opaqueA }, 1, 1, 0, 7, 0.1f, 1, wrap: false);
            ComponentBuildResult animationB = ConnectedComponentModel.Build(
                new[] { new TileShape(0x1000, 99, 3, 3, 3, 3, true) },
                1, 1, 0, 7, 0.1f, 1, wrap: false);
            Near(animationA.Components[0].Depth, animationB.Components[0].Depth,
                "animation graphics retain address-stable automatic depth");
            string overrideId = separated.Components[0].Id;
            var overrides = new System.Collections.Generic.Dictionary<string, float>
                { [overrideId] = 0.625f };
            ComponentBuildResult overridden = ConnectedComponentModel.Build(
                new[] { opaqueA, new TileShape(0x1002, 2, 0, 0, 0, 0, true) },
                2, 1, 0, 7, 0.1f, 1, 64, overrides, false);
            ComponentInfo overriddenInfo = overridden.Components.Find(c => c.Id == overrideId);
            Near(overriddenInfo.Depth, 0.625f, "authored component depth override");

            string roundTrip = DepthMath.ToCsv(new[] { 1f, 0.25f, 2.5f });
            Require(roundTrip == "1,0.25,2.5", "invariant CSV formatting");
            Console.WriteLine("PASS: plane geometry, connected-component safety, authored overrides, native address-table stubs, and CSV persistence.");
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
