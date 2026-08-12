using System;
using System.Collections.Generic;
using System.Linq;

namespace SuperZSNESSpriteDepthStudio
{
    public static class BackgroundObjectDecoder
    {
        public const int ViewWidth = 368;
        public const int ViewHeight = 224;
        public const int ViewLeft = -56;

        public static List<BackgroundObjectRecord> Decode(byte[] vram, byte[] cgram,
            byte[] registers, int[] scrollX, int[] scrollY,
            BackgroundComponentReport report)
        {
            if (vram == null || vram.Length != 65536)
                throw new ArgumentException("VRAM must be 65536 bytes.");
            if (cgram == null || (cgram.Length != 512 && cgram.Length != ViewHeight * 512))
                throw new ArgumentException("CGRAM must be 512 bytes or 224 scanline palettes.");
            if (registers == null || registers.Length < 64)
                throw new ArgumentException("PPU register snapshot must contain at least 64 bytes.");
            if (scrollX == null || scrollX.Length < 3 || scrollY == null || scrollY.Length < 3)
                throw new ArgumentException("Three background scroll pairs are required.");
            if (report?.Components == null) return new List<BackgroundObjectRecord>();

            var result = new List<BackgroundObjectRecord>();
            for (int bg = 0; bg < 3; bg++)
            {
                List<BackgroundComponentInfo> components = report.Components
                    .Where(component => component != null && component.Background == bg &&
                        component.Addresses != null && component.Addresses.Length > 0)
                    .ToList();
                if (components.Count == 0) continue;
                DecodeBackground(vram, cgram, registers, scrollX[bg], scrollY[bg], bg,
                    components, result);
            }
            return result.OrderBy(item => item.Background)
                .ThenByDescending(item => item.OpaquePixels)
                .ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        }

        private static void DecodeBackground(byte[] vram, byte[] cgram, byte[] registers,
            int scrollX, int scrollY, int bg, List<BackgroundComponentInfo> components,
            List<BackgroundObjectRecord> destination)
        {
            byte bgmode = registers[5];
            byte bgsc = registers[7 + bg];
            bool size16 = ((bgmode >> (4 + bg)) & 1) != 0;
            int cellPixels = size16 ? 16 : 8;
            int mapWidth = (bgsc & 1) != 0 ? 64 : 32;
            int mapHeight = (bgsc & 2) != 0 ? 64 : 32;
            int mapBase = (bgsc & 0xFC) << 9;
            int bits = bg == 2 ? 2 : 4;
            int chrBase = GetChrBase(registers, bg);

            var addressOwners = new Dictionary<int, BackgroundComponentInfo>();
            foreach (BackgroundComponentInfo component in components)
                foreach (int address in component.Addresses)
                    addressOwners[address & 0xFFFF] = component;

            var rasters = new Dictionary<string, Raster>(StringComparer.Ordinal);
            for (int sy = 0; sy < ViewHeight; sy++)
            {
                int worldY = scrollY + sy;
                int cellY = FloorDiv(worldY, cellPixels);
                int mapY = Mod(cellY, mapHeight);
                int py = Mod(worldY, cellPixels);
                for (int sx = 0; sx < ViewWidth; sx++)
                {
                    int worldX = scrollX + ViewLeft + sx;
                    int cellX = FloorDiv(worldX, cellPixels);
                    int mapX = Mod(cellX, mapWidth);
                    int px = Mod(worldX, cellPixels);
                    int address = (mapBase + GetTileAddress(mapX, mapY, bgsc)) & 0xFFFF;
                    if (!addressOwners.TryGetValue(address, out BackgroundComponentInfo owner))
                        continue;
                    int descriptor = vram[address] | (vram[(address + 1) & 0xFFFF] << 8);
                    int color = DecodePixel(vram, descriptor, size16, mapX, mapY,
                        px, py, chrBase, bits);
                    if (color == 0) continue;
                    int palette = (descriptor >> 10) & 7;
                    int paletteIndex = palette * (bits == 2 ? 4 : 16) + color;
                    uint argb = ReadPalette(cgram, sy, paletteIndex);
                    if (!rasters.TryGetValue(owner.Id, out Raster raster))
                    {
                        raster = new Raster(owner);
                        rasters.Add(owner.Id, raster);
                    }
                    raster.Set(sx, sy, argb);
                }
            }

            foreach (Raster raster in rasters.Values)
            {
                if (raster.Count == 0) continue;
                int width = raster.MaxX - raster.MinX + 1;
                int height = raster.MaxY - raster.MinY + 1;
                var cropped = new uint[width * height];
                for (int y = raster.MinY; y <= raster.MaxY; y++)
                for (int x = raster.MinX; x <= raster.MaxX; x++)
                    cropped[(y - raster.MinY) * width + x - raster.MinX] =
                        raster.Pixels[y * ViewWidth + x];
                destination.Add(new BackgroundObjectRecord
                {
                    Id = raster.Component.Id,
                    Background = bg,
                    TileCount = raster.Component.TileCount,
                    X = raster.MinX + ViewLeft,
                    Y = raster.MinY,
                    Width = width,
                    Height = height,
                    OpaquePixels = raster.Count,
                    AutomaticDepth = raster.Component.Depth,
                    Pixels = cropped
                });
            }
        }

