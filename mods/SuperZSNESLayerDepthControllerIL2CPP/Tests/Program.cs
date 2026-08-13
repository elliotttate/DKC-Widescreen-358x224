using System;
using System.IO;
using System.Text.Json;
using SuperZSNESLayerDepthControllerIL2CPP;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--inspect-ground-snapshot")
                return InspectGroundSnapshot(args[1], args.Length >= 3 ? args[2] : null);
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

            DepthProfile cohesive = DepthMath.Build(gaps, 0.01f, 6, scales);
            Near(cohesive.PlaneZ[7] - cohesive.PlaneZ[10], 0.03f,
                "cohesive mode keeps BG1 low/high ordered within a tiny geometric gap");

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

            Require(NativeSpriteLoopPatcher.LoopLimitRva == 0x393DC8,
                "v0.300 RenderLines terminal comparison RVA");
            Require(NativeSpriteLoopPatcher.OriginalBytes[2] == 0x80 &&
                NativeSpriteLoopPatcher.ReplacementBytes[2] == 0x7F,
                "129-pass loop is reduced to exactly 128 OAM entries");

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

            TestForegroundGroundRasterizer();
            Near(new ForegroundGroundSettings().Depth, -4f,
                "foreground ground default avoids stock priority-plane coplanarity");
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
            Console.WriteLine("PASS: plane geometry, connected-component safety, foreground-ground cutouts, authored overrides, native address-table stubs, and CSV persistence.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }

    private static int InspectGroundSnapshot(string directory, string outputPath)
    {
        using JsonDocument metadata = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "snapshot.json")));
        JsonElement xValues = metadata.RootElement.GetProperty("BackgroundScrollX");
        JsonElement yValues = metadata.RootElement.GetProperty("BackgroundScrollY");
        byte[] vram = File.ReadAllBytes(Path.Combine(directory, "snapshot-vram.bin"));
        byte[] cgram = File.ReadAllBytes(Path.Combine(directory, "snapshot-cgram.bin"));
        byte[] registers = File.ReadAllBytes(Path.Combine(directory,
            "snapshot-registers.bin"));
        int height = ForegroundGroundRasterizer.ViewHeight;
        int width = ForegroundGroundRasterizer.ViewWidth;
        var scrollX = new int[height];
        var scrollY = new int[height];
        var display = new byte[height];
        var main = new byte[height];
        var colorControl = new byte[height];
        Array.Fill(scrollX, xValues[0].GetInt32());
        Array.Fill(scrollY, yValues[0].GetInt32());
        Array.Fill(display, registers[0]);
        Array.Fill(main, registers[44]);
        Array.Fill(colorControl, registers[48]);
        var edges = new int[width];
        var smooth = new int[width];
        var output = new uint[width * height];
        if (!ForegroundGroundRasterizer.TryRasterize(vram, cgram, registers,
                scrollX, scrollY, display, main, colorControl, 0, 184, true, 56,
                edges, smooth, output, out string reason))
            throw new InvalidOperationException(reason);
        int minX = width, maxX = -1, minY = height, maxY = -1, opaque = 0;
        var occupiedColumns = new bool[width];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if ((output[y * width + x] >> 24) == 0) continue;
            opaque++;
            occupiedColumns[x] = true;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        int columns = 0;
        foreach (bool occupied in occupiedColumns) if (occupied) columns++;
        Console.WriteLine("opaque={0} bounds={1},{2}..{3},{4} columns={5}",
            opaque, minX, minY, maxX, maxY, columns);
        if (!string.IsNullOrWhiteSpace(outputPath))
            WriteBitmap(outputPath, output, width, height);
        return 0;
    }

    private static void WriteBitmap(string path, uint[] argb, int width, int height)
    {
        int pixelBytes = width * height * 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);
        writer.Write(54 + pixelBytes);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835); writer.Write(2835);
        writer.Write(0); writer.Write(0);
        for (int y = height - 1; y >= 0; y--)
        for (int x = 0; x < width; x++)
        {
            uint value = argb[y * width + x];
            writer.Write((byte)value);
            writer.Write((byte)(value >> 8));
            writer.Write((byte)(value >> 16));
            writer.Write((byte)(value >> 24));
        }
    }

    private static void TestForegroundGroundRasterizer()
    {
        var vram = new byte[65536];
        var cgram = new byte[512];
        var registers = new byte[64];
        var scrollX = new int[ForegroundGroundRasterizer.ViewHeight];
        var scrollY = new int[ForegroundGroundRasterizer.ViewHeight];
        var display = new byte[ForegroundGroundRasterizer.ViewHeight];
        var main = new byte[ForegroundGroundRasterizer.ViewHeight];
        var colorControl = new byte[ForegroundGroundRasterizer.ViewHeight];
        var edges = new int[ForegroundGroundRasterizer.ViewWidth];
        var smooth = new int[ForegroundGroundRasterizer.ViewWidth];
        var output = new uint[ForegroundGroundRasterizer.ViewWidth *
            ForegroundGroundRasterizer.ViewHeight];
        registers[0] = 15;
        registers[5] = 1;
        registers[7] = 0x20;
        registers[11] = 0;
        registers[44] = 1;
        Array.Fill(display, (byte)15);
        Array.Fill(main, (byte)1);
        cgram[2] = 0x1F;
        cgram[3] = 0;
        vram[0] = 0x80;
        Require(ForegroundGroundRasterizer.TryRasterize(vram, cgram, registers,
            scrollX, scrollY, display, main, colorControl, 0, 10,
            false, 56, edges, smooth, output,
            out string reason),
            "foreground ground synthetic raster: " + reason);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < ForegroundGroundRasterizer.ViewWidth; x++)
                Require(output[y * ForegroundGroundRasterizer.ViewWidth + x] == 0,
                    "foreground cut line remains transparent above authored Y");
        Require((output[16 * ForegroundGroundRasterizer.ViewWidth + 56] >> 24) == 0xFF,
            "foreground ground preserves opaque BG pixels below authored Y");
        Require((output[16 * ForegroundGroundRasterizer.ViewWidth + 56] & 0x00FFFFFF) ==
                0x00FF0000,
            "foreground ground decodes the live BGR555 palette");
        var cropped = new uint[output.Length];
        ForegroundGroundRasterizer.CropToOpaqueBounds(output, 1, cropped,
            out int left, out int top, out int width, out int height);
        Require(left == 0 && top == 15 && width == 362 && height == 203,
            "foreground crop follows the opaque cutout instead of a full dark quad: " +
            left + "," + top + " " + width + "x" + height);
        Require(cropped[0] == 0 && Array.Exists(cropped, value => value != 0),
            "foreground crop preserves transparent padding and opaque content");
        var band = new uint[output.Length];
        uint sand = 0xFFCD8B39u;
        uint hiddenRock = 0xFF181808u;
        for (int y = 50; y <= 80; y++)
            for (int x = 0; x < ForegroundGroundRasterizer.ViewWidth; x++)
                band[y * ForegroundGroundRasterizer.ViewWidth + x] = sand;
        for (int y = 81; y < ForegroundGroundRasterizer.ViewHeight; y++)
            for (int x = 0; x < ForegroundGroundRasterizer.ViewWidth; x++)
                band[y * ForegroundGroundRasterizer.ViewWidth + x] = hiddenRock;
        ForegroundGroundRasterizer.ApplyNaturalGroundMask(band, 50, 32,
            edges, smooth);
        Require(band[50 * ForegroundGroundRasterizer.ViewWidth + 184] == sand &&
                band[80 * ForegroundGroundRasterizer.ViewWidth + 184] == sand,
            "foreground band keeps the connected sand surface");
        Require(band[96 * ForegroundGroundRasterizer.ViewWidth + 184] == 0 &&
                band[180 * ForegroundGroundRasterizer.ViewWidth + 184] == 0,
            "foreground band excludes opaque hidden scenery below the surface");
        Array.Clear(main, 0, main.Length);
        Require(ForegroundGroundRasterizer.TryRasterize(vram, cgram, registers,
            scrollX, scrollY, display, main, colorControl, 0, 10,
            false, 56, edges, smooth, output,
            out reason),
            "foreground layer accepts a fully transparent main-screen interval");
        Require(Array.TrueForAll(output, value => value == 0),
            "foreground layer stays transparent when source BG is not on main screen");
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
