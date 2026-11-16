using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Imp2Net.Graphics;

internal static class TestNativeMapCache
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ValidateCall(IntPtr map, IntPtr tiles, int tile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AllocateCall(IntPtr map, int candidate, int tile);
    private static int checks;
    private static void Check(bool condition, string message)
    { ++checks; if (!condition) throw new Exception(message); }
    private static IntPtr Block(int count)
    {
        IntPtr p = VirtualAlloc(IntPtr.Zero, (UIntPtr)count, 0x3000, 0x40);
        if (p == IntPtr.Zero) throw new Exception("Fixture VirtualAlloc failed.");
        return p;
    }
    private static void Copy(IntPtr to, byte[] bytes) { Marshal.Copy(bytes, 0, to, bytes.Length); }
    private static void Call(List<byte> b, int address, int target)
    { b.Add(0xE8); b.AddRange(BitConverter.GetBytes(unchecked(target - (address + b.Count + 4)))); }

    // Read the exact native eviction/assignment instructions used by the game.
    // Only the two absolute world-global operands are relocated to fixture data.
    // This executes the real owner invalidation path after the emitted stub.
    private static byte[] NativeEviction(string path, int fixtureWorldGlobal)
    {
        byte[] file = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(file, 0x3C);
        int sections = BitConverter.ToUInt16(file, pe + 6);
        int section = pe + 24 + BitConverter.ToUInt16(file, pe + 20);
        int rva = 0x0055D3EE - 0x00400000;
        int offset = -1;
        for (int i = 0; i < sections; ++i, section += 40)
        {
            int va = BitConverter.ToInt32(file, section + 12), raw = BitConverter.ToInt32(file, section + 16);
            if (rva >= va && rva + 87 <= va + raw) offset = BitConverter.ToInt32(file, section + 20) + rva - va;
        }
        if (offset < 0) throw new Exception("Native fixture code outside raw PE sections.");
        byte[] result = new byte[88]; Buffer.BlockCopy(file, offset, result, 0, 87); result[87] = 0xC3;
        Check(result[0] == 0x8D && result[1] == 0x8C && result[2] == 0xC6, "Eviction starts at native LEA.");
        int first = 0x0055D3FC - 0x0055D3EE, second = 0x0055D438 - 0x0055D3EE;
        Check(BitConverter.ToInt32(result, first) == 0x007606F4 && BitConverter.ToInt32(result, second) == 0x007606F4, "Native world-pointer operands match supported executable.");
        Buffer.BlockCopy(BitConverter.GetBytes(fixtureWorldGlobal), 0, result, first, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(fixtureWorldGlobal), 0, result, second, 4);
        return result;
    }

    private static int Main(string[] args)
    {
        if (IntPtr.Size != 4) throw new Exception("Compile /platform:x86 to execute native stubs.");
        string exe = args.Length == 0 ? Path.GetFullPath("ImpII.exe") : args[0];
        IntPtr memory = Block(4096), map = Block(4096), tiles = Block(6480 * 40), world = Block(4096);
        NativeMapCache installed = null;
        try
        {
            IntPtr validator = memory, allocator = IntPtr.Add(memory, 128), validateWrapper = IntPtr.Add(memory, 512), allocateWrapper = IntPtr.Add(memory, 640);
            IntPtr[] consumerSites = { IntPtr.Add(memory, 256), IntPtr.Add(memory, 320), IntPtr.Add(memory, 384) };
            ValidateCall[] consumerCalls = new ValidateCall[consumerSites.Length];
            Marshal.WriteInt32(world, 0x10, tiles.ToInt32()); Marshal.WriteInt32(world, 0x100, world.ToInt32());
            Copy(validator, NativeMapCache.ValidateSignature);
            Copy(IntPtr.Add(validator, NativeMapCache.ValidateSignature.Length), new byte[] { 0x0F, 0x94, 0xC0, 0x0F, 0xB6, 0xC0, 0xC3 });
            Copy(allocator, NativeMapCache.AllocateSignature);
            Copy(IntPtr.Add(allocator, NativeMapCache.AllocateSignature.Length), NativeEviction(exe, IntPtr.Add(world, 0x100).ToInt32()));
            for (int i = 0; i < consumerSites.Length; ++i)
            {
                Copy(consumerSites[i], NativeMapCache.ConsumerSignatures[i]);
                Copy(IntPtr.Add(consumerSites[i], NativeMapCache.ConsumerSignatures[i].Length), new byte[] {
                    0x66, 0x83, 0xF9, 0xFF, 0x0F, 0x94, 0xC0, 0x0F, 0xB6, 0xC0, 0xC3 });
                IntPtr wrapper = IntPtr.Add(memory, 800 + i * 128);
                List<byte> c = new List<byte>(new byte[] {
                    0x55, 0x53, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x14,
                    0x8B, 0x54, 0x24, 0x18, 0x8B, 0x44, 0x24, 0x1C,
                    0x8D, 0x0C, 0x80, 0x8B, 0xF2 });
                Call(c, wrapper.ToInt32(), consumerSites[i].ToInt32());
                c.AddRange(new byte[] { 0x5F, 0x5E, 0x5B, 0x5D, 0xC3 }); Copy(wrapper, c.ToArray());
                consumerCalls[i] = (ValidateCall)Marshal.GetDelegateForFunctionPointer(wrapper, typeof(ValidateCall));
            }
            // Preserve the x86 cdecl callee-saved registers around native snippets.
            List<byte> v = new List<byte>(new byte[] {
                0x55, 0x53, 0x56, 0x57, 0x8B, 0x74, 0x24, 0x14, // esi=map
                0x8B, 0x4C, 0x24, 0x18, 0x8B, 0x7C, 0x24, 0x1C, // ecx=tiles,edi=tile
                0x6B, 0xEF, 0x28 }); // ebp=tile*40
            Call(v, validateWrapper.ToInt32(), validator.ToInt32());
            v.AddRange(new byte[] { 0x5F, 0x5E, 0x5B, 0x5D, 0xC3 }); Copy(validateWrapper, v.ToArray());
            List<byte> a = new List<byte>(new byte[] {
                0x55, 0x53, 0x56, 0x57, 0x8B, 0x74, 0x24, 0x14,
                0x8B, 0x54, 0x24, 0x18, 0x8B, 0x7C, 0x24, 0x1C,
                0x6B, 0xEF, 0x28, 0x83, 0xEC, 0x40,
                0x89, 0x6C, 0x24, 0x0C, // native [esp+10]=tileOffset after CALL
                0x66, 0xC7, 0x44, 0x24, 0x10, 0x05, 0x00,
                0x66, 0xC7, 0x44, 0x24, 0x28, 0x06, 0x00 });
            Call(a, allocateWrapper.ToInt32(), allocator.ToInt32());
            a.AddRange(new byte[] { 0x83, 0xC4, 0x40, 0x5F, 0x5E, 0x5B, 0x5D, 0xC3 }); Copy(allocateWrapper, a.ToArray());
            ValidateCall validate = (ValidateCall)Marshal.GetDelegateForFunctionPointer(validateWrapper, typeof(ValidateCall));
            AllocateCall allocate = (AllocateCall)Marshal.GetDelegateForFunctionPointer(allocateWrapper, typeof(AllocateCall));
            installed = NativeMapCache.InstallAt(validator, allocator, consumerSites);

            const int tile = 5000;
            for (int slot = 0; slot < 110; ++slot)
            {
                Marshal.WriteByte(tiles, tile * 40 + 0x13, (byte)slot);
                Marshal.WriteInt16(map, 0x8A + slot * 8, (short)tile);
                Check(validate(map, tiles, tile) == 0, "Owned slot is a native cache hit: " + slot);
                Marshal.WriteInt16(map, 0x8A + slot * 8, (short)(tile - 1));
                Check(validate(map, tiles, tile) == 1 && Marshal.ReadByte(tiles, tile * 40 + 0x13) == 255, "Stale old/new view owner becomes a miss: " + slot);
            }
            foreach (byte invalid in new byte[] { 110, 127, 128, 254, 255 })
            {
                Marshal.WriteByte(tiles, tile * 40 + 0x13, invalid);
                Check(validate(map, tiles, tile) == 1 && Marshal.ReadByte(tiles, tile * 40 + 0x13) == 255, "Invalid signed slot refused: " + invalid);
            }
            foreach (ValidateCall consumer in consumerCalls)
            {
                for (int slot = 0; slot < 110; ++slot)
                {
                    Marshal.WriteByte(tiles, tile * 40 + 0x13, (byte)slot);
                    Marshal.WriteInt16(map, 0x8A + slot * 8, (short)tile);
                    Check(consumer(map, tiles, tile) == 0, "Selection/background retains an owned slot.");
                    Marshal.WriteInt16(map, 0x8A + slot * 8, (short)(tile - 1));
                    Check(consumer(map, tiles, tile) == 1, "Selection/background skips another view/tile's slot.");
                    Check(Marshal.ReadByte(tiles, tile * 40 + 0x13) == slot, "Consumer does not change shared tile backlinks.");
                }
                foreach (byte invalid in new byte[] { 110, 127, 128, 254, 255 })
                {
                    Marshal.WriteByte(tiles, tile * 40 + 0x13, invalid);
                    Check(consumer(map, tiles, tile) == 1, "Selection/background rejects an invalid signed slot.");
                }
            }
            // Known native search results keep their original slot. Execute the
            // actual game's owner eviction, metadata, and backlink instructions.
            Marshal.WriteInt32(map, 0x3F4, unchecked((int)0x12345678));
            Marshal.WriteInt32(map, 0x3F8, unchecked((int)0x7FABCDEF));
            for (int slot = 0; slot < 110; ++slot)
            {
                int oldTile = 100 + slot;
                Marshal.WriteInt16(map, 0x8A + slot * 8, (short)oldTile);
                Marshal.WriteByte(tiles, oldTile * 40 + 0x13, (byte)slot);
                allocate(map, slot, tile);
                Check(Marshal.ReadByte(tiles, tile * 40 + 0x13) == slot, "Native EDx backlink agrees with selected slot: " + slot);
                Check(Marshal.ReadByte(tiles, oldTile * 40 + 0x13) == 255, "Native eviction clears previous owner: " + slot);
                Check(Marshal.ReadInt16(map, 0x8A + slot * 8) == tile, "Native metadata assigns current owner: " + slot);
            }
            for (int i = 0; i < 110 * 6; ++i)
            {
                int candidate = i % 3 == 0 ? -1 : (i % 3 == 1 ? 110 : 6479);
                allocate(map, candidate, tile);
                int selected = Marshal.ReadByte(tiles, tile * 40 + 0x13);
                Check(selected == i % 110, "Bounded fallback wraps and keeps EDX/EAX consistent: " + i);
                Check(Marshal.ReadInt32(map, 0x3F4) == 0x12345678 && Marshal.ReadInt32(map, 0x3F8) == 0x7FABCDEF, "Cache pointer/adjacent members remain intact.");
                Check(Marshal.ReadInt16(map, 0x8A + selected * 8) == tile, "Selected slot has current owner.");
            }
            // A 60x40 tile view exceeds the physical cache many times. Execute
            // misses, eviction, hits and reverse traversal with distinct owners;
            // once copied to the map surface a tile need not remain resident.
            for (int slot = 0; slot < 110; ++slot) Marshal.WriteInt16(map, 0x8A + slot * 8, -1);
            for (int t = 0; t < 6480; ++t) Marshal.WriteByte(tiles, t * 40 + 0x13, 255);
            for (int pass = 0; pass < 3; ++pass)
            {
                for (int k = 0; k < 2400; ++k)
                {
                    int t = pass % 2 == 0 ? k : 2399 - k;
                    if (validate(map, tiles, t) != 0) allocate(map, 110, t);
                    int slot = Marshal.ReadByte(tiles, t * 40 + 0x13);
                    Check(slot < 110 && Marshal.ReadInt16(map, 0x8A + slot * 8) == t, "Large view retains exact current tile ownership.");
                    Check(validate(map, tiles, t) == 0, "Immediate tile copy sees a valid owned slot.");
                }
                int resident = 0;
                for (int t = 0; t < 2400; ++t)
                {
                    int slot = Marshal.ReadByte(tiles, t * 40 + 0x13);
                    if (slot == 255) continue;
                    ++resident;
                    Check(slot < 110 && Marshal.ReadInt16(map, 0x8A + slot * 8) == t, "Every resident backlink has an exact metadata owner.");
                }
                Check(resident == 110, "Large view retains only the 110 bounded physical slots.");
                Check(Marshal.ReadInt32(map, 0x3F4) == 0x12345678 && Marshal.ReadInt32(map, 0x3F8) == 0x7FABCDEF, "Large view preserves the cache-world pointer.");
            }
            installed.Dispose(); installed = null;
            for (int i = 0; i < NativeMapCache.ValidateSignature.Length; ++i) Check(Marshal.ReadByte(validator, i) == NativeMapCache.ValidateSignature[i], "Validation hook restored.");
            for (int i = 0; i < NativeMapCache.AllocateSignature.Length; ++i) Check(Marshal.ReadByte(allocator, i) == NativeMapCache.AllocateSignature[i], "Allocation hook restored.");
            for (int i = 0; i < consumerSites.Length; ++i)
                for (int j = 0; j < NativeMapCache.ConsumerSignatures[i].Length; ++j)
                    Check(Marshal.ReadByte(consumerSites[i], j) == NativeMapCache.ConsumerSignatures[i][j], "Selection/background consumer hook restored.");
            Marshal.WriteByte(allocator, 0, 0x90);
            bool refused = false;
            try { NativeMapCache.InstallAt(validator, allocator); } catch (InvalidOperationException) { refused = true; }
            Check(refused && Marshal.ReadByte(validator) == NativeMapCache.ValidateSignature[0], "All signatures are checked before any hook is published.");
            Copy(allocator, NativeMapCache.AllocateSignature);
            Marshal.WriteByte(consumerSites[2], 0, 0x90); refused = false;
            try { NativeMapCache.InstallAt(validator, allocator, consumerSites); } catch (InvalidOperationException) { refused = true; }
            Check(refused && Marshal.ReadByte(validator) == NativeMapCache.ValidateSignature[0] && Marshal.ReadByte(allocator) == NativeMapCache.AllocateSignature[0], "Consumer signatures are checked before allocator/validation publication.");
            Console.WriteLine("PASS: " + checks + " native x86 cache checks, emitted stubs plus original game eviction instructions. Gameplay not exercised.");
            return 0;
        }
        finally
        {
            if (installed != null) installed.Dispose();
            foreach (IntPtr p in new IntPtr[] { memory, map, tiles, world }) VirtualFree(p, UIntPtr.Zero, 0x8000);
        }
    }
}
