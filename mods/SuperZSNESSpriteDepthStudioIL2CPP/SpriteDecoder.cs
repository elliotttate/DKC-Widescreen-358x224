using System;
using System.Collections.Generic;

namespace SuperZSNESSpriteDepthStudio
{
    public static class SpriteDecoder
    {
        private static readonly int[] SmallWidth = { 1, 1, 1, 2, 2, 4, 2, 2 };
        private static readonly int[] SmallHeight = { 1, 1, 1, 2, 2, 4, 4, 4 };
        private static readonly int[] LargeWidth = { 2, 4, 8, 4, 8, 8, 4, 4 };
        private static readonly int[] LargeHeight = { 2, 4, 8, 4, 8, 8, 8, 4 };

        public static List<SpriteRecord> Decode(byte[] vram, byte[] oam, byte[] cgram,
            byte[] objSelByLine, byte[] objActiveByLine = null)
        {
            if (vram == null || vram.Length != 65536) throw new ArgumentException("VRAM must be 65536 bytes.");
            if (oam == null || oam.Length != 544) throw new ArgumentException("OAM must be 544 bytes.");
            if (cgram == null || (cgram.Length != 512 && cgram.Length != 224 * 512))
                throw new ArgumentException("CGRAM must be 512 bytes or 224 scanline palettes.");
            if (objSelByLine == null || objSelByLine.Length < 224) throw new ArgumentException("OBJSEL lines must contain 224 bytes.");
            if (objActiveByLine != null && objActiveByLine.Length < 224) throw new ArgumentException("OBJ-active lines must contain 224 bytes.");
            var result = new List<SpriteRecord>(128);
            for (int slot = 0; slot < 128; slot++) result.Add(DecodeOne(slot, vram, oam, cgram, objSelByLine, objActiveByLine));
            return result;
        }

        public static SpriteRecord ReadMetadata(int slot, byte[] oam, byte[] objSelByLine,
            byte[] objActiveByLine = null)
        {
            if (slot < 0 || slot >= 128) throw new ArgumentOutOfRangeException(nameof(slot));
            if (oam == null || oam.Length != 544) throw new ArgumentException("OAM must be 544 bytes.");
            if (objSelByLine == null || objSelByLine.Length < 224) throw new ArgumentException("OBJSEL lines must contain 224 bytes.");
            int x = oam[slot * 4];
            int y = oam[slot * 4 + 1] + 1;
            int tile = oam[slot * 4 + 2];
            int attr = oam[slot * 4 + 3];
            int upper = (oam[512 + slot / 4] >> ((slot & 3) * 2)) & 3;
            if ((upper & 1) != 0) x = x <= 127 ? x + 256 : x - 256;
            if (y > 239) y -= 256;
            int sampleLine = Math.Max(1, Math.Min(223, y));
            int size = (objSelByLine[sampleLine] >> 5) & 7;
            bool large = (upper & 2) != 0;
            int width = (large ? LargeWidth[size] : SmallWidth[size]) * 8;
            int height = (large ? LargeHeight[size] : SmallHeight[size]) * 8;
            bool active = objActiveByLine == null;
            if (objActiveByLine != null)
                for (int line=Math.Max(0,y);line<Math.Min(224,y+height);line++)
                    if (objActiveByLine[line] != 0) { active=true; break; }
            return new SpriteRecord
            {
                Slot = slot, X = x, Y = y, Tile = tile, Attributes = attr,
                Palette = (attr & 0x0E) >> 1, Priority = (attr >> 4) & 3,
                NameSelect = attr & 1, Large = large, SizeSelector = size,
                Width = width, Height = height,
                IntersectsScreen = active && x < 368 && x + width > -56 && y < 224 && y + height > 0
            };
        }

        private static SpriteRecord DecodeOne(int slot, byte[] vram, byte[] oam,
            byte[] cgram, byte[] objSelByLine, byte[] objActiveByLine)
        {
            SpriteRecord record = ReadMetadata(slot, oam, objSelByLine, objActiveByLine);
            int x = record.X, y = record.Y, tile = record.Tile, attr = record.Attributes;
            int sampleLine = Math.Max(1, Math.Min(223, y));
            int objSel = objSelByLine[sampleLine];
            bool large = record.Large;
            int tilesWide = record.Width / 8, tilesHigh = record.Height / 8;
            int width = record.Width, height = record.Height;
            uint[] pixels = new uint[width * height];
            bool flipX = (attr & 0x40) != 0;
            bool flipY = (attr & 0x80) != 0;
            int palette = (attr & 0x0E) >> 1;
            int nameGap = (((objSel >> 3) & 3) + 1) << 13;
            int nameBase = (objSel & 3) << 14;
            int baseAddress = nameBase + ((attr & 1) != 0 ? nameGap : 0) + tile * 32;
            int opaque = 0;
            for (int ty = 0; ty < tilesHigh; ty++)
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int tileAddress = (baseAddress + tx * 32 + ty * 512) & 0xFFFF;
                for (int py = 0; py < 8; py++)
                for (int px = 0; px < 8; px++)
                {
                    int color = Decode4Bpp(vram, tileAddress, px, py);
                    if (color == 0) continue;
                    int dx = flipX ? width - 1 - (tx * 8 + px) : tx * 8 + px;
                    int dy = flipY ? height - 1 - (ty * 8 + py) : ty * 8 + py;
                    int screenLine=Math.Max(0,Math.Min(223,y+dy));
                    pixels[dy * width + dx] = ReadPalette(cgram, screenLine,
                        128 + palette * 16 + color);
                    opaque++;
                }
            }
            record.OpaquePixels = opaque;
            record.IntersectsScreen = opaque > 0 && record.IntersectsScreen;
            record.Pixels = pixels;
            return record;
        }

        private static int Decode4Bpp(byte[] vram, int address, int x, int y)
        {
            int bit = 7 - x;
            int row = y * 2;
            int value = (vram[(address + row) & 0xFFFF] >> bit) & 1;
            value |= ((vram[(address + row + 1) & 0xFFFF] >> bit) & 1) << 1;
            value |= ((vram[(address + 16 + row) & 0xFFFF] >> bit) & 1) << 2;
            value |= ((vram[(address + 17 + row) & 0xFFFF] >> bit) & 1) << 3;
            return value;
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
    }
}
