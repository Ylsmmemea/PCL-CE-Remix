using System;
using System.Security.Cryptography;
using System.Text;

namespace PCL.ServerHost;

// 管道名和互斥量的取名规则 启动器侧要照着算同一份 改动等于破坏协议
internal static class HostNaming
{
    internal const string PipePrefix = "PCLServerHost_";
    internal const string MutexPrefix = @"Local\PCLServerHost_";

    // 实例目录的稳定短标识 路径大小写和结尾斜杠归一化之后取哈希前 16 位
    internal static string IdentifierOf(string instanceDirectory)
    {
        var normalized = instanceDirectory.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16];
    }

    internal static string PipeName(string identifier) => PipePrefix + identifier;

    internal static string MutexName(string identifier) => MutexPrefix + identifier;
}
