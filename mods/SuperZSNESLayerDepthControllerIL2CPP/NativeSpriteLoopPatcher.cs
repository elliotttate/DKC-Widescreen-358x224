using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    /// <summary>
    /// SuperZSNES v0.300's PPURenderer.RenderLines loops while i &lt;= 128.
    /// The OAM index wraps at 128, so iteration 128 renders the starting sprite
    /// a second time at z + 1. Flat rendering hides the duplicate; a 3D camera
    /// exposes it. This changes the verified terminal comparison to 127 so the
    /// routine visits each of the 128 OAM entries exactly once.
    /// </summary>
    internal sealed class NativeSpriteLoopPatcher : IDisposable
    {
        internal const int LoopLimitRva = 0x393DC8;
        internal static readonly byte[] OriginalBytes =
            { 0x81, 0xF9, 0x80, 0x00, 0x00, 0x00 }; // cmp ecx,128
        internal static readonly byte[] ReplacementBytes =
            { 0x81, 0xF9, 0x7F, 0x00, 0x00, 0x00 }; // cmp ecx,127

        private const uint PageExecuteReadWrite = 0x40;
        private readonly Action<string> _log;
        private IntPtr _address;

        internal bool Applied { get; private set; }
        internal string AddressHex => _address == IntPtr.Zero ? string.Empty :
            "0x" + unchecked((uint)_address.ToInt32()).ToString("X8");

        internal NativeSpriteLoopPatcher(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        internal void Apply(string gameAssemblyPath)
        {
            if (Applied) throw new InvalidOperationException("Sprite loop patch is active.");
            if (IntPtr.Size != 4)
                throw new PlatformNotSupportedException("The verified patch supports x86 v0.300 only.");
            string hash = ComputeSha256(gameAssemblyPath);
            if (!string.Equals(hash, NativeTileDepthPatcher.ExpectedGameAssemblySha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported GameAssembly.dll SHA-256: " + hash);

            IntPtr module = GetModuleHandleW("GameAssembly.dll");
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("Loaded GameAssembly.dll was not found.");
            _address = new IntPtr(module.ToInt64() + LoopLimitRva);
            RequireBytes(_address, OriginalBytes, "RenderLines loop limit");
            try
            {
                WriteMemory(_address, ReplacementBytes);
                RequireBytes(_address, ReplacementBytes, "patched RenderLines loop limit");
                Applied = true;
                _log("Removed RenderLines' duplicate 129th OAM pass at " + AddressHex + ".");
            }
            catch
            {
                try
                {
                    if (Equal(ReadMemory(_address, ReplacementBytes.Length),
                            ReplacementBytes))
                        WriteMemory(_address, OriginalBytes);
                }
                catch { }
                _address = IntPtr.Zero;
                throw;
            }
        }

        public void Dispose()
        {
            if (!Applied || _address == IntPtr.Zero) return;
            try
            {
                byte[] current = ReadMemory(_address, ReplacementBytes.Length);
                if (Equal(current, ReplacementBytes))
                    WriteMemory(_address, OriginalBytes);
                else
                    _log("Sprite-loop rollback skipped because the native site changed.");
            }
            finally
            {
                Applied = false;
                _address = IntPtr.Zero;
            }
        }

        private static void WriteMemory(IntPtr address, byte[] bytes)
        {
            if (!VirtualProtect(address, (UIntPtr)bytes.Length,
                    PageExecuteReadWrite, out uint oldProtection))
                throw new InvalidOperationException("VirtualProtect failed: " +
                    Marshal.GetLastWin32Error());
            try
            {
                Marshal.Copy(bytes, 0, address, bytes.Length);
                FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)bytes.Length);
            }
            finally
            {
                VirtualProtect(address, (UIntPtr)bytes.Length, oldProtection, out _);
            }
        }

        private static void RequireBytes(IntPtr address, byte[] expected, string name)
        {
            byte[] actual = ReadMemory(address, expected.Length);
            if (!Equal(actual, expected))
                throw new InvalidDataException(name + " bytes differ. Expected " +
                    Hex(expected) + ", found " + Hex(actual));
        }

        private static byte[] ReadMemory(IntPtr address, int count)
        {
            byte[] bytes = new byte[count];
            Marshal.Copy(address, bytes, 0, count);
            return bytes;
        }

        private static string ComputeSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static string Hex(byte[] bytes) =>
            BitConverter.ToString(bytes).Replace("-", string.Empty);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size,
            uint newProtect, out uint oldProtect);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address,
            UIntPtr size);
    }
}
