using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CursorStudio.Core;

/// <summary>某一时刻 <c>HKCU\Control Panel\Cursors</c> 的完整快照，用来还原。</summary>
public sealed class CursorSnapshot
{
    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    /// <summary>方案名，就是注册表项的默认值。</summary>
    public string? SchemeName { get; set; }
    /// <summary>2 = 系统自带方案，0 = 自定义。还原时要一起还回去。</summary>
    public int SchemeSource { get; set; } = 2;
    public int CursorBaseSize { get; set; } = 32;
    /// <summary>指针位 → 文件路径。值是 null 表示当时这个值根本不存在。</summary>
    public Dictionary<string, string?> Values { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static CursorSnapshot? FromJson(string json) =>
        JsonSerializer.Deserialize<CursorSnapshot>(json, JsonOpts);
}

public static class CursorRegistry
{
    private const string KeyPath = @"Control Panel\Cursors";

    public static RegistryKey OpenWrite() =>
        Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
        ?? throw new InvalidOperationException(@"打不开注册表项 HKCU\Control Panel\Cursors");

    public static RegistryKey OpenRead() =>
        Registry.CurrentUser.OpenSubKey(KeyPath, writable: false)
        ?? throw new InvalidOperationException(@"打不开注册表项 HKCU\Control Panel\Cursors");

