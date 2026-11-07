.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly extern Imp2Net { .ver 0:0:0:0 }
.assembly dplayx {}
.module dplayx.dll
.corflags 0x00000002 // 32BITREQUIRED
.mresource public Imp2Net.dll {}

.vtfixup [1] int32 fromunmanaged at VT_01
.vtfixup [1] int32 fromunmanaged at VT_02
.vtfixup [1] int32 fromunmanaged at VT_03
.vtfixup [1] int32 fromunmanaged at VT_04
.vtfixup [1] int32 fromunmanaged at VT_05
.vtfixup [1] int32 fromunmanaged at VT_06
.vtfixup [1] int32 fromunmanaged at VT_07
.vtfixup [1] int32 fromunmanaged at VT_08
.vtfixup [1] int32 fromunmanaged at VT_09
.vtfixup [1] int32 fromunmanaged at VT_10
.vtfixup [1] int32 fromunmanaged at VT_11
.vtfixup [1] int32 fromunmanaged at VT_12
.vtfixup [1] int32 fromunmanaged at VT_13

.data VT_01 = int32(0)
.data VT_02 = int32(0)
.data VT_03 = int32(0)
.data VT_04 = int32(0)
.data VT_05 = int32(0)
.data VT_06 = int32(0)
.data VT_07 = int32(0)
.data VT_08 = int32(0)
.data VT_09 = int32(0)
.data VT_10 = int32(0)
.data VT_11 = int32(0)
.data VT_12 = int32(0)
.data VT_13 = int32(0)

