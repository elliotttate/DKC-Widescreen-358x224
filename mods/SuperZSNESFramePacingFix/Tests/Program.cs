using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;

namespace SuperZSNESFramePacingFix.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var gameAssemblyPath = args.Length == 0
                    ? Path.Combine(RequiredEnvironment("SUPERZSNES_MANAGED_DIR"), "Assembly-CSharp.dll")
                    : Path.GetFullPath(args[0]);
                var managedDirectory = Path.GetDirectoryName(gameAssemblyPath);
                var bepinexCore = Path.Combine(RequiredEnvironment("BEPINEX_ROOT"), "BepInEx", "core");
                AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) => Resolve(eventArgs.Name, managedDirectory, bepinexCore);

                VerifyCompiledHelperIl();
                VerifyArithmetic();
                VerifyCadenceModePolicy();
                VerifyTransformedGameIl(gameAssemblyPath);
                Console.WriteLine("PASS all frame-pacing verifier checks");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static string RequiredEnvironment(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Set " + name + " before running the verifier.");

        private static void VerifyCompiledHelperIl()
        {
            var pluginPath = typeof(SuperZSNESFramePacingFixPlugin).Assembly.Location;
            using (var assembly = AssemblyDefinition.ReadAssembly(pluginPath))
            {
                var patch = assembly.MainModule.Types.Single(type => type.FullName == "SuperZSNESFramePacingFix.FramePacingPatch");
                var core = patch.Methods.Single(method => method.Name == "ConsumeElapsedCore");
                var instructions = core.Body.Instructions;
                Require(instructions.Count > 20, "Compiled ConsumeElapsedCore IL is unexpectedly short.");
                Require(instructions.Count(instruction => instruction.OpCode.Code == Mono.Cecil.Cil.Code.Sub) >= 2,
                    "Compiled helper does not contain both stock and normal subtraction paths.");
                Require(instructions.Any(instruction => instruction.Operand is MethodReference method &&
                                                        method.FullName.Contains("System.Math::Min")),
                    "Compiled helper is missing scheduled-frame Math.Min.");
                Require(instructions.Any(instruction => instruction.Operand is MethodReference method &&
                                                        method.FullName.Contains("System.Math::Max")),
                    "Compiled helper is missing nonnegative due-frame Math.Max.");
                Console.WriteLine("compiledHelperIL instructions=" + instructions.Count + " subtractionPaths>=2 min/max=present");
            }
        }

        private static void VerifyArithmetic()
        {
            var patch = typeof(SuperZSNESFramePacingFixPlugin).Assembly.GetType("SuperZSNESFramePacingFix.FramePacingPatch", true);
            var core = patch.GetMethod("ConsumeElapsedCore", BindingFlags.Public | BindingFlags.Static);
            Require(core != null, "Compiled ConsumeElapsedCore method was not found.");

            var accumulators = new[] { 0f, 0.001f, 0.5f, 1.045f, 3.5f };
            var dueValues = new[] { 0, 1, 5, 60 };
            var rates = new[] { 50f, 60f };
            var fastForwardCases = 0;
            foreach (var accumulated in accumulators)
            foreach (var due in dueValues)
            foreach (var rate in rates)
            for (var cap = 1; cap <= 4; cap++)
            {
                var expected = accumulated - (float)due * (1f / rate);
                var actual = CallCore(core, accumulated, due, cap, rate, 120);
                Require(Bits(expected) == Bits(actual), "Fast-forward result was not bit-identical to stock.");
                fastForwardCases++;
            }

            const float hz = 60f;
            var period = 1f / hz;
            var accumulator = 1f;
            var dueFrames = 60;
            var executed = 0;
            var updates = 0;
            while (dueFrames > 0 && updates < 100)
            {
                var scheduled = Math.Min(dueFrames, 5);
                accumulator = CallCore(core, accumulator, dueFrames, 5, hz, 120);
                Require(accumulator >= 0f, "Normal accumulator became negative.");
                executed += scheduled;
                updates++;
                dueFrames = (int)(accumulator / period);
            }
            Require(executed == 60 && updates == 12, "60-frame backlog did not drain as twelve five-frame batches.");
            Require(accumulator >= 0f && accumulator < period, "Synthetic drain did not finish at a valid fractional remainder.");

            var observedStall = 1.045f;
            var observedDue = (int)(observedStall / period);
            var observedExpected = observedStall - 5f * period;
            var observedActual = CallCore(core, observedStall, observedDue, 5, hz, 120);
            Require(Bits(observedExpected) == Bits(observedActual), "Default emergency ceiling altered the observed 1.045-second stall.");

            var unbounded = CallCore(core, 5f, 300, 5, hz, 0);
            Require(Bits(unbounded) == Bits(5f - 5f * period), "Zero did not produce unbounded backlog retention.");
            var emergencyCapped = CallCore(core, 5f, 300, 5, hz, 120);
            Require(Bits(emergencyCapped) == Bits(120f * period), "120-frame emergency ceiling was not applied after charging the batch.");
            Require(CallCore(core, 0.01f, 5, 5, hz, 120) == 0f, "Underrun clamp returned a negative accumulator.");

            Console.WriteLine("arithmetic fastForwardBitExactCases=" + fastForwardCases +
                              " due60Updates=" + updates + " executed=" + executed +
                              " finalAccumulator=" + accumulator.ToString("R") +
                              " observedDue=" + observedDue + " emergencyCapFrames=120");
        }

        private static void VerifyTransformedGameIl(string gameAssemblyPath)
        {
            // Byte-array load avoids changing the downloaded game's Zone.Identifier and
            // keeps this verifier strictly read-only with respect to the installed DLL.
            var game = Assembly.Load(File.ReadAllBytes(gameAssemblyPath));
            var master = game.GetType("MasterExecutor", true);
            var update = master.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Require(update != null, "MasterExecutor.Update was not found.");

            ILGenerator generator;
            var original = PatchProcessor.GetOriginalInstructions(update, out generator);
            var patch = typeof(SuperZSNESFramePacingFixPlugin).Assembly.GetType("SuperZSNESFramePacingFix.FramePacingPatch", true);
            patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0);
            var transpiler = patch.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);
            var transformed = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();
            var consume = patch.GetMethod("ConsumeElapsed", BindingFlags.Public | BindingFlags.Static);
            var callIndices = transformed.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.opcode == OpCodes.Call && Equals(item.instruction.operand, consume))
                .Select(item => item.index).ToList();

            Require((int)patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == 1,
                "Transpiler did not report exactly one replacement.");
            Require(callIndices.Count == 1, "Transformed Update does not contain exactly one ConsumeElapsed call.");
            var call = callIndices[0];
            Require(call >= 6 && call + 1 < transformed.Count, "ConsumeElapsed call is at an invalid instruction boundary.");
            Require(transformed[call - 5].opcode == OpCodes.Ldarg_0 && transformed[call - 4].opcode == OpCodes.Ldfld,
                "Transformed accumulator receiver/load shape is invalid.");
            Require(transformed[call + 1].opcode == OpCodes.Stfld &&
                    ((FieldInfo)transformed[call + 1].operand).Name == "_accumulatedDT",
                "ConsumeElapsed result is not stored to _accumulatedDT.");
            Require(transformed.Count == original.Count - 3, "Unexpected transformed instruction-count delta.");
            Require(CountCallsNamed(original, "GenerateBackgrounds") == CountCallsNamed(transformed, "GenerateBackgrounds"),
                "GenerateBackgrounds call count changed.");
            Require(CountCallsNamed(original, "Min") == CountCallsNamed(transformed, "Min"),
                "Existing scheduled-frame Mathf.Min call count changed.");

            Console.WriteLine("transformedMasterUpdateIL originalInstructions=" + original.Count +
                              " transformedInstructions=" + transformed.Count +
                              " consumeCalls=1 generateBackgroundsCalls=" + CountCallsNamed(transformed, "GenerateBackgrounds") +
                              " callWindow=" + FormatWindow(transformed, call - 6, call + 1));
        }

        private static void VerifyCadenceModePolicy()
        {
            var controller = typeof(SuperZSNESFramePacingFixPlugin).Assembly.GetType(
                "SuperZSNESFramePacingFix.CadenceController", true);
            var policy = controller.GetMethod("WantsHighCadenceCore", BindingFlags.Public | BindingFlags.Static);
            Require(policy != null, "Compiled cadence mode policy was not found.");

            Func<int, bool, bool, bool> wants = (cap, enabled, restoreFastForward) =>
                (bool)policy.Invoke(null, new object[] { cap, enabled, restoreFastForward });
            Require(wants(5, true, true), "Normal cap=5 did not select high cadence.");
            for (var cap = 1; cap <= 4; cap++)
                Require(!wants(cap, true, true), "Fast-forward cap unexpectedly selected high cadence.");
            Require(wants(4, true, false), "Configured fast-forward cadence retention was ignored.");
            Require(!wants(5, false, true), "Disabled cadence policy selected high cadence.");

            using (var assembly = AssemblyDefinition.ReadAssembly(typeof(SuperZSNESFramePacingFixPlugin).Assembly.Location))
            {
                var type = assembly.MainModule.Types.Single(item =>
                    item.FullName == "SuperZSNESFramePacingFix.CadenceController");
                var apply = type.Methods.Single(method => method.Name == "ApplyHighCadence");
                var restore = type.Methods.Single(method => method.Name == "RestoreOriginal");
                Require(apply.Body.Instructions.Any(instruction =>
                        instruction.Operand is MethodReference method && method.Name == "set_vSyncCount"),
                    "High-cadence path does not set QualitySettings.vSyncCount.");
                Require(apply.Body.Instructions.Any(instruction =>
                        instruction.Operand is MethodReference method && method.Name == "set_targetFrameRate"),
                    "High-cadence path does not set Application.targetFrameRate.");
                Require(restore.Body.Instructions.Any(instruction =>
                        instruction.Operand is MethodReference method && method.Name == "set_vSyncCount"),
                    "Restore path does not restore QualitySettings.vSyncCount.");
                Require(restore.Body.Instructions.Any(instruction =>
                        instruction.Operand is MethodReference method && method.Name == "set_targetFrameRate"),
                    "Restore path does not restore Application.targetFrameRate.");
            }
            Console.WriteLine("cadencePolicy normal=high fastForward=original disabled=original UnitySetters=verified");
        }

        private static float CallCore(MethodInfo core, float accumulated, int due, int cap, float hz, int max)
        {
            return (float)core.Invoke(null, new object[] { accumulated, due, cap, hz, max });
        }

        private static int CountCallsNamed(IEnumerable<CodeInstruction> instructions, string name)
        {
            return instructions.Count(instruction => instruction.operand is MethodInfo method && method.Name == name);
        }

        private static string FormatWindow(IList<CodeInstruction> instructions, int first, int last)
        {
            return string.Join(" | ", Enumerable.Range(first, last - first + 1).Select(index =>
                index + ":" + instructions[index].opcode.Name +
                (instructions[index].operand == null ? string.Empty : " " + OperandName(instructions[index].operand))));
        }

        private static string OperandName(object operand)
        {
            if (operand is MemberInfo member) return member.DeclaringType.Name + "." + member.Name;
            return operand.ToString();
        }

        private static int Bits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static Assembly Resolve(string displayName, params string[] directories)
        {
            var filename = new AssemblyName(displayName).Name + ".dll";
            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, filename);
                if (File.Exists(candidate)) return Assembly.Load(File.ReadAllBytes(candidate));
            }
            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
