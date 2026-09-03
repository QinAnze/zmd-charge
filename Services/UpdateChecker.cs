using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace EndfieldCharge.Services;

public static class UpdateChecker
{
    private const string ReleasesUrl = "https://api.github.com/repos/{owner}/{repo}/releases/latest";
    private static readonly HttpClient Client = new();

    static UpdateChecker()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("EndfieldCharge/1.0");
        Client.Timeout = TimeSpan.FromSeconds(8);
    }

    /// <summary>
    /// 检查 GitHub Releases 是否有新版本。
    /// 返回 (hasUpdate, latestVersion, downloadUrl)。
    /// 异常时直接抛出，由调用方处理。
    /// </summary>
    public static async Task<(bool HasUpdate, string? Version, string? Url)> CheckAsync()
    {
        // 替换为实际的 owner/repo
        var url = ReleasesUrl
            .Replace("{owner}", "Lenkmat")
            .Replace("{repo}", "endfield-charge");

        var response = await Client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "0.0.0";
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var latest = ParseVersion(tag);

        var hasUpdate = latest is not null && current is not null && latest > current;

        string? downloadUrl = null;
        if (root.TryGetProperty("html_url", out var html))
            downloadUrl = html.GetString();

        // 去掉 tag 前缀 v
        var display = tag.TrimStart('v');

        return (hasUpdate, display, downloadUrl);
    }

    private static Version? ParseVersion(string tag)
    {
        var v = tag.TrimStart('v');
        var parts = v.Split('-')[0].Split('.');
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out int major) &&
            int.TryParse(parts[1], out int minor))
        {
            int build = parts.Length > 2 && int.TryParse(parts[2], out int b) ? b : 0;
            return new Version(major, minor, build);
        }
        return null;
    }
}