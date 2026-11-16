using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Imp2Net.Graphics
{
    // Native 1.03 cache metadata stays in its original 110-entry object layout.
    // Every miss still runs native eviction at 0x55D3EE..0x55D454, including
    // invalidating the evicted tile's +0x13 backlink. No managed callback runs
    // inside the tile loop. Install/remove only while the game thread is idle.
    public sealed class NativeMapCache : IDisposable
    {
        internal const int Capacity = 110;
        internal const int ValidateAddress = 0x0055D3A4;
        internal const int AllocateAddress = 0x0055D3E3;
        internal static readonly int[] ConsumerAddresses = { 0x00559B0D, 0x0055B2A9, 0x0055B38D };
        internal static readonly byte[][] ConsumerSignatures = {
            new byte[] { 0x66, 0x0F, 0xBE, 0x4C, 0xCE, 0x13 },
            new byte[] { 0x66, 0x0F, 0xBE, 0x4C, 0xCA, 0x13 },
            new byte[] { 0x66, 0x0F, 0xBE, 0x4C, 0xCA, 0x13 }
        };
        internal static readonly byte[] ValidateSignature = { 0x80, 0x7C, 0x29, 0x13, 0xFF };
        internal static readonly byte[] AllocateSignature = { 0x0F, 0xBF, 0xC2, 0x66, 0x8B, 0x9C, 0xC6, 0x8A, 0, 0, 0 };
        private readonly IntPtr validateSite, allocateSite;
        private IntPtr executable;
        private bool validationPatched, allocationPatched;
        private IntPtr[] consumers = new IntPtr[0];
        private bool[] consumerPatched = new bool[0];
        private static NativeMapCache installed;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint oldProtect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private NativeMapCache(IntPtr validate, IntPtr allocate)
        { validateSite = validate; allocateSite = allocate; }

        public static void ValidateProfile()
        {
            if (IntPtr.Size != 4) throw new PlatformNotSupportedException("Native map hooks require an x86 process.");
            RequireSignature(new IntPtr(ValidateAddress), ValidateSignature);
            RequireSignature(new IntPtr(AllocateAddress), AllocateSignature);
            for (int i = 0; i < ConsumerAddresses.Length; ++i)
                RequireSignature(new IntPtr(ConsumerAddresses[i]), ConsumerSignatures[i]);
        }

        public static NativeMapCache Install()
        {
            if (installed != null) return installed;
            ValidateProfile();
            IntPtr[] consumers = new IntPtr[ConsumerAddresses.Length];
            for (int i = 0; i < consumers.Length; ++i) consumers[i] = new IntPtr(ConsumerAddresses[i]);
            installed = InstallAt(new IntPtr(ValidateAddress), new IntPtr(AllocateAddress), consumers);
            return installed;
        }

        // Alternate sites let the x86 fixture execute the actual emitted code
        // and verify transactional signature refusal without patching the game.
        internal static NativeMapCache InstallAt(IntPtr validate, IntPtr allocate, params IntPtr[] consumers)
        {
            if (IntPtr.Size != 4) throw new PlatformNotSupportedException("Native map hooks require an x86 process.");
            RequireSignature(validate, ValidateSignature);
            RequireSignature(allocate, AllocateSignature);
            if (consumers.Length != 0 && consumers.Length != ConsumerSignatures.Length)
                throw new ArgumentException("Provide all three native consumer sites.");
            for (int i = 0; i < consumers.Length; ++i) RequireSignature(consumers[i], ConsumerSignatures[i]);
            NativeMapCache hook = new NativeMapCache(validate, allocate);
            hook.consumers = (IntPtr[])consumers.Clone();
            hook.consumerPatched = new bool[consumers.Length];
            try
            {
                hook.executable = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
                if (hook.executable == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                hook.counterMemory = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
                if (hook.counterMemory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                byte[] validation = BuildValidationStub(hook.executable.ToInt32(), IntPtr.Add(validate, ValidateSignature.Length).ToInt32());
                byte[] allocation = BuildAllocatorStub(IntPtr.Add(hook.executable, 128).ToInt32(), hook.counterMemory.ToInt32(), IntPtr.Add(allocate, AllocateSignature.Length).ToInt32());
                Marshal.Copy(validation, 0, hook.executable, validation.Length);
                Marshal.Copy(allocation, 0, IntPtr.Add(hook.executable, 128), allocation.Length);
                for (int i = 0; i < consumers.Length; ++i)
                {
                    IntPtr stub = IntPtr.Add(hook.executable, 256 + i * 128);
                    byte[] code = BuildConsumerStub(stub.ToInt32(), ConsumerSignatures[i], IntPtr.Add(consumers[i], ConsumerSignatures[i].Length).ToInt32());
                    Marshal.Copy(code, 0, stub, code.Length);
                }
                uint previous;
                if (!VirtualProtect(hook.executable, (UIntPtr)4096, 0x20, out previous)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!FlushInstructionCache(GetCurrentProcess(), hook.executable, (UIntPtr)4096)) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Set rollback flags before the write: changing protection or
                // flushing can fail after the bytes have already been copied.
                hook.validationPatched = true;
                WriteProtected(validate, Jump(validate.ToInt32(), hook.executable.ToInt32(), ValidateSignature.Length));
                hook.allocationPatched = true;
                WriteProtected(allocate, Jump(allocate.ToInt32(), IntPtr.Add(hook.executable, 128).ToInt32(), AllocateSignature.Length));
                for (int i = 0; i < consumers.Length; ++i)
                {
                    hook.consumerPatched[i] = true;
                    WriteProtected(consumers[i], Jump(consumers[i].ToInt32(), IntPtr.Add(hook.executable, 256 + i * 128).ToInt32(), ConsumerSignatures[i].Length));
                }
                return hook;
            }
            catch
            {
                hook.Dispose();
                throw;
            }
        }

        private IntPtr counterMemory;

        // At native 0x55D3A4, ECX is the tile-array base, EBP is tile*40,
        // EDI is the current tile ID, ESI is this map object. Tile backlinks are
        // shared by old/new map views, so a byte in range alone is insufficient.
        // On return the original CMP executes, preserving native branch behavior.
        internal static byte[] BuildValidationStub(int address, int continuation)
        {
            List<byte> b = new List<byte>();
            b.AddRange(new byte[] { 0x50, 0x52 }); // push eax; push edx
            b.AddRange(new byte[] { 0x0F, 0xBE, 0x44, 0x29, 0x13 }); // movsx eax,byte [ecx+ebp+13]
            b.AddRange(new byte[] { 0x83, 0xF8, Capacity }); // cmp eax,110 (unsigned rejects negative slots)
            b.AddRange(new byte[] { 0x73, 0 }); int invalidRange = b.Count - 1;
            b.AddRange(new byte[] { 0x0F, 0xBF, 0x94, 0xC6, 0x8A, 0, 0, 0 }); // movsx edx,word [esi+eax*8+8a]
            b.AddRange(new byte[] { 0x66, 0x3B, 0xD7 }); // cmp dx,di
            b.AddRange(new byte[] { 0x74, 0 }); int validOwner = b.Count - 1;
            int invalidate = b.Count;
            b.AddRange(new byte[] { 0xC6, 0x44, 0x29, 0x13, 0xFF }); // invalidate stale backlink
            int complete = b.Count;
            b.AddRange(new byte[] { 0x5A, 0x58 }); // pop edx; pop eax
            b.AddRange(ValidateSignature);
            b[invalidRange] = checked((byte)(invalidate - invalidRange - 1));
            b[validOwner] = checked((byte)(complete - validOwner - 1));
            AppendJump(b, address, continuation);
            return b.ToArray();
        }

        // Preserve native search results 0..109. If every protected entry was
        // skipped, recycle a bounded slot. Native code at +11 performs both
        // eviction/backlink clearing and allocation; EDx must match EAx here.
        internal static byte[] BuildAllocatorStub(int address, int counter, int continuation)
        {
            List<byte> b = new List<byte>();
            b.AddRange(new byte[] { 0x83, 0xFA, Capacity, 0x72, 0 }); int valid = b.Count - 1;
            b.Add(0x51); // push ecx
            b.AddRange(new byte[] { 0x8B, 0x15 }); b.AddRange(BitConverter.GetBytes(counter));
            b.AddRange(new byte[] { 0x83, 0xFA, Capacity, 0x72, 0x02, 0x33, 0xD2 }); // defend corrupt counter
            b.AddRange(new byte[] { 0x8B, 0xCA, 0x41, 0x83, 0xF9, Capacity, 0x72, 0x02, 0x33, 0xC9 });
            b.AddRange(new byte[] { 0x89, 0x0D }); b.AddRange(BitConverter.GetBytes(counter));
            b.Add(0x59); // pop ecx
            b[valid] = checked((byte)(b.Count - valid - 1));
            b.AddRange(AllocateSignature);
            AppendJump(b, address, continuation);
            return b.ToArray();
        }

        // Selection/background restoration and its six neighboring tiles load
        // CX from a shared tile backlink. EDI is this map, AX is that tile ID.
        // Refuse both out-of-range slots and slots owned by another tile/view.
        // Native CMP CX,-1 immediately follows and skips unavailable terrain.
        internal static byte[] BuildConsumerStub(int address, byte[] load, int continuation)
        {
            List<byte> b = new List<byte>(load);
            b.AddRange(new byte[] { 0x50, 0x52, 0x0F, 0xBF, 0xC1, 0x83, 0xF8, Capacity, 0x73, 0 });
            int badRange = b.Count - 1;
            b.AddRange(new byte[] { 0x0F, 0xBF, 0x94, 0xC7, 0x8A, 0, 0, 0 }); // owner [edi+eax*8+8a]
            b.AddRange(new byte[] { 0x66, 0x3B, 0x54, 0x24, 0x04, 0x74, 0 }); // cmp dx,saved ax
            int goodOwner = b.Count - 1;
            int invalid = b.Count;
            b.AddRange(new byte[] { 0x66, 0xB9, 0xFF, 0xFF });
            int complete = b.Count;
            b.AddRange(new byte[] { 0x5A, 0x58 });
            b[badRange] = checked((byte)(invalid - badRange - 1));
            b[goodOwner] = checked((byte)(complete - goodOwner - 1));
            AppendJump(b, address, continuation);
            return b.ToArray();
        }

        private static void AppendJump(List<byte> bytes, int address, int target)
        {
            int displacement = unchecked(target - (address + bytes.Count + 5));
            bytes.Add(0xE9); bytes.AddRange(BitConverter.GetBytes(displacement));
        }
        private static byte[] Jump(int address, int target, int length)
        {
            List<byte> b = new List<byte>(); AppendJump(b, address, target);
            while (b.Count < length) b.Add(0x90);
            return b.ToArray();
        }
        private static void RequireSignature(IntPtr address, byte[] expected)
        {
            byte[] actual = new byte[expected.Length]; Marshal.Copy(address, actual, 0, actual.Length);
            for (int i = 0; i < actual.Length; ++i)
                if (actual[i] != expected[i]) throw new InvalidOperationException("Unsupported native cache hook at 0x" + address.ToInt32().ToString("X8") + ".");
        }
        private static void WriteProtected(IntPtr address, byte[] bytes)
        {
            uint previous;
            if (!VirtualProtect(address, (UIntPtr)bytes.Length, 0x40, out previous)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, address, bytes.Length); }
            finally
            {
                uint ignored;
                if (!VirtualProtect(address, (UIntPtr)bytes.Length, previous, out ignored)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)bytes.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        public void Dispose()
        {
            for (int i = consumers.Length - 1; i >= 0; --i)
                if (consumerPatched[i]) { WriteProtected(consumers[i], ConsumerSignatures[i]); consumerPatched[i] = false; }
            if (allocationPatched) { WriteProtected(allocateSite, AllocateSignature); allocationPatched = false; }
            if (validationPatched) { WriteProtected(validateSite, ValidateSignature); validationPatched = false; }
            if (executable != IntPtr.Zero) { VirtualFree(executable, UIntPtr.Zero, 0x8000); executable = IntPtr.Zero; }
            if (counterMemory != IntPtr.Zero) { VirtualFree(counterMemory, UIntPtr.Zero, 0x8000); counterMemory = IntPtr.Zero; }
            if (Object.ReferenceEquals(installed, this)) installed = null;
        }
    }
}
