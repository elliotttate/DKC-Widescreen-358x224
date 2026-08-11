using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace SuperZSNESDKCWidthMarginOverride.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var game = args.Length > 0 ? Path.GetFullPath(args[0]) :
                    Path.Combine(Environment.GetEnvironmentVariable("SUPERZSNES_MANAGED_DIR")
                        ?? throw new InvalidOperationException("Set SUPERZSNES_MANAGED_DIR before running the verifier."), "Assembly-CSharp.dll");
                VerifyInstalledIl(game);
                VerifyWidthMath();
                VerifyCapturedDimensions();
                Console.WriteLine("PASS DKC width-margin override offline verification");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static void VerifyInstalledIl(string gamePath)
        {
            using (var assembly = AssemblyDefinition.ReadAssembly(gamePath))
            {
                var renderer = assembly.MainModule.Types.Single(type => type.FullName == "PPURenderer");
                var generate = renderer.Methods.Single(method => method.Name == "GenerateBackground" && method.Parameters.Count == 2);
                var draw = renderer.Methods.Single(method => method.Name == "DrawLines" && method.Parameters.Count == 25);
                var wide = renderer.Fields.Single(field => field.Name == "wideScreenLengths");
                Require(generate.RVA == 0x44aa8 && generate.Body.CodeSize == 0x22cb,
                    "GenerateBackground is not the audited v0.230 IL body.");
                Require(draw.RVA == 0x470f4 && draw.Body.CodeSize == 0x7d6,
                    "DrawLines is not the audited v0.230 IL body.");
                Require(wide.FieldType.FullName == "System.Collections.Generic.List`1<System.Int32>",
                    "wideScreenLengths field type changed.");
                var drawCalls = generate.Body.Instructions.Count(instruction =>
                    instruction.Operand is MethodReference method && method.Name == "DrawLines" && method.Parameters.Count == 25);
                Require(drawCalls == 4, "Expected exactly four GenerateBackground -> DrawLines call sites.");
                var wideLoads = generate.Body.Instructions.Count(instruction =>
                    instruction.Operand is FieldReference field && field.Name == "wideScreenLengths");
                Require(wideLoads >= 4, "GenerateBackground no longer derives its margin from wideScreenLengths.");
                Console.WriteLine("installedIL GenerateBackground=rva0x44aa8/size0x22cb DrawLines=rva0x470f4/size0x7d6 calls=4 wideLoads=" + wideLoads);
            }
        }

        private static void VerifyWidthMath()
        {
            Require(WidthMath.RawColumns(7) == 47 && WidthMath.RawColumnPixels(7) == 376,
                "Seven-tile raw loop model failed.");
            Require(WidthMath.ClampWidthPixels(7) == 368 && WidthMath.PerSideGuardPixels(7, 358) == 5,
                "Seven-tile clamp/guard model failed.");
            Require(WidthMath.RawColumns(6) == 45 && WidthMath.RawColumnPixels(6) == 360,
                "Six-tile raw loop model failed.");
            Require(WidthMath.ClampWidthPixels(6) == 352 && WidthMath.PerSideGuardPixels(6, 358) == -3,
                "Six-tile clamp deficit model failed.");
            Require(WidthMath.RequiredMargin(358) == 7,
                "Ceiling margin requirement failed.");
            Console.WriteLine("geometry w7=47raw/376px/368clamp/+5each w6=45raw/360px/352clamp/-3each required=7");
        }

        private static void VerifyCapturedDimensions()
        {
            var capture = Environment.GetEnvironmentVariable("SUPERZSNES_WIDTH_CAPTURE");
            var monitor = Environment.GetEnvironmentVariable("SUPERZSNES_WIDTH_MONITOR_CAPTURE");
            if (string.IsNullOrWhiteSpace(capture) || string.IsNullOrWhiteSpace(monitor))
            {
                Console.WriteLine("SKIP capture-dimension oracle; set SUPERZSNES_WIDTH_CAPTURE and SUPERZSNES_WIDTH_MONITOR_CAPTURE to enable it.");
                return;
            }
            var state = Path.Combine(Path.GetDirectoryName(capture), "renderer-state.json");
            var settings = Path.Combine(Path.GetDirectoryName(capture), "widescreen-settings.json");
            var captureSize = ReadPngSize(capture);
            var monitorSize = ReadPngSize(monitor);
            Require(captureSize.Item1 == 1592 && captureSize.Item2 == 896,
                "Captured main render target is not 1592x896 (4x 398x224).");
            Require(monitorSize.Item1 == 1707 && monitorSize.Item2 == 1067,
                "Reference 16:10 full-content screenshot dimensions changed.");
            var equivalentSourceWidth = 224.0 * monitorSize.Item1 / monitorSize.Item2;
            Require(equivalentSourceWidth > 358.2 && equivalentSourceWidth < 358.6,
                "Reference display is not approximately 358.4 source pixels wide at 224 high.");
            Require(File.ReadAllText(state).Contains("\"xClampSize\":368"), "Captured OBJ clamp was not 368.");
            Require(File.ReadAllText(settings).Contains("\"wideScreenBG\":7") &&
                    File.ReadAllText(settings).Contains("\"widescreenOBJ\":7"),
                "Captured DKC settings were not BG=7/OBJ=7.");
            Console.WriteLine("capture mainRT=1592x896=>398x224 monitor=1707x1067=>" +
                              equivalentSourceWidth.ToString("0.000") + "x224 capturedClamp=368 BG/OBJ=7");
        }

        private static Tuple<int, int> ReadPngSize(string path)
        {
            var bytes = File.ReadAllBytes(path);
            Require(bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47,
                "Not a PNG: " + path);
            return Tuple.Create(ReadBigEndian(bytes, 16), ReadBigEndian(bytes, 20));
        }

        private static int ReadBigEndian(byte[] bytes, int offset)
        {
            return bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
