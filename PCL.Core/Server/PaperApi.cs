using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;

namespace PCL.Core.Server;

// PaperMC 的版本与构建信息 走 fill.papermc.io 的 v3 接口 老的 api 域名已经停用会返回 410
public static class PaperApi
{
    private const string ProjectUrl = "https://fill.papermc.io/v3/projects/paper";

    // 接口把版本按大版本分组返回 组内和组间都是从新到旧 摊平就好 别自己按字符串排序
    public static async Task<List<string>> ListVersionsAsync()
    {
        var root = await HttpRequest.GetJsonAsync<JsonObject>(ProjectUrl).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Paper 版本列表返回为空");
        var groups = root["versions"] as JsonObject
                     ?? throw new InvalidOperationException("Paper 版本列表格式不对");

        var result = new List<string>();
        foreach (var group in groups)
        {
            if (group.Value is not JsonArray versions) continue;
            foreach (var version in versions)
            {
                var id = version?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
        }

        return result;
    }

    // 取某个版本的最新稳定构建 接口按新到旧返回 没有稳定版就退回第一个
    public static async Task<PaperBuild?> GetLatestBuildAsync(string mcVersion)
    {
        var builds = await HttpRequest
            .GetJsonAsync<List<JsonObject>>(
                $"{ProjectUrl}/versions/{Uri.EscapeDataString(mcVersion)}/builds")
            .ConfigureAwait(false);
        if (builds is null || builds.Count == 0) return null;

        var chosen = builds[0];
        foreach (var build in builds)
            if ((string?)build["channel"] == "STABLE")
            {
                chosen = build;
                break;
            }

        var download = chosen["downloads"]?["server:default"];
        var url = (string?)download?["url"];
        var fileName = (string?)download?["name"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("Paper 构建信息缺少下载地址");

        return new PaperBuild(
            (int?)chosen["id"] ?? 0,
            fileName,
            url,
            (long?)download?["size"] ?? -1,
            (string?)download?["checksums"]?["sha256"] ?? "");
    }
}

// 一个可下载的 Paper 服务端构建
public sealed record PaperBuild(int BuildNumber, string FileName, string Url, long Size, string Sha256);
