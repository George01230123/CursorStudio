using System.Drawing;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CursorStudio.Core;

public static class AppPaths
{
    private static string? _override;

    /// <summary>
    /// 换个根目录。自检用这个把文件写到临时目录，
    /// 免得测试把用户真正的方案和备份覆盖掉。
    /// </summary>
    public static void OverrideRoot(string? path) => _override = path;

    public static string Root => _override ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorStudio");

    public static string Images => Path.Combine(Root, "images");
    public static string Cursors => Path.Combine(Root, "cursors");
    public static string Schemes => Path.Combine(Root, "schemes");
    public static string BackupFile => Path.Combine(Root, "backup-before-first-apply.json");
    public static string StateFile => Path.Combine(Root, "state.json");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Cursors);
        Directory.CreateDirectory(Schemes);
    }
}

/// <summary>
/// 一个指针位的配置。存成 JSON，所以颜色用字符串、不用 <see cref="Color"/>。
/// </summary>
public sealed class SlotState
{
    /// <summary>导入时复制到我们目录里的那份副本的绝对路径；null = 这个位没配图，保持系统原样。</summary>
    public string? SourceImage { get; set; }

    public int Size { get; set; } = 32;
    public bool KeepAspect { get; set; } = true;
    public bool RemoveBackground { get; set; }
    public string BackgroundKey { get; set; } = "#FFFFFF";
    public int Tolerance { get; set; } = 30;

    /// <summary>图形占画布的比例 0.3~1.0。系统自带箭头只占 59%，铺满会显得比原来的指针大一圈。</summary>
    public double Scale { get; set; } = RenderSettings.DefaultScale;

    public bool Shadow { get; set; }

    /// <summary>-1 = 自动算。</summary>
    public int HotX { get; set; } = -1;
    public int HotY { get; set; } = -1;

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(SourceImage);

    public RenderSettings ToRenderSettings() => new()
    {
        Size = Size,
        KeepAspect = KeepAspect,
        RemoveBackground = RemoveBackground,
        BackgroundKey = ParseColor(BackgroundKey, Color.White),
        Tolerance = Tolerance,
        Scale = Scale is >= 0.3 and <= 1.0 ? Scale : RenderSettings.DefaultScale,
        Shadow = Shadow,
        HotX = HotX,
        HotY = HotY,
    };

    public static string FormatColor(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color ParseColor(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v))
            return Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        try { return ColorTranslator.FromHtml("#" + s); }
        catch { return fallback; }
    }

    public SlotState Clone() => (SlotState)MemberwiseClone();
}

/// <summary>一份完整的指针方案：每个指针位配什么图、怎么配。</summary>
public sealed class Workspace
{
    public string SchemeName { get; set; } = "我的指针方案";
    public Dictionary<string, SlotState> Slots { get; set; } = new();

    /// <summary>取某个指针位的配置，没有就现建一个。</summary>
    public SlotState For(string regName)
    {
        if (!Slots.TryGetValue(regName, out var s))
            Slots[regName] = s = new SlotState();
        return s;
    }

    public SlotState? Peek(string regName) => Slots.TryGetValue(regName, out var s) ? s : null;

    public int ConfiguredCount => CursorSlots.All.Count(s => Peek(s.RegName)?.HasImage == true);

    /// <summary>导入方案包时没有随包带来的源图，仅用于界面提示，不写进 JSON。</summary>
    [JsonIgnore]
    public List<string> MissingImages { get; set; } = new();

    public Workspace CloneDeep()
    {
        var w = new Workspace { SchemeName = SchemeName };
        foreach (var (k, v) in Slots) w.Slots[k] = v.Clone();
        return w;
    }
}

public static class Store
{
    /// <summary>
    /// 每个 .cur 里塞进去的尺寸。Windows 会按当前指针大小挑最接近的那个，省得高分屏上糊。
    /// 32/48/64 覆盖 100%/150%/200% 缩放，96 再兜住 300%。
    /// win2xcur 用的标准档是 32/48/64/96/128/256，再往上单张就要几十上百 KB，
    /// 收益很小，这里取到 96 为止。
    /// </summary>
    public static readonly int[] CursorSizes = { 32, 48, 64, 96 };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    // ---------------------------------------------------------------- 图片导入

