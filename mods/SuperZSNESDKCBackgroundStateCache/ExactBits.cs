using System.Runtime.InteropServices;

namespace SuperZSNESDKCBackgroundStateCache
{
    internal static class ExactBits
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct SingleBits
        {
            [FieldOffset(0)] internal float Float;
            [FieldOffset(0)] internal int Int;
        }

        internal static int Single(float value) => new SingleBits { Float = value }.Int;
    }
}
