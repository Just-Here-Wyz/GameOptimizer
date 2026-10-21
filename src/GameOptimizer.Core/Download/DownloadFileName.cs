namespace GameOptimizer.Core.Download;

/// <summary>
/// 从下载链接里提取文件名。纯函数，粘贴链接时界面实时调用。
///
/// 为什么单独一个类：链接里带 <c>?filename=</c>、<c>#</c>、URL 编码、
/// 中文文件名都会影响结果，这些全是「看着简单但一改就错」的地方，
/// 必须能单测，而不是散在 ViewModel 的事件处理里。
/// </summary>
public static class DownloadFileName
{
    private const int MaxLength = 120;

    /// <summary>
    /// 提取建议文件名。取不到时返回空串（界面保留空框让用户自己填），
    /// **绝不**编一个 <c>download.bin</c> 之类的名字。
    /// </summary>
    public static string FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        var text = url.Trim();

        // 没写协议的链接（如 www.example.com/a.zip）也能识别
        if (!text.Contains("://", StringComparison.Ordinal)
            && !text.StartsWith("//", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        string path;
        string? query;
        try
        {
            var uri = new Uri(text.StartsWith("//", StringComparison.Ordinal) ? "http:" + text : text);
            path = uri.AbsolutePath;
            query = uri.Query;
        }
        catch (UriFormatException)
        {
            return "";
        }

        var name = LastSegment(path);

        // 路径最后一个片段不像文件名时（例如 /download/12345），
        // 看查询串里有没有显式的 filename / name 参数；都没有就返回空，
        // 交给服务端的 Content-Disposition —— 不能把「12345」当文件名。
        if (string.IsNullOrEmpty(name) || !LooksLikeFileName(name))
        {
            name = FromQuery(query);
        }

        name = Sanitize(name);
        return Truncate(name, MaxLength);
    }

    /// <summary>
    /// 常见直链会以 <c>/download?file=xxx.zip</c> 或 <c>?filename=xxx.zip</c> 的形式给名字。
    /// </summary>
    private static string FromQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return "";

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;

            var key = pair[..index];
            if (!key.Equals("filename", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("name", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("file", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Unescape(pair[(index + 1)..]);
            if (!string.IsNullOrWhiteSpace(value) && LooksLikeFileName(value)) return value;
        }

        return "";
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return "";

        var slash = trimmed.LastIndexOf('/');
        var segment = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return Unescape(segment);
    }

    /// <summary>URL 解码，失败就退回原文（畸形编码不该让整个提取流程失败）。</summary>
    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return value;
        }
    }

    /// <summary>
    /// 「像文件名」的粗略判断：有扩展名，且不是纯数字段。
    /// <c>/download/12345</c> 这种路径应当去查询串里找更好的名字。
    /// </summary>
    private static bool LooksLikeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var dot = value.LastIndexOf('.');
        if (dot <= 0 || dot == value.Length - 1) return false;

        var extension = value[(dot + 1)..];
        return extension.Length is >= 1 and <= 8 && extension.All(char.IsLetterOrDigit);
    }

    /// <summary>
    /// 把非法字符换成下划线。Windows 文件名的硬约束：
    /// 不能含 <c>\ / : * ? " &lt; &gt; |</c>，不能以点或空格结尾，不能是设备名。
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);

        foreach (var ch in name.Trim())
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var result = builder.ToString().Trim().TrimEnd('.');

        // CON / PRN / AUX / NUL / COM1 / LPT1 这类设备名不能当文件名
        if (IsReservedDeviceName(result)) result += "_";

        return result;
    }

    private static bool IsReservedDeviceName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0) return false;

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && char.IsDigit(stem[3]))
        {
            return true;
        }

        return false;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    /// <summary>
    /// 在目录里给重名文件找一个不冲突的名字：<c>a.zip</c> → <c>a (1).zip</c>。
    /// </summary>
    public static string MakeUnique(string directory, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= path => File.Exists(path);

        var name = Sanitize(fileName);
        if (string.IsNullOrEmpty(name)) name = "download";

        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        var index = 1;
        while (exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{stem} ({index}){extension}";
            index++;

            // 极端情况下的兜底：一万个同名文件就不再查了，直接带上时间戳
            if (index > 10_000)
            {
                candidate = $"{stem}_{DateTime.Now:yyyyMMddHHmmss}{extension}";
                break;
            }
        }

        return candidate;
    }
}
