using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DKCLevelAutomation
{
    internal sealed class ControllerSchedule
    {
        private static readonly Dictionary<string, uint> Buttons = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "B", 0x8000 }, { "Y", 0x4000 }, { "SELECT", 0x2000 }, { "SEL", 0x2000 },
            { "START", 0x1000 }, { "ST", 0x1000 }, { "UP", 0x0800 }, { "U", 0x0800 },
            { "DOWN", 0x0400 }, { "D", 0x0400 }, { "LEFT", 0x0200 }, { "RIGHT", 0x0100 },
            { "A", 0x0080 }, { "X", 0x0040 }, { "L", 0x0020 }, { "R", 0x0010 },
            { "NONE", 0 }, { "NEUTRAL", 0 }, { "0", 0 }
        };

        public bool Enabled { get; private set; }
        public int Cursor { get; private set; }
        public uint[] Masks { get; private set; } = Array.Empty<uint>();
        public string Source { get; private set; } = string.Empty;

        public int Length { get { return Masks.Length; } }

        public void Load(string macro)
        {
            if (string.IsNullOrWhiteSpace(macro)) throw new ArgumentException("Macro must contain at least one frame assignment.");
            var assignments = new List<Tuple<int, int, uint>>();
            var maximum = -1;
            foreach (var raw in macro.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = raw.Trim();
                var equals = segment.IndexOf('=');
                if (equals <= 0 || equals == segment.Length - 1)
                    throw new FormatException("Invalid macro segment '" + segment + "'. Expected FRAME or START-END = BUTTONS.");
                var range = segment.Substring(0, equals).Trim();
                var buttons = segment.Substring(equals + 1).Trim();
                var dash = range.IndexOf('-');
                int first;
                int last;
                if (dash < 0)
                {
                    first = ParseNonNegativeInt(range, "frame");
                    last = first;
                }
                else
                {
                    first = ParseNonNegativeInt(range.Substring(0, dash), "start frame");
                    last = ParseNonNegativeInt(range.Substring(dash + 1), "end frame");
                    if (last < first) throw new FormatException("Macro end frame cannot be before its start frame.");
                }
                if (last > 10000000) throw new ArgumentOutOfRangeException("macro", "A macro cannot exceed 10,000,001 frames.");
                assignments.Add(Tuple.Create(first, last, ParseButtons(buttons)));
                maximum = Math.Max(maximum, last);
            }
            if (maximum < 0) throw new ArgumentException("Macro must contain at least one frame assignment.");
            var masks = new uint[maximum + 1];
            foreach (var assignment in assignments)
                for (var i = assignment.Item1; i <= assignment.Item2; i++) masks[i] = assignment.Item3;
            Masks = masks;
            Source = macro;
            Cursor = 0;
            Enabled = true;
        }

        public uint SampleAndAdvance()
        {
            if (!Enabled) return 0;
            var result = Cursor >= 0 && Cursor < Masks.Length ? Masks[Cursor] : 0u;
            if (Cursor < int.MaxValue) Cursor++;
            return result;
        }

        public void Reset() { Cursor = 0; }

        public void Clear()
        {
            Enabled = false;
            Cursor = 0;
            Masks = Array.Empty<uint>();
            Source = string.Empty;
        }

        private static int ParseNonNegativeInt(string text, string label)
        {
            int value;
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0)
                throw new FormatException("Invalid non-negative " + label + " '" + text + "'.");
            return value;
        }

        private static uint ParseButtons(string text)
        {
            uint mask = 0;
            foreach (var raw in text.Split(new[] { '+', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                uint bit;
                if (!Buttons.TryGetValue(raw.Trim(), out bit))
                    throw new FormatException("Unknown controller button '" + raw + "'. Use B,Y,SELECT,START,UP,DOWN,LEFT,RIGHT,A,X,L,R, or NONE.");
                mask |= bit;
            }
            return mask;
        }
    }

    internal sealed class WramCondition
    {
        public int Offset;
        public int Size;
        public string Operator;
        public ulong Expected;
        public ulong Mask;
        public bool Signed;

        public bool Matches(byte[] ram, out ulong raw)
        {
            raw = Wram.ReadUnsigned(ram, Offset, Size);
            var actualMasked = raw & Mask;
            var expectedMasked = Expected & Mask;
            if (Signed)
            {
                var actualSigned = Wram.ToSigned(actualMasked, Size);
                var expectedSigned = Wram.ToSigned(expectedMasked, Size);
                return Compare(actualSigned, expectedSigned);
            }
            return Compare(actualMasked, expectedMasked);
        }

        private bool Compare(long actual, long expected)
        {
            switch (Operator)
            {
                case "eq": return actual == expected;
                case "ne": return actual != expected;
                case "lt": return actual < expected;
                case "le": return actual <= expected;
                case "gt": return actual > expected;
                case "ge": return actual >= expected;
                default: throw new ArgumentException("Unsupported comparison operator '" + Operator + "'.");
            }
        }

        private bool Compare(ulong actual, ulong expected)
        {
            switch (Operator)
            {
                case "eq": return actual == expected;
                case "ne": return actual != expected;
                case "lt": return actual < expected;
                case "le": return actual <= expected;
                case "gt": return actual > expected;
                case "ge": return actual >= expected;
                default: throw new ArgumentException("Unsupported comparison operator '" + Operator + "'.");
            }
        }
    }

    internal static class Wram
    {
        public static int ParseOffset(string address, int size)
        {
            var value = ParseUnsigned(address);
            if (value < 0x7E0000 || value > 0x7FFFFF)
                throw new ArgumentOutOfRangeException("address", "WRAM address must be in $7E0000-$7FFFFF.");
            var offset = checked((int)(value - 0x7E0000));
            if (size < 1 || size > 4 || offset + size > 0x20000)
                throw new ArgumentOutOfRangeException("size", "Read size must be 1-4 bytes and remain inside WRAM.");
            return offset;
        }

        public static ulong ParseUnsigned(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("A numeric value is required.");
            var clean = text.Trim().Replace("_", string.Empty);
            NumberStyles style = NumberStyles.Integer;
            if (clean.StartsWith("$", StringComparison.Ordinal)) { clean = clean.Substring(1); style = NumberStyles.AllowHexSpecifier; }
            else if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { clean = clean.Substring(2); style = NumberStyles.AllowHexSpecifier; }
            ulong value;
            if (!ulong.TryParse(clean, style, CultureInfo.InvariantCulture, out value)) throw new FormatException("Invalid number '" + text + "'.");
            return value;
        }

        public static ulong ParseSizedValue(string text, int size, bool signed)
        {
            var fullMask = FullMask(size);
            if (signed && !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("-", StringComparison.Ordinal))
            {
                long value;
                if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    throw new FormatException("Invalid signed number '" + text + "'.");
                var bits = size * 8;
                var minimum = -(1L << (bits - 1));
                var maximum = (1L << (bits - 1)) - 1;
                if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException("text", "Signed value does not fit in " + size + " byte(s).");
                return unchecked((ulong)value) & fullMask;
            }
            var result = ParseUnsigned(text);
            if (result > fullMask) throw new ArgumentOutOfRangeException("text", "Value does not fit in " + size + " byte(s).");
            return result;
        }

        public static ulong ReadUnsigned(byte[] ram, int offset, int size)
        {
            if (ram == null || offset < 0 || size < 1 || size > 4 || offset + size > ram.Length)
                throw new ArgumentOutOfRangeException("offset", "WRAM read is outside available memory.");
            ulong value = 0;
            for (var i = 0; i < size; i++) value |= (ulong)ram[offset + i] << (i * 8);
            return value;
        }

        public static void WriteUnsigned(byte[] ram, int offset, int size, ulong value)
        {
            if (ram == null || offset < 0 || size < 1 || size > 4 || offset + size > ram.Length)
                throw new ArgumentOutOfRangeException("offset", "WRAM write is outside available memory.");
            var maximum = size == 4 ? uint.MaxValue : (1UL << (size * 8)) - 1;
            if (value > maximum) throw new ArgumentOutOfRangeException("value", "Value does not fit the requested write size.");
            for (var i = 0; i < size; i++) ram[offset + i] = (byte)(value >> (i * 8));
        }

        public static ulong FullMask(int size) { return size == 4 ? uint.MaxValue : (1UL << (size * 8)) - 1; }

        public static long ToSigned(ulong value, int size)
        {
            var bits = size * 8;
            var mask = FullMask(size);
            value &= mask;
            var sign = 1UL << (bits - 1);
            return (value & sign) == 0 ? (long)value : unchecked((long)(value | ~mask));
        }
    }

    internal sealed class ActiveFrameOperation
    {
        public BridgeRequest Request;
        public string Kind;
        public int TargetFrames;
        public int AdvancedFrames;
        public bool AwaitingFrame;
        public DateTime DeadlineUtc;
        public WramCondition Condition;
    }
}
