using System;
using System.Collections.Generic;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    internal readonly struct TileShape
    {
        internal readonly int Address;
        internal readonly ushort Descriptor;
        internal readonly ushort Left;
        internal readonly ushort Right;
        internal readonly ushort Top;
        internal readonly ushort Bottom;
        internal readonly bool Opaque;

        internal TileShape(int address, ushort descriptor, ushort left, ushort right,
            ushort top, ushort bottom, bool opaque)
        {
            Address = address & 0xFFFF;
            Descriptor = descriptor;
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
            Opaque = opaque;
        }
    }

    internal sealed class ComponentInfo
    {
        public string Id;
        public int Background;
        public int TileCount;
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        public float Depth;
        public int[] Addresses;
    }

    internal sealed class ComponentBuildResult
    {
        internal float[] CellDepths;
        internal List<ComponentInfo> Components;
    }

    internal static class ConnectedComponentModel
    {
        internal static ComponentBuildResult Build(TileShape[] cells, int width, int height,
            int background, int depthBands, float spacing, int minimumTiles,
            int maximumAutoTiles = 64, IDictionary<string, float> depthOverrides = null,
            bool wrap = true)
        {
            if (cells == null || width <= 0 || height <= 0 || cells.Length != width * height)
                throw new ArgumentException("Component grid dimensions do not match its cells.");
            if (background < 0 || background >= NativeTileDepthPatcher.BackgroundCount)
                throw new ArgumentOutOfRangeException(nameof(background));
            depthBands = Math.Max(1, Math.Min(31, depthBands));
            spacing = Math.Max(0f, Math.Min(1f, spacing));
            minimumTiles = Math.Max(1, minimumTiles);
            maximumAutoTiles = Math.Max(minimumTiles, maximumAutoTiles);

            int[] parent = new int[cells.Length];
            byte[] rank = new byte[cells.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (!cells[index].Opaque) continue;
                    int rightX = x + 1;
                    if (rightX < width || wrap)
                    {
                        int other = y * width + (rightX % width);
                        if (cells[other].Opaque && Touches(cells[index].Right,
                                cells[other].Left))
                            Union(parent, rank, index, other);
                    }
                    int bottomY = y + 1;
                    if (bottomY < height || wrap)
                    {
                        int other = (bottomY % height) * width + x;
                        if (cells[other].Opaque && Touches(cells[index].Bottom,
                                cells[other].Top))
                            Union(parent, rank, index, other);
                    }
                }
            }

            var members = new Dictionary<int, List<int>>();
            for (int i = 0; i < cells.Length; i++)
            {
                if (!cells[i].Opaque) continue;
                int root = Find(parent, i);
                if (!members.TryGetValue(root, out List<int> list))
                {
                    list = new List<int>();
                    members.Add(root, list);
                }
                list.Add(i);
            }

            float[] depths = new float[cells.Length];
            var components = new List<ComponentInfo>(members.Count);
            foreach (List<int> list in members.Values)
            {
                string id = BuildId(cells, list, background);
                float depth = 0f;
                if (list.Count >= minimumTiles && list.Count <= maximumAutoTiles &&
                    depthBands > 1 && spacing > 0f)
                {
                    int minimumAddress = 0xFFFF;
                    for (int i = 0; i < list.Count; i++)
                        minimumAddress = Math.Min(minimumAddress,
                            cells[list[i]].Address);
                    ulong signature = ((ulong)background << 16) |
                        (uint)minimumAddress;
                    int bucket = (int)(signature % (ulong)depthBands);
                    depth = (bucket - (depthBands - 1) * 0.5f) * spacing;
                }
                if (depthOverrides != null && depthOverrides.TryGetValue(id,
                        out float overridden) && !float.IsNaN(overridden) &&
                    !float.IsInfinity(overridden))
                    depth = Math.Max(-4f, Math.Min(4f, overridden));

                int minX = width, minY = height, maxX = 0, maxY = 0;
                int[] addresses = new int[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    int index = list[i];
                    depths[index] = depth;
                    int x = index % width;
                    int y = index / width;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                    addresses[i] = cells[index].Address;
                }
                components.Add(new ComponentInfo
                {
                    Id = id,
                    Background = background,
                    TileCount = list.Count,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY,
                    Depth = depth,
                    Addresses = addresses
                });
            }
            components.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            return new ComponentBuildResult { CellDepths = depths, Components = components };
        }

        internal static bool Touches(ushort first, ushort second)
        {
            uint expanded = (uint)(second | (ushort)(second << 1) | (second >> 1));
            return ((uint)first & expanded) != 0;
        }

        private static string BuildId(TileShape[] cells, List<int> members, int background)
        {
            ushort[] descriptors = new ushort[members.Count];
            ulong edgeSummary = 1469598103934665603UL;
            for (int i = 0; i < members.Count; i++)
            {
                TileShape shape = cells[members[i]];
                descriptors[i] = shape.Descriptor;
                edgeSummary = Mix(edgeSummary, shape.Left);
                edgeSummary = Mix(edgeSummary, shape.Right);
                edgeSummary = Mix(edgeSummary, shape.Top);
                edgeSummary = Mix(edgeSummary, shape.Bottom);
            }
            Array.Sort(descriptors);
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, (uint)background);
            hash = Mix(hash, (uint)members.Count);
            for (int i = 0; i < descriptors.Length; i++) hash = Mix(hash, descriptors[i]);
            hash = Mix(hash, (uint)edgeSummary);
            hash = Mix(hash, (uint)(edgeSummary >> 32));
            int minimumAddress = 0xFFFF;
            for (int i = 0; i < members.Count; i++)
                minimumAddress = Math.Min(minimumAddress, cells[members[i]].Address);
            return "BG" + (background + 1) + "-A" +
                minimumAddress.ToString("X4") + "-" + hash.ToString("X16");
        }

        private static ulong Mix(ulong hash, uint value)
        {
            hash ^= value;
            return hash * 1099511628211UL;
        }

        private static int Find(int[] parent, int value)
        {
            int root = value;
            while (parent[root] != root) root = parent[root];
            while (parent[value] != value)
            {
                int next = parent[value];
                parent[value] = root;
                value = next;
            }
            return root;
        }

        private static void Union(int[] parent, byte[] rank, int left, int right)
        {
            int a = Find(parent, left);
            int b = Find(parent, right);
            if (a == b) return;
            if (rank[a] < rank[b]) parent[a] = b;
            else if (rank[a] > rank[b]) parent[b] = a;
            else
            {
                parent[b] = a;
                rank[a]++;
            }
        }
    }
}
