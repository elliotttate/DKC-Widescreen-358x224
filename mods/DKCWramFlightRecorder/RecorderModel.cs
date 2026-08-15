using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DKCWramFlightRecorder
{
    internal sealed class WramRange
    {
        public int Start { get; private set; }
        public int End { get; private set; }
        public string Label { get; private set; }
        public int Length { get { return End - Start + 1; } }

        public WramRange(int start, int end, string label)
        {
            if (start < 0 || end < start || end >= WramAddress.WramSize) throw new ArgumentOutOfRangeException("start");
            Start = start;
            End = end;
            Label = string.IsNullOrWhiteSpace(label) ? WramAddress.Canonical(start) + "-" + WramAddress.Canonical(end) : label.Trim();
        }

        public bool Contains(int offset) { return offset >= Start && offset <= End; }

        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "start", WramAddress.Canonical(Start) }, { "end", WramAddress.Canonical(End) },
                { "startOffset", Start }, { "endOffset", End }, { "length", Length }, { "label", Label }
            };
        }
    }

    internal sealed class RangePlan
    {
        private readonly WramRange[] _ranges;
        public IList<WramRange> Ranges { get { return _ranges; } }
        public int TotalBytes { get; private set; }

        private RangePlan(WramRange[] ranges)
        {
            _ranges = ranges;
            TotalBytes = ranges.Sum(range => range.Length);
        }

        public static RangePlan Parse(string text, int maxRanges, int maxBytes)
        {
            if (maxRanges <= 0 || maxBytes <= 0) throw new ArgumentOutOfRangeException("maxRanges");
            var parsed = new List<WramRange>();
            var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            for (var lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
            {
                var line = lines[lineNumber - 1];
                var comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0) continue;
                var split = FirstWhitespace(line);
                var expression = split < 0 ? line : line.Substring(0, split);
                var label = split < 0 ? string.Empty : line.Substring(split).Trim();
                try { parsed.Add(ParseRange(expression, label)); }
                catch (Exception ex) { throw new FormatException("ranges.txt line " + lineNumber.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message, ex); }
            }
            if (parsed.Count == 0) throw new FormatException("Range plan is empty; refusing to arm an unbounded recorder.");
            if (parsed.Count > maxRanges) throw new FormatException("Range plan has " + parsed.Count + " ranges; maximum is " + maxRanges + ".");
            var ordered = parsed.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
            for (var index = 1; index < ordered.Length; index++)
                if (ordered[index].Start <= ordered[index - 1].End)
                    throw new FormatException("Overlapping ranges are not allowed: " + ordered[index - 1].Label + " and " + ordered[index].Label + ".");
            var result = new RangePlan(ordered);
            if (result.TotalBytes > maxBytes) throw new FormatException("Range plan covers " + result.TotalBytes + " bytes; maximum is " + maxBytes + ".");
            return result;
        }

        public WramRange Find(int offset)
        {
            var low = 0;
            var high = _ranges.Length - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var range = _ranges[middle];
                if (offset < range.Start) high = middle - 1;
                else if (offset > range.End) low = middle + 1;
                else return range;
            }
            return null;
        }

        private static WramRange ParseRange(string expression, string label)
        {
            var plus = expression.IndexOf('+');
            if (plus >= 0)
            {
                if (expression.IndexOf('+', plus + 1) >= 0) throw new FormatException("multiple '+' operators");
                var start = WramAddress.Parse(expression.Substring(0, plus));
                var length = ParseLength(expression.Substring(plus + 1));
                var end64 = (long)start + length - 1L;
                if (length <= 0 || end64 >= WramAddress.WramSize) throw new FormatException("length extends outside 128 KiB WRAM");
                return new WramRange(start, (int)end64, label);
            }
            var dash = expression.IndexOf('-');
            if (dash >= 0)
            {
                if (expression.IndexOf('-', dash + 1) >= 0) throw new FormatException("multiple '-' operators");
                return new WramRange(WramAddress.Parse(expression.Substring(0, dash)), WramAddress.Parse(expression.Substring(dash + 1)), label);
            }
            var single = WramAddress.Parse(expression);
            return new WramRange(single, single, label);
        }

        private static int ParseLength(string token)
        {
            token = (token ?? string.Empty).Trim();
            int result;
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(token.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result))
                    throw new FormatException("invalid hexadecimal length '" + token + "'");
            }
            else if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out result))
                throw new FormatException("invalid decimal length '" + token + "'");
            return result;
        }

        private static int FirstWhitespace(string value)
        {
            for (var index = 0; index < value.Length; index++) if (char.IsWhiteSpace(value[index])) return index;
            return -1;
        }
    }

    internal static class WramAddress
    {
        public const int WramSize = 0x20000;

        public static int Parse(string value)
        {
            var token = (value ?? string.Empty).Trim();
            if (token.StartsWith("$", StringComparison.Ordinal)) token = token.Substring(1);
            else if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) token = token.Substring(2);
            uint raw;
            if (token.Length == 0 || !uint.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out raw))
                throw new FormatException("invalid hexadecimal WRAM address '" + value + "'");
            int offset;
            if (!TryNormalizeConfigured(raw, out offset)) throw new FormatException("address '" + value + "' is outside offsets 00000-1FFFF or banks 7E-7F");
            return offset;
        }

        public static bool TryNormalizeConfigured(uint raw, out int offset)
        {
            if (raw < WramSize) { offset = (int)raw; return true; }
            if (raw >= 0x7E0000 && raw <= 0x7FFFFF) { offset = (int)(raw - 0x7E0000); return true; }
            offset = -1;
            return false;
        }

        public static bool TryNormalizeBus(uint busAddress, out int offset)
        {
            busAddress &= 0xFFFFFF;
            var bank = (int)(busAddress >> 16);
            var word = (int)(busAddress & 0xFFFF);
            if (bank == 0x7E) { offset = word; return true; }
            if (bank == 0x7F) { offset = 0x10000 + word; return true; }
            if ((bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)) && word < 0x2000) { offset = word; return true; }
            offset = -1;
            return false;
        }

        public static string Canonical(int offset)
        {
            if (offset < 0 || offset >= WramSize) return null;
            return "$" + (0x7E0000 + offset).ToString("X6", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class RingBuffer<T>
    {
        private readonly T[] _values;
        private int _next;
        public int Count { get; private set; }
        public int Capacity { get { return _values.Length; } }

        public RingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");
            _values = new T[capacity];
        }

        public void Add(T value)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            if (Count < _values.Length) Count++;
        }

        public T[] Snapshot()
        {
            var result = new T[Count];
            var start = Count == _values.Length ? _next : 0;
            for (var index = 0; index < Count; index++) result[index] = _values[(start + index) % _values.Length];
            return result;
        }

        public void Clear()
        {
            Array.Clear(_values, 0, _values.Length);
            _next = 0;
            Count = 0;
        }
    }

    internal struct InstructionSample
    {
        public long Sequence;
        public int Frame;
        public int Line;
        public int Dot;
        public uint Pc;
        public uint Pb;
        public uint Db;
        public uint D;
        public int A;
        public int X;
        public int Y;
        public uint S;
        public long Cycles;
        public string Flags;
        public string Opcode;

        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "sequence", Sequence }, { "frame", Frame }, { "line", Line }, { "dot", Dot },
                { "pc", Hex(Pc, 6) }, { "pb", Hex(Pb, 6) }, { "db", Hex(Db, 6) }, { "d", Hex(D, 4) },
                { "a", Hex(unchecked((uint)A), 4) }, { "x", Hex(unchecked((uint)X), 4) }, { "y", Hex(unchecked((uint)Y), 4) },
                { "s", Hex(S, 4) }, { "cycles", Cycles }, { "flags", Flags ?? string.Empty }, { "opcode", Opcode ?? string.Empty }
            };
        }

        internal static string Hex(uint value, int digits) { return "$" + value.ToString("X" + digits, CultureInfo.InvariantCulture); }
    }

    internal struct WriteSample
    {
        public long Sequence;
        public int Frame;
        public int Line;
        public int Dot;
        public uint Pc;
        public uint BusAddress;
        public int WramOffset;
        public byte OldValue;
        public byte NewValue;
        public bool Targeted;

        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "sequence", Sequence }, { "frame", Frame }, { "line", Line }, { "dot", Dot },
                { "pc", InstructionSample.Hex(Pc, 6) }, { "busAddress", InstructionSample.Hex(BusAddress & 0xFFFFFF, 6) },
                { "wramAddress", WramAddress.Canonical(WramOffset) }, { "wramOffset", WramOffset },
                { "oldValue", InstructionSample.Hex(OldValue, 2) }, { "newValue", InstructionSample.Hex(NewValue, 2) }, { "targeted", Targeted }
            };
        }
    }

    internal sealed class TargetWriteCapture
    {
        public WriteSample Write;
        public WramRange Range;
        public InstructionSample CurrentInstruction;
        public bool HasCurrentInstruction;
        public InstructionSample[] PrecedingInstructions;
        public WriteSample[] PrecedingWrites;

        public IDictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "type", "target_write" }, { "write", Write.ToData() }, { "range", Range == null ? null : Range.ToData() },
                { "currentInstruction", HasCurrentInstruction ? (object)CurrentInstruction.ToData() : null },
                { "precedingInstructions", (PrecedingInstructions ?? Array.Empty<InstructionSample>()).Select(item => (object)item.ToData()).ToArray() },
                { "precedingWrites", (PrecedingWrites ?? Array.Empty<WriteSample>()).Select(item => (object)item.ToData()).ToArray() }
            };
        }
    }
}
