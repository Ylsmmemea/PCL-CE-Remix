using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Server;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

// 服务器实例的新建与安装 下载走 ModLoader 复用现成的进度显示和镜像源
public static class ServerInstallLib
{
    // 新建实例的完整流程 弹窗是模态的 必须在 UI 线程调
    public static void CreateServerFlow()
    {
        var name = ModMain.MyMsgBoxInput(Lang.Text("Server.New.Title"), Lang.Text("Server.New.NameHint"),
            Lang.Text("Server.New.DefaultName"));
        if (string.IsNullOrWhiteSpace(name)) return;

        var typeIndex = ModMain.MyMsgBoxSelect(
            [new MyRadioBox { Text = Lang.Text("Server.New.Type.Vanilla") },
                new MyRadioBox { Text = Lang.Text("Server.New.Type.Paper") }],
            Lang.Text("Server.New.TypeTitle"));
        if (typeIndex is null) return;

        if (typeIndex.Value == 0) _CreateVanillaFlow(name);
        else _CreatePaperFlow(name);
    }

    #region 原版

    // 版本列表通常在主界面加载时已经拉过 没拉完就等它拉完再弹窗
    private static void _EnsureVersionList(Action<List<JsonNode>> onReady)
    {
        var loader = ModDownload.dlClientListLoader;

        if (loader.State == ModBase.LoadState.Finished)
        {
            onReady(_ExtractReleaseVersions(loader.output.Value));
            return;
        }

        void Handler(ModLoader.LoaderBase sender, ModBase.LoadState state, ModBase.LoadState oldState)
        {
            if (state is not (ModBase.LoadState.Finished or ModBase.LoadState.Failed)) return;
            sender.OnStateChangedUi -= Handler;

            if (state == ModBase.LoadState.Failed)
            {
                HintService.Hint(Lang.Text("Server.New.VersionListFailed"), HintType.Error);
                return;
            }

            onReady(_ExtractReleaseVersions(loader.output.Value));
        }

        loader.OnStateChangedUi += Handler;
        loader.Start();
    }

    private static List<JsonNode> _ExtractReleaseVersions(JsonObject manifest)
    {
        var result = new List<JsonNode>();
        if (manifest?["versions"] is not JsonArray versions) return result;

        foreach (var version in versions)
            if (version is not null && (string?)version["type"] == "release")
                result.Add(version);

        return result;
    }

    private static void _CreateVanillaFlow(string name)
    {
        _EnsureVersionList(versions =>
        {
            if (versions.Count == 0)
            {
                HintService.Hint(Lang.Text("Server.New.VersionListFailed"), HintType.Error);
                return;
            }

            var index = ModMain.MyMsgBoxSelect(
                versions.Select(version => (IMyRadio)new MyRadioBox { Text = (string?)version["id"] ?? "" }).ToList(),
                Lang.Text("Server.New.VersionTitle"));
            if (index is null) return;

            var entry = versions[index.Value];
            var versionId = (string?)entry["id"] ?? "";
            var instance = ServerInstance.Create(name, versionId, "vanilla", 0, Config.Server.DefaultMemory);
            StartVanillaDownload(instance, (string?)entry["url"] ?? "");
        });
    }

    private static void StartVanillaDownload(ServerInstance instance, string versionJsonUrl)
    {
        var versionJsonPath = Path.Combine(instance.DirectoryPath, instance.Version + ".json");

        var loaders = new List<ModLoader.LoaderBase>
        {
            // 先下版本 JSON 服务端 jar 的地址和校验值都在里面
            new LoaderDownload(Lang.Text("Server.Install.Stage.VersionJson"),
                new List<DownloadFile>
                {
                    new(ModDownload.DlSourceLauncherOrMetaGet(versionJsonUrl), versionJsonPath,
                        new ModBase.FileChecker(canUseExistsFile: false, isJson: true))
                }) { ProgressWeight = 2d },

            // 从 JSON 里读出服务端 jar 的地址
            new ModLoader.LoaderTask<string, List<DownloadFile>>(Lang.Text("Server.Install.Stage.BuildTask"), task =>
            {
                var versionJson = (JsonObject)ModBase.GetJson(ModBase.ReadFile(versionJsonPath));
                var serverNode = versionJson["downloads"]?["server"];
                if (serverNode is null)
                {
                    File.Delete(versionJsonPath);
                    HintService.Hint(Lang.Text("Server.Install.Error.NoServerJar", instance.Version), HintType.Error);
                    task.Abort();
                    return;
                }

                task.output = new List<DownloadFile>
                {
                    new(ModDownload.DlSourceLauncherOrMetaGet((string?)serverNode["url"] ?? ""),
                        instance.JarPath,
                        new ModBase.FileChecker(1024L, (long?)serverNode["size"] ?? -1, (string?)serverNode["sha1"]))
                };
                File.Delete(versionJsonPath);
            }) { ProgressWeight = 0.5d, show = false },

            // 下服务端 jar
            new LoaderDownload(Lang.Text("Server.Install.Stage.JarFile"), []) { ProgressWeight = 5d }
        };

        _RunInstallLoader(instance, loaders, instance.Version);
    }

    #endregion

    #region Paper

