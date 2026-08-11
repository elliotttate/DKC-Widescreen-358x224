using System;

namespace DKCLevelAutomation
{
    internal sealed class BridgeRequest { }

    internal static class Program
    {
        private static int Main()
        {
            var schedule = new ControllerSchedule();
            schedule.Load("0-1=RIGHT+Y;2=B;4=LEFT+A");
            Assert(schedule.Length == 5, "schedule length");
            Assert(schedule.SampleAndAdvance() == 0x4100, "frame 0 mask");
            Assert(schedule.SampleAndAdvance() == 0x4100, "frame 1 mask");
            Assert(schedule.SampleAndAdvance() == 0x8000, "frame 2 mask");
            Assert(schedule.SampleAndAdvance() == 0, "unassigned frame is neutral");
            Assert(schedule.SampleAndAdvance() == 0x0280, "frame 4 mask");
            Assert(schedule.SampleAndAdvance() == 0, "post-schedule remains neutral");
            schedule.Reset();
            Assert(schedule.SampleAndAdvance() == 0x4100, "reset rewinds cursor");

            var ram = new byte[0x20000];
            var offset = Wram.ParseOffset("0x7E1234", 2);
            Wram.WriteUnsigned(ram, offset, 2, 0xBEEF);
            Assert(Wram.ReadUnsigned(ram, offset, 2) == 0xBEEF, "little-endian WRAM round trip");

            var condition = new WramCondition
            {
                Offset = offset, Size = 2, Operator = "eq", Expected = 0xBEEF,
                Mask = 0xFFFF, Signed = false
            };
            ulong observed;
            Assert(condition.Matches(ram, out observed) && observed == 0xBEEF, "WRAM equality condition");

            ram[offset] = 0xFE;
            ram[offset + 1] = 0xFF;
            condition.Operator = "lt";
            condition.Expected = 0;
            condition.Signed = true;
            Assert(condition.Matches(ram, out observed), "signed WRAM comparison");
            Assert(Wram.ParseSizedValue("-2", 2, true) == 0xFFFE, "negative signed parse");

            Console.WriteLine("All model smoke tests passed.");
            return 0;
        }

        private static void Assert(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + label);
        }
    }
}