        private static int DecodePixel(byte[] vram, int descriptor, bool size16,
            int cellX, int cellY, int px, int py, int chrBase, int bits)
        {
            int tile = descriptor & 0x3FF;
            bool xflip = (descriptor & 0x4000) != 0;
            bool yflip = (descriptor & 0x8000) != 0;
            int localX = px & 7;
            int localY = py & 7;
            if (size16)
            {
                int tileX = cellX * 2 + (px >> 3);
                int tileY = cellY * 2 + (py >> 3);
                if (((tileX & 1) != 0) ^ xflip) tile++;
                if (((tileY & 1) != 0) ^ yflip) tile += 16;
            }
            if (xflip) localX = 7 - localX;
            if (yflip) localY = 7 - localY;
            int address = (chrBase + (tile & 0x3FF) * bits * 8) & 0xFFFF;
            int bit = 7 - localX;
            int color = 0;
            for (int plane = 0; plane < bits; plane++)
            {
                int planeAddress = address + (plane >> 1) * 16 + localY * 2 + (plane & 1);
                color |= ((vram[planeAddress & 0xFFFF] >> bit) & 1) << plane;
            }
            return color;
        }

        private static uint ReadPalette(byte[] cgram, int line, int index)
        {
            int address = (cgram.Length == 512 ? 0 : line * 512) + (index & 255) * 2;
            int value = cgram[address] | (cgram[address + 1] << 8);
            uint r = (uint)((value & 31) * 255 / 31);
            uint g = (uint)(((value >> 5) & 31) * 255 / 31);
            uint b = (uint)(((value >> 10) & 31) * 255 / 31);
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        private static int GetTileAddress(int x, int y, byte bgsc)
        {
            int offset = 0;
            if ((bgsc & 1) != 0)
            {
                if ((x & 0x20) != 0) offset += 2048;
                if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 4096;
            }
            else if ((bgsc & 2) != 0 && (y & 0x20) != 0) offset += 2048;
            return (x & 31) * 2 + (y & 31) * 64 + offset;
        }

        private static int GetChrBase(byte[] registers, int bg) => bg == 0
            ? (registers[11] & 0x0F) << 13
            : bg == 1 ? ((registers[11] >> 4) & 0x0F) << 13
            : (registers[12] & 0x0F) << 13;

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            if (value < 0 && value % divisor != 0) quotient--;
            return quotient;
        }

        private static int Mod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private sealed class Raster
        {
            internal readonly BackgroundComponentInfo Component;
            internal readonly uint[] Pixels = new uint[ViewWidth * ViewHeight];
            internal int MinX = ViewWidth, MinY = ViewHeight, MaxX = -1, MaxY = -1, Count;

            internal Raster(BackgroundComponentInfo component) => Component = component;

            internal void Set(int x, int y, uint color)
            {
                int index = y * ViewWidth + x;
                if (Pixels[index] == 0) Count++;
                Pixels[index] = color;
                MinX = Math.Min(MinX, x); MinY = Math.Min(MinY, y);
                MaxX = Math.Max(MaxX, x); MaxY = Math.Max(MaxY, y);
            }
        }
    }
}
