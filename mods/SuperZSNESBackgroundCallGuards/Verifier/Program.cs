using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static class Program
{
    private static readonly string Managed = Environment.GetEnvironmentVariable("SUPERZSNES_MANAGED_DIR")
        ?? throw new InvalidOperationException("Set SUPERZSNES_MANAGED_DIR before running the verifier.");
    private static readonly string BepInExCore = Path.Combine(
        Environment.GetEnvironmentVariable("BEPINEX_ROOT")
            ?? throw new InvalidOperationException("Set BEPINEX_ROOT before running the verifier."),
        "BepInEx", "core");
    private static readonly string Plugin = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "net472",
        "SuperZSNESBackgroundCallGuards.dll"));

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        return Verify();
    }

    private static int Verify()
    {
        try
        {
            Assembly.LoadFrom(Path.Combine(Managed, "Assembly-CSharp.dll"));
            var plugin = Assembly.LoadFrom(Plugin);
            var layoutType = plugin.GetType("SuperZSNESBackgroundCallGuards.BackgroundGuardLayout", true);
            var layout = layoutType.GetMethod("ResolveAndVerify", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
            var method = (MethodInfo)layoutType.GetField("GenerateBackground", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(layout);
            ILGenerator generator;
            var original = PatchProcessor.GetOriginalInstructions(method, out generator);

            var optimization = plugin.GetType("SuperZSNESBackgroundCallGuards.BackgroundCallGuardOptimization", true);
            var verify = optimization.GetMethod("VerifyExact", BindingFlags.Static | BindingFlags.NonPublic);
            var report = (string)verify.Invoke(null, new object[] { original, layout, true, true });

            var configure = optimization.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic);
            configure.Invoke(null, new object[] { layout, true, true, null });
            var transpiler = optimization.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.Public);
            var output = ((IEnumerable)transpiler.Invoke(null, new object[] { original, generator }))
                .Cast<CodeInstruction>().ToList();

            var clearCount = (int)optimization.GetField("ClearLoopTransformCount", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
            var processCount = (int)optimization.GetField("ProcessCallTransformCount", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
            Require(clearCount == 1, "Expected one clear-loop guard, got " + clearCount + ".");
            Require(processCount == 5, "Expected five Process2DTiles guards, got " + processCount + ".");
            Require(output.Count == original.Count + 19,
                "Expected 19 inserted instructions, got " + (output.Count - original.Count) + ".");

            var processMethod = (MethodInfo)layoutType.GetField("Process2DTiles", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(layout);
            Require(original.Count(instruction => instruction.Calls(processMethod)) == 5,
                "Original Process2DTiles call count was not five.");
            Require(output.Count(instruction => instruction.Calls(processMethod)) == 5,
                "Transformed Process2DTiles call count changed.");
            Require(output.Count(instruction => instruction.opcode == OpCodes.Blt) ==
                    original.Count(instruction => instruction.opcode == OpCodes.Blt) + 5,
                "Five signed threshold branches were not added.");

            VerifyInjectedControlFlow(output, layoutType, layout);

            Console.WriteLine("BackgroundCallGuards exact v0.230 IL verification: PASS");
            Console.WriteLine(report);
            Console.WriteLine("instructions=" + original.Count + " -> " + output.Count +
                              ", clearGuards=" + clearCount + ", processGuards=" + processCount);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + (exception.InnerException ?? exception));
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void VerifyInjectedControlFlow(IReadOnlyList<CodeInstruction> code, Type layoutType, object layout)
    {
        var tileMap = (FieldInfo)layoutType.GetField("TileAddrToMat", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(layout);
        var count = (MethodInfo)layoutType.GetField("TileAddrToMatCount", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(layout);
        var usedMaterials = (FieldInfo)layoutType.GetField("UsedMaterials", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(layout);
        var clear = (MethodInfo)layoutType.GetField("UsedMaterialsClear", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(layout);

        var clearGuards = new List<int>();
        var processGuards = new List<int>();
        for (var index = 3; index < code.Count; index++)
        {
            if (code[index].opcode == OpCodes.Brfalse && code[index - 1].Calls(count) &&
                Equals(code[index - 2].operand, tileMap) && code[index - 3].opcode == OpCodes.Ldarg_0)
                clearGuards.Add(index);
            if (code[index].opcode == OpCodes.Blt && ReadI4(code[index - 1]) != null && IsLocalLoad(code[index - 2]) &&
                index + 4 < code.Count && code[index + 1].opcode == OpCodes.Ldarg_0 &&
                IsLocalLoad(code[index + 2]) && IsLocalLoad(code[index + 3]) && ReadI4(code[index + 4]) != null)
                processGuards.Add(index);
        }
        Require(clearGuards.Count == 1, "Could not identify exactly one injected empty-map branch.");
        Require(processGuards.Count == 5, "Could not identify exactly five injected remainder branches.");

        var clearTarget = ResolveTarget(code, clearGuards[0]);
        Require(clearTarget + 2 < code.Count && code[clearTarget].opcode == OpCodes.Ldarg_0 &&
                Equals(code[clearTarget + 1].operand, usedMaterials) && code[clearTarget + 2].Calls(clear),
            "Empty-map branch does not land on usedMaterials.Clear.");
        Console.WriteLine("clear branch " + clearGuards[0] + " -> " + clearTarget + " (usedMaterials.Clear)");

        var expected = new[] { 256, 64, 16, 4, 1 };
        for (var index = 0; index < processGuards.Count; index++)
        {
            var branch = processGuards[index];
            var threshold = ReadI4(code[branch - 1]);
            var target = ResolveTarget(code, branch);
            Require(threshold == expected[index], "Unexpected injected threshold order.");
            if (index + 1 < processGuards.Count)
                Require(target == processGuards[index + 1] - 2,
                    "Threshold " + threshold + " does not land on the next threshold guard.");
            else
                Require(code[target].opcode == OpCodes.Ldloca || code[target].opcode == OpCodes.Ldloca_S,
                    "Final threshold branch does not land on the usedMaterials enumerator MoveNext sequence.");
            Console.WriteLine("threshold " + threshold + " branch " + branch + " -> " + target +
                              " (" + code[target].opcode + "), guard=" + code[branch - 2] + " | " +
                              code[branch - 1] + " | " + code[branch]);
        }
    }

    private static int ResolveTarget(IReadOnlyList<CodeInstruction> code, int branchIndex)
    {
        var label = (Label)code[branchIndex].operand;
        var matches = new List<int>();
        for (var index = 0; index < code.Count; index++)
            if (code[index].labels.Contains(label)) matches.Add(index);
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            Console.WriteLine("ambiguous verifier-only label " + label.GetHashCode() + " matches " + string.Join(",", matches));
            // GetOriginalInstructions owns a different DynamicMethod generator in this offline verifier,
            // so integer Label ids can collide. Newly attached labels are on the last matching instruction.
            return matches[matches.Count - 1];
        }
        throw new InvalidOperationException("Injected branch target label was not attached to an instruction.");
    }

    private static bool IsLocalLoad(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S ||
               instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Ldloc_1 ||
               instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;
    }

    private static int? ReadI4(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Ldc_I4_0) return 0;
        if (instruction.opcode == OpCodes.Ldc_I4_1) return 1;
        if (instruction.opcode == OpCodes.Ldc_I4_2) return 2;
        if (instruction.opcode == OpCodes.Ldc_I4_3) return 3;
        if (instruction.opcode == OpCodes.Ldc_I4_4) return 4;
        if (instruction.opcode == OpCodes.Ldc_I4_5) return 5;
        if (instruction.opcode == OpCodes.Ldc_I4_6) return 6;
        if (instruction.opcode == OpCodes.Ldc_I4_7) return 7;
        if (instruction.opcode == OpCodes.Ldc_I4_8) return 8;
        if (instruction.opcode == OpCodes.Ldc_I4_S) return (sbyte)instruction.operand;
        if (instruction.opcode == OpCodes.Ldc_I4) return (int)instruction.operand;
        return null;
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var directory in new[] { Managed, BepInExCore, Path.GetDirectoryName(Plugin) })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
