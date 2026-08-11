using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SuperZSNESDKCBackgroundStateCache.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var gamePath = args.Length == 0
                    ? Path.Combine(RequiredEnvironment("SUPERZSNES_MANAGED_DIR"), "Assembly-CSharp.dll")
                    : Path.GetFullPath(args[0]);
                var managed = Path.GetDirectoryName(gamePath);
                var bepinex = Path.Combine(RequiredEnvironment("BEPINEX_ROOT"), "BepInEx", "core");
                AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) => Resolve(eventArgs.Name, managed, bepinex);
                VerifyExactV0230Shape(gamePath);
                VerifyPpuRegisterCoverage(gamePath);
                VerifyFilteredPpuStreamSemantics();
                VerifyPatchSurface();
                VerifyExactFullVramComparison();
                VerifyCompiledFailClosedGuards();
                Console.WriteLine("PASS DKC exact whole-background cache offline verification");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL " + exception);
                return 1;
            }
        }

        private static string RequiredEnvironment(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Set " + name + " before running the verifier.");

        private static readonly uint[] ExpectedGenerateBackgroundPpuCases =
        {
            0x2100,
            0x2105, 0x2106, 0x2107, 0x2108, 0x2109, 0x210A, 0x210B, 0x210C,
            0x210D, 0x210E, 0x210F, 0x2110, 0x2111, 0x2112, 0x2113, 0x2114,
            0x211A, 0x211B, 0x211C, 0x211D, 0x211E, 0x211F, 0x2120,
            0x2123, 0x2124, 0x2125,
            0x212A, 0x212B, 0x212E, 0x212F,
            0x2130, 0x2131
        };

        private static void VerifyPpuRegisterCoverage(string gamePath)
        {
            uint[] actualCases;
            using (var game = AssemblyDefinition.ReadAssembly(gamePath))
            {
                var renderer = game.MainModule.Types.Single(type => type.FullName == "PPURenderer");
                var method = renderer.Methods.Single(candidate => candidate.Name == "GenerateBackground" &&
                    candidate.Parameters.Count == 2 && candidate.Parameters[0].ParameterType.FullName == "System.Int32");
                var addressSwitch = method.Body.Instructions.Single(instruction =>
                    instruction.OpCode.Code == Code.Switch &&
                    instruction.Operand is Instruction[] targets && targets.Length == 50 &&
                    instruction.Previous != null && instruction.Previous.OpCode.Code == Code.Sub &&
                    TryGetLdcI4(instruction.Previous.Previous, out var value) && value == 0x2100);
                var switchTargets = (Instruction[])addressSwitch.Operand;
                var fallthrough = addressSwitch.Next;
                while (fallthrough != null && fallthrough.OpCode.Code == Code.Nop) fallthrough = fallthrough.Next;
                Require(fallthrough != null && (fallthrough.OpCode.Code == Code.Br || fallthrough.OpCode.Code == Code.Br_S),
                    "GenerateBackground address switch no longer has the expected default branch.");
                var defaultTarget = (Instruction)fallthrough.Operand;
                actualCases = switchTargets.Select((target, index) => new { target, index })
                    .Where(item => item.target.Offset != defaultTarget.Offset)
                    .Select(item => (uint)(0x2100 + item.index)).ToArray();
            }
            Require(actualCases.SequenceEqual(ExpectedGenerateBackgroundPpuCases),
                "GenerateBackground PPULineChange switch cases changed. Actual=" +
                string.Join(",", actualCases.Select(value => "$" + value.ToString("X4"))));

            var snapshot = typeof(SuperZSNESDKCBackgroundStateCachePlugin).Assembly.GetType(
                "SuperZSNESDKCBackgroundStateCache.ExactFrameSnapshot", true);
            var field = snapshot.GetField("RelevantPpuChangeAddresses", BindingFlags.Static | BindingFlags.NonPublic);
            var whitelist = (uint[])field.GetValue(null);
            var expectedWhitelist = ExpectedGenerateBackgroundPpuCases.Concat(new uint[] { 0x212C, 0x212D })
                .OrderBy(value => value).ToArray();
            Require(whitelist.SequenceEqual(expectedWhitelist),
                "Filtered stream whitelist is not exactly IL switch cases plus $212C/$212D activation.");
            Console.WriteLine("ppuCoverage generateBackgroundSwitchCases=33 callerActivationCases=2 whitelist=35 exactIL=1");
        }

        private static void VerifyFilteredPpuStreamSemantics()
        {
            var assembly = typeof(SuperZSNESDKCBackgroundStateCachePlugin).Assembly;
            var snapshotType = assembly.GetType("SuperZSNESDKCBackgroundStateCache.ExactFrameSnapshot", true);
            var copy = snapshotType.GetMethod("CopyPpuChanges", BindingFlags.Instance | BindingFlags.NonPublic);
            var equal = snapshotType.GetMethod("EqualPpuChanges", BindingFlags.Instance | BindingFlags.NonPublic);
            var countField = snapshotType.GetField("_ppuChangeCount", BindingFlags.Instance | BindingFlags.NonPublic);
            var instance = Activator.CreateInstance(snapshotType, true);
            var baseline = new[]
            {
                Change(2, 0x2104, 0x11), // OAMDATA: ignored
                Change(7, 0x210D, 0x22), // BG1HOFS: retained
                Change(7, 0x2122, 0x33), // CGDATA port: represented by CGRAM stream/state
                Change(19, 0x212C, 0x44), // TM: retained for layer activation
                Change(25, 0x2132, 0x55), // COLDATA: outer GenerateBackgrounds still handles it
                Change(31, 0x2124, 0x66)  // W34SEL: retained
            };
            copy.Invoke(instance, new object[] { baseline, baseline.Length });
            Require((int)countField.GetValue(instance) == 3, "Filtered copy did not retain exactly three relevant records.");

            var irrelevantMutation = (SNESPPU.PPULineChange[])baseline.Clone();
            irrelevantMutation[0].lineNo = 99;
            irrelevantMutation[0].val = 0xFE;
            irrelevantMutation[2].address = 0x2102;
            Require((bool)equal.Invoke(instance, new object[] { irrelevantMutation, irrelevantMutation.Length }),
                "OAM/port-only record mutation incorrectly invalidated the filtered stream.");

            var relevantValue = (SNESPPU.PPULineChange[])baseline.Clone();
            relevantValue[1].val ^= 1;
            Require(!(bool)equal.Invoke(instance, new object[] { relevantValue, relevantValue.Length }),
                "Relevant register value change was ignored.");
            var relevantLine = (SNESPPU.PPULineChange[])baseline.Clone();
            relevantLine[3].lineNo++;
            Require(!(bool)equal.Invoke(instance, new object[] { relevantLine, relevantLine.Length }),
                "Relevant register line change was ignored.");
            var relevantOrder = (SNESPPU.PPULineChange[])baseline.Clone();
            var temp = relevantOrder[1];
            relevantOrder[1] = relevantOrder[3];
            relevantOrder[3] = temp;
            Require(!(bool)equal.Invoke(instance, new object[] { relevantOrder, relevantOrder.Length }),
                "Relevant register order change was ignored.");

            var frameView = assembly.GetType("SuperZSNESDKCBackgroundStateCache.FrameView", true);
            var mode7 = frameView.GetMethod("HasMode7ScanlineChange", BindingFlags.Static | BindingFlags.NonPublic);
            var rawWithMode7 = new[] { Change(4, 0x2104, 1), Change(12, 0x2105, 7), Change(20, 0x210D, 2) };
            Require((bool)mode7.Invoke(null, new object[] { rawWithMode7, rawWithMode7.Length }),
                "Raw-stream Mode7 activation was not rejected.");
            rawWithMode7[1].val = 6;
            Require(!(bool)mode7.Invoke(null, new object[] { rawWithMode7, rawWithMode7.Length }),
                "Non-Mode7 BGMODE was incorrectly rejected.");
            Console.WriteLine("filteredPpuStream ignoredOamPort=1 retainedLineValueOrder=1 rawMode7Reject=1");
        }

        private static SNESPPU.PPULineChange Change(int line, uint address, byte value) =>
            new SNESPPU.PPULineChange { lineNo = line, address = address, val = value };

        private static bool TryGetLdcI4(Instruction instruction, out int value)
        {
            value = 0;
            if (instruction == null) return false;
            switch (instruction.OpCode.Code)
            {
                case Code.Ldc_I4: value = (int)instruction.Operand; return true;
                case Code.Ldc_I4_S: value = (sbyte)instruction.Operand; return true;
                case Code.Ldc_I4_M1: value = -1; return true;
                case Code.Ldc_I4_0: value = 0; return true;
                case Code.Ldc_I4_1: value = 1; return true;
                case Code.Ldc_I4_2: value = 2; return true;
                case Code.Ldc_I4_3: value = 3; return true;
                case Code.Ldc_I4_4: value = 4; return true;
                case Code.Ldc_I4_5: value = 5; return true;
                case Code.Ldc_I4_6: value = 6; return true;
                case Code.Ldc_I4_7: value = 7; return true;
                case Code.Ldc_I4_8: value = 8; return true;
                default: return false;
            }
        }

        private static void VerifyExactV0230Shape(string gamePath)
        {
            var game = Assembly.Load(File.ReadAllBytes(gamePath));
            var renderer = game.GetType("PPURenderer", true);
            var all = renderer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var backgrounds = all.Single(method => method.Name == "GenerateBackgrounds" && method.GetParameters().Length == 0);
            var background = all.Single(method => method.Name == "GenerateBackground" && method.GetParameters().Length == 2 &&
                                                       method.GetParameters()[0].ParameterType == typeof(int));
            var calls = PatchProcessor.GetOriginalInstructions(backgrounds);
            Require(calls.Count(instruction => instruction.operand is MethodInfo method && method == background) == 1,
                "GenerateBackgrounds must contain exactly one static call site for the whole layer loop.");
            foreach (var invalidator in new[] { "Init", "ClearCache", "ResetRenderer", "UpdateModData" })
                Require(all.Count(method => method.Name == invalidator) == 1,
                    "Invalidation method is missing or ambiguous: " + invalidator + ".");
            Require(renderer.GetField("snesPPU", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType.FullName == "SNESPPU",
                "PPURenderer.snesPPU shape changed.");
            Require(game.GetType("SNESPPU", true).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Count(method => method.Name == "SetState" && method.GetParameters().Length == 1 &&
                                         method.GetParameters()[0].ParameterType.FullName == "SNESPPU+PPUParams") == 1,
                "SNESPPU.SetState invalidation target changed.");
            Console.WriteLine("v0230Shape backgroundsCallSites=1 invalidators=5 snesPpuField=exact");
        }

        private static void VerifyPatchSurface()
        {
            var assembly = typeof(SuperZSNESDKCBackgroundStateCachePlugin).Assembly;
            var patches = assembly.GetType("SuperZSNESDKCBackgroundStateCache.CachePatches", true);
            var bgPrefix = patches.GetMethod("BackgroundsPrefix", BindingFlags.Public | BindingFlags.Static);
            var bgPostfix = patches.GetMethod("BackgroundsPostfix", BindingFlags.Public | BindingFlags.Static);
            var layerPrefix = patches.GetMethod("BackgroundPrefix", BindingFlags.Public | BindingFlags.Static);
            Require(bgPrefix != null && bgPrefix.ReturnType == typeof(void), "GenerateBackgrounds prefix missing.");
            Require(bgPostfix != null && bgPostfix.ReturnType == typeof(void), "GenerateBackgrounds postfix missing.");
            Require(layerPrefix != null && layerPrefix.ReturnType == typeof(bool), "GenerateBackground all-or-none prefix missing.");
            Require(bgPrefix.GetParameters().Length == 1 && bgPostfix.GetParameters().Length == 1 &&
                    layerPrefix.GetParameters().Length == 1, "Patch ABI is not renderer-only coordination.");
            Console.WriteLine("patchSurface oneDecisionPrefix=1 commitPostfix=1 allOrNoneLayerPrefix=1");
        }

        private static void VerifyExactFullVramComparison()
        {
            var snapshot = typeof(SuperZSNESDKCBackgroundStateCachePlugin).Assembly.GetType(
                "SuperZSNESDKCBackgroundStateCache.ExactFrameSnapshot", true);
            var equal = snapshot.GetMethod("EqualBytes", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Require(equal != null, "Exact byte comparator was not found.");
            var baseline = new byte[65536];
            var current = new byte[65536];
            Require((bool)equal.Invoke(null, new object[] { baseline, current }), "Equal full VRAM was rejected.");
            foreach (var offset in new[] { 0, 0x2000, 0x6000, 0xFFFF })
            {
                current[offset] ^= 0x5A;
                Require(!(bool)equal.Invoke(null, new object[] { baseline, current }),
                    "Full VRAM change was ignored at $" + offset.ToString("X4") + ".");
                current[offset] ^= 0x5A;
            }
            Console.WriteLine("exactVram bytes=65536 probes=4 objOnlyExclusion=none");
        }

        private static void VerifyCompiledFailClosedGuards()
        {
            using (var plugin = AssemblyDefinition.ReadAssembly(typeof(SuperZSNESDKCBackgroundStateCachePlugin).Assembly.Location))
            {
                var view = plugin.MainModule.Types.Single(type =>
                    type.FullName == "SuperZSNESDKCBackgroundStateCache.FrameView");
                var create = view.Methods.Single(method => method.Name == "TryCreate");
                var strings = create.Body.Instructions.Select(instruction => instruction.Operand as string)
                    .Where(value => value != null).ToList();
                Require(strings.Contains("DKC_Widescreen_358x224"), "DKC-only filename gate is absent.");
                Require(strings.Contains("mode7-start") && strings.Contains("mode7-scanline"),
                    "Mode7 start/scanline fail-closed gates are absent.");
                Require(strings.Contains("unsupported-enhanced-material-or-font"),
                    "Unsupported enhancement fail-closed gate is absent.");

                var pluginType = plugin.MainModule.Types.Single(type => type.FullName.EndsWith(
                    ".SuperZSNESDKCBackgroundStateCachePlugin", StringComparison.Ordinal));
                var awake = pluginType.Methods.Single(method => method.Name == "Awake");
                var constants = awake.Body.Instructions.Select(instruction => instruction.Operand as string)
                    .Where(value => value != null).ToList();
                Require(constants.Contains("Enabled") && constants.Contains("DryRun"),
                    "Enabled/dry-run configuration gates are absent.");
            }
            Console.WriteLine("compiledGuards dkcOnly=1 mode7=2 unsupportedEnhancement=1 dryRun=1");
        }

        private static Assembly Resolve(string displayName, params string[] directories)
        {
            var name = new AssemblyName(displayName).Name + ".dll";
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path));
            }
            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
