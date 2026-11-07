using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class TestMultiplayer
{
    private const int DP_OK = 0;
    private const int DPERR_BUFFERTOOSMALL = unchecked((int)0x8877001E);
    private const int DPERR_NOMESSAGES = unchecked((int)0x887700BE);

    private static readonly Guid CLSID_DirectPlay = new Guid("d1eb6d20-8923-11d0-9d97-00a0c90a43cb");
    private static readonly Guid IID_IDirectPlay4A = new Guid("0ab1c531-4745-11d1-a7a1-0000f803abfc");
    private static readonly Guid CLSID_DirectPlayLobby = new Guid("2fe8f810-b2a5-11d0-a787-0000f803abfc");
    private static readonly Guid IID_IDirectPlayLobby3A = new Guid("2db72491-652c-11d1-a7a8-0000f803abfc");
    private static readonly Guid DPSPGUID_TCPIP = new Guid("36e95ee0-8577-11cf-960c-0080c7534e82");
    private static readonly Guid DPAID_ServiceProvider = new Guid("07d916c0-e0af-11cf-9c4e-00a0c905425e");
    private static readonly Guid DPAID_INet = new Guid("c4a54da0-e0af-11cf-9c4e-00a0c905425e");
    private static readonly Guid IMP2_APP_GUID = new Guid("55667788-1122-3344-99aa-bbccddeeff00");

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibraryA(string path);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hMod, IntPtr ordinal);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hMod, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DPEnumA_Del(IntPtr cb, IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DPEnumCb(IntPtr guidSp, IntPtr spName, uint maj, uint min, IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CoCreate_Del(ref Guid rclsid, IntPtr punk, uint ctx, ref Guid riid, out IntPtr ppv);

    // Raw COM vtable delegates matching Imperialism II.exe exact byte offsets
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release_Del(IntPtr self); // +0x08
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Close_Del(IntPtr self); // +0x10
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreatePlayer_Del(IntPtr self, out uint id, IntPtr dpName, IntPtr hEv, IntPtr data, uint size, uint flags); // +0x18
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DestroyPlayer_Del(IntPtr self, uint id); // +0x24
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumPlayers_Del(IntPtr self, IntPtr guidInst, IntPtr cb, IntPtr ctx, uint flags); // +0x30
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumPlayersCb(uint dpId, uint playerType, IntPtr dpName, uint flags, IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumSessions_Del(IntPtr self, IntPtr lpsd, uint timeout, IntPtr cb, IntPtr ctx, uint flags); // +0x34
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumSessionsCb(IntPtr lpsd, ref uint timeout, uint flags, IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPlayerAddress_Del(IntPtr self, uint id, IntPtr addr, ref int size); // +0x48
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPlayerData_Del(IntPtr self, uint id, IntPtr data, ref int size, uint flags); // +0x50
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionDesc_Del(IntPtr self, IntPtr data, ref int size); // +0x58
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Open_Del(IntPtr self, IntPtr lpsd, uint flags); // +0x60
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Receive_Del(IntPtr self, out uint idFrom, out uint idTo, uint flags, IntPtr data, ref int size); // +0x64
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Send_Del(IntPtr self, uint idFrom, uint idTo, uint flags, IntPtr data, uint size); // +0x68
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CanStart_Del();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LoadWorld_Del(int saveId, IntPtr prefix);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int StartGame_Del(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPlayerData_Del(IntPtr self, uint id, IntPtr data, uint size, uint flags); // +0x74
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSessionDesc_Del(IntPtr self, IntPtr lpsd, uint flags); // +0x7C
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitConn_Del(IntPtr self, IntPtr conn, uint flags); // +0x98

    // Lobby vtable delegates
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAddress_Del(IntPtr self, IntPtr cb, IntPtr addr, uint size, IntPtr ctx); // +0x14
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumAddrCdeclCb(IntPtr guidType, uint dataSize, IntPtr data, IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateCompoundAddr_Del(IntPtr self, IntPtr elems, uint count, IntPtr addr, ref int size); // +0x38

    // Native x86 trampoline arena so every COM vtable call & C++ callback goes through genuine x86 machine code
    private static IntPtr s_trampArena;
    private static int s_trampPos;
    private static readonly Dictionary<IntPtr, IntPtr> s_trampCache = new Dictionary<IntPtr, IntPtr>();
    private static readonly List<Delegate> s_pinnedCallbacks = new List<Delegate>();

    private static IntPtr NativeWrap(IntPtr target)
    {
        IntPtr cached;
        if (s_trampCache.TryGetValue(target, out cached)) return cached;
        if (s_trampArena == IntPtr.Zero)
            s_trampArena = VirtualAlloc(IntPtr.Zero, new UIntPtr(65536), 0x3000, 0x40);
        IntPtr slot = new IntPtr(s_trampArena.ToInt64() + s_trampPos);
        s_trampPos += 8;
        // mov eax, <target>; jmp eax (7 bytes)
        Marshal.WriteByte(slot, 0, 0xB8);
        Marshal.WriteInt32(slot, 1, target.ToInt32());
        Marshal.WriteByte(slot, 5, 0xFF);
        Marshal.WriteByte(slot, 6, 0xE0);
        s_trampCache[target] = slot;
        return slot;
    }

    private static IntPtr NativeCb(Delegate cb)
    {
        s_pinnedCallbacks.Add(cb);
        return NativeWrap(Marshal.GetFunctionPointerForDelegate(cb));
    }

    private static T Vt<T>(IntPtr comObj, int byteOffset) where T : class
    {
        IntPtr vt = Marshal.ReadIntPtr(comObj);
        IntPtr fn = Marshal.ReadIntPtr(vt, byteOffset);
        return Marshal.GetDelegateForFunctionPointer(NativeWrap(fn), typeof(T)) as T;
    }

    private static void Check(bool cond, string msg)
    {
        if (!cond) throw new Exception("ASSERT FAILED: " + msg);
    }

    private static byte[] MakeCompoundAddress(IntPtr lobby, string inetStr)
    {
        int count = inetStr == null ? 1 : 2;
        IntPtr elems = Marshal.AllocHGlobal(count * 24);
        IntPtr spMem = Marshal.AllocHGlobal(16);
        IntPtr inetMem = inetStr != null ? Marshal.StringToHGlobalAnsi(inetStr) : IntPtr.Zero;
        try
        {
            Marshal.Copy(DPAID_ServiceProvider.ToByteArray(), 0, elems, 16);
            Marshal.WriteInt32(elems, 16, 16);
            Marshal.Copy(DPSPGUID_TCPIP.ToByteArray(), 0, spMem, 16);
            Marshal.WriteIntPtr(elems, 20, spMem);

            if (inetStr != null)
            {
                IntPtr e2 = new IntPtr(elems.ToInt64() + 24);
                Marshal.Copy(DPAID_INet.ToByteArray(), 0, e2, 16);
                Marshal.WriteInt32(e2, 16, inetStr.Length + 1);
                Marshal.WriteIntPtr(e2, 20, inetMem);
            }

            CreateCompoundAddr_Del fn = Vt<CreateCompoundAddr_Del>(lobby, 0x38);
            int sz = 0;
            int hr1 = fn(lobby, elems, (uint)count, IntPtr.Zero, ref sz);
            Check(hr1 == DPERR_BUFFERTOOSMALL && sz > 24, "CreateCompoundAddress size pass");

            IntPtr outBuf = Marshal.AllocHGlobal(sz);
            try
            {
                int hr2 = fn(lobby, elems, (uint)count, outBuf, ref sz);
                Check(hr2 == DP_OK, "CreateCompoundAddress write pass");
                byte[] res = new byte[sz];
                Marshal.Copy(outBuf, res, 0, sz);
                return res;
            }
            finally { Marshal.FreeHGlobal(outBuf); }
        }
        finally
        {
            if (inetMem != IntPtr.Zero) Marshal.FreeHGlobal(inetMem);
            Marshal.FreeHGlobal(spMem);
            Marshal.FreeHGlobal(elems);
        }
    }

    private static void InitConn(IntPtr dp, IntPtr lobby, string inet)
    {
        byte[] ca = MakeCompoundAddress(lobby, inet);
        IntPtr buf = Marshal.AllocHGlobal(ca.Length);
        try
        {
            Marshal.Copy(ca, 0, buf, ca.Length);
            int hr = Vt<InitConn_Del>(dp, 0x98)(dp, buf, 0);
            Check(hr == DP_OK, "InitializeConnection(" + (inet ?? "<host>") + ")");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static Guid HostOpen(IntPtr dp, string sessionName)
    {
        IntPtr sd = Marshal.AllocHGlobal(80);
        IntPtr namePtr = Marshal.StringToHGlobalAnsi(sessionName);
        try
        {
            for (int i = 0; i < 80; i += 4) Marshal.WriteInt32(sd, i, 0);
            Marshal.WriteInt32(sd, 0x00, 80);
            Marshal.WriteInt32(sd, 0x04, 0xA040);
            Marshal.Copy(IMP2_APP_GUID.ToByteArray(), 0, new IntPtr(sd.ToInt64() + 0x18), 16);
            Marshal.WriteInt32(sd, 0x28, 6);
            Marshal.WriteIntPtr(sd, 0x30, namePtr);

            int hr = Vt<Open_Del>(dp, 0x60)(dp, sd, 2); // DPOPEN_CREATE
            Check(hr == DP_OK, "Host Open(DPOPEN_CREATE)");

            byte[] instBytes = new byte[16];
            Marshal.Copy(new IntPtr(sd.ToInt64() + 0x08), instBytes, 0, 16);
            Guid g = new Guid(instBytes);
            Check(g != Guid.Empty, "Host guidInstance populated");
            return g;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(sd);
        }
    }

    private static List<KeyValuePair<Guid, string>> EnumGames(IntPtr dp, uint timeoutMs)
    {
        List<KeyValuePair<Guid, string>> list = new List<KeyValuePair<Guid, string>>();
        IntPtr sd = Marshal.AllocHGlobal(80);
        try
        {
            for (int i = 0; i < 80; i += 4) Marshal.WriteInt32(sd, i, 0);
            Marshal.WriteInt32(sd, 0x00, 80);
            Marshal.Copy(IMP2_APP_GUID.ToByteArray(), 0, new IntPtr(sd.ToInt64() + 0x18), 16);

            EnumSessionsCb cb = delegate(IntPtr foundSd, ref uint to, uint flags, IntPtr ctx)
            {
                byte[] gb = new byte[16];
                Marshal.Copy(new IntPtr(foundSd.ToInt64() + 0x08), gb, 0, 16);
                IntPtr np = Marshal.ReadIntPtr(foundSd, 0x30);
                string nm = Marshal.PtrToStringAnsi(np);
                list.Add(new KeyValuePair<Guid, string>(new Guid(gb), nm));
                return 1;
            };
            int hr = Vt<EnumSessions_Del>(dp, 0x34)(dp, sd, timeoutMs, NativeCb(cb), IntPtr.Zero, 1);
            Check(hr == DP_OK, "EnumSessions");
        }
        finally { Marshal.FreeHGlobal(sd); }
        return list;
    }

    private static void JoinOpen(IntPtr dp, Guid guidInstance)
    {
        IntPtr sd = Marshal.AllocHGlobal(80);
        try
        {
            for (int i = 0; i < 80; i += 4) Marshal.WriteInt32(sd, i, 0);
            Marshal.WriteInt32(sd, 0x00, 80);
            Marshal.Copy(guidInstance.ToByteArray(), 0, new IntPtr(sd.ToInt64() + 0x08), 16);
            int hr = Vt<Open_Del>(dp, 0x60)(dp, sd, 1); // DPOPEN_JOIN
            Check(hr == DP_OK, "Client Open(DPOPEN_JOIN)");
        }
        finally { Marshal.FreeHGlobal(sd); }
    }

    private static uint MakePlayer(IntPtr dp, string name)
    {
        IntPtr dpn = Marshal.AllocHGlobal(16);
        IntPtr np = Marshal.StringToHGlobalAnsi(name);
        try
        {
            Marshal.WriteInt32(dpn, 0, 16);
            Marshal.WriteInt32(dpn, 4, 0);
            Marshal.WriteIntPtr(dpn, 8, np);
            Marshal.WriteIntPtr(dpn, 12, IntPtr.Zero);
            uint pid;
            int hr = Vt<CreatePlayer_Del>(dp, 0x18)(dp, out pid, dpn, IntPtr.Zero, IntPtr.Zero, 0, 0);
            Check(hr == DP_OK && pid != 0, "CreatePlayer(" + name + ")");
            return pid;
        }
        finally
        {
            Marshal.FreeHGlobal(np);
            Marshal.FreeHGlobal(dpn);
        }
    }

    private static byte[] TwoPassReceive(IntPtr dp, out uint idFrom, out uint idTo)
    {
        Receive_Del fn = Vt<Receive_Del>(dp, 0x64);
        int sz = 0;
        int hr1 = fn(dp, out idFrom, out idTo, 1, IntPtr.Zero, ref sz);
        if (hr1 == DPERR_NOMESSAGES) return null;
        Check(hr1 == DPERR_BUFFERTOOSMALL, "Receive pass 1 expected DPERR_BUFFERTOOSMALL, got 0x" + hr1.ToString("X"));

        IntPtr buf = Marshal.AllocHGlobal(Math.Max(1, sz));
        try
        {
            int hr2 = fn(dp, out idFrom, out idTo, 1, buf, ref sz);
            Check(hr2 == DP_OK, "Receive pass 2 expected DP_OK");
            byte[] res = new byte[sz];
            if (sz > 0) Marshal.Copy(buf, res, 0, sz);
            return res;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static byte[] WaitReceive(IntPtr dp, out uint idFrom, out uint idTo, int maxWaitMs)
    {
        int waited = 0;
        while (waited < maxWaitMs)
        {
            byte[] m = TwoPassReceive(dp, out idFrom, out idTo);
            if (m != null) return m;
            Thread.Sleep(15);
            waited += 15;
        }
        idFrom = 0; idTo = 0;
        return null;
    }

    private static int RunClientWorker(string dllPath, string port)
    {
        Environment.SetEnvironmentVariable("IMP2_PORT", port);
        IntPtr hMod = LoadLibraryA(dllPath);
        Check(hMod != IntPtr.Zero, "Worker LoadLibraryA");
        CoCreate_Del coCreate = (CoCreate_Del)Marshal.GetDelegateForFunctionPointer(NativeWrap(GetProcAddress(hMod, "Imp2CoCreateInstance")), typeof(CoCreate_Del));
        Guid gDp = CLSID_DirectPlay, iDp = IID_IDirectPlay4A, gLob = CLSID_DirectPlayLobby, iLob = IID_IDirectPlayLobby3A;
        IntPtr dp, lob;
        Check(coCreate(ref gDp, IntPtr.Zero, 1, ref iDp, out dp) == DP_OK, "Worker dp");
        Check(coCreate(ref gLob, IntPtr.Zero, 1, ref iLob, out lob) == DP_OK, "Worker lob");

        InitConn(dp, lob, "127.0.0.1:" + port);
        var games = EnumGames(dp, 3000);
        Check(games.Count == 1, "Worker EnumGames found 1 session");
        JoinOpen(dp, games[0].Key);
        uint myPid = MakePlayer(dp, "Prussia");
        Check(myPid == 2, "Worker DPID == 2");

        byte[] hello = Encoding.ASCII.GetBytes("HELLO_FROM_CHILD_PROCESS");
        IntPtr pHello = Marshal.AllocHGlobal(hello.Length);
        Marshal.Copy(hello, 0, pHello, hello.Length);
        Check(Vt<Send_Del>(dp, 0x68)(dp, myPid, 1, 1, pHello, (uint)hello.Length) == DP_OK, "Worker Send");
        Marshal.FreeHGlobal(pHello);

        uint fm, to;
        byte[] reply = WaitReceive(dp, out fm, out to, 3000);
        Check(reply != null && fm == 1 && Encoding.ASCII.GetString(reply) == "WELCOME_FROM_HOST_PROCESS", "Worker received Host reply");

        Vt<Close_Del>(dp, 0x10)(dp);
        Vt<Release_Del>(dp, 0x08)(dp);
        Vt<Release_Del>(lob, 0x08)(lob);
        return 0;
    }

    private static int FallbackCoCreateStub(ref Guid rclsid, IntPtr punk, uint ctx, ref Guid riid, out IntPtr ppv)
    {
        ppv = new IntPtr(0x12345678);
        return 0x112233;
    }

    // Reflection supplies deterministic serializer/loader fixtures while exercising the real
    // COM/socket path. These tests do not claim to execute the game's native deserializer.
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static Type ShimType(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("Imp2Net." + name);
            if (type != null) return type;
        }
        throw new Exception("Shim type not loaded: " + name);
    }
    private static object Engine(IntPtr dp)
    {
        object com = ShimType("ComRuntime").GetMethod("GetDP", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { dp });
        return com.GetType().GetField("Engine").GetValue(com);
    }
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, AnyInstance).Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static byte[] WorldOptions(int saveId)
    {
        byte[] data = new byte[160];
        Buffer.BlockCopy(BitConverter.GetBytes(14), 0, data, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(160), 0, data, 12, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(0x74696D65), 0, data, 16, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(saveId), 0, data, 0x60, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(0x72616E64), 0, data, 0x64, 4);
        return data;
    }
    private static byte[] WorldBytes(int seed)
    {
        byte[] bytes = new byte[3 * 65536 + 113];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
    private static void CheckBytes(byte[] expected, byte[] actual, string name)
    {
        Check(expected.Length == actual.Length, name + " length");
        for (int i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) throw new Exception(name + " differs at " + i);
    }
    private static void SendBytes(IntPtr dp, uint from, uint to, byte[] data)
    {
        IntPtr buffer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            Check(Vt<Send_Del>(dp, 0x68)(dp, from, to, 1, buffer, (uint)data.Length) == DP_OK, "Send fixture");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void SubmitWorld(IntPtr dp, uint from, uint to, int saveId, string file)
    {
        object snapshot = Activator.CreateInstance(ShimType("WorldSnapshot"), AnyInstance, null, new object[] { file, saveId, false }, null);
        try { Check((int)Call(Engine(dp), "SendWorldSnapshot", from, to, WorldOptions(saveId), snapshot) == DP_OK, "Queue world snapshot"); }
        finally { ((IDisposable)snapshot).Dispose(); }
    }
    private static bool WorldsLoaded(IntPtr host)
    {
        object engine = Engine(host);
        return (bool)engine.GetType().GetProperty("WorldsLoaded").GetValue(engine, null);
    }
    private static void WaitWorldsLoaded(IntPtr host)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!WorldsLoaded(host) && clock.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        Check(WorldsLoaded(host), "Host ingestion barrier cleared");
    }
    private static string ReceivedWorld(IntPtr dp)
    {
        object incoming = Engine(dp).GetType().GetField("m_incomingWorld", AnyInstance).GetValue(Engine(dp));
        return (string)incoming.GetType().GetField("FinalPath").GetValue(incoming);
    }
    private static void SetWorldRoot(IntPtr dp, string path)
    {
        Engine(dp).GetType().GetField("m_worldRoot", AnyInstance).SetValue(Engine(dp), path);
    }
    private static byte[] WaitPeek(IntPtr dp)
    {
        Receive_Del receive = Vt<Receive_Del>(dp, 0x64);
        Stopwatch clock = Stopwatch.StartNew();
        uint from, to;
        while (clock.ElapsedMilliseconds < 2000)
        {
            int size = 0;
            int result = receive(dp, out from, out to, 9, IntPtr.Zero, ref size); // ALL | PEEK
            if (result == DPERR_NOMESSAGES) { Thread.Sleep(10); continue; }
            Check(result == DPERR_BUFFERTOOSMALL, "Peek size query");
            IntPtr buffer = Marshal.AllocHGlobal(Math.Max(1, size));
            try
            {
                Check(receive(dp, out from, out to, 9, buffer, ref size) == DP_OK, "Peek data query");
                byte[] bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, size); return bytes;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new Exception("Timed out peeking a message.");
    }
    private static void ExpectInvalid(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, name);
    }
    private static byte[] Manifest(Guid session, Guid transfer, byte[] bytes)
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        using (SHA256 sha = SHA256.Create())
        {
            writer.Write(session.ToByteArray()); writer.Write(transfer.ToByteArray());
            writer.Write(123); writer.Write(1u); writer.Write(2u); writer.Write((long)bytes.Length);
            writer.Write(sha.ComputeHash(bytes)); writer.Write(0x72616E64); return ms.ToArray();
        }
    }
    private static byte[] Chunk(Guid transfer, long offset, byte[] bytes)
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
            writer.Write(transfer.ToByteArray()); writer.Write(offset); writer.Write(bytes); return ms.ToArray();
        }
    }
    private static void TestWorldValidation(string root)
    {
        Directory.CreateDirectory(root);
        Type receiverType = ShimType("WorldReceiver");
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
        byte[] bytes = Encoding.ASCII.GetBytes("AN_ENGINE_SNAPSHOT_FIXTURE");
        byte[] manifest = Manifest(session, transfer, bytes);
        object receiver = Activator.CreateInstance(receiverType, new object[] { manifest, session, root });
        Check((int)receiverType.GetField("OriginalMode").GetValue(receiver) == 0x72616E64,
            "World manifest preserves generated-world lobby mode independently of the load hook");
        try
        {
            string path = (string)receiverType.GetField("FinalPath").GetValue(receiver);
            Check(!File.Exists(path), "Incomplete world never appears at final path");
            ExpectInvalid(delegate { Call(receiver, "AddChunk", Chunk(transfer, 1, bytes)); }, "Wrong offset rejected");
            ExpectInvalid(delegate { Call(receiver, "AddChunk", Chunk(Guid.NewGuid(), 0, bytes)); }, "Wrong transfer rejected");
            ExpectInvalid(delegate { Call(receiver, "Commit", transfer.ToByteArray()); }, "Truncated transfer rejected");
            byte[] corrupted = (byte[])bytes.Clone(); corrupted[0] ^= 0x55;
            Call(receiver, "AddChunk", Chunk(transfer, 0, corrupted));
            ExpectInvalid(delegate { Call(receiver, "Commit", transfer.ToByteArray()); }, "Checksum mismatch rejected");
            Check(!File.Exists(path), "Corrupt world never published");
        }
        finally { ((IDisposable)receiver).Dispose(); }
        Check(Directory.GetFiles(root).Length == 0, "Partial files removed after failed transfer");
        byte[] oversized = (byte[])manifest.Clone();
        Buffer.BlockCopy(BitConverter.GetBytes(32L * 1024 * 1024 + 1), 0, oversized, 44, 8);
        ExpectInvalid(delegate
        {
            try { Activator.CreateInstance(receiverType, new object[] { oversized, session, root }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }, "Oversized manifest rejected before writing");
        ExpectInvalid(delegate
        {
            try { Activator.CreateInstance(receiverType, new object[] { manifest, Guid.NewGuid(), root }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }, "Stale session manifest rejected");
        string unavailableRoot = Path.Combine(root, "not-a-directory"); File.WriteAllText(unavailableRoot, "fixture");
        bool diskFailure = false;
        try { Activator.CreateInstance(receiverType, new object[] { manifest, session, unavailableRoot }); }
        catch (TargetInvocationException ex) { diskFailure = ex.InnerException is IOException; }
        Check(diskFailure, "Storage failure does not report successful receipt");
        Console.WriteLine("[PASS] Test 9: World checksum, bounds, offsets, transfer IDs, atomic publication, and partial cleanup.");
    }
    private static void TestWorldTransfers(CoCreate_Del create, string root)
    {
        Guid dpClass = CLSID_DirectPlay, dpIid = IID_IDirectPlay4A, lobClass = CLSID_DirectPlayLobby, lobIid = IID_IDirectPlayLobby3A;
        IntPtr host, hostLob, c1, c1Lob, c2, c2Lob;
        create(ref dpClass, IntPtr.Zero, 1, ref dpIid, out host);
        create(ref lobClass, IntPtr.Zero, 1, ref lobIid, out hostLob);
        create(ref dpClass, IntPtr.Zero, 1, ref dpIid, out c1);
        create(ref lobClass, IntPtr.Zero, 1, ref lobIid, out c1Lob);
        create(ref dpClass, IntPtr.Zero, 1, ref dpIid, out c2);
        create(ref lobClass, IntPtr.Zero, 1, ref lobIid, out c2Lob);
        try
        {
            InitConn(host, hostLob, null); Guid session = HostOpen(host, "World transfer fixtures");
            uint hostId = MakePlayer(host, "Host");
            byte[] localOptions = WorldOptions(100);
            SendBytes(host, hostId, hostId, localOptions);
            uint localFrom, localTo;
            byte[] localInitialization = WaitReceive(host, out localFrom, out localTo, 1000);
            Check(localInitialization != null && localFrom == hostId && localTo == hostId,
                "Host receives its own world options before remote clients exist");
            CheckBytes(localOptions, localInitialization, "Local host initialization is preserved without snapshot capture");
            InitConn(c1, c1Lob, "127.0.0.1:12357"); EnumGames(c1, 1000); JoinOpen(c1, session); uint id1 = MakePlayer(c1, "First");
            InitConn(c2, c2Lob, "127.0.0.1:12357@Loopback"); EnumGames(c2, 1000); JoinOpen(c2, session); uint id2 = MakePlayer(c2, "Second");
            Check(!WorldsLoaded(host), "Newly joined players block launch before the first snapshot is queued");
            using (TcpClient legacy = new TcpClient("127.0.0.1", 12357))
            {
                legacy.ReceiveTimeout = 2000;
                BinaryWriter writer = new BinaryWriter(legacy.GetStream());
                writer.Write(0x32504D49u); writer.Write((byte)5); writer.Write(16); writer.Write(session.ToByteArray()); writer.Flush();
                BinaryReader reader = new BinaryReader(legacy.GetStream());
                Check(reader.ReadUInt32() == 0x32504D49u && reader.ReadByte() == 6 && reader.ReadInt32() == 1 && reader.ReadByte() == 0,
                    "Legacy join explicitly rejected without entering staging");
            }
            SetWorldRoot(c1, Path.Combine(root, "first")); SetWorldRoot(c2, Path.Combine(root, "second"));
            byte[] first = WorldBytes(21), second = WorldBytes(98);
            string firstFile = Path.Combine(root, "first-source.imp"), secondFile = Path.Combine(root, "second-source.imp");
            File.WriteAllBytes(firstFile, first); File.WriteAllBytes(secondFile, second);
            // Distinct worlds expose global-buffer collisions and incorrect recipient routing.
            SubmitWorld(host, hostId, id1, 101, firstFile); SubmitWorld(host, hostId, id2, 102, secondFile);
            Check(!WorldsLoaded(host), "Host cannot launch while files are being transferred");
            uint from, to;
            byte[] init1 = WaitReceive(c1, out from, out to, 5000);
            Check(init1 != null && init1.Length == 160 && from == hostId && to == id1, "First initialization delivered after commit");
            Check(BitConverter.ToUInt32(init1, 0x64) == 0x72616E64, "Generated-world options retain native staging initialization before applying the map");
            CheckBytes(first, File.ReadAllBytes(ReceivedWorld(c1)), "First client's world");
            byte[] init2 = WaitReceive(c2, out from, out to, 5000);
            Check(init2 != null && init2.Length == 160 && to == id2, "Second initialization delivered independently");
            CheckBytes(second, File.ReadAllBytes(ReceivedWorld(c2)), "Second client's world");
            Check(!WorldsLoaded(host), "Stored files alone do not clear ingestion barrier");
            Call(Engine(c2), "ReportWorldLoaded", 102, true);
            SendBytes(c2, id2, hostId, Encoding.ASCII.GetBytes("FAST_CLIENT_REMAINS_RESPONSIVE"));
            Check(Encoding.ASCII.GetString(WaitPeek(host)) == "FAST_CLIENT_REMAINS_RESPONSIVE", "DPRECEIVE_PEEK inspects without consuming");
            byte[] responsive = WaitReceive(host, out from, out to, 2000);
            Check(responsive != null && Encoding.ASCII.GetString(responsive) == "FAST_CLIENT_REMAINS_RESPONSIVE", "Slow loader does not block other clients");
            Check(!WorldsLoaded(host), "One loaded client cannot release another client's barrier");
            Engine(c1).GetType().GetField("m_loadingWorld", AnyInstance).SetValue(Engine(c1), true);
            byte[] reconnect = new byte[40];
            Array.Copy(BitConverter.GetBytes(0x1F), 0, reconnect, 0, 4);
            Array.Copy(BitConverter.GetBytes(40), 0, reconnect, 12, 4);
            Array.Copy(BitConverter.GetBytes(0x7265706Fu), 0, reconnect, 28, 4);
            SendBytes(c1, id1, hostId, reconnect);
            byte[] savedName = (byte[])reconnect.Clone();
            Array.Copy(BitConverter.GetBytes(0x6E616D65u), 0, savedName, 28, 4);
            SendBytes(c1, id1, hostId, savedName);
            byte[] savedStatus = new byte[52];
            Array.Copy(BitConverter.GetBytes(0x25), 0, savedStatus, 0, 4);
            Array.Copy(BitConverter.GetBytes(52), 0, savedStatus, 12, 4);
            SendBytes(c1, id1, hostId, savedStatus);
            SendBytes(c1, id1, hostId, Encoding.ASCII.GetBytes("LOAD_COMPLETED_EVENT"));
            Call(Engine(c1), "ReportWorldLoaded", 101, true); WaitWorldsLoaded(host);
            Check(Encoding.ASCII.GetString(WaitReceive(host, out from, out to, 2000)) == "LOAD_COMPLETED_EVENT",
                "Saved-player reconnect traffic is suppressed; unrelated events follow the load ACK");
            SendBytes(c1, id1, hostId, reconnect);
            CheckBytes(reconnect, WaitReceive(host, out from, out to, 2000), "Reconnect messages outside snapshot ingestion remain intact");
            // A later revision must replace the prior initialization, not reuse its bytes or ACK.
            SubmitWorld(host, hostId, id1, 103, secondFile);
            Check(WaitReceive(c1, out from, out to, 5000) != null, "Sequential world revision delivered");
            CheckBytes(second, File.ReadAllBytes(ReceivedWorld(c1)), "Sequential revision");
            Call(Engine(c1), "ReportWorldLoaded", 103, false);
            byte[] drop = WaitReceive(host, out from, out to, 5000);
            Check(drop != null && from == 0 && BitConverter.ToUInt32(drop, 0) == 5, "Failed native ingestion disconnects the affected client");
            WaitWorldsLoaded(host);
            InitConn(c1, c1Lob, "127.0.0.1:12357"); EnumGames(c1, 1000); JoinOpen(c1, session); id1 = MakePlayer(c1, "Interrupted");
            string interruptedRoot = Path.Combine(root, "interrupted"); SetWorldRoot(c1, interruptedRoot);
            string changingFile = Path.Combine(root, "changing-source.imp"); File.WriteAllBytes(changingFile, first);
            object changingSnapshot = Activator.CreateInstance(ShimType("WorldSnapshot"), AnyInstance, null,
                new object[] { changingFile, 104, false }, null);
            try
            {
                // Force EOF after a real chunk has crossed the connection, before COMMIT.
                File.WriteAllBytes(changingFile, new byte[65536]);
                Check((int)Call(Engine(host), "SendWorldSnapshot", hostId, id1, WorldOptions(104), changingSnapshot) == DP_OK,
                    "Queue interrupted-transfer fixture");
            }
            finally { ((IDisposable)changingSnapshot).Dispose(); }
            byte[] lost = WaitReceive(c1, out from, out to, 5000);
            Check(lost != null && from == 0 && BitConverter.ToUInt32(lost, 0) == 0x21, "Mid-transfer disconnect reports session lost");
            Check(!Directory.Exists(interruptedRoot) || Directory.GetFiles(interruptedRoot).Length == 0,
                "Interrupted transfer leaves neither partial nor published worlds");
            Check(WaitReceive(host, out from, out to, 5000) != null, "Host observes interrupted client departure");
            // Reuse the same COM objects immediately while old socket readers are unwinding.
            Vt<Close_Del>(c2, 0x10)(c2); Vt<Close_Del>(host, 0x10)(host);
            for (int round = 0; round < 3; round++)
            {
                InitConn(host, hostLob, null); session = HostOpen(host, "Rehost " + round); hostId = MakePlayer(host, "Host");
                InitConn(c2, c2Lob, "127.0.0.1:12357"); EnumGames(c2, 1000); JoinOpen(c2, session); id2 = MakePlayer(c2, "Rejoining");
                SubmitWorld(host, hostId, id2, 500 + round, firstFile);
                Check(WaitReceive(c2, out from, out to, 5000) != null, "Same-object rehost world " + round);
                CheckBytes(first, File.ReadAllBytes(ReceivedWorld(c2)), "Rehost snapshot " + round);
                Call(Engine(c2), "ReportWorldLoaded", 500 + round, true); WaitWorldsLoaded(host);
                Vt<Close_Del>(c2, 0x10)(c2); Vt<Close_Del>(host, 0x10)(host);
            }
            Console.WriteLine("[PASS] Test 10: Concurrent multi-chunk worlds, independent clients, ingestion barrier, sequential revisions, and load failure.");
        }
        finally
        {
            foreach (IntPtr p in new IntPtr[] { c1, c2, host }) { Vt<Close_Del>(p, 0x10)(p); Vt<Release_Del>(p, 8)(p); }
            foreach (IntPtr p in new IntPtr[] { c1Lob, c2Lob, hostLob }) Vt<Release_Del>(p, 8)(p);
        }
    }

    private static int RunWorldClient(string dllPath, string root)
    {
        Environment.SetEnvironmentVariable("IMP2_PORT", "12357");
        IntPtr module = LoadLibraryA(dllPath);
        CoCreate_Del create = (CoCreate_Del)Marshal.GetDelegateForFunctionPointer(NativeWrap(GetProcAddress(module, "Imp2CoCreateInstance")), typeof(CoCreate_Del));
        Guid gc = CLSID_DirectPlay, gi = IID_IDirectPlay4A, lc = CLSID_DirectPlayLobby, li = IID_IDirectPlayLobby3A;
        IntPtr dp, lobby;
        create(ref gc, IntPtr.Zero, 1, ref gi, out dp); create(ref lc, IntPtr.Zero, 1, ref li, out lobby);
        try
        {
            InitConn(dp, lobby, "127.0.0.1:12357");
            var games = EnumGames(dp, 2000); Check(games.Count == 1, "World worker discovery");
            JoinOpen(dp, games[0].Key); uint pid = MakePlayer(dp, "World worker"); SetWorldRoot(dp, root);
            SendBytes(dp, pid, 1, Encoding.ASCII.GetBytes("WORLD_WORKER_READY"));
            uint from, to; byte[] initial = WaitReceive(dp, out from, out to, 5000);
            Check(initial != null && initial.Length == 160, "World worker initialization");
            CheckBytes(WorldBytes(405), File.ReadAllBytes(ReceivedWorld(dp)), "Cross-process snapshot");
            Call(Engine(dp), "ReportWorldLoaded", 405, true);
            SendBytes(dp, pid, 1, Encoding.ASCII.GetBytes("WORLD_WORKER_VERIFIED"));
            Check(WaitReceive(dp, out from, out to, 5000) != null, "World worker completion handshake");
            return 0;
        }
        finally
        {
            Vt<Close_Del>(dp, 0x10)(dp); Vt<Release_Del>(dp, 8)(dp); Vt<Release_Del>(lobby, 8)(lobby);
        }
    }
    private static void TestCrossProcessWorld(CoCreate_Del create, string dllPath, string root)
    {
        Guid gc = CLSID_DirectPlay, gi = IID_IDirectPlay4A, lc = CLSID_DirectPlayLobby, li = IID_IDirectPlayLobby3A;
        IntPtr host, lobby;
        create(ref gc, IntPtr.Zero, 1, ref gi, out host); create(ref lc, IntPtr.Zero, 1, ref li, out lobby);
        Process worker = null;
        try
        {
            InitConn(host, lobby, null); HostOpen(host, "Cross-process world"); uint pid = MakePlayer(host, "World host");
            ProcessStartInfo start = new ProcessStartInfo(typeof(TestMultiplayer).Assembly.Location,
                "--world-client-worker \"" + dllPath + "\" \"" + Path.Combine(root, "child-save") + "\"");
            start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardError = true;
            worker = Process.Start(start);
            uint from, to; byte[] ready = WaitReceive(host, out from, out to, 5000);
            Check(ready != null && Encoding.ASCII.GetString(ready) == "WORLD_WORKER_READY", "World worker ready");
            string snapshot = Path.Combine(root, "cross-process-source.imp"); File.WriteAllBytes(snapshot, WorldBytes(405));
            SubmitWorld(host, pid, from, 405, snapshot);
            byte[] verified = WaitReceive(host, out from, out to, 5000);
            Check(verified != null && Encoding.ASCII.GetString(verified) == "WORLD_WORKER_VERIFIED", "Separate process verified transferred bytes");
            WaitWorldsLoaded(host); SendBytes(host, pid, from, new byte[] { 1 });
            Check(worker.WaitForExit(5000), "World worker exited");
            Check(worker.ExitCode == 0, "World worker success: " + worker.StandardError.ReadToEnd());
            Console.WriteLine("[PASS] Test 11: Cross-process world transfer into a separate save directory.");
        }
        finally
        {
            if (worker != null) { if (!worker.HasExited) worker.Kill(); worker.Dispose(); }
            Vt<Close_Del>(host, 0x10)(host); Vt<Release_Del>(host, 8)(host); Vt<Release_Del>(lobby, 8)(lobby);
        }
    }

    private static void TestNativeWorldHooks(string exePath, IntPtr module)
    {
        TestNativeSavedCountryRoles();
        // Native host writers append private data that remote client readers do not
        // consume. Exercise the scoped override against actual unmanaged vtables,
        // including two different classes, aliases, an unrelated method, and unwind.
        IntPtr countryA = Marshal.AllocHGlobal(4), countryB = Marshal.AllocHGlobal(4);
        IntPtr tableA = Marshal.AllocHGlobal(0x410), tableB = Marshal.AllocHGlobal(0x410);
        IntPtr writers = VirtualAlloc(IntPtr.Zero, new UIntPtr(64), 0x3000, 0x40);
        try
        {
            byte[] zero = new byte[0x410];
            Marshal.Copy(zero, 0, tableA, zero.Length); Marshal.Copy(zero, 0, tableB, zero.Length);
            // thiscall methods returning distinctive serialization results.
            byte[] methods = { 0xB8,1,0,0,0,0xC3, 0xB8,2,0,0,0,0xC3, 0xB8,3,0,0,0,0xC3 };
            Marshal.Copy(methods, 0, writers, methods.Length);
            Marshal.WriteIntPtr(tableA, 0x14, writers); Marshal.WriteIntPtr(tableA, 0x18, writers);
            Marshal.WriteIntPtr(tableB, 0x14, IntPtr.Add(writers, 6)); Marshal.WriteIntPtr(tableB, 0x18, IntPtr.Add(writers, 6));
            Marshal.WriteIntPtr(countryA, tableA); Marshal.WriteIntPtr(countryB, tableB);
            try
            {
                using ((IDisposable)Activator.CreateInstance(ShimType("NativeCountryWriteScope"),
                    new object[] { new IntPtr[] { countryA, countryB, countryA, IntPtr.Zero }, IntPtr.Add(writers, 12), 0x410 }))
                {
                    Check(Vt<StartGame_Del>(countryA, 0x14)(countryA) == 3 && Vt<StartGame_Del>(countryB, 0x14)(countryB) == 3,
                        "Host and AI country snapshots use the remote-compatible writer");
                    Check(Vt<StartGame_Del>(countryA, 0x18)(countryA) == 1 && Vt<StartGame_Del>(countryB, 0x18)(countryB) == 2,
                        "Snapshot override preserves unrelated native methods");
                    Check(Marshal.ReadIntPtr(tableA, 0x14) == writers, "Shared original table remains unchanged");
                    throw new IOException("Simulated native save failure");
                }
            }
            catch (IOException) { }
            Check(Marshal.ReadIntPtr(countryA) == tableA && Marshal.ReadIntPtr(countryB) == tableB &&
                Vt<StartGame_Del>(countryA, 0x14)(countryA) == 1 && Vt<StartGame_Del>(countryB, 0x14)(countryB) == 2,
                "All original country behavior is restored after failed snapshot capture");
        }
        finally
        {
            Marshal.FreeHGlobal(countryA); Marshal.FreeHGlobal(countryB);
            Marshal.FreeHGlobal(tableA); Marshal.FreeHGlobal(tableB); VirtualFree(writers, UIntPtr.Zero, 0x8000);
        }
        CanStart_Del exportedCheck = (CanStart_Del)Marshal.GetDelegateForFunctionPointer(NativeWrap(GetProcAddress(module, "Imp2CanStart")), typeof(CanStart_Del));
        LoadWorld_Del exportedLoad = (LoadWorld_Del)Marshal.GetDelegateForFunctionPointer(NativeWrap(GetProcAddress(module, "Imp2LoadWorld")), typeof(LoadWorld_Del));
        Check(exportedCheck() == 1, "No active host means no pending world barrier");
        for (int i = 0; i < 100; i++) Check(exportedLoad(123, IntPtr.Zero) == 0, "cdecl loader export fails closed without matching world");
        object nativeHost = Activator.CreateInstance(ShimType("DPlaySessionEngine"));
        Type bridge = ShimType("NativeWorldBridge");
        bridge.GetMethod("SetHost").Invoke(null, new object[] { nativeHost });
        try
        {
            for (int i = 0; i < 100; i++)
                Check(exportedLoad(123, IntPtr.Zero) == 1, "Host retains its generated map at the common lounge hook");
            Check(exportedLoad(123, new IntPtr(1)) == 0,
                "cdecl export forwards the prefix; native host file loading rejects a non-game process safely");
        }
        finally { bridge.GetMethod("Clear").Invoke(null, new object[] { nativeHost }); }
        Check(exportedLoad(123, IntPtr.Zero) == 0, "Closing host restores missing-snapshot failure handling");
        byte[] exe = File.ReadAllBytes(exePath);
        Check(exe[0x18B9AD] == 0xE8 && 0x58C5B2 + BitConverter.ToInt32(exe, 0x18B9AE) == 0x6E8890, "Client load hook target");
        Check(exe[0x18A540] == 0xE9 && 0x58B145 + BitConverter.ToInt32(exe, 0x18A541) == 0x6E8900, "StartGame guard target");
        IntPtr mem = VirtualAlloc(IntPtr.Zero, new UIntPtr(512), 0x3000, 0x40);
        IntPtr instance = Marshal.AllocHGlobal(128);
        bool allow = false;
        CanStart_Del check = delegate { return allow ? 1 : 0; };
        try
        {
            byte[] guard = new byte[29]; Array.Copy(exe, 0x2E7D00, guard, 0, guard.Length);
            int baseAddress = mem.ToInt32();
            Array.Copy(BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(check).ToInt32() - baseAddress - 6), 0, guard, 2, 4);
            Array.Copy(BitConverter.GetBytes(64 - 26), 0, guard, 22, 4);
            Marshal.Copy(guard, 0, mem, guard.Length);
            // Simulated native continuation unwinds StartGame's original local stack frame.
            byte[] continuation = new byte[] { 0x83,0xC4,0x1C, 0xB8,1,0,0,0, 0xC3 };
            Marshal.Copy(continuation, 0, new IntPtr(baseAddress + 64), continuation.Length);
            StartGame_Del startGame = (StartGame_Del)Marshal.GetDelegateForFunctionPointer(mem, typeof(StartGame_Del));
            Marshal.WriteInt32(instance, 0x40, 0x12345678);
            Check(startGame(instance) == 0 && Marshal.ReadInt32(instance, 0x40) == 0x12345678, "Pending world prevents native start without mutating this");
            allow = true;
            Check(startGame(instance) == 1 && Marshal.ReadInt32(instance, 0x40) == 0, "Allowed start executes stolen instructions and preserves native stack/ECX");
            Check(exe[0x18B9B5] == 0xE9 && 0x58C5BA + BitConverter.ToInt32(exe, 0x18B9B6) == 0x6E8940,
                "Snapshot load result uses the lounge-aware continuation");
            byte[] resultThunk = new byte[21]; Array.Copy(exe, 0x2E7D40, resultThunk, 0, resultThunk.Length);
            Check(0x6E8948 + BitConverter.ToInt32(resultThunk, 4) == 0x58C57B &&
                0x6E8950 + BitConverter.ToInt32(resultThunk, 12) == 0x58C6CB &&
                0x6E8955 + BitConverter.ToInt32(resultThunk, 17) == 0x58C5BD, "All native load-result targets");
            Array.Copy(BitConverter.GetBytes(240 - (128 + 8)), 0, resultThunk, 4, 4);
            Array.Copy(BitConverter.GetBytes(256 - (128 + 16)), 0, resultThunk, 12, 4);
            Array.Copy(BitConverter.GetBytes(224 - (128 + 21)), 0, resultThunk, 17, 4);
            Marshal.Copy(resultThunk, 0, IntPtr.Add(mem, 128), resultThunk.Length);
            byte[] entry = { 0x8B,0x44,0x24,4,0xE9,0xB7,0xFF,0xFF,0xFF }; // result from cdecl arg, jump back to +128
            Marshal.Copy(entry, 0, IntPtr.Add(mem, 192), entry.Length);
            foreach (int offset in new int[] { 224,240,256 })
            {
                byte[] ret = { 0xB8,0,0,0,0,0xC3 };
                Array.Copy(BitConverter.GetBytes(offset), 0, ret, 1, 4);
                Marshal.Copy(ret, 0, IntPtr.Add(mem, offset), ret.Length);
            }
            LoadWorld_Del route = (LoadWorld_Del)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(mem, 192), typeof(LoadWorld_Del));
            Check(route(0, IntPtr.Zero) == 224 && route(1, IntPtr.Zero) == 256 && route(2, IntPtr.Zero) == 240,
                "Native loader routes failure, normal success, and lobby success independently");
            byte[] mapThunk = new byte[42]; Array.Copy(exe, 0x2E7D60, mapThunk, 0, mapThunk.Length);
            Check(0x6E8980 + BitConverter.ToInt32(mapThunk, 28) == 0x58C5BD &&
                0x6E898A + BitConverter.ToInt32(mapThunk, 38) == 0x58C580,
                "Pre-preview map hook has valid failure and continuation targets");
            bool mapAccepted = false; int loadedSave = 0;
            LoadWorld_Del applyMap = delegate(int id, IntPtr prefix) { loadedSave = id; return mapAccepted ? 1 : 0; };
            Marshal.WriteInt32(instance, 0x60, 6789); Marshal.WriteIntPtr(mem, 400, instance);
            Array.Copy(BitConverter.GetBytes(baseAddress + 400), 0, mapThunk, 6, 4);
            Array.Copy(BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(applyMap).ToInt32() - (baseAddress + 320 + 18)), 0, mapThunk, 14, 4);
            Array.Copy(BitConverter.GetBytes(224 - (320 + 32)), 0, mapThunk, 28, 4);
            Array.Copy(BitConverter.GetBytes(256 - (320 + 42)), 0, mapThunk, 38, 4);
            Marshal.Copy(mapThunk, 0, IntPtr.Add(mem, 320), mapThunk.Length);
            byte[] mapEntry = { 0x56,0x53,0x8B,0xD9,0xE8,0,0,0,0,0x5B,0x5E,0xC3 };
            Array.Copy(BitConverter.GetBytes(320 - (448 + 9)), 0, mapEntry, 5, 4);
            Marshal.Copy(mapEntry, 0, IntPtr.Add(mem, 448), mapEntry.Length);
            StartGame_Del mapHook = (StartGame_Del)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(mem, 448), typeof(StartGame_Del));
            Check(mapHook(instance) == 224 && loadedSave == 6789, "Map hook calls the cdecl loader with the active world identifier and handles failure");
            mapAccepted = true;
            Check(mapHook(instance) == 256, "Map hook applies the committed map before the native lounge continuation");
            Check(exe[0x18ABFC] == 0xE9 && 0x58B801 + BitConverter.ToInt32(exe, 0x18ABFD) == 0x6E8990,
                "Saved-country reservation check uses the reclaim guard");
            byte[] seatThunk = new byte[22]; Array.Copy(exe, 0x2E7D90, seatThunk, 0, seatThunk.Length);
            Check(0x6E8999 + BitConverter.ToInt32(seatThunk, 5) == 0x58B979 &&
                0x6E89A1 + BitConverter.ToInt32(seatThunk, 13) == 0x58B979 &&
                0x6E89A6 + BitConverter.ToInt32(seatThunk, 18) == 0x58B804,
                "Reserved and empty seats use the native claim path; other owners retain requester validation");
            Array.Copy(BitConverter.GetBytes(224 - (128 + 9)), 0, seatThunk, 5, 4);
            Array.Copy(BitConverter.GetBytes(224 - (128 + 17)), 0, seatThunk, 13, 4);
            Array.Copy(BitConverter.GetBytes(256 - (128 + 22)), 0, seatThunk, 18, 4);
            Marshal.Copy(seatThunk, 0, IntPtr.Add(mem, 128), seatThunk.Length);
            Check(route(-2, IntPtr.Zero) == 224 && route(0, IntPtr.Zero) == 224,
                "Disconnected saved player and empty country can be claimed");
            foreach (int owner in new int[] { 1, 2, 42, -1, -3 })
                Check(route(owner, IntPtr.Zero) == 256, "Existing live/special owner is never bypassed: " + owner);
            GC.KeepAlive(applyMap);
            GC.KeepAlive(check);
        }
        finally { Marshal.FreeHGlobal(instance); VirtualFree(mem, UIntPtr.Zero, 0x8000); }
        Console.WriteLine("[PASS] Test 12: Native cdecl loader export, hook targets, and StartGame guard execution.");
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void InitializeCountry_Del(IntPtr simulation, int country, int initialize);
    private static void TestNativeSavedCountryRoles()
    {
        IntPtr simulation = Marshal.AllocHGlobal(512), table = Marshal.AllocHGlobal(0x88);
        IntPtr country = Marshal.AllocHGlobal(2), code = VirtualAlloc(IntPtr.Zero, new UIntPtr(64), 0x3000, 0x40);
        try
        {
            byte[] zero = new byte[512]; Marshal.Copy(zero, 0, simulation, zero.Length);
            Marshal.Copy(zero, 0, table, 0x88); Marshal.WriteIntPtr(simulation, table);
            // Native thiscall fixture observes the configured role and second
            // argument, then unwinds both arguments just like InitCountry.
            byte[] observer = { 0x8B,0x44,0x24,4, 0x0F,0xBF,0x84,0x41,0x34,1,0,0,
                0x89,0x41,0x20, 0x8B,0x44,0x24,8, 0x89,0x41,0x24, 0xC2,8,0 };
            Marshal.Copy(observer, 0, code, observer.Length); Marshal.WriteIntPtr(table, 0x2C, code);
            foreach (int selected in new int[] { 0, 2, 5 })
            {
                try
                {
                    using ((IDisposable)Activator.CreateInstance(ShimType("NativeCountryRoleScope"),
                        new object[] { simulation, selected, country }))
                    {
                        for (int pass = 0; pass < 20; pass++)
                            for (int i = 5; i >= 0; i--)
                            {
                                Marshal.WriteInt16(simulation, 0x134 + i * 2, (short)(i == 1 ? 1 : 4));
                                Marshal.WriteInt16(country, 1); // host identity in the saved file
                                Vt<InitializeCountry_Del>(simulation, 0x2C)(simulation, i, 37);
                                Check(Marshal.ReadInt32(simulation, 0x20) == (i == selected ? 1 : 4) &&
                                    Marshal.ReadInt16(country) == selected && Marshal.ReadInt32(simulation, 0x24) == 37,
                                    "Saved-country construction binds only the claimed client country and preserves native arguments");
                            }
                        throw new IOException("Simulated saved-load failure");
                    }
                }
                catch (IOException) { }
                Check(Marshal.ReadIntPtr(simulation) == table && Marshal.ReadIntPtr(table, 0x2C) == code,
                    "Country initializer table is restored after saved-load unwind");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(simulation); Marshal.FreeHGlobal(table); Marshal.FreeHGlobal(country);
            VirtualFree(code, UIntPtr.Zero, 0x8000);
        }
    }

    private static void VerifyExeX86Thunk(string exePath)
    {
        byte[] exe = System.IO.File.ReadAllBytes(exePath);
        byte[] thunk = new byte[39];
        Array.Copy(exe, 0x2E7C60, thunk, 0, 39);

        // Verify exact instruction opcodes & relative jump displacements from ImpII.exe
        Check(thunk[0] == 0x68 && thunk[5] == 0xFF && thunk[6] == 0x15 &&
              thunk[11] == 0x85 && thunk[12] == 0xC0 && thunk[13] == 0x74 && thunk[14] == 0x12 &&
              thunk[15] == 0x68 && thunk[20] == 0x50 && thunk[21] == 0xFF && thunk[22] == 0x15 &&
              thunk[27] == 0x85 && thunk[28] == 0xC0 && thunk[29] == 0x74 && thunk[30] == 0x02 &&
              thunk[31] == 0xFF && thunk[32] == 0xE0 && thunk[33] == 0xFF && thunk[34] == 0x25,
              "ImpII.exe DPlayCoCreateThunk byte signature");

        IntPtr mem = VirtualAlloc(IntPtr.Zero, new UIntPtr(256), 0x3000, 0x40); // MEM_COMMIT|MEM_RESERVE, PAGE_EXECUTE_READWRITE
        Check(mem != IntPtr.Zero, "VirtualAlloc RWX");
        CoCreate_Del fallbackDel = new CoCreate_Del(FallbackCoCreateStub);
        try
        {
            IntPtr hK32 = GetModuleHandleA("kernel32.dll");
            IntPtr fnGmh = GetProcAddress(hK32, "GetModuleHandleA");
            IntPtr fnGpa = GetProcAddress(hK32, "GetProcAddress");
            IntPtr fnFb  = Marshal.GetFunctionPointerForDelegate(fallbackDel);

            // Layout inside RWX page:
            // +0x00: 39-byte thunk
            // +0x40: IAT slot GetModuleHandleA
            // +0x44: IAT slot GetProcAddress
            // +0x48: IAT slot Fallback CoCreateInstance
            // +0x50: "DPLAYX.dll\0"
            // +0x60: "Imp2CoCreateInstance\0"
            Marshal.WriteIntPtr(mem, 0x40, fnGmh);
            Marshal.WriteIntPtr(mem, 0x44, fnGpa);
            Marshal.WriteIntPtr(mem, 0x48, fnFb);
            byte[] sDll = Encoding.ASCII.GetBytes("DPLAYX.dll\0");
            byte[] sFn  = Encoding.ASCII.GetBytes("Imp2CoCreateInstance\0");
            Marshal.Copy(sDll, 0, new IntPtr(mem.ToInt64() + 0x50), sDll.Length);
            Marshal.Copy(sFn,  0, new IntPtr(mem.ToInt64() + 0x60), sFn.Length);

            int baseAddr = mem.ToInt32();
            Array.Copy(BitConverter.GetBytes(baseAddr + 0x50), 0, thunk, 1, 4);
            Array.Copy(BitConverter.GetBytes(baseAddr + 0x40), 0, thunk, 7, 4);
            Array.Copy(BitConverter.GetBytes(baseAddr + 0x60), 0, thunk, 16, 4);
            Array.Copy(BitConverter.GetBytes(baseAddr + 0x44), 0, thunk, 23, 4);
            Array.Copy(BitConverter.GetBytes(baseAddr + 0x48), 0, thunk, 35, 4);
            Marshal.Copy(thunk, 0, mem, thunk.Length);

            CoCreate_Del nativeThunk = (CoCreate_Del)Marshal.GetDelegateForFunctionPointer(mem, typeof(CoCreate_Del));
            Guid gDp = CLSID_DirectPlay, iDp = IID_IDirectPlay4A;
            IntPtr dpOut;
            int hr = nativeThunk(ref gDp, IntPtr.Zero, 1, ref iDp, out dpOut);
            Check(hr == DP_OK && dpOut != IntPtr.Zero, "Native x86 thunk routed to Imp2CoCreateInstance");
            Vt<Release_Del>(dpOut, 0x08)(dpOut);

            // Now corrupt module string at +0x50 to verify jz fallback (+0x12) jumps cleanly to [ole32!CoCreateInstance]
            Marshal.WriteByte(mem, 0x50, (byte)'Z');
            IntPtr fbOut;
            int hrFb = nativeThunk(ref gDp, IntPtr.Zero, 1, ref iDp, out fbOut);
            Check(hrFb == 0x112233 && fbOut == new IntPtr(0x12345678), "Native x86 thunk fallback branch verified");
            GC.KeepAlive(fallbackDel);
        }
        finally
        {
            VirtualFree(mem, UIntPtr.Zero, 0x8000);
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 3 && args[0] == "--client-worker")
            {
                return RunClientWorker(args[1], args[2]);
            }
            if (args.Length >= 3 && args[0] == "--world-client-worker") return RunWorldClient(args[1], args[2]);

            Environment.SetEnvironmentVariable("IMP2_PORT", "12357");
            Environment.SetEnvironmentVariable("IMP2_BIND", "All");

            string dllPath = args.Length > 0 ? args[0] : @"C:\Users\ephbu\Documents\GitHub\Imperialism2_Patch\Imperialism II - Patched\dplayx.dll";
            Console.WriteLine("Loading: " + dllPath);
            IntPtr hMod = LoadLibraryA(dllPath);
            Check(hMod != IntPtr.Zero, "LoadLibraryA(dplayx.dll)");

            for (int ord = 1; ord <= 13; ord++)
            {
                IntPtr p = GetProcAddress(hMod, new IntPtr(ord));
                Check(p != IntPtr.Zero, "Export Ordinal #" + ord);
            }
            Console.WriteLine("[PASS] Test 1: All 13 Win32 PE exports (#1..#13) verified.");

            DPEnumA_Del dpEnum = (DPEnumA_Del)Marshal.GetDelegateForFunctionPointer(NativeWrap(GetProcAddress(hMod, new IntPtr(2))), typeof(DPEnumA_Del));
            int enumCount = 0;
            dpEnum(NativeCb((DPEnumCb)delegate(IntPtr g, IntPtr nm, uint maj, uint min, IntPtr ctx)
            {
                enumCount++;
                Console.WriteLine("  SP: " + Marshal.PtrToStringAnsi(nm));
                return 1;
            }), IntPtr.Zero);
            Check(enumCount == 1, "DirectPlayEnumerateA returned 1 TCP/IP provider");

            CoCreate_Del coCreate = (CoCreate_Del)Marshal.GetDelegateForFunctionPointer(NativeWrap(GetProcAddress(hMod, "Imp2CoCreateInstance")), typeof(CoCreate_Del));
            IntPtr hostDP, hostLobby, cli1DP, cli1Lobby, cli2DP, cli2Lobby;
            Guid gDp = CLSID_DirectPlay, iDp = IID_IDirectPlay4A, gLob = CLSID_DirectPlayLobby, iLob = IID_IDirectPlayLobby3A;

            Check(coCreate(ref gDp, IntPtr.Zero, 1, ref iDp, out hostDP) == DP_OK, "Create hostDP");
            Check(coCreate(ref gLob, IntPtr.Zero, 1, ref iLob, out hostLobby) == DP_OK, "Create hostLobby");
            Check(coCreate(ref gDp, IntPtr.Zero, 1, ref iDp, out cli1DP) == DP_OK, "Create cli1DP");
            Check(coCreate(ref gLob, IntPtr.Zero, 1, ref iLob, out cli1Lobby) == DP_OK, "Create cli1Lobby");
            Check(coCreate(ref gDp, IntPtr.Zero, 1, ref iDp, out cli2DP) == DP_OK, "Create cli2DP");
            Check(coCreate(ref gLob, IntPtr.Zero, 1, ref iLob, out cli2Lobby) == DP_OK, "Create cli2Lobby");

            // Host session on All Interfaces (0.0.0.0:12357)
            InitConn(hostDP, hostLobby, null);
            Guid sessGuid = HostOpen(hostDP, "Age of Exploration MP");
            uint hostPid = MakePlayer(hostDP, "Britain");
            Check(hostPid == 1, "Host DPID == 1");

            // Set Host marker (DWORD 1) via SetPlayerData (+0x74)
            IntPtr oneBuf = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(oneBuf, 1);
            Check(Vt<SetPlayerData_Del>(hostDP, 0x74)(hostDP, hostPid, oneBuf, 4, 2) == DP_OK, "Host SetPlayerData(1)");
            Marshal.FreeHGlobal(oneBuf);

            // Verify GetPlayerAddress (+0x48) + __cdecl EnumAddress (+0x14) stack safety!
            int addrSz = 0;
            Check(Vt<GetPlayerAddress_Del>(hostDP, 0x48)(hostDP, hostPid, IntPtr.Zero, ref addrSz) == DPERR_BUFFERTOOSMALL, "GetPlayerAddress size");
            IntPtr addrBuf = Marshal.AllocHGlobal(addrSz);
            Check(Vt<GetPlayerAddress_Del>(hostDP, 0x48)(hostDP, hostPid, addrBuf, ref addrSz) == DP_OK, "GetPlayerAddress data");
            string loungeIp = null;
            Vt<EnumAddress_Del>(hostLobby, 0x14)(hostLobby, NativeCb((EnumAddrCdeclCb)delegate(IntPtr gt, uint dsz, IntPtr dp, IntPtr ctx)
            {
                byte[] gb = new byte[16];
                Marshal.Copy(gt, gb, 0, 16);
                if (new Guid(gb) == DPAID_INet)
                {
                    loungeIp = Marshal.PtrToStringAnsi(dp);
                    return 0;
                }
                return 1;
            }), addrBuf, (uint)addrSz, IntPtr.Zero);
            Marshal.FreeHGlobal(addrBuf);
            Check(!string.IsNullOrEmpty(loungeIp), "Host Lounge IP string resolved: " + loungeIp);
            Console.WriteLine("[PASS] Test 2: Host Lounge IP display (__cdecl EnumAddress safe): '" + loungeIp + "'");

            // Test multi-interface blank-IP broadcast scan ("")
            InitConn(cli1DP, cli1Lobby, "");
            var foundAll = EnumGames(cli1DP, 1000);
            Check(foundAll.Count == 1 && foundAll[0].Key == sessGuid, "Blank-IP scan across all interfaces found Host");
            Console.WriteLine("[PASS] Test 3a: Blank-IP scan across ALL network interfaces discovered '" + foundAll[0].Value + "'.");

            // Test interface-pinned scan (@127.0.0.1 and active LAN IP if available)
            InitConn(cli2DP, cli2Lobby, "@127.0.0.1");
            var foundLoop = EnumGames(cli2DP, 1000);
            Check(foundLoop.Count == 1 && foundLoop[0].Key == sessGuid, "@127.0.0.1 interface scan found Host");

            string activeLanIp = null;
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        activeLanIp = ua.Address.ToString();
                        break;
                    }
                }
                if (activeLanIp != null) break;
            }
            if (activeLanIp != null)
            {
                InitConn(cli2DP, cli2Lobby, "@" + activeLanIp);
                var foundLan = EnumGames(cli2DP, 1200);
                Check(foundLan.Count == 1 && foundLan[0].Key == sessGuid, "@" + activeLanIp + " LAN interface scan found Host");
                Console.WriteLine("[PASS] Test 3b: Specific interface selection (@127.0.0.1 and @" + activeLanIp + ") verified.");
            }
            else
            {
                Console.WriteLine("[PASS] Test 3b: Specific interface selection (@127.0.0.1) verified.");
            }

            // Test hostname:port TCP single-port discovery & 3-player Join + IdentifyHost!
            InitConn(cli1DP, cli1Lobby, "localhost:12357");
            var foundTcp = EnumGames(cli1DP, 2000);
            Check(foundTcp.Count == 1 && foundTcp[0].Key == sessGuid, "hostname:port (localhost:12357) TCP discovery");
            JoinOpen(cli1DP, sessGuid);
            uint cli1Pid = MakePlayer(cli1DP, "France");
            Check(cli1Pid == 2, "Client 1 DPID == 2");

            // Replicate IdentifyHost() from WNetMgr.cpp: EnumPlayers(DPENUMPLAYERS_REMOTE=0x10) + GetPlayerData == 1
            uint detectedHostId = 0;
            Vt<EnumPlayers_Del>(cli1DP, 0x30)(cli1DP, IntPtr.Zero, NativeCb((EnumPlayersCb)delegate(uint pid, uint ptype, IntPtr nm, uint fl, IntPtr ctx)
            {
                IntPtr db = Marshal.AllocHGlobal(4);
                int dsz = 4;
                if (Vt<GetPlayerData_Del>(cli1DP, 0x50)(cli1DP, pid, db, ref dsz, 0) == DP_OK && dsz == 4)
                {
                    if (Marshal.ReadInt32(db) == 1) detectedHostId = pid;
                }
                Marshal.FreeHGlobal(db);
                return 1;
            }), IntPtr.Zero, 0x10);
            Check(detectedHostId == 1, "Client 1 IdentifyHost() found Host DPID == 1");

            // Client 2 joins via 127.0.0.1:12357@Loopback
            InitConn(cli2DP, cli2Lobby, "127.0.0.1:12357@Loopback");
            var foundC2 = EnumGames(cli2DP, 2000);
            Check(foundC2.Count == 1, "Client 2 host:port@iface discovery");
            JoinOpen(cli2DP, sessGuid);
            uint cli2Pid = MakePlayer(cli2DP, "Spain");
            Check(cli2Pid == 3, "Client 2 DPID == 3");
            Console.WriteLine("[PASS] Test 4: 3-player session (Host + localhost:12357 + 127.0.0.1:12357@Loopback) & IdentifyHost() verified.");

            // Test Broadcast Send (idTo = 0) of 4096-byte campaign state hunk + Unicast Send
            byte[] bigPayload = new byte[4096];
            for (int i = 0; i < bigPayload.Length; i++) bigPayload[i] = (byte)((i * 37) & 0xFF);
            IntPtr pBig = Marshal.AllocHGlobal(bigPayload.Length);
            Marshal.Copy(bigPayload, 0, pBig, bigPayload.Length);
            Check(Vt<Send_Del>(hostDP, 0x68)(hostDP, hostPid, 0, 1, pBig, (uint)bigPayload.Length) == DP_OK, "Host broadcast Send");
            Marshal.FreeHGlobal(pBig);

            uint f1, t1, f2, t2;
            byte[] r1 = WaitReceive(cli1DP, out f1, out t1, 2000);
            byte[] r2 = WaitReceive(cli2DP, out f2, out t2, 2000);
            Check(r1 != null && r1.Length == 4096 && f1 == 1 && t1 == 0, "Client 1 received 4096B broadcast");
            Check(r2 != null && r2.Length == 4096 && f2 == 1 && t2 == 0 && r2[4095] == bigPayload[4095], "Client 2 received exact 4096B broadcast");

            // Unicast from France (2) -> Britain (1); Spain (3) must NOT receive it
            byte[] secretTreaty = Encoding.ASCII.GetBytes("ALLIANCE_OFFER_1520");
            IntPtr pSec = Marshal.AllocHGlobal(secretTreaty.Length);
            Marshal.Copy(secretTreaty, 0, pSec, secretTreaty.Length);
            Check(Vt<Send_Del>(cli1DP, 0x68)(cli1DP, cli1Pid, hostPid, 1, pSec, (uint)secretTreaty.Length) == DP_OK, "Client 1 unicast Send");
            Marshal.FreeHGlobal(pSec);

            uint fh, th, fs, ts;
            byte[] rh = WaitReceive(hostDP, out fh, out th, 2000);
            Check(rh != null && Encoding.ASCII.GetString(rh) == "ALLIANCE_OFFER_1520" && fh == 2 && th == 1, "Host received unicast");
            Check(TwoPassReceive(cli2DP, out fs, out ts) == null, "Client 2 did not receive unicast meant for Host");

            // Test Advertise(false) -> GetSessionDesc + SetSessionDesc (DPSYS_SETSESSIONDESC = 0x0104)
            int sdescSz = 0;
            Check(Vt<GetSessionDesc_Del>(hostDP, 0x58)(hostDP, IntPtr.Zero, ref sdescSz) == DPERR_BUFFERTOOSMALL, "GetSessionDesc size");
            IntPtr sdescBuf = Marshal.AllocHGlobal(sdescSz);
            Check(Vt<GetSessionDesc_Del>(hostDP, 0x58)(hostDP, sdescBuf, ref sdescSz) == DP_OK, "GetSessionDesc read");
            int curFlags = Marshal.ReadInt32(sdescBuf, 0x04);
            Marshal.WriteInt32(sdescBuf, 0x04, curFlags | 0x21); // disable new joins
            Check(Vt<SetSessionDesc_Del>(hostDP, 0x7C)(hostDP, sdescBuf, 0) == DP_OK, "SetSessionDesc");
            Marshal.FreeHGlobal(sdescBuf);

            byte[] sysDesc = WaitReceive(cli1DP, out f1, out t1, 2000);
            Check(sysDesc != null && f1 == 0 && BitConverter.ToUInt32(sysDesc, 0) == 0x0104, "Client 1 received DPSYS_SETSESSIONDESC");
            Console.WriteLine("[PASS] Test 5: Broadcast, Unicast, Two-Pass Receive, and SetSessionDesc verified.");

            // Test Client 2 disconnect -> DPSYS_DESTROYPLAYERORGROUP (0x0005, playerType=1, dpId=3)
            Vt<Close_Del>(cli2DP, 0x10)(cli2DP);
            Vt<Release_Del>(cli2DP, 0x08)(cli2DP);
            Vt<Release_Del>(cli2Lobby, 0x08)(cli2Lobby);

            byte[] dropMsg = WaitReceive(hostDP, out fh, out th, 2000);
            Check(dropMsg != null && fh == 0 &&
                  BitConverter.ToUInt32(dropMsg, 0) == 0x0005 &&
                  BitConverter.ToUInt32(dropMsg, 4) == 1 &&
                  BitConverter.ToUInt32(dropMsg, 8) == 3,
                  "Host received DPMSG_DESTROYPLAYERORGROUP for DPID 3");

            // Drain DPSYS_SETSESSIONDESC & DESTROYPLAYER on cli1, then close Host -> DPSYS_SESSIONLOST (0x0021)
            WaitReceive(cli1DP, out f1, out t1, 500);
            Vt<Close_Del>(hostDP, 0x10)(hostDP);
            Vt<Release_Del>(hostDP, 0x08)(hostDP);
            Vt<Release_Del>(hostLobby, 0x08)(hostLobby);

            byte[] lostMsg = WaitReceive(cli1DP, out f1, out t1, 2000);
            while (lostMsg != null && BitConverter.ToUInt32(lostMsg, 0) != 0x0021)
                lostMsg = WaitReceive(cli1DP, out f1, out t1, 2000);
            Check(lostMsg != null && f1 == 0 && BitConverter.ToUInt32(lostMsg, 0) == 0x0021, "Client 1 received DPSYS_SESSIONLOST");
            Vt<Close_Del>(cli1DP, 0x10)(cli1DP);
            Vt<Release_Del>(cli1DP, 0x08)(cli1DP);
            Vt<Release_Del>(cli1Lobby, 0x08)(cli1Lobby);

            // Immediate Re-Host on exact same port 12357 without restarting process!
            IntPtr host2DP, host2Lob, cliReDP, cliReLob;
            coCreate(ref gDp, IntPtr.Zero, 1, ref iDp, out host2DP);
            coCreate(ref gLob, IntPtr.Zero, 1, ref iLob, out host2Lob);
            coCreate(ref gDp, IntPtr.Zero, 1, ref iDp, out cliReDP);
            coCreate(ref gLob, IntPtr.Zero, 1, ref iLob, out cliReLob);

            InitConn(host2DP, host2Lob, null);
            Guid sess2 = HostOpen(host2DP, "Second Session No Reboot");
            MakePlayer(host2DP, "Holland");

            InitConn(cliReDP, cliReLob, "127.0.0.1:12357");
            var foundRe = EnumGames(cliReDP, 2000);
            Check(foundRe.Count == 1 && foundRe[0].Key == sess2, "Immediate second session discovered on same port");
            JoinOpen(cliReDP, sess2);
            uint rePid = MakePlayer(cliReDP, "Sweden");
            Check(rePid == 2, "Second session client DPID == 2");

            Vt<Close_Del>(cliReDP, 0x10)(cliReDP);
            Vt<Release_Del>(cliReDP, 0x08)(cliReDP);
            Vt<Release_Del>(cliReLob, 0x08)(cliReLob);
            Vt<Close_Del>(host2DP, 0x10)(host2DP);
            Vt<Release_Del>(host2DP, 0x08)(host2DP);
            Vt<Release_Del>(host2Lob, 0x08)(host2Lob);

            Console.WriteLine("[PASS] Test 6: Disconnect system events & immediate same-port reconnect without restart verified.");

            // Test 7: Execute exact 39-byte native x86 DPlayCoCreateThunk from patched Imperialism II.exe
            string exePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dllPath), "Imperialism II.exe");
            if (!File.Exists(exePath)) exePath = Path.Combine(Path.GetDirectoryName(dllPath), "ImpII.exe");
            VerifyExeX86Thunk(exePath);
            Console.WriteLine("[PASS] Test 7: Native x86 DPlayCoCreateThunk execution (primary + fallback branches) verified.");

            // Test 8: Cross-process Host <-> Client session over real OS sockets
            IntPtr mpHostDP, mpHostLob;
            coCreate(ref gDp, IntPtr.Zero, 1, ref iDp, out mpHostDP);
            coCreate(ref gLob, IntPtr.Zero, 1, ref iLob, out mpHostLob);
            InitConn(mpHostDP, mpHostLob, null);
            HostOpen(mpHostDP, "Cross-Process Session");
            uint mpHostPid = MakePlayer(mpHostDP, "Russia");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = typeof(TestMultiplayer).Assembly.Location;
            psi.Arguments = "--client-worker \"" + dllPath + "\" 12357";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process child = Process.Start(psi);

            uint mpFrom, mpTo;
            byte[] childMsg = WaitReceive(mpHostDP, out mpFrom, out mpTo, 5000);
            Check(childMsg != null && mpFrom == 2 && mpTo == 1 && Encoding.ASCII.GetString(childMsg) == "HELLO_FROM_CHILD_PROCESS",
                  "Host process received packet from Child OS process");

            byte[] welcome = Encoding.ASCII.GetBytes("WELCOME_FROM_HOST_PROCESS");
            IntPtr pWelcome = Marshal.AllocHGlobal(welcome.Length);
            Marshal.Copy(welcome, 0, pWelcome, welcome.Length);
            Check(Vt<Send_Del>(mpHostDP, 0x68)(mpHostDP, mpHostPid, 2, 1, pWelcome, (uint)welcome.Length) == DP_OK, "Host reply to Child OS process");
            Marshal.FreeHGlobal(pWelcome);

            bool exited = child.WaitForExit(5000);
            if (!exited) { child.Kill(); throw new Exception("Client worker timed out."); }
            string childErr = child.StandardError.ReadToEnd();
            Check(exited && child.ExitCode == 0, "Child OS process exited cleanly: " + childErr);

            byte[] childDrop = WaitReceive(mpHostDP, out mpFrom, out mpTo, 2000);
            Check(childDrop != null && mpFrom == 0 && BitConverter.ToUInt32(childDrop, 0) == 0x0005,
                  "Host received DPSYS_DESTROYPLAYERORGROUP when Child OS process closed");

            Vt<Close_Del>(mpHostDP, 0x10)(mpHostDP);
            Vt<Release_Del>(mpHostDP, 0x08)(mpHostDP);
            Vt<Release_Del>(mpHostLob, 0x08)(mpHostLob);
            Console.WriteLine("[PASS] Test 8: Separate OS process Host <-> Client multiplayer session verified.");

            string worldTestRoot = Path.Combine(Path.GetDirectoryName(typeof(TestMultiplayer).Assembly.Location), "world-tests-" + Guid.NewGuid().ToString("N"));
            TestWorldValidation(Path.Combine(worldTestRoot, "validation"));
            TestWorldTransfers(coCreate, worldTestRoot);
            TestCrossProcessWorld(coCreate, dllPath, worldTestRoot);
            TestNativeWorldHooks(exePath, hMod);

            Console.WriteLine("\nALL MULTIPLAYER INTEGRATION TESTS PASSED SUCCESSFULLY!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("TEST FAILED: " + ex);
            return 1;
        }
    }
}
