namespace PPTAssistant.Coords;

public enum UiTier
{

    Compact = 0,

    Standard = 1,

    Large = 2,
}

public readonly record struct UiTierMetrics(
    UiTier Tier,
    double ButtonSize,
    double CornerRadius,
    double IconPadding,
    double Gap,
    double PageFontSize,
    double PageButtonWidth,
    double ThumbnailPanelWidth,
    double ThumbnailWidth,
    double ThumbnailHeight,
    double ThumbnailBadgeWidth,
    double ThumbnailBadgeHeight,
    double ThumbnailBadgeFontSize)
{

    public double IconSize => ButtonSize - (2 * IconPadding);

    /// <summary>图标相对设计格（<see cref="UiMetrics.IconDesignBox"/>）的缩放系数。</summary>
    public double IconScale => IconSize / UiMetrics.IconDesignBox;

    /// <summary>是否窄屏档（判断个别只在窄档收边的细节时用）。</summary>
    public bool IsCompact => Tier == UiTier.Compact;

    public override string ToString()
        => $"{Tier}(按钮 {ButtonSize:0} 圆角 {CornerRadius:0} 内边距 {IconPadding:0} 空隙 {Gap:0})";
}

public static class UiMetrics
{
    /// <summary>标准档的屏宽下界（DIP）。</summary>
    public const double StandardMinWidthDips = 1440;

    /// <summary>大屏档的屏宽下界（DIP）。</summary>
    public const double LargeMinWidthDips = 3800;

    public const double PanelBorderThickness = 1;

    public const double StatusFontSize = 18;

    public const double ThumbnailItemGap = 10;

    public const double ThumbnailItemInset = 3;

    public const double IconDesignBox = 24;

    /// <summary>窄屏档（&lt; 1440）：按钮 30、圆角 3、内边距 8、空隙 2。</summary>
    public static readonly UiTierMetrics Compact = new(
        UiTier.Compact,
        ButtonSize: 30,
        CornerRadius: 3,
        IconPadding: 8,
        Gap: 2,
        PageFontSize: 12,
        PageButtonWidth: 48,
        ThumbnailPanelWidth: 96,
        ThumbnailWidth: 90,
        ThumbnailHeight: 65,
        ThumbnailBadgeWidth: 20,
        ThumbnailBadgeHeight: 13,
        ThumbnailBadgeFontSize: 6);

    /// <summary>标准档（1440–3799）：按钮 50、圆角 6、内边距 11、空隙 4。</summary>
    public static readonly UiTierMetrics Standard = new(
        UiTier.Standard,
        ButtonSize: 50,
        CornerRadius: 6,
        IconPadding: 11,
        Gap: 4,
        PageFontSize: 18,
        PageButtonWidth: 84,
        ThumbnailPanelWidth: 166,
        ThumbnailWidth: 160,
        ThumbnailHeight: 110,
        ThumbnailBadgeWidth: 40,
        ThumbnailBadgeHeight: 19,
        ThumbnailBadgeFontSize: 12);

    /// <summary>大屏档（≥ 3800）：按钮 100、圆角 12、内边距 25、空隙 8。</summary>
    public static readonly UiTierMetrics Large = new(
        UiTier.Large,
        ButtonSize: 100,
        CornerRadius: 12,
        IconPadding: 25,
        Gap: 8,
        PageFontSize: 36,
        PageButtonWidth: 168,
        ThumbnailPanelWidth: 310,
        ThumbnailWidth: 300,
        ThumbnailHeight: 210,
        ThumbnailBadgeWidth: 60,
        ThumbnailBadgeHeight: 29,
        ThumbnailBadgeFontSize: 21);

    /// <summary>按屏宽（DIP）判档。</summary>
    /// <remarks>边界归属：1440 属于标准档、3800 属于大屏档（与 <c>AdaptiveTrigger MinWindowWidth</c> 的「≥」一致）。</remarks>
    public static UiTier TierFor(double screenWidthDips)
    {
        if (double.IsNaN(screenWidthDips) || screenWidthDips < StandardMinWidthDips)
        {
            return UiTier.Compact;
        }

        return screenWidthDips >= LargeMinWidthDips ? UiTier.Large : UiTier.Standard;
    }

    /// <summary>按屏宽取度量表。</summary>
    public static UiTierMetrics For(double screenWidthDips) => ForTier(TierFor(screenWidthDips));

    /// <summary>取某档的度量表。</summary>
    public static UiTierMetrics ForTier(UiTier tier) => tier switch
    {
        UiTier.Compact => Compact,
        UiTier.Large => Large,
        _ => Standard,
    };

    public static double RowWidth(UiTierMetrics metrics, IReadOnlyList<int> buttonsPerCapsule)
    {
        ArgumentNullException.ThrowIfNull(buttonsPerCapsule);
        if (buttonsPerCapsule.Count == 0)
        {
            return 0;
        }

        var width = 0d;
        foreach (var count in buttonsPerCapsule)
        {
            width += (count * metrics.ButtonSize) + (2 * PanelBorderThickness);
        }

        return width + ((buttonsPerCapsule.Count - 1) * metrics.Gap);
    }

    public static double CornerBarWidth(UiTierMetrics metrics)
        => (2 * metrics.ButtonSize) + metrics.PageButtonWidth + (2 * metrics.Gap);

    public static double CornerBarHeight(UiTierMetrics metrics) => metrics.ButtonSize;
}