    /// <summary>把当前的指针设置整个拍一张快照。</summary>
    public static CursorSnapshot Capture()
    {
        using var key = OpenRead();
        var snap = new CursorSnapshot
        {
            SchemeName = key.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            SchemeSource = key.GetValue("Scheme Source") is int ss ? ss : 2,
            CursorBaseSize = key.GetValue("CursorBaseSize") is int bs ? bs : 32,
        };

        foreach (var slot in CursorSlots.All)
        {
            // DoNotExpandEnvironmentNames：保持 %SystemRoot% 这类写法原样存，还原时才不会变味
            snap.Values[slot.RegName] =
                key.GetValue(slot.RegName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        return snap;
    }

    /// <summary>把若干指针位写到注册表并立即生效。没传进来的指针位保持原样不动。</summary>
    public static void Apply(IReadOnlyDictionary<string, string> values, string schemeName)
    {
        using var key = OpenWrite();

        foreach (var (name, path) in values)
            key.SetValue(name, path, RegistryValueKind.String);

        // 0 = 自定义方案，这样 Windows 的「鼠标属性」里不会显示成某个内置方案名
        key.SetValue("Scheme Source", 0, RegistryValueKind.DWord);
        key.SetValue("", schemeName, RegistryValueKind.String);

        key.Flush();
        Win32.BroadcastCursorChange();
    }

    /// <summary>按快照还原。快照里为 null 的值会被删掉，恢复成"当时这个值不存在"的状态。</summary>
    public static void Restore(CursorSnapshot snap)
    {
        using var key = OpenWrite();

        foreach (var slot in CursorSlots.All)
        {
            snap.Values.TryGetValue(slot.RegName, out string? path);
            if (path is null)
                key.DeleteValue(slot.RegName, throwOnMissingValue: false);
            else
                key.SetValue(slot.RegName, path, RegistryValueKind.String);
        }

        key.SetValue("Scheme Source", snap.SchemeSource, RegistryValueKind.DWord);
        if (!string.IsNullOrEmpty(snap.SchemeName))
            key.SetValue("", snap.SchemeName, RegistryValueKind.String);

        key.Flush();
        Win32.BroadcastCursorChange();
    }

    /// <summary>
    /// 恢复成 Windows 自带的 Aero 指针。给"备份也救不回来"的兜底用。
    /// 会按当前指针大小挑普通 / _l / _xl 三档里合适的那一套。
    /// </summary>
    public static void RestoreWindowsDefault()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors");

        int baseSize = ReadBaseSize();
        string suffix = baseSize <= 32 ? "" : baseSize <= 48 ? "_l" : "_xl";

        // 指针位 → 文件名主干（不含后缀）。Crosshair / IBeam 系统本来就是内置的，留空。
        var files = new Dictionary<string, string?>
        {
            ["Arrow"] = "aero_arrow.cur",
            ["Help"] = "aero_helpsel.cur",
            ["AppStarting"] = "aero_working.ani",
            ["Wait"] = "aero_busy.ani",
            ["Crosshair"] = null,
            ["IBeam"] = null,
            ["NWPen"] = "aero_pen.cur",
            ["No"] = "aero_unavail.cur",
            ["SizeNS"] = "aero_ns.cur",
            ["SizeWE"] = "aero_ew.cur",
            ["SizeNWSE"] = "aero_nwse.cur",
            ["SizeNESW"] = "aero_nesw.cur",
            ["SizeAll"] = "aero_move.cur",
            ["UpArrow"] = "aero_up.cur",
            ["Hand"] = "aero_link.cur",
            ["Pin"] = "aero_pin.cur",
            ["Person"] = "aero_person.cur",
        };

        using var key = OpenWrite();

        // 系统内置方案的显示名是本地化的：中文系统叫「Windows 默认」，英文系统叫
        // Windows Default。写死一个字符串换个语言版本就显示错了，所以能沿用就沿用。
        string? existingName = key.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        bool wasSystemScheme = key.GetValue("Scheme Source") is int src && src == 2;

        foreach (var (name, file) in files)
        {
            if (file is null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                continue;
            }

            string? resolved = ResolveVariant(dir, file, suffix);
            if (resolved is null)
                key.DeleteValue(name, throwOnMissingValue: false);  // 系统里没有这套文件，交给 Windows 内置兜底
            else
                key.SetValue(name, resolved, RegistryValueKind.String);
        }

        // 2 = 系统方案
        key.SetValue("Scheme Source", 2, RegistryValueKind.DWord);
        key.SetValue("",
            wasSystemScheme && !string.IsNullOrWhiteSpace(existingName) ? existingName : DefaultSchemeName(),
            RegistryValueKind.String);

        key.Flush();
        Win32.BroadcastCursorChange();
    }

    /// <summary>
    /// 系统方案该叫什么名字。这个字符串只是显示在「鼠标属性」的下拉框里，
    /// 但以前是写死的中文"Windows 默认"——英文版 Windows 上会显示成中文，很出戏。
    /// 能沿用注册表里原有的名字就沿用，实在没有才按系统语言给一个。
    /// </summary>
    private static string DefaultSchemeName()
    {
        try
        {
            using var key = OpenRead();
            // Schemes 里的第一条一般就是系统默认方案，它的名字是本地化的
            using var schemes = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors\Schemes");
            string? first = schemes?.GetValueNames()
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (!string.IsNullOrWhiteSpace(first)) return first!;
        }
        catch { /* 读不到就退回按语言猜 */ }

        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "Windows 默认"
            : "Windows Default";
    }

    /// <summary>先找带后缀的（_l / _xl），没有就退回不带后缀的；再没有就返回 null。</summary>
    private static string? ResolveVariant(string dir, string file, string suffix)
    {
        string ext = Path.GetExtension(file);
        string stem = Path.GetFileNameWithoutExtension(file);

        if (suffix.Length > 0)
        {
            string big = Path.Combine(dir, stem + suffix + ext);
            if (File.Exists(big)) return big;
        }

        string plain = Path.Combine(dir, file);
        return File.Exists(plain) ? plain : null;
    }

    public static int ReadBaseSize()
    {
        try
        {
            using var key = OpenRead();
            return key.GetValue("CursorBaseSize") is int v && v > 0 ? v : 32;
        }
        catch
        {
            return 32;
        }
    }

    /// <summary>当前注册表里各指针位的实际值，界面上用来显示"现在用的是什么"。</summary>
    public static Dictionary<string, string?> ReadCurrent()
    {
        try
        {
            return Capture().Values;
        }
        catch
        {
            return new Dictionary<string, string?>();
        }
    }
}
