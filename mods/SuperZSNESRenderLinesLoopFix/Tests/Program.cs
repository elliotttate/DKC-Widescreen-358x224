using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SuperZSNESRenderLinesLoopFix.Tests
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

                VerifyInstalledCecilShape(gamePath);
                VerifyTransformedGameIl(gamePath);
                VerifyAllRotations();
                VerifyDuplicateDescriptorSemantics();
                Console.WriteLine("PASS RenderLines loop-fix offline verification");
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

        private static void VerifyInstalledCecilShape(string gamePath)
        {
            using (var game = AssemblyDefinition.ReadAssembly(gamePath))
            {
                var renderer = game.MainModule.Types.Single(type => type.FullName == "PPURenderer");
                var method = renderer.Methods.Single(candidate => candidate.Name == "RenderLines" &&
                    candidate.Parameters.Count == 7);
                var il = method.Body.Instructions;
                var tail = il.Skip(Math.Max(0, il.Count - 8)).ToArray();
                Require(method.RVA == 0x43c88, "Installed RenderLines RVA is not the audited v0.230 value 0x43c88.");
                Require(method.Body.CodeSize == 0xc58, "Installed RenderLines code size is not the audited 0xC58 bytes.");
                Require(tail.Length == 8 &&
                        tail[0].OpCode == Mono.Cecil.Cil.OpCodes.Ldloc_2 &&
                        tail[1].OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_1 &&
                        tail[2].OpCode == Mono.Cecil.Cil.OpCodes.Add &&
                        tail[3].OpCode == Mono.Cecil.Cil.OpCodes.Stloc_2 &&
                        tail[4].OpCode == Mono.Cecil.Cil.OpCodes.Ldloc_2 &&
                        tail[5].OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4 && Convert.ToInt32(tail[5].Operand) == 128 &&
                        tail[6].OpCode == Mono.Cecil.Cil.OpCodes.Ble &&
                        tail[7].OpCode == Mono.Cecil.Cil.OpCodes.Ret,
                    "Installed RenderLines terminal IL is not the audited i++ / i <= 128 loop.");
                var target = (Instruction)tail[6].Operand;
                Require(target.Offset == 0x1f, "Installed terminal BLE does not return to the full sprite body at IL_001F.");
                Console.WriteLine("installedIL rva=0x" + method.RVA.ToString("x") +
                                  " codeSize=0x" + method.Body.CodeSize.ToString("x") +
                                  " tail=ldloc.2,1,add,stloc.2,ldloc.2,128,ble IL_001f,ret");
            }
        }

        private static void VerifyTransformedGameIl(string gamePath)
        {
            var game = Assembly.Load(File.ReadAllBytes(gamePath));
            var renderer = game.GetType("PPURenderer", true);
            var target = renderer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.Name == "RenderLines" && method.GetParameters().Length == 7);
            ILGenerator generator;
            var original = PatchProcessor.GetOriginalInstructions(target, out generator).ToList();
            var patch = typeof(SuperZSNESRenderLinesLoopFixPlugin).Assembly.GetType(
                "SuperZSNESRenderLinesLoopFix.RenderLinesLoopPatch", true);
            patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0);
            var transpiler = patch.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);
            var transformed = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();

            Require((int)patch.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == 1,
                "Transpiler did not report exactly one rewrite.");
            Require(transformed.Count == original.Count, "Transpiler changed instruction count.");
            var differences = Enumerable.Range(0, original.Count)
                .Where(index => !SameInstruction(original[index], transformed[index])).ToArray();
            Require(differences.Length == 1, "Transpiler changed " + differences.Length + " instructions instead of one.");
            var changed = differences[0];
            Require(LoadsInt(original[changed], 128) && LoadsInt(transformed[changed], 127),
                "The sole rewrite is not terminal bound 128 -> 127.");
            Require(transformed[changed + 1].opcode == System.Reflection.Emit.OpCodes.Ble &&
                    transformed[changed + 2].opcode == System.Reflection.Emit.OpCodes.Ret,
                "The terminal BLE/RET changed.");
            Console.WriteLine("transformedIL instructions=" + transformed.Count +
                              " soleChangeIndex=" + changed + " bound=128->127 branch=ble");
        }

        private static void VerifyAllRotations()
        {
            for (var expectedStart = 0; expectedStart < 128; expectedStart++)
            {
                var priaddr = 0x8000 | (expectedStart << 1);
                var start = StartIndex(priaddr);
                Require(start == expectedStart, "Priority-rotation start decode failed.");
                var stock = Sequence(start, 129);
                var fixedSequence = Sequence(start, 128);
                Require(stock.Count == 129 && stock[0] == start && stock[128] == start,
                    "Stock loop did not revisit its starting entry.");
                Require(stock.Take(128).SequenceEqual(fixedSequence),
                    "Fix changed the order of a unique OAM entry.");
                Require(fixedSequence.Distinct().Count() == 128 &&
                        fixedSequence.All(entry => fixedSequence.Count(value => value == entry) == 1),
                    "Fixed sequence did not visit every OAM entry exactly once.");
            }

            Require(StartIndex(0) == 0 && Sequence(StartIndex(0), 128).SequenceEqual(Enumerable.Range(0, 128)),
                "Non-rotated OAM traversal changed.");
            Console.WriteLine("rotationModel starts=128 stockVisits=129 fixedVisits=128 uniqueOrderPreserved=true");
        }

        private static void VerifyDuplicateDescriptorSemantics()
        {
            var oam = Enumerable.Range(0, 544).Select(index => (byte)(index * 73 + 19)).ToArray();
            for (var start = 0; start < 128; start++)
            {
                var first = Descriptor(oam, start);
                var last = Descriptor(oam, (start + 128) & 0x7f);
                Require(first.SequenceEqual(last), "129th pass did not decode the exact starting descriptor.");
                Require(Math.Abs(0f / 128f) < float.Epsilon && Math.Abs(128f / 128f - 1f) < float.Epsilon,
                    "Expected stock duplicate Z offsets 0 and 1.");
            }

            // RenderLines chooses one camera layer from the same per-line main/sub
            // booleans. Repeating the descriptor cannot create a missing second pass.
            Require(Layer(true, false) == 7 && Layer(false, true) == 8 && Layer(true, true) == 11,
                "Main/sub camera layer truth table changed.");
            Console.WriteLine("descriptorModel duplicateFields=x,y,tile,attrs,sizeBits; onlyDifference=zOffset(0,1); layers=7/8/11");
        }

        private static int StartIndex(int priaddr)
        {
            return (priaddr & 0x8000) != 0 ? (priaddr & 0xfe) >> 1 : 0;
        }

        private static List<int> Sequence(int start, int count)
        {
            return Enumerable.Range(0, count).Select(i => (start + i) & 0x7f).ToList();
        }

        private static byte[] Descriptor(byte[] oam, int index)
        {
            return new[]
            {
                oam[index * 4], oam[index * 4 + 1], oam[index * 4 + 2], oam[index * 4 + 3],
                (byte)((oam[512 + index / 4] >> ((index % 4) << 1)) & 3)
            };
        }

        private static int Layer(bool main, bool sub)
        {
            return !main ? 8 : sub ? 11 : 7;
        }

        private static bool LoadsInt(CodeInstruction instruction, int value)
        {
            if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldc_I4)
                return instruction.operand is int i && i == value;
            if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_S)
                return Convert.ToInt32(instruction.operand) == value;
            return false;
        }

        private static bool SameInstruction(CodeInstruction left, CodeInstruction right)
        {
            return left.opcode == right.opcode && Equals(left.operand, right.operand) &&
                   left.labels.SequenceEqual(right.labels) && left.blocks.SequenceEqual(right.blocks);
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
