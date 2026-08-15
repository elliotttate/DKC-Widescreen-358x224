using System;
using System.Collections.Generic;
using DKCPlaytestRecorder;

internal static class Program
{
    private static int Main()
    {
        try
        {
            RecordsAndCompresses();
            ResetsOnDiscontinuity();
            TrimsWithCheckpointOverlap();
            Console.WriteLine("DKCPlaytestRecorder model tests: PASS (3/3)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RecordsAndCompresses()
    {
        var model = new TimelineModel(60, 5);
        model.Record(100, Masks(0x4100));
        model.Record(101, Masks(0x4100));
        model.Record(102, Masks(0));
        model.Record(103, Masks(0x0800));
        model.Record(104, Masks(0x0800));
        Equal("0-1=Y+RIGHT;2=NONE;3-4=UP", TimelineModel.BuildMacro(model.Inputs, 0), "macro");
        True(model.CheckpointDue(0), "first checkpoint due");
        True(!model.CheckpointDue(1), "interval not due");
        True(model.CheckpointDue(0), "initial remains due");
        Equal(2, model.SliceAfter(3).Count, "slice after anchor");
    }

    private static void ResetsOnDiscontinuity()
    {
        var model = new TimelineModel(60, 5);
        True(!model.Record(9, Masks(0x0100)), "first record does not reset");
        True(!model.Record(10, Masks(0x0100)), "contiguous frame does not reset");
        True(model.Record(4, Masks(0x0100)), "backward state load resets");
        Equal(1, model.Count, "only new episode retained");
        Equal(1L, model.Sequence, "sequence restarted");
        Equal(4, model.LastEmulatedFrame.Value, "new episode frame");
    }

    private static void TrimsWithCheckpointOverlap()
    {
        var model = new TimelineModel(5, 2);
        for (var i = 0; i < 20; i++) model.Record(i, Masks((ushort)i));
        Equal(9, model.Count, "history plus checkpoint overlap retained");
        Equal(11, model.Inputs[0].EmulatedFrame, "old inputs trimmed");
        Equal("B+Y+SELECT+START", TimelineModel.MaskName(0xF000), "button names");
    }

    private static ushort[] Masks(ushort p1) => new[] { p1, (ushort)0, (ushort)0, (ushort)0, (ushort)0 };

    private static void True(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("Assertion failed: " + name);
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
    }
}
