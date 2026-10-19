namespace GameOptimizer.Core.Models;

/// <summary>画质档位。游戏内的「低 / 中 / 高 / 极高」映射到这四档。</summary>
public enum QualityPreset
{
    Low = 0,
    Medium = 1,
    High = 2,
    Ultra = 3,
}

/// <summary>分辨率档位。以 1080p 为基准，向上按像素数放大显存需求。</summary>
public enum ResolutionPreset
{
    P1080 = 0,
    P1440 = 1,
    P4K = 2,
}

public static class RenderPresetText
{
    public static string Text(this QualityPreset quality) => quality switch
    {
        QualityPreset.Low => "低",
        QualityPreset.Medium => "中",
        QualityPreset.High => "高",
        QualityPreset.Ultra => "极致",
        _ => "高",
    };

    public static string Text(this ResolutionPreset resolution) => resolution switch
    {
        ResolutionPreset.P1080 => "1080p",
        ResolutionPreset.P1440 => "1440p",
        ResolutionPreset.P4K => "4K",
        _ => "1080p",
    };
}
