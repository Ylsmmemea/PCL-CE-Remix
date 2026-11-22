using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PCL.Core.ShaderCompiler;

// 把编好的 .ps 反汇编成可读指令 跟参考实现比对算法用 也走 d3dcompiler_47.dll
internal static class Disassembler
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DDisassemble(
        byte[] pSrcData,
        IntPtr srcDataSize,
        uint flags,
        string? szComments,
        out IntPtr ppDisassembly);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointerDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferSizeDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr thisPtr);

    private const int VtblRelease = 2;
    private const int VtblGetBufferPointer = 3;
    private const int VtblGetBufferSize = 4;

    private static T GetComMethod<T>(IntPtr comObject, int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var entry = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }

    public static bool Run(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"找不到文件：{path}");
            return false;
        }

        var data = File.ReadAllBytes(path);
        var hr = D3DDisassemble(data, (IntPtr)data.Length, 0, null, out var blob);
        if (hr != 0 || blob == IntPtr.Zero)
        {
            Console.Error.WriteLine($"反汇编失败（HRESULT 0x{hr:X8}）");
            return false;
        }

        try
        {
            var size = (int)GetComMethod<GetBufferSizeDelegate>(blob, VtblGetBufferSize)(blob);
            var ptr = GetComMethod<GetBufferPointerDelegate>(blob, VtblGetBufferPointer)(blob);
            var buf = new byte[size];
            Marshal.Copy(ptr, buf, 0, size);
            Console.WriteLine(Encoding.ASCII.GetString(buf));
            return true;
        }
        finally
        {
            GetComMethod<ReleaseDelegate>(blob, VtblRelease)(blob);
        }
    }
}
