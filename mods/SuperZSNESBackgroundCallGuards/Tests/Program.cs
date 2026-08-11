using System;
using System.Reflection.Emit;

internal static class Program
{
    private static readonly int[] Sizes = { 256, 64, 16, 4, 1 };

    private static int Main()
    {
        for (var count = 0; count <= 4096; count++)
        {
            var stock = Decompose(count, false, out var stockCalls);
            var guarded = Decompose(count, true, out var guardedCalls);
            Require(stock == guarded, "Batch decomposition changed for count " + count + ".");
            Require(guardedCalls <= stockCalls, "Guarded call count increased for count " + count + ".");
        }

        var emitted = BuildHighLocalGuardModel();
        for (var count = 0; count <= 4096; count++)
        {
            Decompose(count, true, out var expectedCalls);
            var encoded = emitted(count);
            Require((encoded >> 16) == expectedCalls,
                "Emitted local-141 branch call count changed for count " + count + ".");
            Require((encoded & 0xffff) == 0,
                "Emitted local-141 branch model left a remainder for count " + count + ".");
        }

        var representative = new[] { 1, 2, 3, 4, 7, 8, 15, 16, 23, 31, 32, 63, 64, 91 };
        var stockTotal = 0;
        var guardedTotal = 0;
        foreach (var count in representative)
        {
            Decompose(count, false, out var stockCalls);
            Decompose(count, true, out var guardedCalls);
            stockTotal += stockCalls;
            guardedTotal += guardedCalls;
        }
        Require(guardedTotal < stockTotal, "Representative workload did not remove no-op calls.");

        Require(ClearedEntries(0, 91) == 0, "Empty scratch-map clear fast path must do no work.");
        Require(ClearedEntries(17, 91) == 17, "Non-empty scratch map must retain stock clear behavior.");

        Console.WriteLine("Semantic model: PASS (counts 0..4096 preserve batch output; emitted ldloc.141/blt model PASS; representative calls " +
                          stockTotal + " -> " + guardedTotal + ").");
        return 0;
    }

    private static string Decompose(int count, bool guarded, out int calls)
    {
        var remainder = count;
        var result = string.Empty;
        calls = 0;
        foreach (var size in Sizes)
        {
            if (guarded && remainder < size)
                continue;
            calls++;
            while (remainder >= size)
            {
                result += size + ",";
                remainder -= size;
            }
        }
        Require(remainder == 0, "Decomposition left a remainder.");
        return result;
    }

    private static int ClearedEntries(int dictionaryCount, int usedMaterialCount)
    {
        if (dictionaryCount == 0) return 0;
        return Math.Min(dictionaryCount, usedMaterialCount);
    }

    private static Func<int, int> BuildHighLocalGuardModel()
    {
        var method = new DynamicMethod("HighLocalGuardModel", typeof(int), new[] { typeof(int) });
        var il = method.GetILGenerator();
        var locals = new LocalBuilder[142];
        for (var index = 0; index < locals.Length; index++)
            locals[index] = il.DeclareLocal(typeof(int));
        var callCount = locals[140];
        var remainder = locals[141];
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, remainder);
        foreach (var threshold in Sizes)
        {
            var skip = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, remainder);
            il.Emit(OpCodes.Ldc_I4, threshold);
            il.Emit(OpCodes.Blt, skip);
            il.Emit(OpCodes.Ldloc, callCount);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, callCount);
            il.Emit(OpCodes.Ldloc, remainder);
            il.Emit(OpCodes.Ldc_I4, threshold);
            il.Emit(OpCodes.Rem);
            il.Emit(OpCodes.Stloc, remainder);
            il.MarkLabel(skip);
        }
        il.Emit(OpCodes.Ldloc, callCount);
        il.Emit(OpCodes.Ldc_I4, 16);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, remainder);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Ret);
        return (Func<int, int>)method.CreateDelegate(typeof(Func<int, int>));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
