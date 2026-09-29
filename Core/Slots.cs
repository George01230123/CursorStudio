namespace CursorStudio.Core;

/// <summary>热点（点击生效的那个点）的默认取法。</summary>
public enum HotSpotPreset
{
    /// <summary>取最靠左上的不透明像素——箭头、手型这类"尖角就是点击点"的图用这个。</summary>
    TipTopLeft,
    /// <summary>取正中心——十字、I 形、双向箭头这类对称的图用这个。</summary>
    Center,
}

/// <summary>
/// 一个 Windows 指针位。<see cref="RegName"/> 是 <c>HKCU\Control Panel\Cursors</c> 下的值名，
/// 必须和系统约定完全一致，写错了系统就不认。
/// </summary>
public sealed class CursorSlot
{
    public required string RegName { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>界面上给用户看的说明：这个指针什么时候会出现。</summary>
    public required string Hint { get; init; }
    public HotSpotPreset DefaultHotSpot { get; init; } = HotSpotPreset.Center;

    public override string ToString() => DisplayName;
}

public static class CursorSlots
{
    /// <summary>
    /// 顺序按 Windows「鼠标属性 → 指针」里出现的顺序排，方便对照。
    /// </summary>
    public static readonly IReadOnlyList<CursorSlot> All = new CursorSlot[]
    {
        new() { RegName = "Arrow",       DisplayName = "正常选择",     Hint = "默认箭头，出现频率最高",           DefaultHotSpot = HotSpotPreset.TipTopLeft },
        new() { RegName = "Help",        DisplayName = "帮助选择",     Hint = "带问号的箭头，点「?」按钮时出现",  DefaultHotSpot = HotSpotPreset.TipTopLeft },
        new() { RegName = "AppStarting", DisplayName = "后台运行",     Hint = "箭头旁带转圈，正在后台加载时出现", DefaultHotSpot = HotSpotPreset.TipTopLeft },
        new() { RegName = "Wait",        DisplayName = "忙碌",         Hint = "转圈，程序正忙时出现",             DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "Crosshair",   DisplayName = "精确选择",     Hint = "十字准星，截图/画图工具里常见",    DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "IBeam",       DisplayName = "文本选择",     Hint = "I 形，在输入框和文字上出现",       DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "NWPen",       DisplayName = "手写",         Hint = "笔形，手写输入时出现",             DefaultHotSpot = HotSpotPreset.TipTopLeft },
        new() { RegName = "No",          DisplayName = "不可用",       Hint = "圆圈斜杠，拖到不允许放的地方",      DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "SizeNS",      DisplayName = "垂直调整",     Hint = "上下双箭头，拖窗口上下边框",        DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "SizeWE",      DisplayName = "水平调整",     Hint = "左右双箭头，拖窗口左右边框",        DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "SizeNWSE",    DisplayName = "对角线调整 1", Hint = "↖↘ 双箭头，拖左上/右下角",          DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "SizeNESW",    DisplayName = "对角线调整 2", Hint = "↗↙ 双箭头，拖右上/左下角",          DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "SizeAll",     DisplayName = "移动",         Hint = "四向箭头，拖动窗口/文件时出现",     DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "UpArrow",     DisplayName = "候选选择",     Hint = "向上箭头，手写候选列表出现时",      DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "Hand",        DisplayName = "链接选择",     Hint = "手形，指向网页链接和按钮",          DefaultHotSpot = HotSpotPreset.TipTopLeft },
        new() { RegName = "Pin",         DisplayName = "位置选择",     Hint = "图钉（Win10+），触摸/定位时出现",   DefaultHotSpot = HotSpotPreset.Center },
        new() { RegName = "Person",      DisplayName = "人员选择",     Hint = "人形（Win10+），触摸键盘/人脉",     DefaultHotSpot = HotSpotPreset.Center },
    };

    public static CursorSlot? ByRegName(string regName) =>
        All.FirstOrDefault(s => string.Equals(s.RegName, regName, StringComparison.OrdinalIgnoreCase));
}
