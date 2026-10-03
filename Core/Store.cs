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

    /// <summary>
    /// 第 2..N 帧的源图路径。null 或空 = 静态指针，<see cref="SourceImage"/> 就是唯一一帧。
    /// 单列一个字段而不是把 SourceImage 改成列表，是为了让老版本存下来的
    /// state.json / 方案包还能直接读——老文件里没有这个键，反序列化出来就是 null。
    /// </summary>
    public List<string>? ExtraFrames { get; set; }

    /// <summary>每帧显示多久（毫秒）。只有动画指针用得上。</summary>
    public int FrameDelayMs { get; set; } = AniFile.DefaultDelayMs;

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(SourceImage);

    /// <summary>是不是动画指针（两帧及以上）。</summary>
    [JsonIgnore]
    public bool IsAnimated => ExtraFrames is { Count: > 0 };

    [JsonIgnore]
    public int FrameCount => HasImage ? 1 + (ExtraFrames?.Count ?? 0) : 0;

    /// <summary>所有帧的源图路径，第一帧在最前。没配图时返回空表。</summary>
    public List<string> AllFrames()
    {
        var list = new List<string>();
        if (HasImage) list.Add(SourceImage!);
        if (ExtraFrames is not null) list.AddRange(ExtraFrames);
        return list;
    }

    /// <summary>把一串帧路径装回去：第一个进 SourceImage，其余进 ExtraFrames。</summary>
    public void SetFrames(IReadOnlyList<string> frames)
    {
        SourceImage = frames.Count > 0 ? frames[0] : null;
        ExtraFrames = frames.Count > 1 ? frames.Skip(1).ToList() : null;
    }

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

    /// <summary>
    /// MemberwiseClone 是浅拷贝——列表会被两个对象共用，改一个另一个跟着变。
    /// ExtraFrames 必须自己复制一份，否则"套用到全部指针"会把所有位串到同一串帧上。
    /// </summary>
    public SlotState Clone()
    {
        var copy = (SlotState)MemberwiseClone();
        copy.ExtraFrames = ExtraFrames is null ? null : new List<string>(ExtraFrames);
        return copy;
    }
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

    /// <summary>
    /// 把已经在内存里的一帧存进 images\。
    /// GIF 解出来的帧本来就没有对应文件，只能编码成 PNG 再落盘。
    /// 命名同样是内容哈希：GIF 里大量重复的帧、同一次导入的多帧，都只占一份。
    /// </summary>
    public static string ImportBitmap(Bitmap bmp)
    {
        AppPaths.EnsureAll();

        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        byte[] bytes = ms.ToArray();

        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        string dest = Path.Combine(AppPaths.Images, hash + ".png");
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
    /// 把方案里配了图的所有指针位生成 .cur / .ani 文件。
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

            var sources = st.AllFrames();
            bool animated = sources.Count > 1;

            try
            {
                // 热点先一次性定在"用户当前看到的那个尺寸"上，再往各尺寸换算。
                // 动画时用所有帧的并集来定，免得每帧各算各的导致系统播放时指针抖动。
                Point baseHot = ResolveHotSpot(st, slot, sources, cache);

                string ext = animated ? ".ani" : ".cur";
                string tmp = Path.Combine(staging, slot.RegName + ext);

                if (animated)
                {
                    var curFrames = new List<byte[]>(sources.Count);
                    for (int f = 0; f < sources.Count; f++)
                    {
                        // 每帧内部都是一个完整的 .cur，仍然带 32/48/64/96 四个尺寸
                        var bytes = RenderFrameBytes(st, slot, sources[f], baseHot, cache, out string? err);
                        if (bytes is null)
                        {
                            warnings.Add($"{slot.DisplayName}：第 {f + 1} 帧渲染失败 — {err}");
                            break;
                        }
                        curFrames.Add(bytes);
                    }

                    if (curFrames.Count != sources.Count) continue;

                    AniFile.Write(tmp, curFrames, st.Size, st.FrameDelayMs);
                }
                else
                {
                    var bytes = RenderFrameBytes(st, slot, sources[0], baseHot, cache, out string? err);
                    if (bytes is null)
                    {
                        warnings.Add($"{slot.DisplayName}：渲染失败 — {err}");
                        continue;
                    }
                    CurFile.WriteBytes(tmp, bytes);
                }

                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tmp)))[..16].ToLowerInvariant();
                string final = Path.Combine(AppPaths.Cursors, $"{slot.RegName}_{hash}{ext}");

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

    /// <summary>
    /// 定这个指针位的热点（在 <see cref="SlotState.Size"/> 这个尺寸下）。
    /// 手点的直接用；自动的先渲染各帧、叠成并集再按类型判尖角/中心。
    ///
    /// 界面预览和生成 .cur/.ani 都必须走这里——两边各算各的，
    /// 迟早会出现"预览上热点在这、装到系统里跑到别处去"。
    /// </summary>
    public static Point ResolveHotSpot(SlotState st, CursorSlot slot, IReadOnlyList<string> sources, RenderCache cache)
    {
        var settings = st.ToRenderSettings();
        if (!settings.IsAutoHotSpot)
            return new Point(
                Math.Clamp(st.HotX, 0, Math.Max(0, st.Size - 1)),
                Math.Clamp(st.HotY, 0, Math.Max(0, st.Size - 1)));

        if (slot.DefaultHotSpot == HotSpotPreset.Center)
            return new Point(st.Size / 2, st.Size / 2);

        if (sources.Count == 1)
        {
            var single = cache.Render(sources[0], settings, out _);
            return single is null ? new Point(0, 0) : Renderer.AutoHotSpot(single, slot.DefaultHotSpot);
        }

        // 一帧渲染完就并进累加图，而不是先把 N 帧都攥在手里再合。
        // 这里用 RenderOwned：图归自己所有，不受渲染缓存淘汰影响。
        Bitmap? union = null;
        try
        {
            foreach (var src in sources)
            {
                var bmp = cache.RenderOwned(src, settings, out _);
                if (bmp is null) continue;

                if (union is null) union = bmp;          // 第一张直接接管，不用再复制一份
                else
                {
                    Renderer.MergeAlphaMax(union, bmp);
                    bmp.Dispose();
                }
            }

            if (union is null) return new Point(0, 0);
            return Renderer.AutoHotSpot(union, slot.DefaultHotSpot);
        }
        finally
        {
            union?.Dispose();
        }
    }

    /// <summary>
    /// 渲染一帧 → 拼成 .cur 字节 → 立刻把位图放掉。
    /// 位图是 RenderOwned 出来的（归调用方），拼完字节就没有别的用处了。
    /// </summary>
    private static byte[]? RenderFrameBytes(SlotState st, CursorSlot slot, string source,
                                            Point baseHot, RenderCache cache, out string? error)
    {
        var images = RenderFrame(st, slot, source, baseHot, cache, out error);
        if (images is null) return null;

        try
        {
            return CurFile.BuildBytes(images);
        }
        finally
        {
            foreach (var ci in images) ci.Bitmap.Dispose();
        }
    }

    /// <summary>
    /// 把一帧源图渲染成"每个尺寸一张"的列表，热点按比例从 <paramref name="baseHot"/> 换算过去。
    /// 每个 <see cref="CurImage.Bitmap"/> 都归调用方，读完像素要自己 Dispose——
    /// 故意走 <see cref="RenderCache.RenderOwned"/> 而不是缓存版，免得被缓存淘汰掉。
    /// </summary>
    private static List<CurImage>? RenderFrame(SlotState st, CursorSlot slot, string source,
                                              Point baseHot, RenderCache cache, out string? error)
    {
        error = null;
        var images = new List<CurImage>(CursorSizes.Length);

        foreach (int size in CursorSizes)
        {
            var rs = st.ToRenderSettings();
            rs.Size = size;

            var bmp = cache.RenderOwned(source, rs, out error);
            if (bmp is null)
            {
                foreach (var done in images) done.Bitmap.Dispose();
                return null;
            }

            var hot = Renderer.RescaleHotSpot(baseHot.X, baseHot.Y, st.Size, size);
            images.Add(new CurImage(bmp, hot.X, hot.Y));
        }

        return images;
    }

    private static void CleanupOldCursors(string regName, string keep)
    {
        try
        {
            // .cur 和 .ani 都要扫：同一个指针位可能这次是动画、下次是静态，
            // 只清 .cur 的话旧 .ani 会一直躺在目录里
            foreach (var pattern in new[] { regName + "_*.cur", regName + "_*.ani" })
            foreach (var f in Directory.EnumerateFiles(AppPaths.Cursors, pattern))
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

        // 结尾的点和空格 Windows 存不出文件来（会被悄悄吃掉，导致名字和预期对不上）
        s = s.TrimEnd('.', ' ');

        if (s.Length == 0) s = "未命名方案";

        // CON / PRN / NUL / COM1… 是设备名，这些名字建不出文件
        if (ReservedNames.Contains(s, StringComparer.OrdinalIgnoreCase)) s += "_";

        return s.Length > 60 ? s[..60] : s;
    }

    /// <summary>Windows 的保留设备名，拿来当文件名会失败。</summary>
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // ---------------------------------------------------------------- 导出

    /// <summary>方案包里存的源图长边上限。重新生成 32/48/64/96 的指针绰绰有余，又不至于把包撑大。</summary>
    private const int PackImageMax = 512;

    /// <summary>
    /// 导出一个方案包（zip）：
    ///   cursors/*.cur / *.ani —— 成品，可以脱离本工具手动安装
    ///   images/*.png          —— 源图（缩到 512 以内），导入方能继续改
    ///   install.inf           —— 右键"安装"就能装成 Windows 主题包
    ///   scheme.json           —— 本工具的方案数据，导入时用
    ///   说明.txt
    /// </summary>
    public static void ExportPack(Workspace ws, string zipPath, BuildResult built)
    {
        var portable = ws.CloneDeep();
        portable.SchemeName = Sanitize(ws.SchemeName);

        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // 成品文件名要和 install.inf 里引用的一致，先统一算出来。
        // 用注册表值名（Arrow / Wait / Hand…）而不是中文显示名当文件名：
        // 包里要发给别人、还要被 install.inf 引用，纯 ASCII 名字最省事，
        // 换个语言版本的 Windows 也不会出乱码
        var cursorEntries = new List<(string RegName, string DisplayName, string EntryName)>();
        foreach (var (regName, path) in built.Files)
        {
            var slot = CursorSlots.ByRegName(regName);
            string name = regName + Path.GetExtension(path);
            zip.CreateEntryFromFile(path, $"cursors/{name}", CompressionLevel.Optimal);
            cursorEntries.Add((regName, slot?.DisplayName ?? regName, name));
        }

        // 源图：重新编码成 PNG，同时把长边压到 512 以内。
        // 动画要把每一帧都带上，不然对方拿到的是个只有第一帧的方案
        foreach (var slot in CursorSlots.All)
        {
            var st = portable.Peek(slot.RegName);
            if (st?.HasImage != true) continue;

            var frames = st.AllFrames();
            var entries = new List<string>(frames.Count);

            try
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    var packed = LoadPackedImage(frames[i]);
                    string imageEntry = $"images/{slot.RegName}_{i:D2}.png";
                    var e = zip.CreateEntry(imageEntry, CompressionLevel.Optimal);
                    using (var s = e.Open())
                        packed.Save(s, System.Drawing.Imaging.ImageFormat.Png);
                    packed.Dispose();

                    // 包里的路径是相对的，导入方按这个位置找
                    entries.Add(imageEntry);
                }

                st.SetFrames(entries);
            }
            catch
            {
                st.SetFrames(Array.Empty<string>());   // 有一帧导不出来就整个留空，不留半个动画
            }
        }

        WriteEntry(zip, "install.inf", ExportInstallInf(ws, cursorEntries), new UnicodeEncoding(false, true));
        WriteEntry(zip, "uninstall.bat", ExportUninstallBat(ws));
        WriteEntry(zip, "scheme.json", JsonSerializer.Serialize(portable, JsonOpts));
        WriteEntry(zip, "说明.txt", ExportReadme(ws, cursorEntries));
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

    private static void WriteEntry(ZipArchive zip, string name, string content, Encoding? encoding = null)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        // install.inf 用 UTF-16LE + BOM：方案名可能是中文，ANSI 的 inf 换个语言版本就成乱码了
        using var w = new StreamWriter(s, encoding ?? new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ExportReadme(Workspace ws, IReadOnlyList<(string RegName, string DisplayName, string EntryName)> cursorEntries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"指针方案：{ws.SchemeName}");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("用法（推荐）：");
        sb.AppendLine("  打开「鼠标指针美化」→ 底部方案栏点「导入方案包」→ 选这个 zip → 再点「应用」。");
        sb.AppendLine("  这条路不需要管理员权限。");
        sb.AppendLine();
        sb.AppendLine("用法（装成 Windows 指针方案，需要管理员权限）：");
        sb.AppendLine("  1. 把整个 zip 解压出来（install.inf 和 cursors 里的文件必须在同一个文件夹里）。");
        sb.AppendLine("  2. 右键 install.inf → 安装。");
        sb.AppendLine("  3. 打开「控制面板 → 鼠标 → 指针」，在下拉框里选中本方案。");
        sb.AppendLine("  想撤销就双击 uninstall.bat：只把它从方案列表里去掉，不删任何文件。");
        sb.AppendLine();
        sb.AppendLine("用法（完全手动）：");
        sb.AppendLine("  1. 把 cursors 文件夹整个复制到某个固定的位置（之后不要挪动或删除）。");
        sb.AppendLine("  2. 打开 控制面板 → 鼠标 → 指针，逐个选中指针位，点「浏览」指向对应的 .cur / .ani 文件。");
        sb.AppendLine();
        sb.AppendLine("本包含以下指针位：");
        foreach (var (regName, displayName, entryName) in cursorEntries)
            sb.AppendLine($"  · {displayName}（{regName}）→ cursors/{entryName}");
        sb.AppendLine();
        sb.AppendLine("注：每个 .cur 里打包了 32/48/64/96 四种尺寸，Windows 会按你的指针大小自动挑最合适的一个。");
        sb.AppendLine("    动画指针（.ani）只在「忙碌」和「后台运行」两个位置会被 Windows 播放。");
        return sb.ToString();
    }

    /// <summary>
    /// 生成 install.inf —— 就是各指针主题包通用的那种"右键 → 安装"。
    ///
    /// 写法对齐 star 最高的那几个主题包（Bibata_Cursor 4.1k★、apple_cursor 2.0k★
    /// 的 release 包里那份由 clickgen 生成的 install.inf）：
    ///
    ///   1. 文件复制到 <c>%windir%\Cursors\&lt;方案名&gt;\</c>
    ///   2. 往 <c>HKCU\Control Panel\Cursors\Schemes</c> 写一条方案，
    ///      这样它会出现在「鼠标属性 → 指针」的下拉框里
    ///   3. **顺带把各个指针位的值也写进 <c>HKCU\Control Panel\Cursors</c>**，
    ///      装完立刻生效，不用再去控制面板点一遍
    ///
    /// 几件容易踩的事：
    /// 1. DestinationDirs 用的是 dirid 10（%windir%），所以右键"安装"一定会弹 UAC。
    ///    主题包都这样，绕不开；不想提权就走本工具的「导入方案包」。
    /// 2. Schemes 的值是**逗号分隔、顺序固定**的一串路径。顺序错了指针位就整体张冠李戴——
    ///    这是这类主题包最常见的事故（Bibata 当年就栽在这上面）。顺序就是
    ///    <see cref="CursorSlots.All"/> 的顺序，和 Windows 自带方案的 17 段一一对应；
    ///    没配图的位置留空，Windows 会回落到默认指针。
    /// 3. [Strings] 里引用的每个键都必须真的有定义，引用了没定义 setupapi 会报错。
    /// 4. 非 ASCII 的 INF 必须存成 UTF-16LE，否则中文方案名在别的语言版本上会乱码
    ///    （调用方传的编码就是为这个）。
    /// </summary>
    private static string ExportInstallInf(Workspace ws, IReadOnlyList<(string RegName, string DisplayName, string EntryName)> files)
    {
        string scheme = Sanitize(ws.SchemeName);
        var byReg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) byReg[f.RegName] = f.EntryName;

        var sb = new StringBuilder();
        sb.AppendLine("; 由「鼠标指针美化」导出");
        sb.AppendLine($"; 方案：{ws.SchemeName}");
        sb.AppendLine("; 右键这个 install.inf → 安装，整套指针就装进 Windows 了。");
        sb.AppendLine("; 需要管理员权限（要把文件复制到 %windir%\\Cursors）。");
        sb.AppendLine("; 只想自己用、又不想提权的话，直接用「鼠标指针美化」里的「导入方案包」。");
        sb.AppendLine();
        sb.AppendLine("[Version]");
        sb.AppendLine("signature=\"$CHICAGO$\"");
        sb.AppendLine();
        sb.AppendLine("[DefaultInstall]");
        sb.AppendLine("CopyFiles = Scheme.Cur");
        sb.AppendLine("AddReg    = Scheme.Reg, Scheme.Apply");
        sb.AppendLine();
        sb.AppendLine("[DestinationDirs]");
        sb.AppendLine("Scheme.Cur = 10,\"%CUR_DIR%\"");
        sb.AppendLine();

        // Schemes 的值：17 段，一段一个指针位，没配图的留空
        var parts = new List<string>(CursorSlots.All.Count);
        foreach (var slot in CursorSlots.All)
            parts.Add(byReg.ContainsKey(slot.RegName) ? $"%10%\\%CUR_DIR%\\%{slot.RegName}%" : "");

        sb.AppendLine("; 方案列表：这一段决定它会不会出现在「鼠标属性 → 指针」的下拉框里。");
        sb.AppendLine("; 值是逗号分隔的 17 段，顺序和 Windows 自带的方案完全一致，不能改。");
        sb.AppendLine("[Scheme.Reg]");
        sb.AppendLine($"HKCU,\"Control Panel\\Cursors\\Schemes\",\"%SCHEME_NAME%\",,\"{string.Join(",", parts)}\"");
        sb.AppendLine();

        sb.AppendLine("; 直接把这套指针设为当前使用的指针（0x00020000 = REG_EXPAND_SZ）。");
        sb.AppendLine("; 只写配了图的指针位，没配的保持系统原样。");
        sb.AppendLine("[Scheme.Apply]");
        sb.AppendLine("HKCU,\"Control Panel\\Cursors\",,0x00020000,\"%SCHEME_NAME%\"");
        foreach (var slot in CursorSlots.All)
            if (byReg.ContainsKey(slot.RegName))
                sb.AppendLine($"HKCU,\"Control Panel\\Cursors\",{slot.RegName},0x00020000,\"%10%\\%CUR_DIR%\\%{slot.RegName}%\"");
        sb.AppendLine();
        sb.AppendLine("[Scheme.Cur]");
        foreach (var f in files) sb.AppendLine(f.EntryName);
        sb.AppendLine();
        sb.AppendLine("[Strings]");
        sb.AppendLine($"CUR_DIR     = \"Cursors\\{scheme}\"");
        sb.AppendLine($"SCHEME_NAME = \"{ws.SchemeName.Replace("\"", "'")}\"");
        foreach (var slot in CursorSlots.All)
            if (byReg.TryGetValue(slot.RegName, out string? name))
                sb.AppendLine($"{slot.RegName,-11} = \"{name}\"");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// 卸载脚本。主题包都会附一个——装完指针就进了 <c>C:\Windows\Cursors</c> 和注册表，
    /// 用户想反悔时不该只能靠手动翻注册表。
    /// 只删方案列表里那一条（也就是从「鼠标属性」的列表里去掉），文件留着不动：
    /// 删文件需要管理员权限，而且用户可能还想切回去。
    /// </summary>
    private static string ExportUninstallBat(Workspace ws)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"echo 正在从「鼠标属性 → 指针」的方案列表里移除：{ws.SchemeName}");
        sb.AppendLine($"reg delete \"HKCU\\Control Panel\\Cursors\\Schemes\" /v \"{ws.SchemeName.Replace("\"", "")}\" /f");
        sb.AppendLine("echo.");
        sb.AppendLine("echo 已移除。指针文件本身还留在 C:\\Windows\\Cursors 下，没有删除。");
        sb.AppendLine("echo 想换成别的方案，打开「控制面板 → 鼠标 → 指针」选一个即可。");
        sb.AppendLine("pause");
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

            var wanted = st.AllFrames();
            var landed = new List<string>(wanted.Count);
            string display = CursorSlots.ByRegName(regName)?.DisplayName ?? regName;
            bool broken = false;

            for (int i = 0; i < wanted.Count; i++)
            {
                var entry = FindEntry(zip, wanted[i]);
                if (entry is null) { broken = true; break; }

                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (ext.Length is 0 or > 6) ext = ".png";
                string dest = Path.Combine(AppPaths.Images, $"pack_{Sanitize(regName)}_{i}{ext}");

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
                    broken = true;
                    break;
                }

                landed.Add(dest);
            }

            // 动画少一帧就不成样子了，所以只要有一帧对不上，整个指针位留空，
            // 而不是留个"看起来能用、其实缺帧"的动画
            if (broken || landed.Count != wanted.Count)
            {
                foreach (var f in landed) { try { File.Delete(f); } catch { } }
                foreach (var f in wanted) { try { File.Delete(f); } catch { } }
                missing.Add(display);
                st.SetFrames(Array.Empty<string>());
                continue;
            }

            st.SetFrames(landed);
        }

        ws.MissingImages = missing;
        return ws;
    }

    /// <summary>在 zip 里找包内相对路径对应的条目，大小写和斜杠方向都容错。</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path)
    {
        return zip.GetEntry(path.Replace('\\', '/'))
            ?? zip.GetEntry(path)
            ?? zip.Entries.FirstOrDefault(e =>
                   string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
    }
}
