using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.Utils;

namespace PCL.Core.Server;

// 服务器实例 一个实例就是一个文件夹 元数据跟服务端文件放在一起 整个目录拷走就能迁移
public sealed class ServerInstance
{
    public const string MetaFileName = ".pclmeta.json";

    // 实例统一放在便携数据目录的 Servers 下 目录名就是实例名
    public static string RootDirectory => Path.Combine(Paths.Data, "Servers");

    // 元数据文件所在目录 由 Load Create 赋值
    [JsonIgnore] public string DirectoryPath { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    // 服务端对应的 Minecraft 版本 id 例如 1.20.1
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    // vanilla 或 paper
    [JsonPropertyName("type")] public string ServerType { get; set; } = "vanilla";

    // 仅 paper 用
    [JsonPropertyName("paper_build")] public int PaperBuild { get; set; }

    [JsonPropertyName("java_path")] public string JavaPath { get; set; } = "";

    [JsonPropertyName("memory_mb")] public int MemoryMb { get; set; } = 2048;

    [JsonPropertyName("port")] public int Port { get; set; } = 25565;

    [JsonPropertyName("eula_accepted")] public bool EulaAccepted { get; set; }

    [JsonPropertyName("max_players")] public int MaxPlayers { get; set; } = 10;

    [JsonPropertyName("motd")] public string Motd { get; set; } = "";

    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }

    // vanilla 是 {版本}-server.jar paper 固定叫 server.jar
    [JsonIgnore]
    public string JarFileName => ServerType == "paper" ? "server.jar" : $"{Version}-server.jar";

    [JsonIgnore] public string JarPath => Path.Combine(DirectoryPath, JarFileName);

    [JsonIgnore] public string LogPath => Path.Combine(DirectoryPath, "logs", "latest.log");

    // 宿主写的控制台日志 服务端输出加宿主事件 界面上显示的是这一份
    [JsonIgnore] public string ConsoleLogPath => Path.Combine(DirectoryPath, "logs", "console.log");

    [JsonIgnore] public bool IsInstalled => File.Exists(JarPath);

    private static readonly JsonSerializerOptions _JsonOptions = JsonCompat.SerializerOptions;

    public static List<ServerInstance> Enumerate()
    {
        var result = new List<ServerInstance>();
        var root = RootDirectory;
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var instance = Load(dir);
            if (instance is not null) result.Add(instance);
        }

        return result.OrderBy(instance => instance.CreatedAt).ToList();
    }

    public static ServerInstance? Load(string directoryPath)
    {
        var metaPath = Path.Combine(directoryPath, MetaFileName);
        if (!File.Exists(metaPath)) return null;

        try
        {
            var instance = JsonSerializer.Deserialize<ServerInstance>(File.ReadAllText(metaPath), _JsonOptions);
            if (instance is null) return null;
            instance.DirectoryPath = directoryPath;
            return instance;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ServerInstance Create(string name, string version, string serverType, int paperBuild, int memoryMb)
    {
        var directoryPath = Path.Combine(RootDirectory, SanitizeName(name));
        Directory.CreateDirectory(directoryPath);

        var instance = new ServerInstance
        {
            DirectoryPath = directoryPath,
            Name = name.Trim(),
            Version = version,
            ServerType = serverType,
            PaperBuild = paperBuild,
            MemoryMb = memoryMb,
            CreatedAt = DateTimeOffset.UtcNow
        };
        instance.Save();
        return instance;
    }

    public void Save()
        => File.WriteAllText(Path.Combine(DirectoryPath, MetaFileName),
            JsonSerializer.Serialize(this, _JsonOptions));

    // 删掉整个实例目录 调用方必须已经让用户确认过
    public void Delete()
    {
        if (Directory.Exists(DirectoryPath))
            Directory.Delete(DirectoryPath, true);
    }

    // 目录名不能带路径分隔符这类字符 用户起中文名没问题
    private static string SanitizeName(string name)
    {
        var cleaned = new string(name.Trim()
            .Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? $"server-{DateTime.Now:yyyyMMddHHmmss}" : cleaned;
    }
}
