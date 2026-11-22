using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PCL.Core.ShaderCompiler;

// 把同目录的 .hlsl 编译成 WPF 能用的 .ps
internal static class Program
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        byte[] pSrcData,
        IntPtr srcDataSize,
        string? pSourceName,
        IntPtr pDefines,
        IntPtr pInclude,
        string pEntrypoint,
        string pTarget,
        uint flags1,
        uint flags2,
        out IntPtr ppCode,
        out IntPtr ppErrorMsgs);

    // ID3DBlob 的缓冲区读取方法签名
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointerDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferSizeDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr thisPtr);

    // ID3DBlob 的 vtable 继承自 IUnknown 顺序是 QueryInterface AddRef Release GetBufferPointer GetBufferSize
    private const int VtblRelease = 2;
    private const int VtblGetBufferPointer = 3;
    private const int VtblGetBufferSize = 4;

    // 从 COM 对象的 vtable 里取方法
    private static T _GetComMethod<T>(IntPtr comObject, int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var entry = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }

    // 读 ID3DBlob 的内容
    private static byte[] _ReadBlob(IntPtr blob)
    {
        var size = (int)_GetComMethod<GetBufferSizeDelegate>(blob, VtblGetBufferSize)(blob);
        if (size <= 0) return [];

        var data = _GetComMethod<GetBufferPointerDelegate>(blob, VtblGetBufferPointer)(blob);
        var buffer = new byte[size];
        Marshal.Copy(data, buffer, 0, size);
        return buffer;
    }

    private static void _ReleaseCom(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;
        try
        {
            _GetComMethod<ReleaseDelegate>(comObject, VtblRelease)(comObject);
        }
        catch
        {
            // 释放失败就算了
        }
    }

    private const string EntryPoint = "PixelShaderFunction";
    private const string Target = "ps_3_0";

    private static int Main(string[] args)
    {
        // 传 .ps 进来就是反汇编模式 输出可读指令 跟参考实现比对用
        var psArg = Array.Find(args, a => a.EndsWith(".ps", StringComparison.OrdinalIgnoreCase));
        if (psArg is not null)
            return Disassembler.Run(File.Exists(psArg) ? psArg : Path.GetFullPath(psArg)) ? 0 : 1;

        var shaderDir = _ResolveShaderDirectory();
        if (shaderDir is null)
        {
            Console.Error.WriteLine("找不到着色器目录。请在 ShaderCompiler 目录内运行，或把着色器目录路径作为第一个参数传入。");
            return 1;
        }

        var files = args.Length > 0 && !Directory.Exists(args[0])
            ? args.Select(a => Path.Combine(shaderDir, a)).ToArray()
            : Directory.GetFiles(shaderDir, "*.hlsl").OrderBy(f => f).ToArray();

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"在 {shaderDir} 下没有找到 .hlsl 文件。");
            return 1;
        }

        var failed = 0;
        foreach (var hlslPath in files)
        {
            if (!File.Exists(hlslPath))
            {
                Console.Error.WriteLine($"找不到文件：{hlslPath}");
                failed++;
                continue;
            }

            if (!_CompileFile(hlslPath))
                failed++;
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"全部编译完成（{files.Length} 个文件）。"
            : $"编译结束，{failed} 个文件失败。");

        return failed == 0 ? 0 : 1;
    }

    // 编译一个 .hlsl 输出同名的 .ps
    private static bool _CompileFile(string hlslPath)
    {
        var name = Path.GetFileName(hlslPath);
        var outputPath = Path.ChangeExtension(hlslPath, ".ps");
        Console.WriteLine($"编译 {name} → {Path.GetFileName(outputPath)}  (入口 {EntryPoint}, 目标 {Target})");

        var source = File.ReadAllBytes(hlslPath);
        var code = IntPtr.Zero;
        var errors = IntPtr.Zero;
        try
        {
            var hr = D3DCompile(source, (IntPtr)source.Length, name,
                IntPtr.Zero, IntPtr.Zero, EntryPoint, Target, 0, 0, out code, out errors);

            if (hr != 0 || code == IntPtr.Zero)
            {
                Console.Error.WriteLine($"  失败（HRESULT 0x{hr:X8}）");
                if (errors != IntPtr.Zero)
                {
                    var message = System.Text.Encoding.ASCII.GetString(_ReadBlob(errors));
                    if (!string.IsNullOrWhiteSpace(message))
                        Console.Error.WriteLine("  " + message.Trim());
                }

                return false;
            }

            var buffer = _ReadBlob(code);
            File.WriteAllBytes(outputPath, buffer);
            Console.WriteLine($"  成功，{buffer.Length} 字节");
            return true;
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("  失败：找不到 d3dcompiler_47.dll。该文件通常位于 C:\\Windows\\System32。");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  失败：{ex.Message}");
            return false;
        }
        finally
        {
            _ReleaseCom(code);
            _ReleaseCom(errors);
        }
    }

    // 找着色器目录 先看命令行参数 再从当前目录往上翻
    private static string? _ResolveShaderDirectory()
    {
        var explicitDir = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(Directory.Exists);
        if (explicitDir is not null) return Path.GetFullPath(explicitDir);

        // 当前工作目录和程序集所在目录都拿来当起点往上找
        var probes = new List<string> { Directory.GetCurrentDirectory() };
        var asmDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(asmDir)) probes.Add(asmDir);

        foreach (var start in probes)
        {
            var dir = new DirectoryInfo(start);
            for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                if (dir.GetFiles("*.hlsl").Length > 0)
                    return dir.FullName;

                var candidate = Path.Combine(dir.FullName, "UI", "Assets", "Shaders");
                if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.hlsl").Length > 0)
                    return candidate;
            }
        }

        return null;
    }
}
