using System.Text;

namespace CursorStudio.Core;

/// <summary>一个文件是怎么被认到某个指针位上的。</summary>
public enum SlotMatchSource
{
    /// <summary>没认出来。</summary>
    None,
    /// <summary>主题包里的 install.inf 明确写了（最可靠）。</summary>
    InstallInf,
    /// <summary>按文件名猜的。</summary>
    FileName,
}

public sealed record ThemeFile(string Path, string? SlotRegName, SlotMatchSource Source);

public sealed record ThemeScan(
    string Folder,
    string? InfPath,
    List<ThemeFile> Files)
{
    public IEnumerable<ThemeFile> Mapped => Files.Where(f => f.SlotRegName is not null);
    public IEnumerable<ThemeFile> Unmapped => Files.Where(f => f.SlotRegName is null);
    public int MappedCount => Files.Count(f => f.SlotRegName is not null);
}

/// <summary>
/// 把别人做好的指针主题包认到我们的 17 个指针位上。
///
/// 两条路，优先走第一条：
///
/// 1. **读包里的 install.inf**。主题包（Bibata、apple_cursor、BlueArchive…）都会带一份，
///    里面已经写明了哪个文件属于哪个指针位。这是作者本人的意图，比猜名字可靠得多。
///    两种写法都认：<c>[Wreg]</c> 那样逐个指针位写死值名的（最明确），
///    以及 <c>Schemes</c> 那一长串逗号分隔、位置固定的列表。
/// 2. **按文件名猜**。没有 inf 的包只能这样，但高星包的文件名其实高度一致
///    （normal / link / busy / resizeNS / dgn1 …），命中率并不低。
///
/// 认不出来的文件会原样报给用户，不会硬塞到某个指针位上——
/// 塞错了比没认出来更让人恼火。
/// </summary>
public static class ThemeImport
{
    /// <summary>主题包里会被考虑的文件类型。本工具只处理静态和动画指针，不碰 .ico。</summary>
    public static readonly string[] CursorExtensions = { ".cur", ".ani" };

    /// <summary>按文件名猜的时候用的别名表。越靠前越优先。</summary>
    private static readonly (string Slot, string[] Aliases)[] FileNameHints =
    {
        ("Arrow",       new[] { "normalselect", "normal", "arrow", "pointer", "default", "正常选择" }),
        ("Help",        new[] { "helpsel", "helpselect", "help", "question", "帮助选择" }),
        ("AppStarting", new[] { "workinginbackground", "working", "background", "appstarting", "progress", "work", "后台运行" }),
        ("Wait",        new[] { "busy", "wait", "loading", "spinner", "spin", "忙碌" }),
        ("Crosshair",   new[] { "areaselect", "precisionselect", "crosshair", "precision", "cross", "精确选择" }),
        ("IBeam",       new[] { "textselect", "ibeam", "beam", "text", "文本选择" }),
        ("NWPen",       new[] { "handwriting", "nwpen", "pen", "手写" }),
        ("No",          new[] { "unavailable", "unavailiable", "notallowed", "forbidden", "block", "no", "不可用" }),
        ("SizeNS",      new[] { "verticalresize", "resizens", "vertical", "vert", "updown", "ns", "垂直调整" }),
        ("SizeWE",      new[] { "horizontalresize", "resizewe", "horizontal", "horz", "leftright", "ew", "we", "水平调整" }),
        ("SizeNWSE",    new[] { "resizediag1", "resizediagonal1", "sizenwse", "diagonalresize1", "diagonal1", "nwse", "dgn1", "diag1", "对角线调整1" }),
        ("SizeNESW",    new[] { "resizediag2", "resizediagonal2", "sizenesw", "diagonalresize2", "diagonal2", "nesw", "dgn2", "diag2", "对角线调整2" }),
        ("SizeAll",     new[] { "moveall", "sizeall", "move", "grab", "all", "移动" }),
        ("UpArrow",     new[] { "alternativeselect", "alternateselect", "alternative", "alternate", "uparrow", "up", "候选选择" }),
        ("Hand",        new[] { "linkselect", "link", "hand", "链接选择" }),
        ("Pin",         new[] { "location", "pin", "位置选择" }),
        ("Person",      new[] { "person", "people", "人员选择" }),
    };