    private static void _CreatePaperFlow(string name)
    {
        ModBase.RunInNewThread(() =>
        {
            List<string> versions;
            try
            {
                versions = PaperApi.ListVersionsAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Server] Paper 版本列表获取失败");
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Server.New.VersionListFailed"), HintType.Error));
                return;
            }

            ModBase.RunInUi(() =>
            {
                if (versions.Count == 0)
                {
                    HintService.Hint(Lang.Text("Server.New.VersionListFailed"), HintType.Error);
                    return;
                }

                var index = ModMain.MyMsgBoxSelect(
                    versions.Select(version => (IMyRadio)new MyRadioBox { Text = version }).ToList(),
                    Lang.Text("Server.New.VersionTitle"));
                if (index is null) return;
                var version = versions[index.Value];

                ModBase.RunInNewThread(() =>
                {
                    PaperBuild? build;
                    try
                    {
                        build = PaperApi.GetLatestBuildAsync(version).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, "[Server] Paper 构建列表获取失败");
                        ModBase.RunInUi(() => HintService.Hint(Lang.Text("Server.New.PaperBuildFailed"), HintType.Error));
                        return;
                    }

                    if (build is null)
                    {
                        ModBase.RunInUi(() => HintService.Hint(Lang.Text("Server.New.PaperBuildFailed"), HintType.Error));
                        return;
                    }

                    ModBase.RunInUi(() =>
                    {
                        var instance = ServerInstance.Create(name, version, "paper", build.BuildNumber,
                            Config.Server.DefaultMemory);
                        StartPaperDownload(instance, build);
                    });
                });
            });
        });
    }

    private static void StartPaperDownload(ServerInstance instance, PaperBuild build)
    {
        var loaders = new List<ModLoader.LoaderBase>
        {
            new LoaderDownload(Lang.Text("Server.Install.Stage.JarFile"),
                new List<DownloadFile>
                {
                    new([build.Url], instance.JarPath,
                        new ModBase.FileChecker(1024L, build.Size, build.Sha256))
                }) { ProgressWeight = 5d }
        };

        _RunInstallLoader(instance, loaders, $"{instance.Version} #{build.BuildNumber}");
    }

    #endregion

    // 跑安装任务 完成后补目录里的配置文件并通知界面刷新
    private static void _RunInstallLoader(ServerInstance instance, List<ModLoader.LoaderBase> loaders, string title)
    {
        var loader = new ModLoader.LoaderCombo<string>(Lang.Text("Server.Install.Stage.Title", title), loaders);

        void OnStateChanged(ModLoader.LoaderBase sender, ModBase.LoadState state, ModBase.LoadState oldState)
        {
            if (state is not (ModBase.LoadState.Finished or ModBase.LoadState.Failed)) return;
            sender.OnStateChangedUi -= OnStateChanged;

            if (state == ModBase.LoadState.Finished)
            {
                WriteDefaultProperties(instance);
                HintService.Hint(Lang.Text("Server.Install.Success", instance.Name), HintType.Success);
            }

            ModMain.frmServerRight?.RefreshWall();
        }

        loader.OnStateChangedUi += OnStateChanged;
        loader.Start();
        ModLoader.LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
    }

    // 只写我们要固定的键 其余交给服务端首次启动自己补 已有的话不覆盖
    public static void WriteDefaultProperties(ServerInstance instance)
    {
        var path = Path.Combine(instance.DirectoryPath, "server.properties");
        if (File.Exists(path)) return;

        var content = new StringBuilder()
            .AppendLine("#Minecraft server properties")
            .AppendLine($"server-port={instance.Port}")
            .AppendLine($"max-players={instance.MaxPlayers}")
            .AppendLine("online-mode=true")
            .Append("motd=A Minecraft Server\n")
            .ToString();
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    // 服务端文件里的正版验证开关 文件或这一行还没有就按默认的开着算
    public static bool ReadOnlineMode(ServerInstance instance)
    {
        var path = Path.Combine(instance.DirectoryPath, "server.properties");
        if (!File.Exists(path)) return true;

        try
        {
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith("online-mode=", StringComparison.OrdinalIgnoreCase))
                    return !line["online-mode=".Length..].Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            // 读不出来按默认值 不至于把界面卡住
        }

        return true;
    }

    // 服务端只在启动时读这个文件 运行中改要重启才生效
    public static bool WriteOnlineMode(ServerInstance instance, bool enabled)
    {
        var path = Path.Combine(instance.DirectoryPath, "server.properties");
        if (!File.Exists(path)) return false;

        var value = $"online-mode={(enabled ? "true" : "false")}";
        try
        {
            var lines = File.ReadAllLines(path);
            var found = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("online-mode=", StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = value;
                found = true;
                break;
            }

            File.WriteAllLines(path, found ? lines : [.. lines, value], new UTF8Encoding(false));
            return true;
        }
        catch (IOException)
        {
            // 服务端正好在写这个文件 交给界面提示用户
            return false;
        }
    }

    // 写 eula.txt 勾选同意后调用
    public static void AcceptEula(ServerInstance instance)
    {
        File.WriteAllText(Path.Combine(instance.DirectoryPath, "eula.txt"),
            "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\neula=true\n",
            new UTF8Encoding(false));
        instance.EulaAccepted = true;
        instance.Save();
    }
}
