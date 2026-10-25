namespace GameOptimizer.Core.Library;

/// <summary>
/// 一个进程在管理界面里的完整展示信息。
///
/// 每个字段都对应需求里的一项「尽可能显示」：应用名称 / 进程名 / 用途说明 /
/// 发布者 / 当前状态 / 识别依据。读不到的字段一律为 null，由界面整行隐藏或写「未知」，
/// <b>不留空串也不编造</b>。
/// </summary>
/// <param name="ProcessName">原始进程名（不含 .exe），排查问题的唯一稳定标识。</param>
/// <param name="ExecutablePath">可执行文件完整路径；读不到为 null。</param>
/// <param name="AppName">友好应用名：文件描述 → 产品名称 → 内置中文名 → 「未知应用」。</param>
/// <param name="FileDescription">exe 版本资源里的文件描述。</param>
/// <param name="ProductName">exe 版本资源里的产品名称。</param>
/// <param name="Publisher">发布者：产品信息里的公司名（不是签名主体，界面会标明来源）。</param>
/// <param name="FileVersion">文件版本。</param>
/// <param name="Purpose">用途说明；无法确认时固定为「用途未知」。</param>
/// <param name="PurposeBasis">用途的识别依据。</param>
/// <param name="PurposeKnown">用途是否真的识别出来了（界面据此决定用普通色还是警示色）。</param>
/// <param name="IsIdentityUnknown">是否是「读不出任何身份信息」的未知应用。</param>
/// <param name="State">保护状态（受保护 / 未保护 / 系统关键进程）。用户勾选保护时优先显示「受保护」。</param>
/// <param name="SystemReason">被系统保护的依据。</param>
/// <param name="InPreset">是否属于内置名单里的已知后台大户。</param>
/// <param name="IsService">是否以已注册服务身份运行。</param>
/// <param name="IsSystemProtected">
/// 是否被系统安全机制保护。<b>与 <paramref name="State"/> 分开存</b>：
/// 用户同时勾了保护时 State 显示「受保护」，但系统保护这件事不能因此丢掉，
/// 否则该行会被误判为「可以标记结束」。
/// </param>
public sealed record BackgroundProcessInfo(
    string ProcessName,
    string? ExecutablePath,
    string AppName,
    string? FileDescription,
    string? ProductName,
    string? Publisher,
    string? FileVersion,
    string Purpose,
    string PurposeBasis,
    bool PurposeKnown,
    bool IsIdentityUnknown,
    ProtectionState State,
    string? SystemReason,
    bool InPreset,
    bool IsService = false,
    bool IsSystemProtected = false)
{
    /// <summary>无法识别时的固定应用名。不允许编造。</summary>
    public const string UnknownAppName = "未知应用";

    public bool IsSystemEssential => IsSystemProtected || State == ProtectionState.SystemProtected;

    /// <summary>「chrome.exe」这种带后缀的写法，方便用户对照任务管理器。</summary>
    public string ExecutableName => ProcessName + ".exe";
}

/// <summary>
/// 把扫描结果拼成展示模型。
///
/// 分层理由：扫描只回答「这台机器上有什么」，这里回答「怎么把它讲清楚」——
/// 元数据读取、用途识别、状态推导都在这一层，全是纯逻辑 + 只读文件信息，
/// 因此可单测，也不会把 WPF 拖进 Core。
///
/// <b>失败降级</b>：读不到版本信息、读不到路径、用途认不出来，
/// 都只会让对应字段变成「未知」，绝不会抛异常中断整轮加载。
/// </summary>
public static class BackgroundProcessInspector
{
    /// <param name="entry">扫描结果。</param>
    /// <param name="isProtectedByUser">用户是否勾选了保护。</param>
    /// <param name="systemRoot">仅供测试注入。</param>
    public static BackgroundProcessInfo Inspect(
        BackgroundScanEntry entry,
        bool isProtectedByUser,
        string? systemRoot = null)
    {
        var metadata = ProcessFileMetadataReader.Read(entry.ExecutablePath);
        var purpose = ProcessPurposeCatalog.Resolve(
            entry.ProcessName, entry.ExecutablePath, metadata, entry.IsService, systemRoot);

        var state = BackgroundProtectionPolicy.StateOf(
            isProtectedByUser, entry.Kind == BackgroundCandidateKind.SystemProtected);

        var unknown = !metadata.HasAny
                      && !ProcessDisplayNames.HasName(entry.ProcessName)
                      && !ProcessPurposeCatalog.Knows(entry.ProcessName);

        return new BackgroundProcessInfo(
            ProcessName: entry.ProcessName,
            ExecutablePath: entry.ExecutablePath,
            AppName: ResolveAppName(entry.ProcessName, metadata, unknown),
            FileDescription: metadata.FileDescription,
            ProductName: metadata.ProductName,
            Publisher: metadata.CompanyName,
            FileVersion: metadata.FileVersion,
            Purpose: purpose.Text,
            PurposeBasis: purpose.Basis,
            PurposeKnown: purpose.IsKnown,
            IsIdentityUnknown: unknown,
            State: state,
            SystemReason: entry.Kind == BackgroundCandidateKind.SystemProtected
                ? entry.SystemReason ?? "系统关键进程"
                : null,
            InPreset: entry.InPreset,
            IsService: entry.IsService,
            IsSystemProtected: entry.Kind == BackgroundCandidateKind.SystemProtected);
    }

    /// <summary>
    /// 应用名优先级：文件描述 → 产品名称 → 内置中文名 → 「未知应用」。
    ///
    /// 先文件描述：它比产品名更贴近用户看到的窗口标题（例如
    /// 「Google Chrome」→「Google Chrome」，「微信」→「微信」）。
    /// </summary>
    internal static string ResolveAppName(
        string processName,
        ProcessFileMetadata metadata,
        bool identityUnknown)
    {
        if (!string.IsNullOrWhiteSpace(metadata.FileDescription)) return metadata.FileDescription!;
        if (!string.IsNullOrWhiteSpace(metadata.ProductName)) return metadata.ProductName!;

        if (ProcessDisplayNames.HasName(processName))
            return ProcessDisplayNames.Resolve(processName);

        return identityUnknown ? BackgroundProcessInfo.UnknownAppName : processName;
    }
}
