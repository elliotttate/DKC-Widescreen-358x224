using System;
using System.Collections.Generic;

internal static class Program
{
    private static readonly int[] Sizes = { 256, 64, 16, 4, 1 };

    private static int Main()
    {
        for (var count = 0; count <= 4095; count++)
        {
            var stock = StockOrder(count, out var stockMeshes);
            var combined = CombinedOrder(count, out var combinedMeshes);
            Require(stock.Count == combined.Count, "Tile count changed at " + count + ".");
            for (var index = 0; index < count; index++)
                Require(stock[index] == combined[index], "Tile ordering changed at count " + count + ".");
            Require(combinedMeshes == (count == 0 ? 0 : 1), "Combined mesh count changed at " + count + ".");
            Require(combinedMeshes <= stockMeshes, "Mesh count increased at " + count + ".");
            VerifyTopology(count);
        }

        var projection = ProjectBounds(91, 1375);
        Require(projection.MinStock == 91 && projection.MaxStock == 544,
            "91-list/1375-tile projection bounds changed: " + projection.MinStock + ".." + projection.MaxStock + ".");
        VerifyShapePoolPlateau();
        Console.WriteLine("Variable batching semantic model: PASS (0..4095 tile order/topology; 91-list/1375-tile stock meshes " +
                          projection.MinStock + ".." + projection.MaxStock + " -> 91 eligible meshes; shape-array pool plateaus).");
        return 0;
    }

    private static List<int> StockOrder(int count, out int meshes)
    {
        var result = new List<int>(count);
        var remaining = count;
        var next = 0;
        meshes = 0;
        foreach (var size in Sizes)
        {
            while (remaining >= size)
            {
                meshes++;
                for (var index = 0; index < size; index++) result.Add(next++);
                remaining -= size;
            }
        }
        return result;
    }

    private static List<int> CombinedOrder(int count, out int meshes)
    {
        var result = new List<int>(count);
        for (var index = 0; index < count; index++) result.Add(index);
        meshes = count == 0 ? 0 : 1;
        return result;
    }

    private static void VerifyTopology(int count)
    {
        if (count > 512 && count != 4095) return;
        var triangles = new int[count * 6];
        for (var index = 0; index < count; index++)
        {
            var vertex = index * 4;
            var triangle = index * 6;
            triangles[triangle] = vertex;
            triangles[triangle + 1] = vertex + 3;
            triangles[triangle + 2] = vertex + 1;
            triangles[triangle + 3] = vertex + 3;
            triangles[triangle + 4] = vertex;
            triangles[triangle + 5] = vertex + 2;
        }
        for (var index = 0; index < count; index++)
        {
            var offset = index * 6;
            var expected = new[] { 0, 3, 1, 3, 0, 2 };
            for (var corner = 0; corner < 6; corner++)
                Require(triangles[offset + corner] - index * 4 == expected[corner],
                    "Triangle winding changed at tile " + index + ".");
        }
        if (count > 0) Require(count * 4 - 1 <= 65534, "UInt16 vertex index limit exceeded.");
    }

    private static (int MinStock, int MaxStock) ProjectBounds(int keys, int total)
    {
        const int impossible = 1_000_000;
        var meshCost = new int[total + 1];
        for (var count = 1; count <= total; count++)
        {
            var remaining = count;
            foreach (var size in Sizes)
            {
                meshCost[count] += remaining / size;
                remaining %= size;
            }
        }
        var min = new int[total + 1];
        var max = new int[total + 1];
        for (var sum = 1; sum <= total; sum++) { min[sum] = impossible; max[sum] = -impossible; }
        for (var key = 0; key < keys; key++)
        {
            var nextMin = new int[total + 1];
            var nextMax = new int[total + 1];
            for (var sum = 0; sum <= total; sum++) { nextMin[sum] = impossible; nextMax[sum] = -impossible; }
            for (var sum = 0; sum <= total; sum++)
            {
                if (min[sum] == impossible) continue;
                for (var count = 1; sum + count <= total; count++)
                {
                    nextMin[sum + count] = Math.Min(nextMin[sum + count], min[sum] + meshCost[count]);
                    nextMax[sum + count] = Math.Max(nextMax[sum + count], max[sum] + meshCost[count]);
                }
            }
            min = nextMin;
            max = nextMax;
        }
        return (min[total], max[total]);
    }

    private static void VerifyShapePoolPlateau()
    {
        var freeByLength = new Dictionary<int, int>();
        var slots = new[] { 15, 20, 15, 64, 20, 1 };
        var allocations = 0;
        var active = 0;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            foreach (var next in slots)
            {
                var length = next * 4;
                int free;
                if (!freeByLength.TryGetValue(length, out free) || free == 0) allocations++;
                else freeByLength[length] = free - 1;
                if (active != 0)
                {
                    freeByLength.TryGetValue(active, out free);
                    freeByLength[active] = free + 1;
                }
                active = length;
            }
            if (cycle == 1)
            {
                var afterWarmup = allocations;
                foreach (var next in slots)
                {
                    var length = next * 4;
                    int free;
                    if (!freeByLength.TryGetValue(length, out free) || free == 0) allocations++;
                    else freeByLength[length] = free - 1;
                    freeByLength.TryGetValue(active, out free);
                    freeByLength[active] = free + 1;
                    active = length;
                }
                Require(allocations == afterWarmup, "Shape-array pool did not plateau after the repeated size cycle.");
                break;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
