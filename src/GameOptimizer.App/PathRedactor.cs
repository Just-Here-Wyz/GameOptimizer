namespace GameOptimizer.App;

/// <summary>
/// 截图路径脱敏。只在 <c>--render</c> 离屏渲染时启用，正常运行不做任何替换。
///
/// 为什么需要：文档截图会进 README，而仓库是公开的。
/// 直接把 <c>C:\Users\&lt;真实用户名&gt;\...</c> 拍进图里，等于把本机账户名公开出去
/// （v2.2.x 的 download.png 就是这样漏了用户名）。
/// 这里只替换「用户目录前缀」，盘符之后的结构与文件名原样保留——
/// 读者照样能看出「这是用户目录下的某个程序」，信息量不丢。
/// </summary>
internal static class PathRedactor
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>把用户目录前缀换成 <c>%USERPROFILE%</c>；其他路径原样返回。</summary>
    public static string Redact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;

        // Windows 路径不区分大小写，别用 Ordinal 判断
        if (UserProfile.Length > 0
            && path.StartsWith(UserProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + path[UserProfile.Length..];
        }

        return path;
    }
}
