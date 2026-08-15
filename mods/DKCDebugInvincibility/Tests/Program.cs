using System;
using System.Collections.Generic;
using DKCDebugInvincibility;

internal static class Program
{
    private static int Main()
    {
        try
        {
            SignatureValidation();
            LeaseLifecycle();
            ConflictsAreNotClobbered();
            DictionaryReplacementIsReversible();
            Console.WriteLine("DKCDebugInvincibility offline tests passed (4/4).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void SignatureValidation()
    {
        var rom = new byte[DkcInvincibilityPatch.HiRomOffset + DkcInvincibilityPatch.ExpectedBytes.Length];
        Array.Copy(DkcInvincibilityPatch.ExpectedBytes, 0, rom, DkcInvincibilityPatch.HiRomOffset,
            DkcInvincibilityPatch.ExpectedBytes.Length);
        string reason;
        Assert(DkcInvincibilityPatch.ValidateRom(rom, out reason), "Known signature must validate.");
        rom[DkcInvincibilityPatch.HiRomOffset + 3] ^= 1;
        Assert(!DkcInvincibilityPatch.ValidateRom(rom, out reason), "Modified signature must be rejected.");
        Assert(!DkcInvincibilityPatch.ValidateRom(null, out reason), "Null ROM must be rejected.");
    }

    private static void LeaseLifecycle()
    {
        var lease = new CheatLease();
        var dictionary = new Dictionary<int, byte>();
        string result;
        Assert(lease.Apply(dictionary, out result), "Lease should apply to an empty dictionary.");
        Assert(dictionary[DkcInvincibilityPatch.Address] == 0x60, "Lease should install RTS.");
        Assert(lease.Applied, "Lease should report applied.");
        Assert(lease.Release(), "Owned entry should be removed.");
        Assert(!dictionary.ContainsKey(DkcInvincibilityPatch.Address), "Release should restore absence.");
    }

    private static void ConflictsAreNotClobbered()
    {
        foreach (var value in new byte[] { 0x60, 0xEA })
        {
            var lease = new CheatLease();
            var dictionary = new Dictionary<int, byte> { { DkcInvincibilityPatch.Address, value } };
            string result;
            Assert(!lease.Apply(dictionary, out result), "Pre-existing override must be a conflict.");
            Assert(!lease.Release(), "Unowned override must not be removed.");
            Assert(dictionary[DkcInvincibilityPatch.Address] == value, "Conflict value must survive.");
        }
    }

    private static void DictionaryReplacementIsReversible()
    {
        var lease = new CheatLease();
        var first = new Dictionary<int, byte>();
        var second = new Dictionary<int, byte>();
        string result;
        Assert(lease.Apply(first, out result), "First dictionary apply failed.");
        Assert(lease.Apply(second, out result), "Replacement dictionary apply failed.");
        Assert(!first.ContainsKey(DkcInvincibilityPatch.Address), "Old dictionary should be restored.");
        second[DkcInvincibilityPatch.Address] = 0xEA;
        Assert(!lease.Release(), "Externally changed owned slot must not be removed.");
        Assert(second[DkcInvincibilityPatch.Address] == 0xEA, "Changed value must survive release.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
