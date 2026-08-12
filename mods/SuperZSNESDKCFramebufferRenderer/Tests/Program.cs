using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            Assembly plugin = Assembly.Load("SuperZSNESDKCFramebufferRenderer");
            Type rasterizer = plugin.GetType("SuperZSNESDKCFramebufferRenderer.DkcFrameRasterizer", true);
            TestPlanar(rasterizer);
            TestTileMap(rasterizer);
            TestColorMath(rasterizer);
            TestLegacyShaderAdd(rasterizer);
            TestRegionModes(rasterizer);
            TestStockPaletteExpansion(rasterizer);
            TestCachePrimitives(rasterizer);
            TestDecodedTileAtlas(rasterizer);
            TestRasterPartialModel(rasterizer);
            TestFixedNativePillarbox(rasterizer);
            TestFallbackTelemetry(plugin);
            TestRuntimeShape();
            Console.WriteLine("PASS: planar decode, decoded-tile atlas, tilemap addressing, SNES and legacy-shader color math, window regions, retained-cache and raster-partial equivalence, fixed-native pillarbox, fallback telemetry, and v0.230 patch targets.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void TestPlanar(Type type)
    {
        MethodInfo decode = Required(type, "DecodePlanarAtAddress");
        byte[] vram = new byte[65536];
        // One 4bpp row: colors 1,2,4,8,15,0,3,12.
        int[] expected = { 1, 2, 4, 8, 15, 0, 3, 12 };
        for (int x = 0; x < 8; x++)
        {
            int bit = 7 - x;
            int color = expected[x];
            if ((color & 1) != 0) vram[0] |= (byte)(1 << bit);
            if ((color & 2) != 0) vram[1] |= (byte)(1 << bit);
            if ((color & 4) != 0) vram[16] |= (byte)(1 << bit);
            if ((color & 8) != 0) vram[17] |= (byte)(1 << bit);
        }
        for (int x = 0; x < 8; x++)
            Require((int)decode.Invoke(null, new object[] { vram, 0, 4, x, 0 }) == expected[x],
                "4bpp planar decode mismatch at x=" + x);
    }

    private static void TestTileMap(Type type)
    {
        MethodInfo address = Required(type, "GetTileAddress");
        int A(int x, int y, byte size, bool large) =>
            (int)address.Invoke(null, new object[] { x, y, size, large });
        Require(A(0, 0, 0, false) == 0, "32x32 origin");
        Require(A(31, 31, 0, false) == 2046, "32x32 end");
        Require(A(32, 0, 1, false) == 2048, "horizontal second screen");
        Require(A(0, 32, 2, false) == 2048, "vertical second screen");
        Require(A(32, 32, 3, false) == 6144, "64x64 fourth screen");
        Require(A(2, 0, 0, true) == 2, "16x16 coordinate reduction");
    }

    private static void TestColorMath(Type type)
    {
        MethodInfo blend = Required(type, "Blend");
        ushort red10 = 10;
        ushort red7 = 7;
        Require((ushort)blend.Invoke(null, new object[] { red10, red7, false, false }) == 17, "add");
        Require((ushort)blend.Invoke(null, new object[] { red10, red7, true, false }) == 3, "subtract");
        Require((ushort)blend.Invoke(null, new object[] { red7, red10, true, false }) == 0, "subtract clamp");
        Require((ushort)blend.Invoke(null, new object[] { (ushort)31, (ushort)31, false, false }) == 31, "add clamp");
        Require((ushort)blend.Invoke(null, new object[] { (ushort)20, (ushort)10, false, true }) == 15, "half");
        Require((ushort)blend.Invoke(null, new object[] { (ushort)31, (ushort)31, false, true }) == 31,
            "half-add divides before saturation");
    }

    private static void TestLegacyShaderAdd(Type type)
    {
        MethodInfo add = Required(type, "LegacyAddChannel");
        int A(int main, int sub, int brightness) =>
            (byte)add.Invoke(null, new object[] { main, sub, brightness });
        // Stable channel combinations from the Slip-Slide Ride frame-573672
        // legacy main/sub/final oracle.
        Require(A(2, 1, 15) == 31, "legacy shader add 16+8");
        Require(A(5, 1, 15) == 53, "legacy shader add 40+8");
        Require(A(6, 1, 15) == 61, "legacy shader add 48+8");
        Require(A(9, 1, 15) == 84, "legacy shader add 72+8");
        Require(A(31, 31, 15) == 255, "legacy shader add saturation");
        Require(A(31, 31, 0) == 0, "legacy shader add zero brightness");
    }

    private static void TestRegionModes(Type type)
    {
        MethodInfo apply = Required(type, "ApplyRegionMode");
        bool R(int mode, bool inside, bool math) =>
            (bool)apply.Invoke(null, new object[] { mode, inside, math });
        Require(R(0, false, true) && R(0, true, true), "math always");
        Require(!R(3, false, true) && !R(3, true, true), "math never");
        Require(!R(0, false, false) && !R(0, true, false), "clip never");
        Require(R(3, false, false) && R(3, true, false), "clip always");
    }

    private static void TestCachePrimitives(Type type)
    {
        MethodInfo bucket = Required(type, "TileBucket");
        Require((int)bucket.Invoke(null, new object[] { 0x8161 }) == 0x8160, "positive tile bucket");
        Require((int)bucket.Invoke(null, new object[] { 0xFFFF }) == 0xFFF8, "wrapped tile bucket");

        MethodInfo equal = Required(type, "CircularRangeEquals");
        byte[] source = new byte[65536];
        source[65534] = 1; source[65535] = 2; source[0] = 3; source[1] = 4;
        byte[] snapshot = { 1, 2, 3, 4 };
        Require((bool)equal.Invoke(null, new object[] { source, 65534, 4, snapshot }),
            "circular VRAM range compare");
        source[0] = 5;
        Require(!(bool)equal.Invoke(null, new object[] { source, 65534, 4, snapshot }),
            "circular VRAM change invalidation");
    }

    private static void TestRasterPartialModel(Type type)
    {
        MethodInfo test = Required(type, "RasterPartialModelSelfTest");
        Require((bool)test.Invoke(null, null),
            "scroll-only raster partial refresh must equal a clean full rebuild and reject changed VRAM");
    }

    private static void TestStockPaletteExpansion(Type type)
    {
        MethodInfo expand = Required(type, "ExpandStockChannel");
        int E(int value, int brightness) =>
            (byte)expand.Invoke(null, new object[] { value, brightness });
        Require(E(0, 15) == 1, "stock palette zero clamp");
        Require(E(25, 15) == 199, "stock palette c/32 conversion");
        Require(E(31, 15) == 247, "stock palette maximum");
        Require(E(31, 0) == 0, "zero brightness");
    }

    private static void TestDecodedTileAtlas(Type type)
    {
        object rasterizer = Activator.CreateInstance(type, BindingFlags.Instance |
            BindingFlags.NonPublic, null, new object[] { true }, null);
        Type cacheType = type.GetNestedType("BackgroundCache", BindingFlags.NonPublic);
        object cache = Activator.CreateInstance(cacheType, true);
        MethodInfo prepare = type.GetMethod("PrepareDecodedTileFrame", BindingFlags.Static |
            BindingFlags.NonPublic);
        MethodInfo read = type.GetMethod("ReadDecodedTileColor", BindingFlags.Instance |
            BindingFlags.NonPublic);
        MethodInfo decode = Required(type, "DecodePlanar");
        byte[] vram = new byte[65536];
        for (int i = 0; i < vram.Length; i++) vram[i] = (byte)((i * 37 + 11) & 0xFF);

        prepare.Invoke(null, new object[] { cache, 0xFFF0, 4 });
        foreach (int tile in new[] { 0, 1, 127, 511, 1023 })
        foreach (int y in new[] { 0, 3, 7 })
        foreach (int x in new[] { 0, 4, 7 })
        {
            int expected = (int)decode.Invoke(null, new object[] { vram, 0xFFF0, tile, 4, x, y });
            int actual = (int)read.Invoke(rasterizer,
                new object[] { vram, cache, 0xFFF0, tile, 4, x, y, 0 });
            Require(actual == expected,
                "decoded atlas mismatch at tile=" + tile + " x=" + x + " y=" + y);
        }

        prepare.Invoke(null, new object[] { cache, 0xFFF0, 4 });
        foreach (int tile in new[] { 0, 1, 127, 511, 1023 })
            read.Invoke(rasterizer, new object[] { vram, cache, 0xFFF0, tile, 4, 0, 0, 0 });
        long[] hits = (long[])type.GetField("PerBgDecodedTileHits", BindingFlags.Instance |
            BindingFlags.NonPublic).GetValue(rasterizer);
        long[] misses = (long[])type.GetField("PerBgDecodedTileMisses", BindingFlags.Instance |
            BindingFlags.NonPublic).GetValue(rasterizer);
        Require(hits[0] == 5 && misses[0] == 5, "decoded atlas hit/miss accounting");

        vram[0xFFF0] ^= 0x80;
        prepare.Invoke(null, new object[] { cache, 0xFFF0, 4 });
        read.Invoke(rasterizer, new object[] { vram, cache, 0xFFF0, 0, 4, 0, 0, 0 });
        Require(misses[0] == 6, "decoded atlas VRAM invalidation");
    }

    private static void TestFixedNativePillarbox(Type type)
    {
        MethodInfo signature = Required(type, "IsFixedNativeFrameSignature");
        bool S(byte mode, byte tm, byte ts, byte math, int sx1, int sx2, byte bg1sc, byte bg2sc) =>
            (bool)signature.Invoke(null, new object[] { mode, tm, ts, math, sx1, sx2, bg1sc, bg2sc });
        Require(S(1, 0x11, 0, 0, 0, 0, 0x7C, 0x78), "Snow map fixed-frame signature");
        Require(!S(9, 0x17, 0x17, 0x93, 0, 0, 0x7D, 0x79), "gameplay must stay wide");
        Require(!S(1, 0x11, 0, 0, 1, 0, 0x7C, 0x78), "scrolling BG1 must stay wide");
        Require(!S(1, 0x11, 0, 0, 0, 0, 0x7D, 0x78), "64-wide BG1 must stay wide");
        Require(!S(1, 0x11, 0, 1, 0, 0, 0x7C, 0x78), "color-math scene must stay wide");
    }

    private static void TestRuntimeShape()
    {
        Require(AccessTools.Method(typeof(PPURenderer), "GenerateBackgrounds", Type.EmptyTypes) != null,
            "GenerateBackgrounds target missing");
        MethodInfo image = AccessTools.Method(typeof(MainScreenBlit), "OnRenderImage",
            new[] { typeof(RenderTexture), typeof(RenderTexture) });
        Require(image != null && image.IsPrivate, "private OnRenderImage target missing");
        Require(AccessTools.Method(typeof(SNESPPU), "WriteIO", new[] { typeof(uint), typeof(byte) }) != null,
            "SNESPPU.WriteIO target missing");
        Require(AccessTools.Field(typeof(MainScreenBlit), "_transferMaterialUsed") != null,
            "transfer material field missing");
        string[] required = { "_ppuStartFrame", "_ppuLineChanges", "_cgLineChanges" };
        foreach (string property in required)
            Require(typeof(SNESPPU).GetProperty(property) != null, "SNESPPU property missing: " + property);
    }

    private static void TestFallbackTelemetry(Assembly plugin)
    {
        Type controller = plugin.GetType("SuperZSNESDKCFramebufferRenderer.FramebufferController", true);
        Type patches = plugin.GetType("SuperZSNESDKCFramebufferRenderer.RendererPatches", true);
        Require(patches.GetMethod("GenerateBackgroundsPostfix", BindingFlags.Static |
            BindingFlags.Public) != null, "fallback timing postfix missing");

        MethodInfo reset = controller.GetMethod("ResetTelemetry", BindingFlags.Static |
            BindingFlags.NonPublic);
        MethodInfo toJson = controller.GetMethod("FallbackReasonsJson", BindingFlags.Static |
            BindingFlags.NonPublic);
        Type metricType = controller.GetNestedType("FallbackMetric", BindingFlags.NonPublic);
        Require(reset != null && toJson != null && metricType != null,
            "fallback telemetry shape missing");
        reset.Invoke(null, null);

        object metric = Activator.CreateInstance(metricType, true);
        metricType.GetField("Reason", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(metric, "mosaic\"\nmode");
        metricType.GetField("Frames", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(metric, 3L);
        metricType.GetField("MeasuredFrames", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(metric, 2L);
        metricType.GetField("RendererMs", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(metric, 8.0);
        metricType.GetField("MaxRendererMs", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(metric, 5.5);
        metricType.GetField("MaxConsecutiveFrames", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(metric, 3);

        var metrics = (System.Collections.IList)controller.GetField("FallbackMetrics",
            BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        metrics.Add(metric);
        string json = (string)toJson.Invoke(null, null);
        Require(json.Contains("mosaic\\\"\\nmode"), "fallback reason JSON escaping");
        Require(json.Contains("\"frames\":3"), "fallback frame count JSON");
        Require(json.Contains("\"averageStockRendererMs\":4.0000"),
            "fallback average JSON");
        Require(json.Contains("\"maxStockRendererMs\":5.5000"),
            "fallback maximum JSON");
        reset.Invoke(null, null);
    }

    private static MethodInfo Required(Type type, string name)
    {
        MethodInfo method = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(m => m.Name == name);
        if (method == null) throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
