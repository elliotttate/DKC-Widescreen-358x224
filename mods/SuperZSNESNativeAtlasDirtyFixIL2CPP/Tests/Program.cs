using System;
using System.IO;
using System.Security.Cryptography;
using SuperZSNESNativeAtlasDirtyFixIL2CPP;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || !File.Exists(args[0]))
                throw new ArgumentException("Pass the verified v0.300 GameAssembly.dll path.");
            string hash;
            using (FileStream stream = File.OpenRead(args[0]))
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            Require(hash == NativeAtlasPatcher.ExpectedGameAssemblySha256, "GameAssembly hash");

            foreach (NativeAtlasPatcher.PatchSite site in NativeAtlasPatcher.Sites)
            {
                Require(Equal(ReadAtRva(args[0], site.StoreRva, site.StoreBytes.Length),
                    site.StoreBytes), site.Name + " store bytes");
                Require(Equal(ReadAtRva(args[0], site.HookRva, site.ReplayBytes.Length),
                    site.ReplayBytes), site.Name + " hook bytes");

                const long stubAddress = 0x20001000;
                long returnAddress = 0x10300000L + site.HookRva + site.ReplayBytes.Length;
                byte[] stub = NativeAtlasPatcher.BuildStub(site, stubAddress, returnAddress);
                Require(stub[0] == 0x51 && stub[1] == 0x8B && stub[2] == 0x46 &&
                    stub[3] == site.ArrayFieldOffset, site.Name + " array load");
                Require(stub[4] == 0x8B && stub[5] == 0x4D &&
                    stub[6] == site.IndexLocalDisplacement, site.Name + " page local load");
                Require(stub[7] == 0xC6 && stub[8] == 0x44 && stub[9] == 0x08 &&
                    stub[10] == 0x10 && stub[11] == 0x01 && stub[12] == 0x59,
                    site.Name + " conditional store");
                for (int i = 0; i < site.ReplayBytes.Length; i++)
                    Require(stub[13 + i] == site.ReplayBytes[i], site.Name + " replay byte " + i);
                int jumpIndex = 13 + site.ReplayBytes.Length;
                Require(stub[jumpIndex] == 0xE9, site.Name + " return jump opcode");
                int displacement = BitConverter.ToInt32(stub, jumpIndex + 1);
                Require(stubAddress + jumpIndex + 5 + displacement == returnAddress,
                    site.Name + " return jump target");

                byte[] hook = NativeAtlasPatcher.BuildJump(0x10300000L + site.HookRva,
                    stubAddress, site.ReplayBytes.Length);
                Require(hook.Length == site.ReplayBytes.Length && hook[0] == 0xE9,
                    site.Name + " hook jump shape");
            }
            Console.WriteLine("PASS: exact v0.300 hash/bytes and all native atlas trampoline encodings.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static byte[] ReadAtRva(string path, int rva, int count)
    {
        using (FileStream stream = File.OpenRead(path))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            stream.Position = 0x3C;
            int pe = reader.ReadInt32();
            stream.Position = pe + 6;
            int sections = reader.ReadUInt16();
            stream.Position = pe + 20;
            int optionalSize = reader.ReadUInt16();
            long table = pe + 24L + optionalSize;
            for (int i = 0; i < sections; i++)
            {
                stream.Position = table + i * 40L + 8;
                uint virtualSize = reader.ReadUInt32();
                uint virtualAddress = reader.ReadUInt32();
                uint rawSize = reader.ReadUInt32();
                uint rawAddress = reader.ReadUInt32();
                uint span = Math.Max(virtualSize, rawSize);
                if ((uint)rva >= virtualAddress && (uint)rva < virtualAddress + span)
                {
                    stream.Position = rawAddress + ((uint)rva - virtualAddress);
                    return reader.ReadBytes(count);
                }
            }
        }
        throw new InvalidDataException("RVA is outside the PE sections: 0x" + rva.ToString("X8"));
    }

    private static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
