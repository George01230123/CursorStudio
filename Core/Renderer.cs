using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CursorStudio.Core;

/// <summary>一个指针位的全部渲染参数。</summary>
public sealed class RenderSettings
{
    /// <summary>渲染尺寸。Windows 默认按 32 渲染，高分屏是 48/64。</summary>
    public int Size { get; set; } = 32;

    /// <summary>true = 等比缩放居中（留透明边），false = 拉伸填满。非正方形图一定要开。</summary>
    public bool KeepAspect { get; set; } = true;

    /// <summary>把背景色抠成透明。导入带白底的图/截图时必开。</summary>
    public bool RemoveBackground { get; set; }

    public Color BackgroundKey { get; set; } = Color.White;

    /// <summary>抠图容差 0..100。0 = 不抠。</summary>
    public int Tolerance { get; set; } = 30;

    /// <summary>
    /// 图形占画布的比例，0.3~1.0。
    ///
    /// 为什么不是 1.0：系统自带的箭头在 32×32 画布里其实只占了 59%（内容框 12×19），
    /// 手型 75%、移动 72%，平均 66%。铺满整个画布的话，同一个位置跟系统指针一比
    /// 会明显大一圈、重一圈。所以默认按 70% 走，视觉重量才和原来的指针是一个量级。
    /// </summary>
    public double Scale { get; set; } = DefaultScale;

    public const double DefaultScale = 0.70;

    /// <summary>叠加一层投影，浅色背景上更容易看清。参数取自 Bibata 的 anicursorgen。</summary>
    public bool Shadow { get; set; }

    /// <summary>热点 X；-1 表示自动。单位是当前 <see cref="Size"/> 下的像素。</summary>
    public int HotX { get; set; } = -1;
    public int HotY { get; set; } = -1;

    public bool IsAutoHotSpot => HotX < 0 || HotY < 0;

    public RenderSettings Clone() => (RenderSettings)MemberwiseClone();
}

public static class Renderer
{
    /// <summary>一次渲染的产物：图 + 最终热点。</summary>
    public sealed record Result(Bitmap Image, int HotX, int HotY);

    public static Result Render(Bitmap source, RenderSettings s, HotSpotPreset preset)
    {
        using var keyed = s.RemoveBackground && s.Tolerance > 0
            ? KeyOutBackground(source, s.BackgroundKey, s.Tolerance)
            : null;

        var scaled = Compose(keyed ?? source, s);

        Point hot;
        if (s.IsAutoHotSpot)
        {
            hot = AutoHotSpot(scaled, preset);
        }
        else
        {
            // 用户手点的热点可能因为换尺寸而越界，夹一下保证 .cur 合法
            hot = new Point(
                Math.Clamp(s.HotX, 0, scaled.Width - 1),
                Math.Clamp(s.HotY, 0, scaled.Height - 1));
        }

        return new Result(scaled, hot.X, hot.Y);
    }

