using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.IO.Net.Http;

namespace PCL;

/// <summary>
///     背景内容的获取与缓存。
/// </summary>
public static class ModBackground
{
    /// <summary>
    ///     必应每日一图接口。<br/>
    ///     <c>idx</c> 为日期偏移（0 为今天，1 为昨天，最多可回溯约 7 天），<c>n</c> 为返回数量（最大 8）。
    /// </summary>
    private const string BingApiUrl =
        "https://cn.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=zh-CN";

    private const string BingImageHost = "https://cn.bing.com";

    /// <summary>
    ///     单次从接口获取的图片数量上限。
    /// </summary>
    private const int BingImageCount = 8;

    /// <summary>
    ///     必应壁纸的缓存目录。<br/>
    ///     位于共享本地数据目录而非临时目录，以便跨天、跨启动器会话复用。
    /// </summary>
    private static string BingCacheDirectory =>
        Path.Combine(Paths.SharedLocalData, "BingWallpaper");

    /// <summary>
    ///     必应每日一图接口的响应。
    /// </summary>
    public class BingImageArchive
    {
        [JsonPropertyName("images")] public BingImageInfo[]? Images { get; set; }
    }

    /// <summary>
    ///     必应每日一图的图片信息。
    /// </summary>
    public class BingImageInfo
    {
        [JsonPropertyName("startdate")] public string? StartDate { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("copyright")] public string? Copyright { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("hsh")] public string? Hash { get; set; }
    }

    /// <summary>
    ///     用于串行化壁纸下载，避免并发调用互相干扰。
    /// </summary>
    private static readonly object DownloadLock = new();

    /// <summary>
    ///     读取本地已缓存的必应壁纸。不发起任何网络请求，可在 UI 线程调用。
    /// </summary>
    /// <returns>按文件修改时间由新到旧排序的图片路径列表，无缓存时为空列表。</returns>
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

    /// <summary>
    ///     从接口获取最新的必应壁纸并缓存到本地，返回成功获取的图片路径。<br/>
    ///     已缓存的图片不会重复下载；下载完成后会清理不再需要的旧图。<br/>
    ///     该方法会发起网络请求，不应在 UI 线程调用。
    /// </summary>
    /// <param name="keepPath">需要保留的图片路径（通常是当前正在使用的那张），可为 <c>null</c>。</param>
    public static List<string> DownloadBingWallpapers(string? keepPath = null)
    {
        // 串行化，避免多处同时触发下载时互相覆盖或误删文件
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
                    // 单张失败不影响其余图片
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

    /// <summary>
    ///     清理必应壁纸缓存：删除既不在本次接口返回列表中、也不是当前正在使用的图片。<br/>
    ///     用于在更新缓存后移除过期壁纸，避免目录无限增长。
    /// </summary>
    /// <param name="keepPaths">本次接口返回并已缓存的图片路径。</param>
    /// <param name="keepPath">当前正在使用的图片路径，可为 <c>null</c>。</param>
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
