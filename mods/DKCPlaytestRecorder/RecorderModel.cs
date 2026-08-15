using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DKCPlaytestRecorder
{
    public sealed class RecordedInput
    {
        public long Sequence;
        public int EmulatedFrame;
        public ushort[] Controllers;
    }

    public sealed class TimelineModel
    {
        private readonly int _checkpointInterval;
        private readonly RecordedInput[] _inputs;
        private int _start;
        private int _count;
        private long _sequence;
        private int? _lastEmulatedFrame;

        public TimelineModel(int maxFrames, int checkpointInterval)
        {
            if (maxFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxFrames));
            if (checkpointInterval < 1) throw new ArgumentOutOfRangeException(nameof(checkpointInterval));
            _checkpointInterval = checkpointInterval;
            _inputs = new RecordedInput[maxFrames + checkpointInterval + 2];
            for (var i = 0; i < _inputs.Length; i++)
                _inputs[i] = new RecordedInput { Controllers = new ushort[5] };
        }

        public long Sequence => _sequence;
        public int Count => _count;
        public int? LastEmulatedFrame => _lastEmulatedFrame;
        public IReadOnlyList<RecordedInput> Inputs => Snapshot();

        public bool Record(int emulatedFrame, ushort[] controllers)
        {
            if (controllers == null || controllers.Length != 5)
                throw new ArgumentException("Exactly five controller masks are required.", nameof(controllers));

            var reset = _lastEmulatedFrame.HasValue && emulatedFrame != _lastEmulatedFrame.Value + 1;
            if (reset) Reset();

            _sequence++;
            _lastEmulatedFrame = emulatedFrame;
            int index;
            if (_count < _inputs.Length)
            {
                index = (_start + _count) % _inputs.Length;
                _count++;
            }
            else
            {
                index = _start;
                _start = (_start + 1) % _inputs.Length;
            }
            var item = _inputs[index];
            item.Sequence = _sequence;
            item.EmulatedFrame = emulatedFrame;
            for (var i = 0; i < 5; i++) item.Controllers[i] = controllers[i];
            return reset;
        }

        public bool CheckpointDue(long lastCheckpointSequence)
        {
            return _sequence > 0 && (lastCheckpointSequence <= 0 || _sequence - lastCheckpointSequence >= _checkpointInterval);
        }

        public List<RecordedInput> SliceAfter(long anchorSequence)
        {
            var result = new List<RecordedInput>();
            for (var i = 0; i < _count; i++)
            {
                var input = _inputs[(_start + i) % _inputs.Length];
                if (input.Sequence <= anchorSequence) continue;
                result.Add(Clone(input));
            }
            return result;
        }

        public void Reset()
        {
            _start = 0;
            _count = 0;
            _sequence = 0;
            _lastEmulatedFrame = null;
        }

        private IReadOnlyList<RecordedInput> Snapshot()
        {
            var result = new List<RecordedInput>(_count);
            for (var i = 0; i < _count; i++) result.Add(Clone(_inputs[(_start + i) % _inputs.Length]));
            return result;
        }

        private static RecordedInput Clone(RecordedInput input)
        {
            return new RecordedInput
            {
                Sequence = input.Sequence,
                EmulatedFrame = input.EmulatedFrame,
                Controllers = (ushort[])input.Controllers.Clone()
            };
        }

        public static string BuildMacro(IReadOnlyList<RecordedInput> inputs, int controller)
        {
            if (controller < 0 || controller >= 5) throw new ArgumentOutOfRangeException(nameof(controller));
            if (inputs == null || inputs.Count == 0) return string.Empty;
            var text = new StringBuilder();
            var start = 0;
            var mask = inputs[0].Controllers[controller];
            for (var i = 1; i <= inputs.Count; i++)
            {
                if (i < inputs.Count && inputs[i].Controllers[controller] == mask) continue;
                if (text.Length != 0) text.Append(';');
                text.Append(start.ToString(CultureInfo.InvariantCulture));
                if (i - 1 != start) text.Append('-').Append((i - 1).ToString(CultureInfo.InvariantCulture));
                text.Append('=').Append(MaskName(mask));
                if (i < inputs.Count)
                {
                    start = i;
                    mask = inputs[i].Controllers[controller];
                }
            }
            return text.ToString();
        }

        public static string MaskName(ushort mask)
        {
            if (mask == 0) return "NONE";
            var names = new[] { "B", "Y", "SELECT", "START", "UP", "DOWN", "LEFT", "RIGHT", "A", "X", "L", "R" };
            var bits = new ushort[] { 0x8000, 0x4000, 0x2000, 0x1000, 0x0800, 0x0400, 0x0200, 0x0100, 0x0080, 0x0040, 0x0020, 0x0010 };
            var text = new StringBuilder();
            for (var i = 0; i < bits.Length; i++)
            {
                if ((mask & bits[i]) == 0) continue;
                if (text.Length != 0) text.Append('+');
                text.Append(names[i]);
            }
            var unknown = (ushort)(mask & 0x000F);
            if (unknown != 0)
            {
                if (text.Length != 0) text.Append('+');
                text.Append("0x").Append(unknown.ToString("X4", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }
    }
}
