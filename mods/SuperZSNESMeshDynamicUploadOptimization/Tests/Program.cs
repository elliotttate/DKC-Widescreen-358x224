using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;

namespace SuperZSNESMeshDynamicUploadOptimization.Tests
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
                VerifyCompiledHelperOrder();
                VerifyTransformedGameIl(gamePath);
                Console.WriteLine("PASS mesh dynamic-upload optimization offline verification");
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

        private static void VerifyCompiledHelperOrder()
        {
            using (var plugin = AssemblyDefinition.ReadAssembly(
                       typeof(SuperZSNESMeshDynamicUploadOptimizationPlugin).Assembly.Location))
            {
                var patch = plugin.MainModule.Types.Single(type =>
                    type.FullName == "SuperZSNESMeshDynamicUploadOptimization.GenerateNewMeshPatch");
                var helper = patch.Methods.Single(method => method.Name == "SetInitialVerticesAfterMarkDynamic");
                var calls = helper.Body.Instructions
                    .Select((instruction, index) => new
                    {
                        Method = instruction.Operand as MethodReference,
                        Index = index
                    })
                    .Where(item => item.Method != null).ToList();
                var mark = calls.Single(item => item.Method.Name == "MarkDynamic");
                var vertices = calls.Single(item => item.Method.Name == "set_vertices");
                Require(mark.Index < vertices.Index, "Compiled helper does not call MarkDynamic before set_vertices.");
                Require(calls.Count(item => item.Method.Name == "MarkDynamic") == 1,
                    "Compiled helper has an unexpected MarkDynamic count.");
                Require(calls.Count(item => item.Method.Name == "set_vertices") == 1,
                    "Compiled helper has an unexpected set_vertices count.");
            }
            Console.WriteLine("compiledHelper MarkDynamic=1 then set_vertices=1");
        }

        private static void VerifyTransformedGameIl(string gamePath)
        {
            var game = Assembly.Load(File.ReadAllBytes(gamePath));
            var renderer = game.GetType("PPURenderer", true);
            var target = renderer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.Name == "GenerateNewMesh" &&
                                  method.GetParameters().Length == 1 &&
                                  method.GetParameters()[0].ParameterType == typeof(int));
            ILGenerator generator;
            var original = PatchProcessor.GetOriginalInstructions(target, out generator);
            var patch = typeof(SuperZSNESMeshDynamicUploadOptimizationPlugin).Assembly.GetType(
                "SuperZSNESMeshDynamicUploadOptimization.GenerateNewMeshPatch", true);
            patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0);
            var transpiler = patch.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);
            var transformed = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();
            var helper = patch.GetMethod("SetInitialVerticesAfterMarkDynamic", BindingFlags.Public | BindingFlags.Static);

            Require((int)patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == 1,
                "Transpiler did not report exactly one transform.");
            Require(CountCalls(original, "set_vertices") == 1 && CountCalls(original, "MarkDynamic") == 1,
                "Installed GenerateNewMesh does not have the expected stock initialization calls.");
            Require(FirstCallIndex(original, "set_vertices") < FirstCallIndex(original, "MarkDynamic"),
                "Stock MarkDynamic is no longer late; this patch should be re-audited.");
            Require(transformed.Count == original.Count, "Transpiler unexpectedly changed instruction count.");
            Require(transformed.Count(instruction => instruction.opcode == OpCodes.Call &&
                                                       Equals(instruction.operand, helper)) == 1,
                "Transformed method does not contain exactly one early-init helper call.");
            Require(CountCalls(transformed, "set_vertices") == 0,
                "The original direct set_vertices call remains in GenerateNewMesh.");
            Require(CountCalls(transformed, "MarkDynamic") == 0,
                "The original late MarkDynamic call remains in GenerateNewMesh.");
            Require(transformed.Count(instruction => instruction.opcode == OpCodes.Pop) ==
                    original.Count(instruction => instruction.opcode == OpCodes.Pop) + 1,
                "Expected exactly one receiver pop in place of the late MarkDynamic.");

            foreach (var preserved in new[] { "set_triangles", "set_uv", "set_normals", "RecalculateTangents" })
                Require(CountCalls(transformed, preserved) == CountCalls(original, preserved),
                    "Unrelated mesh initialization call count changed: " + preserved + ".");

            Console.WriteLine("transformedIL instructions=" + transformed.Count +
                              " earlyHelper=1 lateMarkDynamic=0 preservedUploads=4 receiverPop=1");
        }

        private static int FirstCallIndex(IList<CodeInstruction> code, string name)
        {
            for (var i = 0; i < code.Count; i++)
                if (code[i].operand is MethodInfo method && method.Name == name) return i;
            return -1;
        }

        private static int CountCalls(IEnumerable<CodeInstruction> code, string name)
        {
            return code.Count(instruction => instruction.operand is MethodInfo method && method.Name == name);
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
