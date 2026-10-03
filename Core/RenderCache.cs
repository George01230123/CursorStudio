using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;

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
    private readonly BitmapCache _keyed = new(StringComparer.Ordinal);
    private readonly BitmapCache _rendered = new(StringComparer.Ordinal);

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
        if (_keyed.TryGet(key, out var hit)) return hit;

        var keyed = Renderer.KeyOutBackground(working, s.BackgroundKey, s.Tolerance);
        _keyed.Add(key, keyed, max: 6);
        return keyed;
    }

    // ---------------- 渲染 ----------------

    /// <summary>渲染成最终指针像素图。返回的 Bitmap 归缓存所有，不要 Dispose。</summary>
    public Bitmap? Render(string sourcePath, RenderSettings s, out string? error)
    {
        string key = string.Join('|',
            sourcePath, s.Size, s.KeepAspect, s.RemoveBackground,
            s.BackgroundKey.ToArgb(), s.Tolerance, s.Scale, s.Shadow);

        if (_rendered.TryGet(key, out var cached))
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

        _rendered.Add(key, result, MaxRendered);
        return result;
    }

    /// <summary>
    /// 渲染一张并**把所有权交给调用方**（不进渲染缓存）。用完必须 Dispose。
    ///
    /// 批量生成 .cur/.ani 必须走这个，不能用 <see cref="Render"/>：
    /// 那种场景每个 (帧, 尺寸) 组合只会渲染一次，进缓存纯属白费；更要命的是缓存有上限、满了要淘汰，
    /// 而同一帧的 32 尺寸是在"定热点"那一趟先进缓存的，比它自己的 48/64/96 尺寸都早——
    /// 于是渲染后三个尺寸时正好把这一帧的 32 尺寸淘汰掉，接着 BuildBytes 读像素
    /// 就是 GDI+ 的 <c>Parameter is not valid</c>。60 帧动画会稳定踩中。
    /// </summary>
    public Bitmap? RenderOwned(string sourcePath, RenderSettings s, out string? error)
    {
        var working = GetWorking(sourcePath, out error);
        if (working is null) return null;

        var src = s.RemoveBackground && s.Tolerance > 0
            ? GetKeyed(working, sourcePath, s)
            : working;

        return Renderer.Compose(src, s);
    }

    // ---------------- 失效 ----------------

    /// <summary>换掉某个指针位的源图时，把它的中间结果全清掉，否则反复导大图会越吃越多内存。</summary>
    public void ForgetSource(string sourcePath)
    {
        DropWhere(_working, k => k.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
        _keyed.DropWhere(k => k.StartsWith(sourcePath + "|", StringComparison.OrdinalIgnoreCase));
        _rendered.DropWhere(k => k.StartsWith(sourcePath + "|", StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        DropWhere(_working, _ => true);
        _keyed.DropWhere(_ => true);
        _rendered.DropWhere(_ => true);
    }

    private static void DropWhere(Dictionary<string, Bitmap> map, Func<string, bool> predicate)
    {
        foreach (var k in map.Keys.Where(predicate).ToList())
        {
            map[k].Dispose();
            map.Remove(k);
        }
    }

    /// <summary>
    /// 定长 FIFO 缓存，淘汰的一定是最早放进去的那张。
    ///
    /// 这里不能用 <c>Dictionary</c> 加一句"Keys.First() 就是最旧的"：Dictionary 的枚举顺序
    /// **不是插入顺序**，删过元素之后（复用空闲槽位）就更乱，于是"淘汰最旧的"实际上变成了
    /// 随机淘汰——把调用方还拿在手里的位图 Dispose 掉，下一句读像素就是 GDI+ 的
    /// <c>Parameter is not valid</c>。
    ///
    /// 这个坑是 60 帧动画暴露出来的：那种规模一定会把缓存塞满，于是必然踩中；
    /// 而自检里原来最多只造 3 帧，缓存根本到不了上限，所以一直没被发现。
    /// </summary>
    private sealed class BitmapCache(IEqualityComparer<string> comparer)
    {
        private readonly Dictionary<string, Bitmap> _map = new(comparer);
        private readonly LinkedList<string> _order = new();

        public bool TryGet(string key, [NotNullWhen(true)] out Bitmap? bmp) => _map.TryGetValue(key, out bmp);

        /// <summary>放进去，必要时先淘汰最旧的，保证总数不超过 <paramref name="max"/>。</summary>
        public void Add(string key, Bitmap bmp, int max)
        {
            if (_map.ContainsKey(key))
            {
                _map[key].Dispose();
                _map[key] = bmp;      // 位置不变：它还是原来那个"新旧"
                return;
            }

            while (_map.Count >= max && _order.First is not null)
            {
                string oldest = _order.First.Value;
                _order.RemoveFirst();
                if (_map.Remove(oldest, out var victim)) victim.Dispose();
            }

            _map[key] = bmp;
            _order.AddLast(key);
        }

        public void DropWhere(Func<string, bool> predicate)
        {
            foreach (var key in _map.Keys.Where(predicate).ToList())
            {
                _map[key].Dispose();
                _map.Remove(key);
                _order.Remove(key);
            }
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
        "PNG|*.png|JPEG|*.jpg;*.jpeg|BMP|*.bmp|GIF（多帧会导成动画指针）|*.gif|" +
        "TIFF|*.tif;*.tiff|图标|*.ico|所有文件|*.*";

    /// <summary>多选时能一次挑一堆图当动画的帧。</summary>
    public const string FramesDialogFilter =
        "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico|所有文件|*.*";

    /// <summary>GIF 每帧延时的属性标签，值是 4 字节一组，单位 1/100 秒。</summary>
    private const int PropertyTagFrameDelay = 0x5100;

    /// <summary>
    /// 一次动画导入的结果。
    /// 里面的位图归调用方，Dispose 这个对象就会把它们一起放掉——
    /// 一帧一张位图，忘了放很容易在大 GIF 上把内存吃满。
    /// </summary>
    public sealed record AnimationLoad(List<Bitmap> Frames, int DelayMs, bool Truncated) : IDisposable
    {
        public void Dispose()
        {
            foreach (var f in Frames) f.Dispose();
            Frames.Clear();
        }
    }

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

        byte[] bytes = ReadBytes(path);

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

    private static byte[] ReadBytes(string path)
    {
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
        return bytes;
    }

    /// <summary>
    /// 读一个文件里的所有帧。
    /// 静态图就只有一帧；GIF 有多少帧读多少帧（超过 <paramref name="maxFrames"/> 就均匀抽帧）。
    ///
    /// GDI+ 的 SelectActiveFrame 会把每一帧按 GIF 自己的处置方式合成到画布上，
    /// 所以拿到的就是"眼睛看到的第 N 帧"，不用自己拼。
    /// </summary>
    public static AnimationLoad LoadFrames(string path, int maxFrames = AniFile.MaxFrames)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到文件：{path}");

        byte[] bytes = ReadBytes(path);
        var frames = new List<Bitmap>();

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);

            int total = 1;
            try
            {
                if (img.RawFormat.Guid == ImageFormat.Gif.Guid)
                    total = img.GetFrameCount(FrameDimension.Time);
            }
            catch { total = 1; }
            if (total < 1) total = 1;

            int keep = Math.Min(total, Math.Max(1, maxFrames));
            bool truncated = keep < total;

            // 超过上限就均匀抽帧，而不是掐掉尾巴——抽帧至少还能看出整个动作
            for (int i = 0; i < keep; i++)
            {
                int index = truncated ? (int)((long)i * total / keep) : i;
                if (total > 1) img.SelectActiveFrame(FrameDimension.Time, index);
                var bmp = new Bitmap(img);
                if (bmp.Width == 0 || bmp.Height == 0)
                {
                    bmp.Dispose();
                    continue;
                }
                frames.Add(bmp);
            }

            int delay = ReadGifDelay(img, total);
            if (frames.Count == 0) throw new InvalidOperationException("这个文件里一帧都没解出来");
            return new AnimationLoad(frames, delay, truncated);
        }
        catch (InvalidOperationException)
        {
            foreach (var f in frames) f.Dispose();
            throw;
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
            throw new InvalidOperationException(
                "解不开这个图片格式。支持 PNG / JPG / BMP / GIF / TIFF / ICO——" +
                "WebP 和 AVIF 系统不带解码器，请先转成 PNG 再导入。");
        }
    }

    /// <summary>
    /// 取 GIF 的帧延时，换算成毫秒。
    /// 很多 GIF 会把延时写成 0（"尽可能快"），浏览器一律当成 100ms；这里同理，
    /// 并夹到指针合理的区间——太快看不清，太慢就不像"忙"了。
    /// </summary>
    private static int ReadGifDelay(Image img, int total)
    {
        try
        {
            if (img.RawFormat.Guid != ImageFormat.Gif.Guid || total <= 1) return AniFile.DefaultDelayMs;

            var prop = img.GetPropertyItem(PropertyTagFrameDelay);
            if (prop?.Value is null || prop.Value.Length < 4) return AniFile.DefaultDelayMs;

            long sum = 0;
            int n = Math.Min(total, prop.Value.Length / 4);
            for (int i = 0; i < n; i++)
            {
                int hundredths = BitConverter.ToInt32(prop.Value, i * 4);
                sum += hundredths <= 1 ? 100 : hundredths * 10;   // 0 和 1 都按 100ms 算
            }
            if (n == 0) return AniFile.DefaultDelayMs;

            return Math.Clamp((int)(sum / n), AniFile.MinDelayMs, AniFile.MaxDelayMs);
        }
        catch
        {
            return AniFile.DefaultDelayMs;
        }
    }
}