    /// <summary>
    /// 把背景色抠成透明。
    ///
    /// 两个要点：
    /// 1. 边界做渐变过渡，不然 JPG 那种带压缩噪点的图会留一圈生硬的毛边。
    /// 2. 边缘像素还要把背景色从它的颜色里减掉。抗锯齿的画在纯色底上的图形，
    ///    边缘那圈像素其实是"前景色 × 覆盖度 + 背景色 × (1−覆盖度)"混出来的。
    ///    只改 alpha 不改颜色的话，这些像素会带着白底的颜色留下，缩到 32px 就是
    ///    围一圈发白的晕边——白底 logo 抠出来最难看的就这一处。
    /// </summary>
    public static Bitmap KeyOutBackground(Bitmap src, Color key, int tolerancePercent)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
        }

        double t = Math.Clamp(tolerancePercent / 100.0, 0.0, 1.0);

        // 容差 0 就是"不许动"。这里必须显式返回：
        // 下面的前景距离估计只看颜色分布，不看容差，哪怕 t=0 也能估出一个正值，
        // 不拦住的话容差 0 反而会抠掉一圈像素
        if (t <= 0) return dst;

        double lo = t * 0.35;   // 距离小于 lo → 完全透明

        var data = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int len = stride * dst.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);

            // 距离大于 hi → 完全保留。hi 不是直接取容差，而是估出来的
            // "实心前景离背景有多远"，理由见 EstimateForegroundDistance。
            // 再和容差取大值兜底：容差是用户明确要求的"至少保留到这个距离"，
            // 估计值只允许把这个范围放宽（更少见的情形是估计不可靠、估小了）
            double hi = Math.Max(t, EstimateForegroundDistance(buf, len, key, t));

            // 32bpp 每像素 4 字节、无行填充，所以可以放心按 4 步进
            for (int i = 0; i + 3 < len; i += 4)
            {
                byte a = buf[i + 3];
                if (a == 0) continue;

                int db = Math.Abs(buf[i] - key.B);
                int dg = Math.Abs(buf[i + 1] - key.G);
                int dr = Math.Abs(buf[i + 2] - key.R);
                double dist = Math.Max(db, Math.Max(dg, dr)) / 255.0;

                double f = hi <= lo
                    ? (dist >= hi ? 1.0 : 0.0)
                    : Math.Clamp((dist - lo) / (hi - lo), 0.0, 1.0);

                if (f >= 1.0) continue;

                buf[i + 3] = (byte)Math.Round(a * f);

                // 覆盖度太低时除法会放大噪点，那点像素反正也看不见，不动它
                if (f >= 0.15)
                {
                    buf[i] = Unmix(buf[i], key.B, f);
                    buf[i + 1] = Unmix(buf[i + 1], key.G, f);
                    buf[i + 2] = Unmix(buf[i + 2], key.R, f);
                }
            }

            Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally
        {
            dst.UnlockBits(data);
        }

        return dst;
    }

    /// <summary>观测色 = f·真色 + (1−f)·背景色  ⇒  真色 = (观测色 − (1−f)·背景色) / f</summary>
    private static byte Unmix(byte observed, byte bg, double f)
    {
        double v = (observed - (1.0 - f) * bg) / f;
        return (byte)Math.Clamp(Math.Round(v), 0, 255);
    }

    /// <summary>
    /// 估计"实心前景"离背景色有多远：取明显不属于背景的那些像素的 85 分位。
    ///
    /// 为什么不能直接用容差：抗锯齿边缘像素的距离正比于它的覆盖度，
    /// 要知道"满覆盖时距离是多少"才能把覆盖度反算出来。深蓝箭头在白底上，
    /// 实心处距离约 0.84；那么距离 0.42 的边缘像素覆盖度就是一半，
    /// 而不是"离容差 0.30 还差一点、算全不透明"。
    ///
    /// 取 85 分位是为了不被个别极值带跑。前景和背景对比不足
    /// （比如浅灰图形放在白底上）时这个估计不可靠，直接退回容差值。
    /// </summary>
    private static double EstimateForegroundDistance(byte[] buf, int len, Color key, double t)
    {
        int tIdx = (int)Math.Round(t * 255);
        var hist = new int[256];
        int total = 0, above = 0;

        for (int i = 0; i + 3 < len; i += 4)
        {
            if (buf[i + 3] == 0) continue;
            total++;

            int d = Math.Max(Math.Abs(buf[i] - key.B),
                    Math.Max(Math.Abs(buf[i + 1] - key.G), Math.Abs(buf[i + 2] - key.R)));
            hist[d]++;
            if (d >= tIdx) above++;
        }

        // 样本太少说明这多半不是"纯色底上的图形"，别瞎估
        if (above < Math.Max(24, total / 40)) return Math.Min(1.0, t);

        int want = (int)Math.Round(above * 0.85);
        int acc = 0;
        for (int b = tIdx; b < 256; b++)
        {
            acc += hist[b];
            if (acc >= want) return Math.Min(1.0, (b + 1) / 255.0);
        }
        return 1.0;
    }

    /// <summary>
    /// 已经抠好背景的图 → 最终像素图：按「相对大小」缩放到画布上，再叠可选的投影。
    ///
    /// 这里是"渲染参数 → 像素"的**唯一**实现，<see cref="Render"/> 和
    /// <c>RenderCache.Render</c> 都必须走这条。曾经两边各写了一遍，缓存那条路上
    /// 漏掉了 Scale 和 Shadow——界面上拖「相对大小」、勾「投影」完全没效果，
    /// 而自检只测了 <see cref="Render"/> 这一侧，照样全绿。
    /// </summary>
    public static Bitmap Compose(Bitmap src, RenderSettings s)
    {
        var scaled = Scale(src, s.Size, s.KeepAspect, s.Scale);

        if (s.Shadow)
        {
            var shadowed = AddDropShadow(scaled);
            scaled.Dispose();
            scaled = shadowed;
        }

        return scaled;
    }

    /// <summary>缩放到 size×size 的正方形画布，多余的部分留透明。</summary>
    public static Bitmap Scale(Bitmap src, int size, bool keepAspect, double scale = 1.0)
    {
        var dst = NewCanvas(size, size);
        using var g = Prepare(dst);
        DrawScaled(g, src, FitRect(src.Width, src.Height, size, keepAspect, scale));
        return dst;
    }

    /// <summary>直接缩放到指定宽高（不保持比例、不留边），预缩放和缩略图用。</summary>
    public static Bitmap ScaleTo(Bitmap src, int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        var dst = NewCanvas(w, h);
        using var g = Prepare(dst);
        DrawScaled(g, src, new Rectangle(0, 0, w, h));
        return dst;
    }

    private static Bitmap NewCanvas(int w, int h) => new(w, h, PixelFormat.Format32bppArgb);

    private static Graphics Prepare(Bitmap canvas)
    {
        var g = Graphics.FromImage(canvas);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        return g;
    }

    private static void DrawScaled(Graphics g, Bitmap src, Rectangle dest)
    {
        // TileFlipXY：让双三次插值在图像边缘"翻转"取样，而不是取到画布外的透明像素。
        // 不加这个，缩小后的图四边会有一圈发暗的边。
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(src, dest, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
    }

    /// <summary>
    /// 算出图形在画布上实际占的那块矩形。
    /// <paramref name="scale"/> 是占画布的比例——长边会缩到这个尺寸上，
    /// 这样导入的图不会比系统自带指针显得大一圈。
    /// </summary>
    public static Rectangle FitRect(int sw, int sh, int size, bool keepAspect, double scale = 1.0)
    {
        int target = Math.Clamp((int)Math.Round(size * scale), 1, size);

        if (!keepAspect)
        {
            // "拉伸填满"仍然是拉伸（不管比例），只是整体缩到设定的占比
            int off = (size - target) / 2;
            return new Rectangle(off, off, target, target);
        }

        double k = Math.Min((double)target / sw, (double)target / sh);
        int w = Math.Max(1, (int)Math.Round(sw * k));
        int h = Math.Max(1, (int)Math.Round(sh * k));
        return new Rectangle((size - w) / 2, (size - h) / 2, w, h);
    }

    /// <summary>
    /// 画布布局变了（改尺寸或改相对大小），把手动定的热点按它在图形里的相对位置搬过去。
    /// 直接按比例乘是不够的：图形在画布里是居中摆的，缩放的同时偏移也在变。
    /// </summary>
    public static Point MapHotSpot(Point hot, Rectangle oldRect, Rectangle newRect)
    {
        if (oldRect.Width <= 0 || oldRect.Height <= 0) return hot;

        double u = (hot.X - oldRect.X) / (double)oldRect.Width;
        double v = (hot.Y - oldRect.Y) / (double)oldRect.Height;

        return new Point(
            (int)Math.Round(newRect.X + u * newRect.Width),
            (int)Math.Round(newRect.Y + v * newRect.Height));
    }

    /// <summary>
    /// 给图形加一层投影。
    /// 参数照搬 Bibata 的 anicursorgen：右移 9.375%、下移 3.125%、模糊半径 3.125%、
    /// 颜色 25% 黑——都是按画布尺寸的百分比算，所以 32 和 64 上观感一致。
    /// </summary>
    public static Bitmap AddDropShadow(Bitmap src)
    {
        int n = src.Width;
        int right = (int)Math.Round(n * 0.09375);
        int down = (int)Math.Round(n * 0.03125);
        int blur = Math.Max(1, (int)Math.Round(n * 0.03125));

        // 阴影就是"把图形的不透明区域涂成半透明黑"
        var mask = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        var data = src.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var md = mask.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int len = data.Stride * n;
            var srcBuf = new byte[len];
            Marshal.Copy(data.Scan0, srcBuf, 0, len);

            var dstBuf = new byte[md.Stride * n];
            for (int i = 0; i + 3 < len; i += 4)
            {
                // 25% 黑，alpha 跟着原图的不透明度走（0x00000040）
                dstBuf[i + 3] = (byte)(srcBuf[i + 3] * 0x40 / 255);
            }
            Marshal.Copy(dstBuf, 0, md.Scan0, dstBuf.Length);
        }
        finally
        {
            src.UnlockBits(data);
            mask.UnlockBits(md);
        }

        using (var blurred = BoxBlurAlpha(mask, blur))
        {
            mask.Dispose();

            var result = NewCanvas(n, n);
            using (var g = Prepare(result))
            {
                g.DrawImageUnscaled(blurred, right, down);
                g.DrawImageUnscaled(src, 0, 0);
            }
            return result;
        }
    }

    /// <summary>
    /// 对 alpha 通道做可分离盒式模糊，做三遍近似高斯。
    /// GDI+ 没带高斯模糊，与其拖一个图像库进来不如自己磨三遍盒子。
    /// </summary>
    private static Bitmap BoxBlurAlpha(Bitmap src, int radius)
    {
        int n = src.Width;
        var bmp = new Bitmap(src);
        var buf = new byte[n * n];

        for (int pass = 0; pass < 3; pass++)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var raw = new byte[d.Stride * n];
                Marshal.Copy(d.Scan0, raw, 0, raw.Length);

                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    buf[y * n + x] = raw[y * d.Stride + x * 4 + 3];

                // 横向
                var tmp = new byte[n * n];
                for (int y = 0; y < n; y++)
                {
                    int sum = 0;
                    for (int x = -radius; x <= radius; x++) sum += buf[y * n + Math.Clamp(x, 0, n - 1)];
                    for (int x = 0; x < n; x++)
                    {
                        tmp[y * n + x] = (byte)(sum / (2 * radius + 1));
                        sum -= buf[y * n + Math.Clamp(x - radius, 0, n - 1)];
                        sum += buf[y * n + Math.Clamp(x + radius + 1, 0, n - 1)];
                    }
                }

                // 纵向
                for (int x = 0; x < n; x++)
                {
                    int sum = 0;
                    for (int y = -radius; y <= radius; y++) sum += tmp[Math.Clamp(y, 0, n - 1) * n + x];
                    for (int y = 0; y < n; y++)
                    {
                        buf[y * n + x] = (byte)(sum / (2 * radius + 1));
                        sum -= tmp[Math.Clamp(y - radius, 0, n - 1) * n + x];
                        sum += tmp[Math.Clamp(y + radius + 1, 0, n - 1) * n + x];
                    }
                }

                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    raw[y * d.Stride + x * 4 + 3] = buf[y * n + x];

                Marshal.Copy(raw, 0, d.Scan0, raw.Length);
            }
            finally
            {
                bmp.UnlockBits(d);
            }
        }

        return bmp;
    }

    /// <summary>
    /// 自动定热点。
    /// 中心：直接取画布中心，对称图形（十字、I 形、双向箭头）用这个。
    /// 尖角：从上往下逐行扫描，取遇到的第一个不透明像素——箭头类图形的"尖"就在那儿。
    /// </summary>
    public static Point AutoHotSpot(Bitmap bmp, HotSpotPreset preset)
    {
        if (preset == HotSpotPreset.Center)
            return new Point(bmp.Width / 2, bmp.Height / 2);

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int len = stride * bmp.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);

            // 阈值取 128：只看"明显不透明"的像素，避免插值出来的淡淡边缘把尖角带偏
            for (int y = 0; y < bmp.Height; y++)
            {
                int rowStart = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (buf[rowStart + x * 4 + 3] > 128)
                        return new Point(x, y);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return new Point(0, 0);
    }

    /// <summary>换尺寸时按比例搬运手点的热点。</summary>
    public static Point RescaleHotSpot(int hx, int hy, int fromSize, int toSize)
    {
        if (fromSize <= 0 || toSize <= 0 || fromSize == toSize) return new Point(hx, hy);
        double k = (double)toSize / fromSize;
        return new Point(
            Math.Clamp((int)Math.Round(hx * k), 0, toSize - 1),
            Math.Clamp((int)Math.Round(hy * k), 0, toSize - 1));
    }

    /// <summary>
    /// 猜这张图的背景色，并判断它是不是"纯色底上的图形"。
    ///
    /// 判据是看四个角：如果四个角都是不透明的、而且颜色几乎一致，那基本就是
    /// logo/图标贴在纯色背景上，可以放心自动开抠图。只要有一个角是透明的或者
    /// 颜色对不上，就说明这多半是张照片，不碰它。
    /// </summary>
    public static (Color Key, bool Uniform) DetectBackground(Bitmap bmp)
    {
        const int inset = 2;
        const int block = 3;
        const int spreadLimit = 14;   // 四角之间允许的最大通道差

        if (bmp.Width < inset * 2 + block || bmp.Height < inset * 2 + block)
            return (Color.White, false);

        var corners = new[]
        {
            new Point(inset, inset),
            new Point(bmp.Width - inset - block, inset),
            new Point(inset, bmp.Height - inset - block),
            new Point(bmp.Width - inset - block, bmp.Height - inset - block),
        };

        var samples = new List<Color>(4);
        foreach (var p in corners)
        {
            long r = 0, g = 0, b = 0, a = 0;
            for (int y = 0; y < block; y++)
            for (int x = 0; x < block; x++)
            {
                var c = bmp.GetPixel(p.X + x, p.Y + y);
                r += c.R; g += c.G; b += c.B; a += c.A;
            }
            int n = block * block;
            samples.Add(Color.FromArgb((int)(a / n), (int)(r / n), (int)(g / n), (int)(b / n)));
        }

        // 已经带透明通道的图不用抠
        if (samples.Any(c => c.A < 250))
            return (samples[0], false);

        int maxR = samples.Max(c => c.R), minR = samples.Min(c => c.R);
        int maxG = samples.Max(c => c.G), minG = samples.Min(c => c.G);
        int maxB = samples.Max(c => c.B), minB = samples.Min(c => c.B);

        bool uniform = maxR - minR <= spreadLimit
                    && maxG - minG <= spreadLimit
                    && maxB - minB <= spreadLimit;

        var avg = Color.FromArgb(255,
            samples.Sum(c => c.R) / samples.Count,
            samples.Sum(c => c.G) / samples.Count,
            samples.Sum(c => c.B) / samples.Count);

        return (avg, uniform);
    }

    /// <summary>把位图铺在白底/黑底上做可视化检查用（自检时导出对照图）。</summary>
    public static Bitmap OnBackground(Bitmap src, Color bg, int zoom)
    {
        var dst = new Bitmap(src.Width * zoom, src.Height * zoom, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(bg);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height));
        return dst;
    }
}
