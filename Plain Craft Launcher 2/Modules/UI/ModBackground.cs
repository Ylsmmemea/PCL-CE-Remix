using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.IO.Net.Http;

namespace PCL;

// 背景图的获取与缓存
public static class ModBackground
{
    // 必应每日一图接口
    // idx 是日期偏移 0 为今天 1 为昨天 最多能往前翻约 7 天 n 是返回数量 最大 8
    private const string BingApiUrl =
        "https://cn.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=zh-CN";

    private const string BingImageHost = "https://cn.bing.com";

    // 一次最多取这么多张 接口上限就是 8
    private const int BingImageCount = 8;

    // 放在共享本地数据目录而不是临时目录 这样跨天和跨启动器会话都能复用
    private static string BingCacheDirectory =>
        Path.Combine(Paths.SharedLocalData, "BingWallpaper");

    public class BingImageArchive
    {
        [JsonPropertyName("images")] public BingImageInfo[]? Images { get; set; }
    }

    public class BingImageInfo
    {
        [JsonPropertyName("startdate")] public string? StartDate { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("copyright")] public string? Copyright { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("hsh")] public string? Hash { get; set; }
    }

    // 下载串行化 好几处同时触发的话会互相覆盖或者误删文件
    private static readonly object DownloadLock = new();

    // 读本地缓存 不发网络请求 UI 线程可以调
    // 返回按文件修改时间由新到旧排好的路径 没有缓存就是空列表
    public static List<string> GetCachedBingWallpapers()
    {
        try
        {
            if (!Directory.Exists(BingCacheDirectory))
                return [];

            return new DirectoryInfo(BingCacheDirectory)
                .EnumerateFiles("*.jpg")
                .Where(file => file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToList();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[UI] 读取必应壁纸缓存失败");
            return [];
        }
    }

    // 拉最新的必应壁纸并缓存到本地 返回拿到手的路径
    // 已经缓存过的不会重复下载 下完顺手清理不再需要的旧图
    // 会发网络请求 别在 UI 线程调
    // keepPath 是要保留的图 通常是当前正在用的那张 可以传 null
    public static List<string> DownloadBingWallpapers(string? keepPath = null)
    {
        lock (DownloadLock)
        {
            return _DownloadBingWallpapersCore(keepPath);
        }
    }

    private static List<string> _DownloadBingWallpapersCore(string? keepPath)
    {
        var result = new List<string>();
        try
        {
            var archive = HttpRequest.GetJsonAsync<BingImageArchive>(BingApiUrl).GetAwaiter().GetResult();
            var images = archive?.Images;
            if (images is null || images.Length == 0)
            {
                ModBase.Log("[UI] 必应壁纸接口未返回图片信息");
                return result;
            }

            Directory.CreateDirectory(BingCacheDirectory);
            foreach (var image in images.Take(BingImageCount))
            {
                if (image.Url is null)
                    continue;

                var name = image.Hash ?? image.StartDate ?? "bing";
                var safeName = new string(name.Where(char.IsLetterOrDigit).ToArray());
                if (string.IsNullOrEmpty(safeName)) safeName = "bing";
                var filePath = Path.Combine(BingCacheDirectory, safeName + ".jpg");

                if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                {
                    result.Add(filePath);
                    continue;
                }

                try
                {
                    using var response = HttpRequest.Create(BingImageHost + image.Url)
                        .SendAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccess)
                    {
                        ModBase.Log($"[UI] 下载必应壁纸失败，状态码 {response.StatusCode}");
                        continue;
                    }

                    File.WriteAllBytes(filePath, response.AsByteArrayAsync().GetAwaiter().GetResult());
                    result.Add(filePath);
                }
                catch (Exception ex)
                {
                    // 单张挂了不影响其他图
                    ModBase.Log(ex, $"[UI] 下载必应壁纸失败：{image.Copyright}");
                }
            }

            ModBase.Log($"[UI] 已获取 {result.Count} 张必应壁纸");
            CleanupBingCache(result, keepPath);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[UI] 获取必应壁纸失败");
        }

        return result;
    }

    // 清理缓存 这次接口没返回的又不是当前正在用的 就删掉 免得目录一直涨
    private static void CleanupBingCache(IReadOnlyCollection<string> keepPaths, string? keepPath)
    {
        try
        {
            if (!Directory.Exists(BingCacheDirectory))
                return;

            var removed = 0;
            foreach (var file in new DirectoryInfo(BingCacheDirectory).EnumerateFiles("*.jpg"))
            {
                if (keepPaths.Contains(file.FullName))
                    continue;
                if (keepPath is not null && string.Equals(file.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                file.Delete();
                removed++;
            }

            if (removed > 0)
                ModBase.Log($"[UI] 已清理 {removed} 张过期的必应壁纸");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[UI] 清理必应壁纸缓存失败");
        }
    }
}