.class private abstract auto ansi sealed DPlayBridge extends [mscorlib]System.Object
{
    .field private static bool s_init

    .method public hidebysig static void Ensure() cil managed
    {
        ldsfld bool DPlayBridge::s_init
        brtrue.s DONE
        ldc.i4.1
        stsfld bool DPlayBridge::s_init
        call class [mscorlib]System.AppDomain [mscorlib]System.AppDomain::get_CurrentDomain()
        ldnull
        ldftn class [mscorlib]System.Reflection.Assembly DPlayBridge::OnResolve(object, class [mscorlib]System.ResolveEventArgs)
        newobj instance void [mscorlib]System.ResolveEventHandler::.ctor(object, native int)
        callvirt instance void [mscorlib]System.AppDomain::add_AssemblyResolve(class [mscorlib]System.ResolveEventHandler)
    DONE:
        ret
    }

    .method private hidebysig static class [mscorlib]System.Reflection.Assembly OnResolve(object s, class [mscorlib]System.ResolveEventArgs e) cil managed
    {
        .locals init (class [mscorlib]System.IO.Stream st, uint8[] buf, int32 len, string reqName)
        ldarg.1
        callvirt instance string [mscorlib]System.ResolveEventArgs::get_Name()
        stloc.3
        ldloc.3
        ldstr "Imp2Net"
        callvirt instance bool [mscorlib]System.String::StartsWith(string)
        brtrue.s READ_RES
        ldnull
        ret
    READ_RES:
        call class [mscorlib]System.Reflection.Assembly [mscorlib]System.Reflection.Assembly::GetExecutingAssembly()
        ldstr "Imp2Net.dll"
        callvirt instance class [mscorlib]System.IO.Stream [mscorlib]System.Reflection.Assembly::GetManifestResourceStream(string)
        stloc.0
        ldloc.0
        brtrue.s LOAD_BYTES
        ldnull
        ret
    LOAD_BYTES:
        ldloc.0
        callvirt instance int64 [mscorlib]System.IO.Stream::get_Length()
        conv.i4
        stloc.2
        ldloc.2
        newarr [mscorlib]System.Byte
        stloc.1
        ldloc.0
        ldloc.1
        ldc.i4.0
        ldloc.2
        callvirt instance int32 [mscorlib]System.IO.Stream::Read(uint8[], int32, int32)
        pop
        ldloc.0
        callvirt instance void [mscorlib]System.IO.Stream::Close()
        ldloc.1
        call class [mscorlib]System.Reflection.Assembly [mscorlib]System.Reflection.Assembly::Load(uint8[])
        ret
    }

    // Separate forwarding helpers so JIT only resolves [Imp2Net] AFTER Ensure() registers AssemblyResolve
    .method private hidebysig static int32 FwdCreate(native int a, native int b, native int c) cil managed noinlining
    {
        ldarg.0
        ldarg.1
        ldarg.2
        call int32 [Imp2Net]Imp2Net.Exports::DirectPlayCreate(native int, native int, native int)
        ret
    }

    .method private hidebysig static int32 FwdEnumA(native int cb, native int ctx) cil managed noinlining
    {
        ldarg.0
        ldarg.1
        call int32 [Imp2Net]Imp2Net.Exports::DirectPlayEnumerateA(native int, native int)
        ret
    }

    .method private hidebysig static int32 FwdLobbyCreateA(native int a, native int b, native int c, native int d, uint32 e) cil managed noinlining
    {
        ldarg.0
        ldarg.1
        ldarg.2
        ldarg.3
        ldarg.s 4
        call int32 [Imp2Net]Imp2Net.Exports::DirectPlayLobbyCreateA(native int, native int, native int, native int, uint32)
        ret
    }

    .method private hidebysig static int32 FwdGetClassObj(native int a, native int b, native int c) cil managed noinlining
    {
        ldarg.0
        ldarg.1
        ldarg.2
        call int32 [Imp2Net]Imp2Net.Exports::DllGetClassObject(native int, native int, native int)
        ret
    }

    .method private hidebysig static int32 FwdCoCreate(native int a, native int b, uint32 c, native int d, native int e) cil managed noinlining
    {
        ldarg.0
        ldarg.1
        ldarg.2
        ldarg.3
        ldarg.s 4
        call int32 [Imp2Net]Imp2Net.Exports::Imp2CoCreateInstance(native int, native int, uint32, native int, native int)
        ret
    }

    // Exported Win32 stdcall entry points
    .method private hidebysig static int32 FwdLoadWorld(int32 saveId, native int prefix) cil managed noinlining
    {
        ldarg.0
        ldarg.1
        call int32 [Imp2Net]Imp2Net.Exports::Imp2LoadWorld(int32, native int)
        ret
    }

    .method private hidebysig static int32 FwdCanStart() cil managed noinlining
    {
        call int32 [Imp2Net]Imp2Net.Exports::Imp2CanStart()
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvCdecl)
        Imp2LoadWorld(int32 saveId, native int prefix) cil managed
    {
        .vtentry 12 : 1
        .export [12] as Imp2LoadWorld
        call void DPlayBridge::Ensure()
        ldarg.0
        ldarg.1
        call int32 DPlayBridge::FwdLoadWorld(int32, native int)
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        Imp2CanStart() cil managed
    {
        .vtentry 13 : 1
        .export [13] as Imp2CanStart
        call void DPlayBridge::Ensure()
        call int32 DPlayBridge::FwdCanStart()
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DirectPlayCreate(native int lpGUID, native int lplpDP, native int pUnk) cil managed
    {
        .vtentry 1 : 1
        .export [1] as DirectPlayCreate
        call void DPlayBridge::Ensure()
        ldarg.0
        ldarg.1
        ldarg.2
        call int32 DPlayBridge::FwdCreate(native int, native int, native int)
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DirectPlayEnumerateA(native int lpEnumDPCallback, native int lpContext) cil managed
    {
        .vtentry 2 : 1
        .export [2] as DirectPlayEnumerateA
        call void DPlayBridge::Ensure()
        ldarg.0
        ldarg.1
        call int32 DPlayBridge::FwdEnumA(native int, native int)
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DirectPlayEnumerateW(native int lpEnumDPCallback, native int lpContext) cil managed
    {
        .vtentry 3 : 1
        .export [3] as DirectPlayEnumerateW
        ldc.i4 0x80004001
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DirectPlayLobbyCreateA(native int a, native int b, native int c, native int d, uint32 e) cil managed
    {
        .vtentry 4 : 1
        .export [4] as DirectPlayLobbyCreateA
        call void DPlayBridge::Ensure()
        ldarg.0
        ldarg.1
        ldarg.2
        ldarg.3
        ldarg.s 4
        call int32 DPlayBridge::FwdLobbyCreateA(native int, native int, native int, native int, uint32)
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DirectPlayLobbyCreateW(native int a, native int b, native int c, native int d, uint32 e) cil managed
    {
        .vtentry 5 : 1
        .export [5] as DirectPlayLobbyCreateW
        ldc.i4 0x80004001
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DllCanUnloadNow() cil managed
    {
        .vtentry 6 : 1
        .export [6] as DllCanUnloadNow
        ldc.i4.1 // S_FALSE
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DllGetClassObject(native int rclsid, native int riid, native int ppv) cil managed
    {
        .vtentry 7 : 1
        .export [7] as DllGetClassObject
        call void DPlayBridge::Ensure()
        ldarg.0
        ldarg.1
        ldarg.2
        call int32 DPlayBridge::FwdGetClassObj(native int, native int, native int)
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DllRegisterServer() cil managed
    {
        .vtentry 8 : 1
        .export [8] as DllRegisterServer
        ldc.i4.0
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DirectPlayEnumerate(native int lpEnumDPCallback, native int lpContext) cil managed
    {
        .vtentry 9 : 1
        .export [9] as DirectPlayEnumerate
        call void DPlayBridge::Ensure()
        ldarg.0
        ldarg.1
        call int32 DPlayBridge::FwdEnumA(native int, native int)
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        DllUnregisterServer() cil managed
    {
        .vtentry 10 : 1
        .export [10] as DllUnregisterServer
        ldc.i4.0
        ret
    }

    .method public hidebysig static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall)
        Imp2CoCreateInstance(native int rclsid, native int pUnkOuter, uint32 dwClsContext, native int riid, native int ppv) cil managed
    {
        .vtentry 11 : 1
        .export [11] as Imp2CoCreateInstance
        call void DPlayBridge::Ensure()
        ldarg.0
        ldarg.1
        ldarg.2
        ldarg.3
        ldarg.s 4
        call int32 DPlayBridge::FwdCoCreate(native int, native int, uint32, native int, native int)
        ret
    }
}
