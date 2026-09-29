using System.Drawing;

namespace CursorStudio.Core;

/// <summary>
/// 渲染结果缓存。
///
/// 关键设计：渲染出来的像素只跟「源图 + 尺寸 + 缩放方式 + 相对大小 + 抠图参数 + 投影」有关，
/// **跟热点无关**。
/// 所以拖热点定位置时渲染参数一点没变，直接命中缓存，一帧像素运算都不用做。
///
/// 缓存按完整参数串做键，而不是只留"最后一个结果"——因为生成一个 .cur 要同时要
/// 32/48/64/96 四个尺寸，只留一份的话前一张会在下一张渲染出来时被提前 Dispose 掉。
///
/// 缓存里的 Bitmap 归这个对象所有，调用方**不要** Dispose 拿到的图。
/// </summary>
public sealed class RenderCache : IDisposable
{
    /// <summary>源图预缩放上限。目标才 32~96，再大纯属浪费。</summary>
    private const int WorkingSize = 1024;

    /// <summary>
    /// 渲染结果最多留这么多张，超了按插入顺序淘汰最旧的。
    /// 给得宽一点：左侧列表一次刷新就要 17 个缩略图，太紧的话
    /// 缩略图会把画布正在显示的那张挤出去，白做一轮渲染。
    /// </summary>
    private const int MaxRendered = 64;

    private readonly Dictionary<string, Bitmap> _working = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _keyed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _rendered = new(StringComparer.Ordinal);

    // ---------------- 源图 ----------------

