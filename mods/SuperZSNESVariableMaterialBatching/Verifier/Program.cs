using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

internal static class Program
{
    private static readonly string Managed = Environment.GetEnvironmentVariable("SUPERZSNES_MANAGED_DIR")
        ?? throw new InvalidOperationException("Set SUPERZSNES_MANAGED_DIR before running the verifier.");
    private static readonly string Core = Path.Combine(
        Environment.GetEnvironmentVariable("BEPINEX_ROOT")
            ?? throw new InvalidOperationException("Set BEPINEX_ROOT before running the verifier."),
        "BepInEx", "core");
    private static readonly string Plugin = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "net472",
        "SuperZSNESVariableMaterialBatching.dll"));

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
            var layoutType = plugin.GetType("SuperZSNESVariableMaterialBatching.VariableBatchLayout", true);
            var layout = layoutType.GetMethod("ResolveAndVerify", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            var transpilers = plugin.GetType("SuperZSNESVariableMaterialBatching.VariableBatchTranspilers", true);
            transpilers.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { layout, null });

            var generate = (MethodInfo)layoutType.GetField("GenerateBackground", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(layout);
            var process2D = (MethodInfo)layoutType.GetField("Process2DTiles", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(layout);
            var process = (MethodInfo)layoutType.GetField("ProcessTiles", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(layout);
            var selectMethod = transpilers.GetMethod("GenerateBackgroundTranspiler", BindingFlags.Static | BindingFlags.Public);
            var shapeMethod = transpilers.GetMethod("MeshProcessTranspiler", BindingFlags.Static | BindingFlags.Public);

            var originalGenerate = PatchProcessor.GetOriginalInstructions(generate);
            var transformedGenerate = Invoke(selectMethod, originalGenerate);
            Require(transformedGenerate.Count == originalGenerate.Count + 3, "GenerateBackground must gain exactly three instructions.");
            Require(transformedGenerate.Count(instruction => NamedCall(instruction, "SelectFirstBatchSize")) == 1,
                "GenerateBackground selector call count was not one.");
            Require(transformedGenerate.Count(instruction => instruction.Calls(process2D)) == 5,
                "GenerateBackground Process2DTiles call count changed.");

            foreach (var target in new[] { process2D, process })
            {
                var original = PatchProcessor.GetOriginalInstructions(target);
                var transformed = Invoke(shapeMethod, original, target);
                Require(transformed.Count == original.Count + 7, target.Name + " must gain exactly seven instructions.");
                Require(transformed.Count(instruction => NamedCall(instruction, "EnsureMeshShape")) == 1,
                    target.Name + " shape helper call count was not one.");
                var helperIndex = transformed.FindIndex(instruction => NamedCall(instruction, "EnsureMeshShape"));
                Console.WriteLine(target.Name + " helper loads: " + string.Join(" | ",
                    transformed.Skip(helperIndex - 6).Take(7).Select(instruction => instruction.ToString())));
                Require(transformed.Count(instruction => NamedCall(instruction, "GenerateNewMesh")) == 1,
                    target.Name + " GenerateNewMesh call count changed.");
            }

            var selectorCount = (int)transpilers.GetField("GenerateBackgroundTransformCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var shapeCount = (int)transpilers.GetField("MeshProcessTransformCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Require(selectorCount == 1 && shapeCount == 2, "Transform counters were selector=" + selectorCount + ", shape=" + shapeCount + ".");
            VerifyHarmonyEmission(generate, process2D, process, selectMethod, shapeMethod);
            VerifyHarmonyNumericArgumentSemantics();
            Console.WriteLine("VariableMaterialBatching exact v0.230 IL verification: PASS");
            Console.WriteLine("selector=1, mesh-shape guards=2, Process2DTiles calls preserved=5");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + (exception.InnerException ?? exception));
            return 1;
        }
    }

    private static List<CodeInstruction> Invoke(MethodInfo method, List<CodeInstruction> input, MethodBase original = null)
    {
        var args = original == null ? new object[] { input } : new object[] { input, original };
        return ((IEnumerable)method.Invoke(null, args)).Cast<CodeInstruction>().ToList();
    }

    private static void VerifyHarmonyEmission(MethodInfo generate, MethodInfo process2D, MethodInfo process,
        MethodInfo selectorTranspiler, MethodInfo shapeTranspiler)
    {
        var harmony = new Harmony("dev.local.superzsnes.variablematerialbatching.offline-verifier");
        try
        {
            harmony.Patch(generate, transpiler: new HarmonyMethod(selectorTranspiler));
            harmony.Patch(process2D, transpiler: new HarmonyMethod(shapeTranspiler));
            harmony.Patch(process, transpiler: new HarmonyMethod(shapeTranspiler));
        }
        finally
        {
            harmony.UnpatchSelf();
        }
    }

    private static object _capturedArgument;

    public static IEnumerable<CodeInstruction> NumericArgumentProbeTranspiler(IEnumerable<CodeInstruction> input)
    {
        var code = input.Select(instruction => new CodeInstruction(instruction)).ToList();
        code.Insert(0, new CodeInstruction(System.Reflection.Emit.OpCodes.Call,
            typeof(Program).GetMethod(nameof(CaptureArgument), BindingFlags.Static | BindingFlags.Public)));
        code.Insert(0, new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_S, (byte)9));
        return code;
    }

    public static void CaptureArgument(object value)
    {
        _capturedArgument = value;
    }

    private static void VerifyHarmonyNumericArgumentSemantics()
    {
        var target = typeof(ArgumentProbe).GetMethod(nameof(ArgumentProbe.Target));
        var transpiler = typeof(Program).GetMethod(nameof(NumericArgumentProbeTranspiler), BindingFlags.Static | BindingFlags.Public);
        var harmony = new Harmony("dev.local.superzsnes.variablematerialbatching.argument-probe");
        var values = Enumerable.Range(0, 11).Select(index => (object)("p" + index)).ToArray();
        try
        {
            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
            target.Invoke(new ArgumentProbe(), values);
            Require(Equals(_capturedArgument, values[8]),
                "Harmony numeric ldarg.s 9 selected " + (_capturedArgument ?? "null") + ", expected explicit parameter 8.");
        }
        finally
        {
            harmony.UnpatchSelf();
        }
    }

    private sealed class ArgumentProbe
    {
        public void Target(object p0, object p1, object p2, object p3, object p4, object p5,
            object p6, object p7, object p8, object p9, object p10)
        {
        }
    }

    private static bool NamedCall(CodeInstruction instruction, string name)
    {
        return instruction.operand is MethodInfo method && method.Name == name;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var directory in new[] { Managed, Core, Path.GetDirectoryName(Plugin) })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