    /// <summary>
    /// 把用户选的图片复制进我们的目录。
    /// 用内容哈希命名：同一张图反复导入只占一份，而且换了图就是新路径——
    /// 这一点对 .cur 尤其重要，因为 Windows 的 LoadCursorFromFile 是按路径缓存的，
    /// 路径不变的话改了内容也不会生效。
    /// </summary>
    public static string ImportImage(string sourcePath)
    {
        AppPaths.EnsureAll();

        byte[] bytes = File.ReadAllBytes(sourcePath);
        // 先验证能解码，别把一个坏文件拷进来，之后渲染时才发现
        using (var ms = new MemoryStream(bytes, writable: false))
        using (var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true))
        {
            if (img.Width == 0 || img.Height == 0)
                throw new InvalidOperationException("图片尺寸是 0");
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext.Length is 0 or > 6) ext = ".img";

        string dest = Path.Combine(AppPaths.Images, hash + ext);
        if (!File.Exists(dest))
        {
            string tmp = dest + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, dest, overwrite: true);
        }

        return dest;
    }

    // ---------------------------------------------------------------- .cur 生成

    public sealed record BuildResult(Dictionary<string, string> Files, List<string> Warnings);

    /// <summary>
    /// 把方案里配了图的所有指针位生成 .cur 文件。
    /// 文件名带内容哈希——内容变了路径就变，绕开 Windows 的光标缓存；
    /// 内容没变就复用同一个文件名，注册表里也不会堆一堆死路径。
    /// </summary>
    public static BuildResult BuildCursorFiles(Workspace ws, RenderCache cache)
    {
        AppPaths.EnsureAll();
        var files = new Dictionary<string, string>();
        var warnings = new List<string>();
        string staging = Path.Combine(AppPaths.Root, "staging");
        Directory.CreateDirectory(staging);

        foreach (var slot in CursorSlots.All)
        {
            var st = ws.Peek(slot.RegName);
            if (st is null || !st.HasImage) continue;

            var images = new List<CurImage>();
            bool ok = true;

            foreach (int size in CursorSizes)
            {
                var rs = st.ToRenderSettings();
                rs.Size = size;

                var bmp = cache.Render(st.SourceImage!, rs, out string? err);
                if (bmp is null)
                {
                    warnings.Add($"{slot.DisplayName}：渲染失败 — {err}");
                    ok = false;
                    break;
                }

                Point hot = rs.IsAutoHotSpot
                    ? Renderer.AutoHotSpot(bmp, slot.DefaultHotSpot)
                    : Renderer.RescaleHotSpot(rs.HotX, rs.HotY, st.Size, size);

                images.Add(new CurImage(bmp, hot.X, hot.Y));
            }

            if (!ok || images.Count == 0) continue;

            try
            {
                string tmp = Path.Combine(staging, slot.RegName + ".cur");
                CurFile.Write(tmp, images);

                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tmp)))[..16].ToLowerInvariant();
                string final = Path.Combine(AppPaths.Cursors, $"{slot.RegName}_{hash}.cur");

                if (!File.Exists(final)) File.Move(tmp, final, overwrite: true);
                else File.Delete(tmp);

                files[slot.RegName] = final;
                CleanupOldCursors(slot.RegName, keep: Path.GetFileName(final));
            }
            catch (Exception ex)
            {
                warnings.Add($"{slot.DisplayName}：写文件失败 — {ex.Message}");
            }
        }

        try { Directory.Delete(staging, recursive: true); } catch { /* 清不掉就算了，不影响功能 */ }

        return new BuildResult(files, warnings);
    }

    private static void CleanupOldCursors(string regName, string keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Cursors, regName + "_*.cur"))
            {
                if (!string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                    File.Delete(f);
            }
        }
        catch { /* 旧文件删不掉不影响这次应用 */ }
    }

    // ---------------------------------------------------------------- 备份

    public static bool HasBackup => File.Exists(AppPaths.BackupFile);

    public static CursorSnapshot? ReadBackup()
    {
        try
        {
            return File.Exists(AppPaths.BackupFile)
                ? CursorSnapshot.FromJson(File.ReadAllText(AppPaths.BackupFile))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 只在还没有备份的时候拍快照。
    /// 必须"只存第一次"——否则第二次应用时拍到的就是被我们自己改过的状态，
    /// 一键还原就还原到自己的指针上去了。
    /// </summary>
    public static bool EnsureBackup()
    {
        AppPaths.EnsureAll();
        if (HasBackup) return false;

        var snap = CursorRegistry.Capture();
        File.WriteAllText(AppPaths.BackupFile, snap.ToJson(), new UTF8Encoding(false));
        return true;
    }

    // ---------------------------------------------------------------- 工作状态 / 方案

    public static void SaveWorkspace(string path, Workspace ws)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(ws, JsonOpts), new UTF8Encoding(false));
    }

    public static Workspace? LoadWorkspace(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveState(Workspace ws) { AppPaths.EnsureAll(); SaveWorkspace(AppPaths.StateFile, ws); }
    public static Workspace? LoadState() => LoadWorkspace(AppPaths.StateFile);

    public static List<string> ListSchemes()
    {
        AppPaths.EnsureAll();
        return Directory.EnumerateFiles(AppPaths.Schemes, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList()!;
    }

    public static string SchemePath(string name) => Path.Combine(AppPaths.Schemes, Sanitize(name) + ".json");

    public static void SaveScheme(Workspace ws) { AppPaths.EnsureAll(); SaveWorkspace(SchemePath(ws.SchemeName), ws); }

    public static Workspace? LoadScheme(string name) => LoadWorkspace(SchemePath(name));

    public static void DeleteScheme(string name)
    {
        try { File.Delete(SchemePath(name)); } catch { /* 删不掉就算了 */ }
    }

    /// <summary>方案名会变成文件名，挡掉路径分隔符和非法字符。</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')') sb.Append(c);
            else sb.Append('_');
        }
        string s = sb.ToString().Trim();
        if (s.Length == 0) s = "未命名方案";
        return s.Length > 60 ? s[..60] : s;
    }

    // ---------------------------------------------------------------- 导出

    /// <summary>方案包里存的源图长边上限。重新生成 32/48/64/96 的指针绰绰有余，又不至于把包撑大。</summary>
    private const int PackImageMax = 512;

    /// <summary>
    /// 导出一个方案包（zip）：
    ///   cursors/*.cur   —— 成品，可以脱离本工具手动安装
    ///   images/*.png    —— 源图（缩到 512 以内），导入方能继续改
    ///   scheme.json     —— 本工具的方案数据，导入时用
    ///   说明.txt
    /// </summary>
    public static void ExportPack(Workspace ws, string zipPath, BuildResult built)
    {
        var portable = ws.CloneDeep();
        portable.SchemeName = Sanitize(ws.SchemeName);

        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        foreach (var (regName, path) in built.Files)
        {
            var slot = CursorSlots.ByRegName(regName);
            zip.CreateEntryFromFile(path, $"cursors/{slot?.DisplayName ?? regName}.cur", CompressionLevel.Optimal);
        }

        // 源图：重新编码成 PNG，同时把长边压到 512 以内
        foreach (var slot in CursorSlots.All)
        {
            var st = portable.Peek(slot.RegName);
            if (st?.HasImage != true) continue;

            try
            {
                var packed = LoadPackedImage(st.SourceImage!);
                string imageEntry = $"images/{slot.RegName}.png";
                var e = zip.CreateEntry(imageEntry, CompressionLevel.Optimal);
                using (var s = e.Open())
                    packed.Save(s, System.Drawing.Imaging.ImageFormat.Png);
                packed.Dispose();

                // 包里的路径是相对的，导入方按这个位置找
                st.SourceImage = imageEntry;
            }
            catch
            {
                st.SourceImage = null;
            }
        }

        WriteEntry(zip, "scheme.json", JsonSerializer.Serialize(portable, JsonOpts));
        WriteEntry(zip, "说明.txt", ExportReadme(ws));
    }

    private static Bitmap LoadPackedImage(string path)
    {
        var src = ImageLoader.Load(path);
        double k = Math.Min(1.0, (double)PackImageMax / Math.Max(src.Width, src.Height));

        // 本来就没超限就别重采样了：1:1 走一遍双三次插值未必是逐像素等价的，
        // 而"导出再导入应该得到完全一样的 .cur"是自检里明确要验的性质
        if (k >= 1.0) return src;

        using (src)
        {
            int w = Math.Max(1, (int)Math.Round(src.Width * k));
            int h = Math.Max(1, (int)Math.Round(src.Height * k));
            return Renderer.ScaleTo(src, w, h);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ExportReadme(Workspace ws)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"指针方案：{ws.SchemeName}");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("用法（推荐）：");
        sb.AppendLine("  打开「鼠标指针美化」→ 底部方案栏点「导入方案包」→ 选这个 zip → 再点「应用」。");
        sb.AppendLine();
        sb.AppendLine("用法（手动）：");
        sb.AppendLine("  1. 把 cursors 文件夹整个复制到某个固定的位置（之后不要挪动或删除）。");
        sb.AppendLine("  2. 打开 控制面板 → 鼠标 → 指针，逐个选中指针位，点「浏览」指向对应的 .cur 文件。");
        sb.AppendLine();
        sb.AppendLine("本包含以下指针位：");
        foreach (var slot in CursorSlots.All)
        {
            if (ws.Peek(slot.RegName)?.HasImage == true)
                sb.AppendLine($"  · {slot.DisplayName}（{slot.RegName}）→ cursors/{slot.DisplayName}.cur");
        }
        sb.AppendLine();
        sb.AppendLine("注：每个 .cur 里打包了 32/48/64/96 四种尺寸，Windows 会按你的指针大小自动挑最合适的一个。");
        return sb.ToString();
    }

    public static Workspace ImportPack(string zipPath)
    {
        AppPaths.EnsureAll();

        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var schemeEntry = zip.GetEntry("scheme.json")
            ?? throw new InvalidOperationException("这个 zip 里没有 scheme.json，不是本工具导出的方案包。");

        string json;
        using (var sr = new StreamReader(schemeEntry.Open(), Encoding.UTF8))
            json = sr.ReadToEnd();

        var ws = JsonSerializer.Deserialize<Workspace>(json, JsonOpts)
            ?? throw new InvalidOperationException("scheme.json 内容无法解析。");

        // 包里的 SourceImage 是导出时的相对路径（images/XXX.png），在这里落到本机。
        // 找不到的（比如是更早版本导出的、或者 zip 被改过）就清掉并在界面上提示，
        // 而不是留一个不存在的路径等到点了「应用」才报错。
        var missing = new List<string>();
        foreach (var (regName, st) in ws.Slots)
        {
            if (!st.HasImage) continue;

            var entry = zip.GetEntry(st.SourceImage!.Replace('\\', '/'))
                        ?? zip.Entries.FirstOrDefault(e =>
                            string.Equals(e.FullName, st.SourceImage, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                missing.Add(CursorSlots.ByRegName(regName)?.DisplayName ?? regName);
                st.SourceImage = null;
                continue;
            }

            string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext.Length is 0 or > 6) ext = ".png";
            string dest = Path.Combine(AppPaths.Images, $"pack_{Sanitize(regName)}{ext}");

            using (var input = entry.Open())
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write))
                input.CopyTo(output);

            // 解出来要能解码才算数，否则一样当成缺失
            try
            {
                using var check = ImageLoader.Load(dest);
            }
            catch
            {
                try { File.Delete(dest); } catch { }
                missing.Add(CursorSlots.ByRegName(regName)?.DisplayName ?? regName);
                st.SourceImage = null;
                continue;
            }

            st.SourceImage = dest;
        }

        ws.MissingImages = missing;
        return ws;
    }
}
