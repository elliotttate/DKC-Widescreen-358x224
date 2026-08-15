using System;
using System.Linq;
using DKCWramFlightRecorder;

internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        Run("full bank range", FullBankRange);
        Run("offset plus length", OffsetPlusLength);
        Run("single address", SingleAddress);
        Run("comments and labels", CommentsAndLabels);
        Run("empty rejected", EmptyRejected);
        Run("overlap rejected", OverlapRejected);
        Run("out of WRAM rejected", OutsideRejected);
        Run("range count bound", RangeCountBound);
        Run("byte bound", ByteBound);
        Run("native WRAM normalization", NativeNormalization);
        Run("low mirror normalization", MirrorNormalization);
        Run("non-WRAM normalization rejected", NonWramRejected);
        Run("binary range match", RangeMatch);
        Run("ring preserves order", RingOrder);
        Run("ring wrap preserves newest", RingWrap);
        Run("target capture carries context", TargetCaptureContext);
        Console.WriteLine("PASS: " + _passed + " offline model tests.");
        return 0;
    }

    private static void FullBankRange()
    {
        var plan = RangePlan.Parse("$7E192B-$7E1930 scanner\n$7F0000-$7F0001 upper", 4, 32);
        Equal(2, plan.Ranges.Count);
        Equal(8, plan.TotalBytes);
        Equal(0x192B, plan.Ranges[0].Start);
        Equal(0x10001, plan.Ranges[1].End);
    }

    private static void OffsetPlusLength()
    {
        var plan = RangePlan.Parse("0x192B+6 bookmarks", 2, 8);
        Equal(0x192B, plan.Ranges[0].Start);
        Equal(0x1930, plan.Ranges[0].End);
        Equal("bookmarks", plan.Ranges[0].Label);
    }

    private static void SingleAddress()
    {
        var plan = RangePlan.Parse("1FFFF final-byte", 1, 1);
        Equal(0x1FFFF, plan.Ranges[0].Start);
        Equal("$7FFFFF", WramAddress.Canonical(plan.Ranges[0].Start));
    }

    private static void CommentsAndLabels()
    {
        var plan = RangePlan.Parse("# generated\n  $7E0100-$7E0101   two byte field # detail\n", 2, 4);
        Equal("two byte field", plan.Ranges[0].Label);
    }

    private static void EmptyRejected() { Throws<FormatException>(() => RangePlan.Parse("# none", 1, 1)); }
    private static void OverlapRejected() { Throws<FormatException>(() => RangePlan.Parse("100-110 a\n110-120 b", 2, 64)); }
    private static void OutsideRejected() { Throws<FormatException>(() => RangePlan.Parse("$801000 no", 1, 1)); }
    private static void RangeCountBound() { Throws<FormatException>(() => RangePlan.Parse("100 a\n200 b", 1, 2)); }
    private static void ByteBound() { Throws<FormatException>(() => RangePlan.Parse("100-110 too-wide", 1, 16)); }

    private static void NativeNormalization()
    {
        int offset;
        True(WramAddress.TryNormalizeBus(0x7E192B, out offset)); Equal(0x192B, offset);
        True(WramAddress.TryNormalizeBus(0x7F1234, out offset)); Equal(0x11234, offset);
    }

    private static void MirrorNormalization()
    {
        int offset;
        True(WramAddress.TryNormalizeBus(0x00192B, out offset)); Equal(0x192B, offset);
        True(WramAddress.TryNormalizeBus(0x801000, out offset)); Equal(0x1000, offset);
        True(WramAddress.TryNormalizeBus(0xBF1FFF, out offset)); Equal(0x1FFF, offset);
    }

    private static void NonWramRejected()
    {
        int offset;
        True(!WramAddress.TryNormalizeBus(0x402000, out offset));
        True(!WramAddress.TryNormalizeBus(0xC00010, out offset));
    }

    private static void RangeMatch()
    {
        var plan = RangePlan.Parse("200-20F b\n100-10F a", 2, 32);
        Equal("a", plan.Find(0x108).Label);
        Equal("b", plan.Find(0x200).Label);
        True(plan.Find(0x110) == null);
    }

    private static void RingOrder()
    {
        var ring = new RingBuffer<int>(3);
        ring.Add(1); ring.Add(2);
        True(ring.Snapshot().SequenceEqual(new[] { 1, 2 }));
    }

    private static void RingWrap()
    {
        var ring = new RingBuffer<int>(3);
        ring.Add(1); ring.Add(2); ring.Add(3); ring.Add(4); ring.Add(5);
        True(ring.Snapshot().SequenceEqual(new[] { 3, 4, 5 }));
        ring.Clear();
        Equal(0, ring.Count);
    }

    private static void TargetCaptureContext()
    {
        var instruction = new InstructionSample { Sequence = 7, Frame = 10, Pc = 0x818705, Pb = 0x810000, Db = 0x7E0000, D = 0x1234, A = 1, X = 2, Y = 3, S = 0x1FF, Flags = "NvMXdIzCe", Opcode = "STA $192B" };
        var write = new WriteSample { Sequence = 9, Frame = 10, Pc = 0x818705, BusAddress = 0x7E192B, WramOffset = 0x192B, OldValue = 0, NewValue = 0xFF, Targeted = true };
        var capture = new TargetWriteCapture
        {
            Write = write, Range = new WramRange(0x192B, 0x1930, "bookmarks"), CurrentInstruction = instruction,
            HasCurrentInstruction = true, PrecedingInstructions = new[] { instruction }, PrecedingWrites = new[] { write }
        };
        var data = capture.ToData();
        True(data["currentInstruction"] != null);
        Equal(1, ((object[])data["precedingInstructions"]).Length);
        Equal(1, ((object[])data["precedingWrites"]).Length);
        Equal("bookmarks", ((System.Collections.Generic.IDictionary<string, object>)data["range"])["label"]);
    }

    private static void Run(string name, Action test)
    {
        try { test(); _passed++; }
        catch (Exception ex) { throw new Exception("FAILED: " + name + ": " + ex.Message, ex); }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
}