    // ------------------------------------------------------------------ 扫描

    /// <summary>扫一个文件夹，把里面的 .cur/.ani 认到指针位上。</summary>
    public static ThemeScan Scan(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"找不到文件夹：{folder}");

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => CursorExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? inf = FindInstallInf(folder);
        var bySlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (inf is not null)
        {
            foreach (var (slot, fileName) in ReadInstallInf(inf))
            {
                // inf 里写的是文件名，得在扫出来的文件里按名字找
                string? hit = files.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) bySlot[slot] = hit;
            }
        }

        var results = new List<ThemeFile>(files.Count);
        foreach (var f in files)
        {
            string? slot = bySlot.FirstOrDefault(kv =>
                string.Equals(kv.Value, f, StringComparison.OrdinalIgnoreCase)).Key;

            if (slot is not null)
            {
                results.Add(new ThemeFile(f, slot, SlotMatchSource.InstallInf));
                continue;
            }

            // inf 里没提到（或者压根没有 inf）就按文件名猜；
            // 已经被 inf 占掉的指针位不再参与竞猜，免得把作者的本意顶掉
            string? guess = GuessSlot(Path.GetFileName(f), bySlot.Keys);
            results.Add(new ThemeFile(f, guess, guess is null ? SlotMatchSource.None : SlotMatchSource.FileName));
        }

        return new ThemeScan(folder, inf, results);
    }

    private static string? FindInstallInf(string folder)
    {
        string exact = Path.Combine(folder, "install.inf");
        if (File.Exists(exact)) return exact;

        return Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories)
            .OrderBy(f => f.Length)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------ 按文件名猜

    /// <summary>
    /// 规范化文件名：去掉扩展名、空格、下划线、连字符，全部小写；**数字要保留**。
    ///
    /// 数字不能丢：主题包里 <c>dgn1</c>/<c>dgn2</c>、<c>resizeDIAG1</c>/<c>resizeDIAG2</c>
    /// 是两个不同的指针位（对角线调整 1 和 2），把数字一去掉它们就变成同一个字符串，
    /// 第二个会被认成第一个——实测真实主题包时就是这么错的。
    /// 「-mini」「_static」「48」这类噪声交给"包含/前缀"匹配去吸收。
    /// </summary>
    internal static string Normalize(string fileName)
    {
        var sb = new StringBuilder();
        foreach (char c in Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c > 0x2E80) sb.Append(c);   // 字母数字 + 中文
        }
        return sb.ToString();
    }

    private static string? GuessSlot(string fileName, IEnumerable<string> taken)
    {
        string norm = Normalize(fileName);
        if (norm.Length == 0) return null;

        var skip = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

        // 一轮比一轮宽松。顺序不能反：先紧后松才不会把 diag2 认成 diag1。
        foreach (var (slot, aliases) in FileNameHints)
        {
            if (skip.Contains(slot)) continue;
            if (aliases.Any(a => string.Equals(Normalize(a), norm, StringComparison.OrdinalIgnoreCase)))
                return slot;
        }

        // 名字里带着这个别名（"resizeNS-mini"、"text_static_blue"）
        foreach (var (slot, aliases) in FileNameHints)
        {
            if (skip.Contains(slot)) continue;
            if (aliases.Any(a => a.Length >= 4 && norm.Contains(Normalize(a), StringComparison.OrdinalIgnoreCase)))
                return slot;
        }

        // 以这个别名开头/结尾（"pen-mini"、"millennium_NS"）。
        // 这一轮允许短别名（pen / ns），但只在词首词尾，不会误伤中间
        foreach (var (slot, aliases) in FileNameHints)
        {
            if (skip.Contains(slot)) continue;
            if (aliases.Any(a => a.Length >= 2 &&
                                 (norm.StartsWith(Normalize(a), StringComparison.OrdinalIgnoreCase) ||
                                  norm.EndsWith(Normalize(a), StringComparison.OrdinalIgnoreCase))))
                return slot;
        }

        return null;
    }

    // ------------------------------------------------------------------ 读 install.inf

    /// <summary>
    /// 从 install.inf 里读出"指针位 → 文件名"。
    /// 先看有没有逐个指针位写死值名的注册表行（最明确），没有再用 Schemes 那串位置列表。
    /// </summary>
    internal static List<(string Slot, string FileName)> ReadInstallInf(string infPath)
    {
        string text;
        try { text = ReadInfText(infPath); }
        catch { return new List<(string, string)>(); }

        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitRaw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var schemesRaw = new List<string>();

        string section = "";
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                section = close > 0 ? line[1..close].Trim() : "";
                continue;
            }

            if (section.Equals("Strings", StringComparison.OrdinalIgnoreCase))
            {
                var (k, v) = SplitKeyValue(line);
                if (k.Length > 0) strings[k] = v;
                continue;
            }

            // HKCU,"Control Panel\Cursors",<值名>,<类型>,"<路径>"
            if (line.StartsWith("HKCU,", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(@"Control Panel\Cursors", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitCsv(line);
                if (parts.Count >= 5)
                {
                    string valueName = parts[2].Trim().Trim('"');
                    string value = parts[4].Trim().Trim('"');

                    if (valueName.Length == 0) continue;   // 默认值 = 方案名，不是指针位

                    if (valueName.Equals("Schemes", StringComparison.OrdinalIgnoreCase))
                        schemesRaw.Add(value);
                    else if (values.Contains(valueName, StringComparer.OrdinalIgnoreCase))
                        explicitRaw[valueName] = value;
                }
            }
        }

        // 注意：%token% 必须**等整个文件读完**再解。
        // 真实主题包（Bibata、apple_cursor…）的 [Strings] 都写在最后，
        // 边读边解的话到注册表那几行时 strings 还是空的，一个都对不上。
        if (explicitRaw.Count > 0)
            return explicitRaw
                .Select(kv => (kv.Key, ResolveToken(kv.Value, strings)))
                .Where(x => x.Item2.Length > 0)
                .ToList();

        // 退回到 Schemes 的位置列表：逗号分隔，顺序和 CursorSlots.All 一一对应
        foreach (var value in schemesRaw)
        {
            var fields = value.Split(',');
            if (fields.Length < 15) continue;

            var list = new List<(string, string)>();
            for (int i = 0; i < CursorSlots.All.Count && i < fields.Length; i++)
            {
                string file = ResolveToken(fields[i], strings);
                if (file.Length > 0) list.Add((CursorSlots.All[i].RegName, file));
            }
            if (list.Count > 0) return list;
        }

        return new List<(string, string)>();
    }

    /// <summary>inf 里引用的值名的合法集合（就是 17 个指针位的注册表值名）。</summary>
    private static readonly string[] values = CursorSlots.All.Select(s => s.RegName).ToArray();

    /// <summary>
    /// 把 <c>%10%\%CUR_DIR%\%pointer%</c> 里的最后一节字符串键换成真实文件名。
    /// 认不出来就返回空串，调用方会当成"这个位置没配"。
    /// </summary>
    private static string ResolveToken(string value, Dictionary<string, string> strings)
    {
        if (value.Length == 0) return "";

        int last = value.LastIndexOf('%');
        if (last <= 0) return Path.GetFileName(value);

        int first = value.LastIndexOf('%', last - 1);
        if (first < 0) return Path.GetFileName(value);

        string token = value[(first + 1)..last];
        if (strings.TryGetValue(token, out string? file)) return Path.GetFileName(file);

        // 键没定义（有些包会引用不存在的键），退而求其次：整串当路径看
        return Path.GetFileName(value.Replace("%10%", "").Replace("%CUR_DIR%", ""));
    }

    /// <summary>inf 可能是 UTF-16LE（带 BOM，中文主题包常用）也可能是 ANSI/UTF-8。</summary>
    private static string ReadInfText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        return new UTF8Encoding(false).GetString(bytes);
    }

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return ("", "");
        return (line[..eq].Trim(), line[(eq + 1)..].Trim().Trim('"'));
    }

    /// <summary>按逗号切，但引号里的逗号不算分隔符。</summary>
    private static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false;

        foreach (char c in line)
        {
            if (c == '"') { inQuote = !inQuote; sb.Append(c); }
            else if (c == ',' && !inQuote) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }
}
