using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SI.Bindable;

namespace FMBridge.World;

/// <summary>
/// Direct native calls into SI.Bindable.Bindings' span-taking entry points.
///
/// Il2CppInterop cannot marshal ReadOnlySpan&lt;char&gt; arguments: spans are
/// ref structs, il2cpp cannot box them, so every span-taking generated
/// wrapper crosses the boundary with length 0. Proven live Aug 26:
/// MakeKey("abc") == MakeKey("xyz") == MakeKey(""), and rooted
/// Create(parent, "Name") returned the parent's own key (empty relative
/// path). Key-only APIs (Bind/Set/Get by Bindings.Key) marshal fine through
/// the generated wrappers — only the path-based calls need this detour.
///
/// Mechanics: resolve the il2cpp MethodInfo* by name + param count from the
/// Bindings class, read the native function pointer (first field of
/// MethodInfo), and call it with a hand-built native span struct
/// { char* ptr; int length; } over unmanaged UTF-16 memory. il2cpp instance
/// methods are (this, args..., MethodInfo*); `in` params are pointers;
/// Bindings.Key is a single ulong returned in x0 on arm64.
/// </summary>
internal static class NativeBindings
{
    private static bool _resolved;
    private static IntPtr _miCreateRooted; // Create(in Key, in ReadOnlySpan<char>, NodeFlags)
    private static IntPtr _miCreatePath;   // Create(in ReadOnlySpan<char>, NodeFlags)
    private static IntPtr _miMakeKeyPath;  // static MakeKey(in ReadOnlySpan<char>)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong CreateRootedDel(IntPtr self, IntPtr rootKey, IntPtr span, int flags, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong CreatePathDel(IntPtr self, IntPtr span, int flags, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong MakeKeyDel(IntPtr span, IntPtr mi);

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        var klass = Il2CppClassPointerStore<Bindings>.NativeClassPtr;
        if (klass == IntPtr.Zero) return;
        var iter = IntPtr.Zero;
        IntPtr m;
        while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
        {
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(m));
            var pc = (int)IL2CPP.il2cpp_method_get_param_count(m);
            if (name == "Create" && pc == 3 && _miCreateRooted == IntPtr.Zero) _miCreateRooted = m;
            else if (name == "Create" && pc == 2 && _miCreatePath == IntPtr.Zero) _miCreatePath = m;
            else if (name == "MakeKey" && pc == 1 && _miMakeKeyPath == IntPtr.Zero) _miMakeKeyPath = m;
        }
    }

    public static bool Resolved
    {
        get
        {
            Resolve();
            return _miCreateRooted != IntPtr.Zero && _miCreatePath != IntPtr.Zero && _miMakeKeyPath != IntPtr.Zero;
        }
    }

    private static ulong WithNativeSpan(string s, Func<IntPtr, ulong> call)
    {
        var str = s ?? string.Empty;
        var strMem = Marshal.StringToHGlobalUni(str);
        var spanMem = Marshal.AllocHGlobal(IntPtr.Size + 8);
        try
        {
            Marshal.WriteIntPtr(spanMem, 0, strMem);
            Marshal.WriteInt32(spanMem, IntPtr.Size, str.Length);
            return call(spanMem);
        }
        finally
        {
            Marshal.FreeHGlobal(spanMem);
            Marshal.FreeHGlobal(strMem);
        }
    }

    public static Bindings.Key CreateRooted(Bindings bindings, Bindings.Key root, string relPath, Bindings.NodeFlags flags)
    {
        Resolve();
        if (_miCreateRooted == IntPtr.Zero) throw new InvalidOperationException("native Create(rooted) not resolved");
        var fn = Marshal.GetDelegateForFunctionPointer<CreateRootedDel>(Marshal.ReadIntPtr(_miCreateRooted));
        var rootMem = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(rootMem, unchecked((long)root.m_key));
            var raw = WithNativeSpan(relPath, span => fn(bindings.Pointer, rootMem, span, (int)flags, _miCreateRooted));
            return new Bindings.Key(raw);
        }
        finally { Marshal.FreeHGlobal(rootMem); }
    }

    public static Bindings.Key CreatePath(Bindings bindings, string path, Bindings.NodeFlags flags)
    {
        Resolve();
        if (_miCreatePath == IntPtr.Zero) throw new InvalidOperationException("native Create(path) not resolved");
        var fn = Marshal.GetDelegateForFunctionPointer<CreatePathDel>(Marshal.ReadIntPtr(_miCreatePath));
        var raw = WithNativeSpan(path, span => fn(bindings.Pointer, span, (int)flags, _miCreatePath));
        return new Bindings.Key(raw);
    }

    public static ulong MakeKeyRaw(string path)
    {
        Resolve();
        if (_miMakeKeyPath == IntPtr.Zero) throw new InvalidOperationException("native MakeKey not resolved");
        var fn = Marshal.GetDelegateForFunctionPointer<MakeKeyDel>(Marshal.ReadIntPtr(_miMakeKeyPath));
        return WithNativeSpan(path, span => fn(span, _miMakeKeyPath));
    }

    /// <summary>Distinct strings must hash to distinct keys and differ from
    /// the empty hash — regression canary for span marshaling.</summary>
    public static bool SpanCheck()
    {
        try
        {
            var a = MakeKeyRaw("abc");
            var b = MakeKeyRaw("xyz");
            var e = MakeKeyRaw("");
            return a != b && a != e && b != e;
        }
        catch { return false; }
    }
}
