using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Overlay;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPTAssistant.Session;

namespace PPTAssistant.App;

public sealed class AppConfig
{
    /// <summary>当前配置架构版本（将来做迁移用）。</summary>
    public int Version { get; set; } = 1;

    /// <summary>放映界面相对自动尺寸的缩放比例（0.75–2.0；100% 保持三档响应式基准）。</summary>
    public double UiScale { get; set; } = UiScalePolicy.Default;

    /// <summary>手改配置中的非法缩放回到 100%；与 Windows DPI 和笔宽无关。</summary>
    [JsonIgnore]
    public double SanitizedUiScale => UiScalePolicy.Normalize(UiScale);

    public double PenWidthDips { get; set; } = 6.0;

    public string PenColorHex { get; set; } = "#D91B1B";

    public List<string> RecentPenColors { get; set; } = [];

    public bool IgnoreTouchWhenPenPresent { get; set; } = true;

    /// <summary>是否允许鼠标绘制（开发期验证用）。</summary>
    public bool AllowMouseDrawing { get; set; } = true;

    public bool AlwaysRoutePenToInk { get; set; } = true;

    public string WriteBackMode { get; set; } = "Ask";

    /// <summary>设置页上次保存的自启状态；实际状态以当前用户的 Windows 启动项为准，手改此值不注册自启。</summary>
    public bool AutoStart { get; set; }

    public string? LastLayoutProfile { get; set; }

    public string SurfaceMode { get; set; } = "Layered";

    /// <summary>解析出来的表面模式（非法值回落 Layered——安全的那个）。</summary>
    [JsonIgnore]
    public OverlaySurfaceMode ParsedSurfaceMode =>
        Enum.TryParse<OverlaySurfaceMode>(SurfaceMode, ignoreCase: true, out var mode)
            ? mode
            : OverlaySurfaceMode.Layered;

    /// <summary>转成会话参数。</summary>
    /// <summary>旁路日志根目录（null = 默认位置）。</summary>
    public string? SidecarRootPath => null;

    /// <summary>写回备份目录（null = 默认位置）。</summary>
    public string? BackupDirectoryPath => null;

    /// <summary>解析出来的写回模式（非法值回落到 Ask）。</summary>
    public WriteBackMode ParsedWriteBackMode => WriteBackModes.Parse(WriteBackMode);

    public SessionOptions ToSessionOptions() => new()
    {
        PenWidthDips = SanitizedPenWidth,
        PenColor = InkColor.Parse(PenColorHex),
        RecentPenColors = ParsedRecentPenColors,
        IgnoreTouchWhenPenPresent = IgnoreTouchWhenPenPresent,
        AllowMouseDrawing = AllowMouseDrawing,
    };

    /// <summary>解析出来的最近使用色（坏值逐个忽略；≤ 容量上限）。</summary>
    [JsonIgnore]
    public IReadOnlyList<InkColor> ParsedRecentPenColors
    {
        get
        {
            var result = new List<InkColor>(InkPalette.MaxRecent);
            foreach (var hex in RecentPenColors ?? [])
            {
                var color = InkColor.Parse(hex);
                if (!result.Contains(color) && result.Count < InkPalette.MaxRecent)
                {
                    result.Add(color);
                }
            }

            return result;
        }
    }

    [JsonIgnore]
    public double SanitizedPenWidth
        => PenWidthRange.IsValid(PenWidthDips) ? PenWidthDips : PenWidthRange.DefaultDips;
}

/// <summary>
/// 配置读写（<c>%APPDATA%\PPTAssistant\config.json</c>）。
/// </summary>
/// <remarks>
/// 容错优先：读不到 / 解析失败一律回落到默认配置并记日志，**绝不让配置问题阻止程序启动**
/// （教室里老师不会去看 JSON）。
/// </remarks>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>配置目录（<c>%APPDATA%\PPTAssistant</c>）。</summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PPTAssistant");

    /// <summary>配置文件路径。</summary>
    public static string FilePath => Path.Combine(Directory, "config.json");

    /// <summary>读取配置（不存在 / 损坏时返回默认值）。</summary>
    public static AppConfig Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                AppLog.Info($"配置不存在，使用默认值：{path}");
                return new AppConfig();
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (config is null)
            {
                AppLog.Warn("配置解析结果为空，使用默认值");
                return new AppConfig();
            }

            AppLog.Info($"已载入配置：{path}");
            return config;
        }
        catch (Exception ex)
        {
            AppLog.Error("读取配置失败，使用默认值", ex);
            return new AppConfig();
        }
    }

    /// <summary>保存配置（先写临时文件再替换，避免掉电写出半截 JSON）。</summary>
    public static bool Save(AppConfig config)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = FilePath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, SerializerOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("保存配置失败", ex);
            return false;
        }
    }
}