    private Bitmap? GetWorking(string sourcePath, out string? error)
    {
        error = null;
        if (_working.TryGetValue(sourcePath, out var cached)) return cached;

        try
        {
            var decoded = ImageLoader.Load(sourcePath);
            var prepared = PreScale(decoded);
            if (!ReferenceEquals(prepared, decoded)) decoded.Dispose();
            _working[sourcePath] = prepared;
            return prepared;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 源图的像素尺寸，拿不到返回 null。
    /// 界面在换算热点位置时要用它算图形在画布上的落位矩形，但不需要碰位图本身。
    /// </summary>
    public Size? GetSourceSize(string sourcePath)
    {
        var work = GetWorking(sourcePath, out _);
        return work is null ? null : new Size(work.Width, work.Height);
    }

    // ---------------- 抠图 ----------------

    private Bitmap GetKeyed(Bitmap working, string sourcePath, RenderSettings s)
    {
        string key = string.Join('|', sourcePath, s.BackgroundKey.ToArgb(), s.Tolerance);
        if (_keyed.TryGetValue(key, out var hit)) return hit;

        var keyed = Renderer.KeyOutBackground(working, s.BackgroundKey, s.Tolerance);
        Evict(_keyed, max: 6);
        _keyed[key] = keyed;
        return keyed;
    }

    // ---------------- 渲染 ----------------

    /// <summary>渲染成最终指针像素图。返回的 Bitmap 归缓存所有，不要 Dispose。</summary>
    public Bitmap? Render(string sourcePath, RenderSettings s, out string? error)
    {
        string key = string.Join('|',
            sourcePath, s.Size, s.KeepAspect, s.RemoveBackground,
            s.BackgroundKey.ToArgb(), s.Tolerance, s.Scale, s.Shadow);

        if (_rendered.TryGetValue(key, out var cached))
        {
            error = null;
            return cached;
        }

        var working = GetWorking(sourcePath, out error);
        if (working is null) return null;

        var src = s.RemoveBackground && s.Tolerance > 0
            ? GetKeyed(working, sourcePath, s)
            : working;

        // 缩放（含「相对大小」）和投影都在 Renderer.Compose 里，别在这里自己拼——
        // 拼漏一个参数，界面上的开关就会变成摆设
        var result = Renderer.Compose(src, s);

        Evict(_rendered, MaxRendered);
        _rendered[key] = result;
        return result;
    }

    // ---------------- 失效 ----------------

    /// <summary>换掉某个指针位的源图时，把它的中间结果全清掉，否则反复导大图会越吃越多内存。</summary>
    public void ForgetSource(string sourcePath)
    {
        DropWhere(_working, k => k.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
        DropWhere(_keyed, k => k.StartsWith(sourcePath + "|", StringComparison.OrdinalIgnoreCase));
        DropWhere(_rendered, k => k.StartsWith(sourcePath + "|", StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        DropWhere(_working, _ => true);
        DropWhere(_keyed, _ => true);
        DropWhere(_rendered, _ => true);
    }

    private static void DropWhere(Dictionary<string, Bitmap> map, Func<string, bool> predicate)
    {
        foreach (var k in map.Keys.Where(predicate).ToList())
        {
            map[k].Dispose();
            map.Remove(k);
        }
    }

    private static void Evict(Dictionary<string, Bitmap> map, int max)
    {
        while (map.Count >= max)
        {
            var oldest = map.Keys.First();
            map[oldest].Dispose();
            map.Remove(oldest);
        }
    }

    // ---------------- 预缩放 ----------------

    /// <summary>
    /// 把过大的源图分步缩到 1024 以内。
    /// 一次性从 6000 缩到 32，双三次插值会"跳采样"，边缘出现锯齿和摩尔纹；
    /// 反复对折到接近目标再插值，结果干净得多。返回的图归调用方。
    /// </summary>
    private static Bitmap PreScale(Bitmap src)
    {
        if (src.Width <= WorkingSize && src.Height <= WorkingSize) return src;

        var current = src;
        bool owned = false;

        while (current.Width > WorkingSize * 2 || current.Height > WorkingSize * 2)
        {
            var next = Renderer.ScaleTo(current, current.Width / 2, current.Height / 2);
            if (owned) current.Dispose();
            current = next;
            owned = true;
        }

        if (current.Width <= WorkingSize && current.Height <= WorkingSize)
            return owned ? current : src;

        double k = Math.Min((double)WorkingSize / current.Width, (double)WorkingSize / current.Height);
        var final = Renderer.ScaleTo(current, (int)Math.Round(current.Width * k), (int)Math.Round(current.Height * k));
        if (owned) current.Dispose();
        return final;
    }

    public void Dispose() => Clear();
}

public static class ImageLoader
{
    /// <summary>能导入的格式。GDI+ 自带这些；WebP 不在其中，系统不带解码器。</summary>
    public const string DialogFilter =
        "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico|" +
        "PNG|*.png|JPEG|*.jpg;*.jpeg|BMP|*.bmp|GIF（只取第一帧）|*.gif|" +
        "TIFF|*.tif;*.tiff|图标|*.ico|所有文件|*.*";

    /// <summary>
    /// 从文件读图。
    /// 先把字节读进内存再解码：GDI+ 直接 Bitmap(path) 会一直占着文件，
    /// 用户导入之后想删/改原图就会被拦。复制一份彻底脱钩。
    /// 返回的 Bitmap 归调用方，用完要 Dispose。
    /// </summary>
    public static Bitmap Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到文件：{path}");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"读不了这个文件（可能被别的程序占用）：{ex.Message}");
        }

        if (bytes.Length == 0)
            throw new InvalidOperationException("文件是空的");

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
            var bmp = new Bitmap(img);
            if (bmp.Width == 0 || bmp.Height == 0)
            {
                bmp.Dispose();
                throw new InvalidOperationException("图片尺寸是 0");
            }
            return bmp;
        }
        catch (InvalidOperationException) { throw; }
        catch
        {
            throw new InvalidOperationException(
                "解不开这个图片格式。支持 PNG / JPG / BMP / GIF / TIFF / ICO——" +
                "WebP 和 AVIF 系统不带解码器，请先转成 PNG 再导入。");
        }
    }
}
