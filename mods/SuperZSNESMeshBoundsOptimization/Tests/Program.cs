using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;

namespace SuperZSNESMeshBoundsOptimization.Tests
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
                VerifyCompiledHelper();
                VerifyBroadBoundsMath();
                VerifyTransformedGameIl(gamePath);
                Console.WriteLine("PASS mesh-bounds optimization offline verification");
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

        private static void VerifyCompiledHelper()
        {
            using (var plugin = AssemblyDefinition.ReadAssembly(typeof(SuperZSNESMeshBoundsOptimizationPlugin).Assembly.Location))
            {
                var patch = plugin.MainModule.Types.Single(type =>
                    type.FullName == "SuperZSNESMeshBoundsOptimization.Process2DTilesPatch");
                var helper = patch.Methods.Single(method => method.Name == "UploadVerticesWithFixedBounds");
                var calls = helper.Body.Instructions.Select(instruction => instruction.Operand as MethodReference)
                    .Where(method => method != null).ToList();
                Require(calls.Any(method => method.Name == "SetVertices" && method.Parameters.Count == 4 &&
                                            method.Parameters[3].ParameterType.FullName ==
                                            "UnityEngine.Rendering.MeshUpdateFlags"),
                    "Compiled helper does not call the flagged four-argument SetVertices overload.");
                Require(calls.Any(method => method.Name == "set_bounds"),
                    "Compiled helper does not assign Mesh.bounds.");
                Require(!calls.Any(method => method.Name == "RecalculateBounds"),
                    "Compiled helper unexpectedly recalculates bounds.");

                var uvHelper = patch.Methods.Single(method => method.Name == "UploadUvsAndNotify");
                var uvCalls = uvHelper.Body.Instructions.Select(instruction => instruction.Operand as MethodReference)
                    .Where(method => method != null).ToList();
                Require(uvCalls.Any(method => method.Name == "SetUVs" && method.Parameters.Count == 5 &&
                                              method.Parameters[4].ParameterType.FullName ==
                                              "UnityEngine.Rendering.MeshUpdateFlags"),
                    "Compiled UV helper does not call the flagged five-argument SetUVs overload.");
                Require(uvCalls.Any(method => method.Name == "set_bounds"),
                    "Compiled UV helper does not assign Mesh.bounds.");
                Require(uvCalls.Any(method => method.Name == "MarkModified"),
                    "Compiled UV helper does not notify mesh users after the batched uploads.");
            }
            Console.WriteLine("compiledHelpers flaggedVertices=1 flaggedUvs=1 boundsSetter=2 markModified=1 recalculateBounds=0");
        }

        private static void VerifyBroadBoundsMath()
        {
            var patch = typeof(SuperZSNESMeshBoundsOptimizationPlugin).Assembly.GetType(
                "SuperZSNESMeshBoundsOptimization.Process2DTilesPatch", true);
            var contains = patch.GetMethod("ContainsCore", BindingFlags.Public | BindingFlags.Static);
            Require(contains != null, "ContainsCore was not found.");
            Func<float, float, float, float, bool> inside = (x, y, z, half) =>
                (bool)contains.Invoke(null, new object[] { x, y, z, half });

            const float halfExtent = 2048f;
            // Seven-tile widescreen, native/PAL vertical limits, stock z priorities,
            // and deliberately exaggerated custom width/depth probes.
            var probes = new[]
            {
                new[] { -24f, -15f, 0f }, new[] { 24f, 15f, 13f },
                new[] { -24f, -27f, -13f }, new[] { 24f, 27f, 13f },
                new[] { -1024f, -128f, -128f }, new[] { 1024f, 128f, 128f }
            };
            foreach (var point in probes)
                Require(inside(point[0], point[1], point[2], halfExtent), "Conservative bounds rejected a probe.");
            Require(!inside(2049f, 0f, 0f, halfExtent), "Bounds math failed its outside control.");
            Console.WriteLine("boundsMath halfExtent=2048 probes=" + probes.Length + " outsideControl=pass");
        }

        private static void VerifyTransformedGameIl(string gamePath)
        {
            var game = Assembly.Load(File.ReadAllBytes(gamePath));
            var renderer = game.GetType("PPURenderer", true);
            var target = renderer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.Name == "Process2DTiles" && method.GetParameters().Length == 12);
            ILGenerator generator;
            var original = PatchProcessor.GetOriginalInstructions(target, out generator);
            var patch = typeof(SuperZSNESMeshBoundsOptimizationPlugin).Assembly.GetType(
                "SuperZSNESMeshBoundsOptimization.Process2DTilesPatch", true);
            var configure = patch.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic);
            patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0);
            var transpiler = patch.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);
            configure.Invoke(null, new object[] { 2048f, false });
            var transformed = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();
            var helper = patch.GetMethod("UploadVerticesWithFixedBounds", BindingFlags.Public | BindingFlags.Static);
            var uvHelper = patch.GetMethod("UploadUvsAndNotify", BindingFlags.Public | BindingFlags.Static);

            Require((int)patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == 1,
                "Transpiler did not report exactly one transform.");
            Require(CountCalls(original, "SetVertices") == 1 && CountCalls(original, "SetUVs") == 1 &&
                    CountCalls(original, "RecalculateBounds") == 1,
                "Installed Process2DTiles does not have the expected stock mesh calls.");
            Require(transformed.Count == original.Count, "Transpiler unexpectedly changed instruction count.");
            Require(transformed.Count(instruction => instruction.opcode == OpCodes.Call &&
                                                       Equals(instruction.operand, helper)) == 1,
                "Transformed method does not contain exactly one upload helper call.");
            Require(CountCalls(transformed, "SetUVs") == 1, "UV upload changed.");
            Require(CountCalls(transformed, "RecalculateBounds") == 0, "Bounds recalculation remains.");
            Require(transformed.Count(instruction => instruction.opcode == OpCodes.Pop) ==
                    original.Count(instruction => instruction.opcode == OpCodes.Pop) + 1,
                "Expected exactly one receiver pop in place of RecalculateBounds.");

            patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0);
            configure.Invoke(null, new object[] { 2048f, true });
            var batched = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();
            Require((int)patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == 1,
                "Batched transpiler did not report exactly one transform.");
            Require(batched.Count == original.Count, "Batched transpiler unexpectedly changed instruction count.");
            Require(batched.Count(instruction => instruction.opcode == OpCodes.Call &&
                                                   Equals(instruction.operand, helper)) == 1,
                "Batched method does not contain exactly one vertex upload helper call.");
            Require(batched.Count(instruction => instruction.opcode == OpCodes.Call &&
                                                   Equals(instruction.operand, uvHelper)) == 1,
                "Batched method does not contain exactly one UV upload helper call.");
            Require(CountCalls(batched, "SetUVs") == 0, "Stock UV upload remains in the batched path.");
            Require(CountCalls(batched, "RecalculateBounds") == 0, "Bounds recalculation remains in the batched path.");
            Console.WriteLine("transformedIL instructions=" + transformed.Count +
                              " acceptedHelper=1 acceptedSetUVs=1 batchedVertexHelper=1 batchedUvHelper=1 recalculateBounds=0 receiverPop=1");
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
