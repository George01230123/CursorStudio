using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using CursorStudio.Core;
using CursorStudio.UI;

namespace CursorStudio;

/// <summary>
/// 自带的自检。跑法：CursorStudio.exe --selftest
/// 想连注册表一起测（会真的动一下系统指针，跑完自动还原）：加 --touch-system
/// </summary>
internal static class SelfTest
{
    private enum Level { Pass, Fail, Warn, Info }

    private sealed record Line(Level Level, string Text);

    private static readonly List<Line> Lines = new();
    private static int _pass, _fail, _warn;

    private static void Pass(string t) { _pass++; Lines.Add(new(Level.Pass, t)); }
    private static void Fail(string t) { _fail++; Lines.Add(new(Level.Fail, t)); }
    private static void Warn(string t) { _warn++; Lines.Add(new(Level.Warn, t)); }
    private static void Info(string t) => Lines.Add(new(Level.Info, t));

    private static void Check(bool ok, string what, string detail = "")
    {
        if (ok) Pass(what);
        else Fail(what + (detail.Length > 0 ? $"  ← {detail}" : ""));
    }

    private static void Section(string title)
    {
        Lines.Add(new(Level.Info, ""));
        Lines.Add(new(Level.Info, $"=== {title} ==="));
    }

    public static int Run(string outDir)
    {
        Program.AttachToParentConsole();
        // 界面布局那段要真建窗口，必须先开视觉样式，否则量出来的尺寸和实际跑的时候不一样
        ApplicationConfiguration.Initialize();

        // 全程写临时目录，绝不碰用户真正的 %LOCALAPPDATA%\CursorStudio
        string sandbox = Path.Combine(Path.GetTempPath(), "cursorstudio_selftest_" + Guid.NewGuid().ToString("N")[..8]);
        AppPaths.OverrideRoot(sandbox);
        AppPaths.EnsureAll();

        // 截图写在当前目录下。exe 要是被放在 Program Files 之类不可写的地方，
        // 这里建目录会直接抛异常——那会连一份结果都打印不出来，
        // 所以退到临时目录继续跑，别让"写不出截图"变成"自检崩了"
        string shots = Path.Combine(outDir, "selftest-shots");
        try
        {
            Directory.CreateDirectory(shots);
        }
        catch
        {
            shots = Path.Combine(sandbox, "selftest-shots");
            try { Directory.CreateDirectory(shots); } catch { /* 真建不出来就只是没有截图 */ }
            Lines.Add(new(Level.Info, $"当前目录写不了截图，改用：{shots}"));
        }

        Lines.Add(new(Level.Info, $"鼠标指针美化 · 自检     {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        Lines.Add(new(Level.Info, $"临时目录：{sandbox}"));
        Lines.Add(new(Level.Info, $"截图目录：{shots}"));

        try
        {
            SectionRenderer();
            SectionPipeline();
            SectionAnimation();
            SectionCurFile();
            SectionSlots();
            SectionStockCursors();
            SectionPack();

            bool touchSystem = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--touch-system", StringComparison.OrdinalIgnoreCase));
            if (touchSystem) SectionRegistry();
            else Info("（跳过注册表实测，加 --touch-system 可以真跑一遍）");

            SectionLayout(shots);
        }
        catch (Exception ex)
        {
            Fail("自检本身崩了：" + ex);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }

        string report = Render(shots);
        Console.WriteLine(report);

        string reportPath = Path.Combine(outDir, "selftest-report.txt");
        try { File.WriteAllText(reportPath, report, new UTF8Encoding(false)); }
        catch { /* 写不出来也不影响控制台输出 */ }

        return _fail == 0 ? 0 : 1;
    }

    private static string Render(string shots)
    {
        var sb = new StringBuilder();
        foreach (var l in Lines)
        {
            string tag = l.Level switch
            {
                Level.Pass => "  [OK] ",
                Level.Fail => "  [!!] ",
                Level.Warn => "  [??] ",
                _ => "",
            };
            sb.AppendLine(tag + l.Text);
        }
        sb.AppendLine();
        sb.AppendLine($"通过 {_pass} · 失败 {_fail} · 警告 {_warn}");
        if (Directory.Exists(shots) && Directory.EnumerateFiles(shots, "*.png").Any())
            sb.AppendLine($"布局截图：{shots}");
        return sb.ToString();
    }

    // ================================================================ A. 图像处理

    private static void SectionRenderer()
    {
        Section("A. 图像处理");

        // --- 等比缩放：非方图不能被压扁 ---
        using (var wide = TestImages.Solid(200, 100, Color.Red))
        {
            using var fitted = Renderer.Scale(wide, 32, keepAspect: true);
            Check(fitted.Width == 32 && fitted.Height == 32, "等比缩放输出 32×32 画布");

            var rect = Renderer.FitRect(200, 100, 32, keepAspect: true);
            Check(rect.Width == 32 && rect.Height == 16,
                "2:1 的图在 32 画布上占 32×16", $"实际 {rect.Width}×{rect.Height}");
            Check(rect.X == 0 && rect.Y == 8, "并且垂直居中", $"实际 ({rect.X},{rect.Y})");

            using var stretched = Renderer.Scale(wide, 32, keepAspect: false);
            var srect = Renderer.FitRect(200, 100, 32, keepAspect: false);
            Check(srect.Width == 32 && srect.Height == 32, "拉伸模式填满 32×32");
            Check(stretched.GetPixel(16, 16).A > 200, "拉伸后中心是不透明的");
        }

        // --- 抠背景 ---
        using (var logo = TestImages.WhiteBgLogo(64))
        {
            using var src = new Bitmap(logo);
            Check(src.GetPixel(2, 2).A == 255, "原始图左上角是不透明白色");

            using var keyed = Renderer.KeyOutBackground(src, Color.White, 30);
            Check(keyed.GetPixel(2, 2).A == 0, "抠图后左上角变全透明", $"alpha={keyed.GetPixel(2, 2).A}");
            Check(keyed.GetPixel(32, 32).A > 200, "中间的红色圆形保留下来", $"alpha={keyed.GetPixel(32, 32).A}");

            var c = keyed.GetPixel(32, 32);
            Check(c.R > 180 && c.G < 80, "保留下来的颜色仍然是红色", $"RGB({c.R},{c.G},{c.B})");
        }

        // --- 容差 0 = 不抠 ---
        using (var logo = TestImages.WhiteBgLogo(64))
        using (var keyed = Renderer.KeyOutBackground(logo, Color.White, 0))
            Check(keyed.GetPixel(2, 2).A == 255, "容差为 0 时不做任何抠除");

        // --- 背景探测 ---
        using (var logo = TestImages.WhiteBgLogo(64))
        {
            var (key, uniform) = Renderer.DetectBackground(logo);
            Check(uniform, "白底 logo 被识别为纯色背景");
            Check(key.R > 240 && key.G > 240 && key.B > 240, "探测到的背景色是白色", $"RGB({key.R},{key.G},{key.B})");
        }

        using (var trans = TestImages.TransparentCircle(64))
        {
            var (_, uniform) = Renderer.DetectBackground(trans);
            Check(!uniform, "四角透明的图不会被误判成纯色底");
        }

        using (var grad = TestImages.Gradient(64))
        {
            var (_, uniform) = Renderer.DetectBackground(grad);
            Check(!uniform, "四角颜色不同的渐变图不会被误判成纯色底");
        }

        // --- 抗锯齿边缘不能残留背景色的晕边 ---
        using (var shape = TestImages.SoftEdgeShape(64))
        {
            using var keyed = Renderer.KeyOutBackground(shape, Color.White, 25);

            int n = 0;
            long sumR = 0, sumG = 0, sumB = 0;
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                var c = keyed.GetPixel(x, y);
                if (c.A is > 60 and < 200) { n++; sumR += c.R; sumG += c.G; sumB += c.B; }
            }

            Check(n > 20, "确实存在抗锯齿造成的半透明边缘像素", $"只找到 {n} 个");

            if (n > 0)
            {
                double avgR = (double)sumR / n, avgG = (double)sumG / n, avgB = (double)sumB / n;
                // 前景是深蓝 (24,52,140)。没做去背景色处理的话，这些边缘像素
                // 会是"深蓝混白"，R 会明显偏向 150 以上
                Check(avgR < 110,
                    "边缘像素的颜色被还原成了前景色（没有残留白色晕边）",
                    $"边缘平均色 RGB({avgR:F0},{avgG:F0},{avgB:F0})，纯白边的话 R 应该在 150 以上");
            }

            // 纯白区域必须干干净净地全透明
            Check(keyed.GetPixel(1, 1).A == 0 && keyed.GetPixel(62, 62).A == 0,
                "远离图形的纯背景区完全透明");
        }

        // --- 相对大小：导入的图不该比系统指针大一圈 ---
        using (var square = TestImages.Solid(64, 64, Color.FromArgb(255, 40, 60, 130)))
        {
            using (var full = Renderer.Scale(square, 32, keepAspect: true, scale: 1.0))
            {
                var fi = CurFile.InkBounds(full);
                Check(Math.Max(fi.Width, fi.Height) == 32, "100% 时铺满整个画布", $"实际 {fi.Width}×{fi.Height}");
            }

            using var scaled = Renderer.Scale(square, 32, keepAspect: true, scale: RenderSettings.DefaultScale);
            var ink = CurFile.InkBounds(scaled);
            int maxDim = Math.Max(ink.Width, ink.Height);

            // 32 × 0.70 = 22.4 → 22。留两个像素容差给双三次插值的边缘
            Check(maxDim is >= 21 and <= 24,
                $"默认 70% 时长边约 22px（画布的 70%）",
                $"实际 {maxDim}px，画布占比 {maxDim * 100.0 / 32:F0}%");
            // 光比 |ink.X - ink.Y| 是不够的：方形图摆在画布角上时两个都是 0，
            // 也照样"相等"。直接和居中该留的边对照
            Check(Math.Abs(ink.X - (32 - ink.Width) / 2) <= 1 &&
                  Math.Abs(ink.Y - (32 - ink.Height) / 2) <= 1,
                "图形在画布里居中",
                $"内容框 {ink.Width}×{ink.Height} 左上角 ({ink.X},{ink.Y})，居中该在 ({(32 - ink.Width) / 2},{(32 - ink.Height) / 2})");

            // 这是这次改动要解决的问题本身
            Check(maxDim < 32, "默认不会是铺满的（铺满就会比系统指针明显大一圈）");
        }

        // --- 改尺寸 / 改相对大小时，热点要跟着搬 ---
        var whole = Renderer.FitRect(64, 64, 32, keepAspect: true, scale: 1.0);    // (0,0,32,32)
        var half = Renderer.FitRect(64, 64, 32, keepAspect: true, scale: 0.5);     // (8,8,16,16)

        Check(Renderer.MapHotSpot(new Point(0, 0), whole, half) == new Point(8, 8),
            "铺满时定在左上角的热点，缩到 50% 后跟着挪到 (8,8)",
            $"实际 {Renderer.MapHotSpot(new Point(0, 0), whole, half)}");
        Check(Renderer.MapHotSpot(new Point(16, 16), whole, half) == new Point(16, 16),
            "定在正中点的热点，缩放后仍然落在正中",
            $"实际 {Renderer.MapHotSpot(new Point(16, 16), whole, half)}");
        var corner = Renderer.MapHotSpot(new Point(31, 31), whole, half);
        Check(corner.X >= half.Right - 2 && corner.Y >= half.Bottom - 2,
            "定在右下角的热点，跟着挪到新布局的右下角",
            $"实际 {corner}，新矩形是 {half}");

        // 换算必须是等比的：32→64 的同布局放大，坐标正好翻倍
        var big = Renderer.FitRect(64, 64, 64, keepAspect: true, scale: RenderSettings.DefaultScale);
        var small = Renderer.FitRect(64, 64, 32, keepAspect: true, scale: RenderSettings.DefaultScale);
        var up = Renderer.MapHotSpot(new Point(8, 8), small, big);
        Check(Math.Abs(up.X - 16) <= 1 && Math.Abs(up.Y - 16) <= 1,
            "32→64 等比例放大时热点坐标近似翻倍", $"实际 {up}");

        // --- 投影 ---
        using (var body = TestImages.Solid(32, 32, Color.FromArgb(255, 245, 245, 245)))
        using (var noShadow = Renderer.Scale(body, 32, true, 0.7))
        {
            using var shadowed = Renderer.AddDropShadow(noShadow);

            var bodyInk = CurFile.InkBounds(noShadow);
            int shadowOnly = 0;
            int darkest = 255;

            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                bool isBody = bodyInk.Contains(x, y) && noShadow.GetPixel(x, y).A > 24;
                if (isBody) continue;
                var c = shadowed.GetPixel(x, y);
                if (c.A > 24)
                {
                    shadowOnly++;
                    if (c.R < darkest) darkest = c.R;
                }
            }

            Check(shadowOnly > 10, "投影在图形之外产生了额外的可见像素", $"只有 {shadowOnly} 个");
            Check(darkest < 120, "那些多出来的像素是暗色的（确实是投影不是脏点）", $"最暗的 R={darkest}");
            Check(shadowed.GetPixel(16, 16).A > 200, "图形本体没有被投影影响",
                $"中心 alpha={shadowed.GetPixel(16, 16).A}");

            // 投影应该偏向右下（和 anicursorgen 的参数一致：右移 9.375%、下移 3.125%）。
            // 别只看最右边那一列和最左边那一列：图形缩到 70% 之后离画布边还有一段距离，
            // 投影根本扩不到 x=31 和 x=0，两边都是 0——"0 >= 0" 是一句永远成立的空话。
            // 改成统计"落在图形右侧/下方"和"落在左侧/上方"的像素各有多少。
            int outward = 0, wrongWay = 0;
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                if (bodyInk.Contains(x, y)) continue;
                if (shadowed.GetPixel(x, y).A <= 24) continue;
                if (x >= bodyInk.Right || y >= bodyInk.Bottom) outward++;
                else wrongWay++;
            }
            Check(outward > 10 && wrongWay == 0,
                "投影只出现在图形的右侧和下方（确实偏向右下）",
                $"右/下 {outward} 个，左/上 {wrongWay} 个");
        }

        // --- 热点自动判定 ---
        using (var bmp = TestImages.ArrowTipAt(10, 4, 32))
        {
            var tip = Renderer.AutoHotSpot(bmp, HotSpotPreset.TipTopLeft);
            Check(tip == new Point(10, 4), "尖角模式找到的正是那个尖点", $"实际 {tip}");

            var center = Renderer.AutoHotSpot(bmp, HotSpotPreset.Center);
            Check(center == new Point(16, 16), "正中模式落在 (16,16)", $"实际 {center}");
        }

        using (var blank = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        {
            var p = Renderer.AutoHotSpot(blank, HotSpotPreset.TipTopLeft);
            Check(p == new Point(0, 0), "全透明的图不会卡死，兜底返回 (0,0)");
        }

        // --- 换尺寸时热点按比例走 ---
        var rescaled = Renderer.RescaleHotSpot(8, 4, 32, 64);
        Check(rescaled == new Point(16, 8), "32→64 热点翻倍", $"实际 {rescaled}");
        var clamped = Renderer.RescaleHotSpot(31, 31, 32, 32);
        Check(clamped == new Point(31, 31), "同尺寸不变");
    }

    // ================================================================ A2. 渲染管线

    /// <summary>
    /// 上面 A 段测的是 Renderer 里的零件，零件对**不代表接线对**。
    /// 界面上那两个开关（「相对大小」「投影」）最终是从 RenderCache.Render 走
    /// Store.BuildCursorFiles 把像素写进 .cur 的；曾经 RenderCache 自己另拼了一遍流程、
    /// 漏掉 Scale 和 Shadow，两个开关在真实路径里完全没效果，而自检照样全绿。
    /// 这一段专门盯"接线"，测的是界面真正走的那条路。
    /// </summary>
    private static void SectionPipeline()
    {
        Section("A2. 渲染管线（界面设置 → .cur 的真实路径）");

        try
        {
            // 用铺满整张画布的方形：这样"内容框该多大"可以直接拿 32×scale 对照，
            // 不用再折算源图自己留了多少边
            using var demo = TestImages.Solid(96, 96, Color.FromArgb(255, 20, 120, 220));
            string src = Path.Combine(AppPaths.Root, "pipeline-src.png");
            demo.Save(src, ImageFormat.Png);

            using var cache = new RenderCache();

            // --- 「相对大小」要真的作用在渲染结果上 ---
            foreach (double scale in new[] { 1.00, 0.70, 0.30 })
            {
                var rs = new RenderSettings { Size = 32, Scale = scale };
                var bmp = cache.Render(src, rs, out string? err);
                if (bmp is null) { Fail($"相对大小 {scale * 100:F0}%：渲染失败 {err}"); continue; }

                var ink = CurFile.InkBounds(bmp);
                int want = (int)Math.Round(32 * scale);
                // 抗锯齿的边缘像素 + 24 的 alpha 阈值会让内容框差一两个像素，给 ±2 的余量
                Check(Math.Abs(Math.Max(ink.Width, ink.Height) - want) <= 2,
                    $"界面路径认得「相对大小」：{scale * 100:F0}% → 内容框约 {want}px",
                    $"实际 {ink.Width}×{ink.Height}，画布 {bmp.Width}×{bmp.Height}");
            }

            // --- 「投影」也要真的作用在渲染结果上 ---
            int plainInk, shadowInk, plainDark, shadowDark;
            {
                var plain = cache.Render(src, new RenderSettings { Size = 32, Scale = 0.70 }, out _);
                var withShadow = cache.Render(src, new RenderSettings { Size = 32, Scale = 0.70, Shadow = true }, out _);
                if (plain is null || withShadow is null) { Fail("投影对照渲染失败"); return; }

                plainInk = MaxSide(CurFile.InkBounds(plain));
                shadowInk = MaxSide(CurFile.InkBounds(withShadow));
                plainDark = ShadowPixels(plain);
                shadowDark = ShadowPixels(withShadow);
            }

            Check(plainDark == 0 && shadowDark > 0,
                "界面路径认得「投影」：勾上之后多出半透明黑的像素",
                $"关 {plainDark} 个 / 开 {shadowDark} 个");
            Check(shadowInk > plainInk,
                "投影往右下方扩出去了，内容框跟着变大",
                $"关 {plainInk}px / 开 {shadowInk}px");

            // --- 整条路：Store.BuildCursorFiles（点「应用到系统」走的就是它）---
            var ws = new Workspace { SchemeName = "管线测试" };
            var arrow = ws.For("Arrow");
            arrow.SourceImage = src;
            arrow.Size = 32;
            arrow.Scale = 0.30;      // 故意调到最小
            arrow.Shadow = true;     // 故意打开投影

            using var cache2 = new RenderCache();
            var built = Store.BuildCursorFiles(ws, cache2);
            string? cur = built.Files.GetValueOrDefault("Arrow");
            Check(cur is not null, "「应用到系统」这条路生成出了 Arrow 的 .cur",
                string.Join("；", built.Warnings));

            if (cur is not null)
            {
                var info = CurFile.Inspect(cur);
                Check(info.Entries[0].Width == 32, "第一档是 32", $"实际 {info.Entries[0].Width}");

                using var decoded = CurFile.Decode(cur, 0);
                var ink = CurFile.InkBounds(decoded);
                int want = (int)Math.Round(32 * 0.30);
                // 上限放到 want+4：投影会往右下扩一点，但不该是整个 32 画布
                Check(MaxSide(ink) <= want + 4,
                    $"写进 .cur 的像素也按「相对大小」缩了（约 {want}px）",
                    $"实际内容框 {ink.Width}×{ink.Height}");
                Check(ShadowPixels(decoded) > 0, "写进 .cur 的像素也带上了投影");
            }
        }
        catch (Exception ex)
        {
            Fail("渲染管线测试出错：" + ex);
        }
    }

    private static int MaxSide(Rectangle r) => r.IsEmpty ? 0 : Math.Max(r.Width, r.Height);

    /// <summary>数一下"半透明黑"的像素——投影就长这样，图形本体是有颜色的。</summary>
    private static int ShadowPixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            int n = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * Math.Abs(data.Stride)), row, 0, row.Length);
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (row[x * 4 + 3] > 20 && row[x * 4] < 40 && row[x * 4 + 1] < 40 && row[x * 4 + 2] < 40)
                        n++;
                }
            }
            return n;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ================================================================ A3. 动画指针

    /// <summary>
    /// 动画这一整条链：GIF 导入 → 多帧状态 → 生成 .ani → 系统认不认 → 方案包里的 install.inf。
    /// </summary>
    private static void SectionAnimation()
    {
        Section("A3. 动画指针（.ani / GIF / install.inf）");

        try
        {
            // ---- GIF 多帧导入 ----
            string gif = Path.Combine(AppPaths.Root, "spin.gif");
            File.WriteAllBytes(gif, TestImages.TinyAnimatedGif());

            try
            {
                using var anim = ImageLoader.LoadFrames(gif);

                Check(anim.Frames.Count == 3, "GIF 的 3 帧都读出来了", $"实际 {anim.Frames.Count}");
                Check(anim.DelayMs == 120, "GIF 的帧延时读对了（12/100 秒 → 120ms）", $"实际 {anim.DelayMs}ms");

                if (anim.Frames.Count == 3)
                {
                    Check(anim.Frames[0].GetPixel(0, 0).R > 200 && anim.Frames[0].GetPixel(0, 0).G < 60,
                        "第 1 帧是红的（帧序没乱）", anim.Frames[0].GetPixel(0, 0).ToString());
                    Check(anim.Frames[2].GetPixel(0, 0).B > 200 && anim.Frames[2].GetPixel(0, 0).R < 60,
                        "第 3 帧是蓝的（帧序没乱）", anim.Frames[2].GetPixel(0, 0).ToString());
                }
            }
            catch (Exception ex)
            {
                Fail("GIF 导入出错：" + ex.Message);
            }

            // ---- .ani 读写往返 ----
            var frames = new List<byte[]>();
            const int N = 4, SZ = 32;
            for (int i = 0; i < N; i++)
            {
                using var bmp = TestImages.Solid(SZ, SZ, Color.FromArgb(255, (byte)(40 + i * 50), 60, 200));
                frames.Add(CurFile.BuildBytes(new[] { new CurImage(bmp, 5, 7) }));
            }

            string aniPath = Path.Combine(AppPaths.Root, "roundtrip.ani");
            AniFile.Write(aniPath, frames, SZ, 50);
            Check(File.Exists(aniPath), "写出了 .ani 文件");

            var info = AniFile.Inspect(aniPath);
            Check(info is not null, "能读回 .ani 的头部");
            if (info is not null)
            {
                Check(info.FrameCount == N, $"anih 里 nFrames={N}", $"实际 {info.FrameCount}");
                Check(info.Steps == N, $"anih 里 nSteps={N}", $"实际 {info.Steps}");
                Check(info.IconCount == N, $"LIST fram 里有 {N} 个 icon 块", $"实际 {info.IconCount}");
                Check(info.FrameDelayMs == 50, "50ms 换算成 iDispRate=3 再换回来还是 50ms",
                    $"实际 {info.FrameDelayMs}ms");
                Check((info.Attributes & AniFile.AF_ICON) != 0, "bfAttributes 带上了 AF_ICON（不带系统不认）",
                    $"实际 0x{info.Attributes:X}");
            }

            var back = AniFile.ReadFrames(aniPath, SZ);
            Check(back is not null && back.Count == N, $"解回来还是 {N} 帧", $"实际 {back?.Count ?? -1}");

            if (back is not null && back.Count == N)
            {
                Check(back.All(f => f.HotX == 5 && f.HotY == 7), "每帧的热点都原样带回来了");

                bool samePixels = true;
                for (int i = 0; i < N && samePixels; i++)
                {
                    var want = Color.FromArgb(255, (byte)(40 + i * 50), 60, 200);
                    if (back[i].Image.GetPixel(10, 10) != want) samePixels = false;
                }
                Check(samePixels, "每帧的像素和写进去的一致（帧序没串）");

                foreach (var f in back) f.Dispose();
            }

            // ---- 系统自带的 .ani 也要读得动 ----
            string stock = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", "aero_busy.ani");
            if (File.Exists(stock))
            {
                var si = AniFile.Inspect(stock);
                Check(si is not null, "能读系统自带 aero_busy.ani 的头部");
                if (si is not null)
                {
                    Check(si.IconCount == si.FrameCount && si.FrameCount > 1,
                        $"系统文件的帧数和 icon 块数对得上（{si.FrameCount} 帧）",
                        $"nFrames={si.FrameCount} icon={si.IconCount}");
                    Check(Math.Abs(si.FrameDelayMs - 50) <= 2,
                        "系统文件的 iDispRate=3 换回来是 50ms", $"实际 {si.FrameDelayMs}ms");
                }

                var sf = AniFile.ReadFrames(stock, 32, 3);   // 只解 3 帧，不用为了自检把 18 帧全解开
                Check(sf is not null && sf.Count == 3, "能解出系统文件的前 3 帧", $"实际 {sf?.Count ?? -1}");
                if (sf is not null)
                {
                    Check(sf[0].Image.Width == 32, "系统文件能按 32 尺寸解出来", $"实际 {sf[0].Image.Width}");
                    foreach (var f in sf) f.Dispose();
                }
            }
            else
            {
                Warn("系统里没有 aero_busy.ani，跳过实测");
            }

            // ---- Windows 认不认我们写的 .ani ----
            Check(Win32.CanWindowsLoad(aniPath, 32), "Windows 的 LoadImage 能加载我们写的 .ani");

            // ---- 走完整条生成路径 ----
            var ws = new Workspace { SchemeName = "动画自检" };
            var wait = ws.For("Wait");
            var storedFrames = new List<string>();
            using (var anim = ImageLoader.LoadFrames(gif))
            {
                foreach (var f in anim.Frames)
                {
                    try { storedFrames.Add(Store.ImportBitmap(f)); }
                    finally { f.Dispose(); }
                }
                wait.SetFrames(storedFrames);
                wait.FrameDelayMs = anim.DelayMs;
            }
            wait.Size = 32;

            var arrow = ws.For("Arrow");
            arrow.SourceImage = TestImages.SaveTemp(TestImages.DemoArrow(96), "ani-arrow.png");

            Check(wait.IsAnimated && wait.FrameCount == 3, "动画位是 3 帧",
                $"IsAnimated={wait.IsAnimated} FrameCount={wait.FrameCount}");

            using var cache = new RenderCache();
            var built = Store.BuildCursorFiles(ws, cache);
            Check(built.Warnings.Count == 0, "生成过程没有警告", string.Join("；", built.Warnings));

            string waitFile = built.Files.GetValueOrDefault("Wait") ?? "";
            string arrowFile = built.Files.GetValueOrDefault("Arrow") ?? "";

            Check(waitFile.EndsWith(".ani", StringComparison.OrdinalIgnoreCase),
                "动画位生成的是 .ani", Path.GetFileName(waitFile));
            Check(arrowFile.EndsWith(".cur", StringComparison.OrdinalIgnoreCase),
                "静态位还是生成 .cur", Path.GetFileName(arrowFile));

            if (waitFile.Length > 0)
            {
                var wi = AniFile.Inspect(waitFile);
                Check(wi is not null && wi.FrameCount == 3, "生成的 .ani 里是 3 帧", $"实际 {wi?.FrameCount ?? -1}");

                var wf = AniFile.ReadFrames(waitFile, 32);
                if (wf is not null && wf.Count == 3)
                {
                    // 动画最要紧的一条：各帧热点不一样的话，系统播放时指针会在屏幕上抖
                    Check(wf.All(f => f.HotX == wf[0].HotX && f.HotY == wf[0].HotY),
                        "生成的动画所有帧共用一个热点（播放时不会抖）",
                        string.Join(" / ", wf.Select(f => $"({f.HotX},{f.HotY})")));

                    var firstBlob = AniFile.ReadIconBlob(waitFile, 0);
                    if (firstBlob is not null)
                    {
                        var ci = CurFile.Inspect(firstBlob);
                        Check(ci.Count == 4, "动画每一帧里都打包了 4 个尺寸（32/48/64/96）",
                            "实际 " + string.Join("/", ci.Entries.Select(e => e.Width)));
                    }
                    foreach (var f in wf) f.Dispose();
                }

                Check(Win32.CanWindowsLoad(waitFile, 32), "Windows 认生成的 .ani");
            }

            // ---- 大动画：帧数一多就会把渲染缓存塞满，专门踩一下那个上限 ----
            // 缓存淘汰原来拿 Dictionary.Keys.First() 当"最旧的"，而 Dictionary 的枚举顺序
            // 不是插入顺序、删过元素后更乱，于是会随机淘汰掉调用方正在用的位图，
            // 报出来是 "写文件失败 — Parameter is not valid"。帧数少的时候缓存到不了上限，
            // 所以只有这种规模的用例才拦得住。
            var big = new Workspace { SchemeName = "大动画" };
            // 用「后台运行」而不是「忙碌」：同一个指针位重新生成时会把旧文件清掉，
            // 用同一个位会把上面 3 帧测试的产物删掉，下面方案包那段就找不到文件了
            var bigSlot = big.For("AppStarting");
            var bigFrames = new List<string>();
            for (int i = 0; i < AniFile.MaxFrames; i++)
            {
                using var bmp = TestImages.Spinner(64, i, AniFile.MaxFrames);
                bigFrames.Add(Store.ImportBitmap(bmp));
            }
            bigSlot.SetFrames(bigFrames);
            bigSlot.Size = 32;
            bigSlot.FrameDelayMs = 50;

            using (var bigCache = new RenderCache())
            {
                var bigBuilt = Store.BuildCursorFiles(big, bigCache);
                Check(bigBuilt.Warnings.Count == 0,
                    $"{AniFile.MaxFrames} 帧动画生成时一条警告都没有（把渲染缓存塞满也不出错）",
                    string.Join("；", bigBuilt.Warnings));

                string bigAni = bigBuilt.Files.GetValueOrDefault("AppStarting") ?? "";
                var bigInfo = bigAni.Length > 0 ? AniFile.Inspect(bigAni) : null;
                Check(bigInfo?.FrameCount == AniFile.MaxFrames,
                    $"生成的 .ani 里 {AniFile.MaxFrames} 帧一个不少", $"实际 {bigInfo?.FrameCount ?? -1}");

                if (bigAni.Length > 0)
                {
                    var bigRead = AniFile.ReadFrames(bigAni, 32, 4);
                    if (bigRead is not null)
                    {
                        Check(bigRead.All(f => f.HotX == bigRead[0].HotX && f.HotY == bigRead[0].HotY),
                            "60 帧动画的热点也是共用的");
                        foreach (var f in bigRead) f.Dispose();
                    }
                    Check(Win32.CanWindowsLoad(bigAni, 32), "系统认这个 60 帧的 .ani");
                }
            }

            // ---- 方案包里的 install.inf ----
            string zip = Path.Combine(AppPaths.Root, "ani-pack.zip");
            string copy = Path.Combine(AppPaths.Root, "ani-pack-copy.zip");
            Store.ExportPack(ws, zip, built);

            using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                var e = z.GetEntry("install.inf");
                Check(e is not null, "方案包里有 install.inf");

                if (e is not null)
                {
                    string text;
                    using (var s = e.Open())
                    using (var r = new StreamReader(s, Encoding.Unicode, detectEncodingFromByteOrderMarks: true))
                        text = r.ReadToEnd();

                    Check(text.Contains("signature=\"$CHICAGO$\""), "install.inf 有 [Version] signature");
                    Check(text.Contains("[DefaultInstall]") && text.Contains("AddReg"),
                        "install.inf 有 DefaultInstall / AddReg");
                    Check(text.Contains("Control Panel\\Cursors\\Schemes"), "install.inf 往 Schemes 里写方案");
                    Check(text.Contains("Wait.ani") && text.Contains("Arrow.cur"),
                        "install.inf 引用的文件名和包里的对得上");

                    // 光进方案列表还不够，还要顺手把这套指针设为当前指针
                    Check(text.Contains("[Scheme.Apply]") &&
                          text.Contains("HKCU,\"Control Panel\\Cursors\",Arrow,0x00020000,"),
                        "install.inf 把指针位也真的设成了当前使用的");
                    Check(text.Contains("HKCU,\"Control Panel\\Cursors\",Wait,0x00020000,"),
                        "动画位同样被设为当前指针");
                    // 没配图的指针位不该去动
                    Check(!text.Contains("HKCU,\"Control Panel\\Cursors\",Hand,0x00020000,"),
                        "没配图的指针位没有被写进注册表");

                    // Schemes 是一串逗号分隔、顺序固定的路径，段数错了指针位就整体张冠李戴
                    var m = System.Text.RegularExpressions.Regex.Match(text, "Schemes\",\"[^\"]+\",,\"([^\"]*)\"");
                    if (!m.Success) Fail("install.inf 里的 Schemes 值没找到");
                    else
                    {
                        var parts = m.Groups[1].Value.Split(',');
                        Check(parts.Length == CursorSlots.All.Count,
                            $"Schemes 里有 {CursorSlots.All.Count} 段（一个指针位一段）", $"实际 {parts.Length} 段");
                        // 路径是 %10%\%CUR_DIR%\%<注册表值名>%，真正的文件名在 [Strings] 里
                        Check(parts.Length > 3 && parts[0] == @"%10%\%CUR_DIR%\%Arrow%",
                            "第 1 段是「正常选择」", parts.Length > 0 ? parts[0] : "");
                        Check(parts.Length > 3 && parts[3] == @"%10%\%CUR_DIR%\%Wait%",
                            "第 4 段是「忙碌」（顺序没错位）", parts.Length > 3 ? parts[3] : "");
                        Check(parts.Length > 1 && parts[1].Length == 0, "没配的指针位留空",
                            parts.Length > 1 ? $"[{parts[1]}]" : "");
                    }

                    // UTF-16LE BOM：方案名可能是中文，ANSI 的 inf 换个语言版本就乱码
                    bool bom;
                    using (var s = e.Open())
                    {
                        var head = new byte[2];
                        bom = s.Read(head, 0, 2) == 2 && head[0] == 0xFF && head[1] == 0xFE;
                    }
                    Check(bom, "install.inf 是 UTF-16LE 带 BOM（中文方案名不会乱码）");
                }

                Check(z.Entries.Any(x => x.FullName == "cursors/Wait.ani"), "包里带了 .ani 成品");
                int frameImages = z.Entries.Count(x => x.FullName.StartsWith("images/Wait_"));
                Check(frameImages == 3, "动画的 3 帧源图都进了包", $"实际 {frameImages}");

                var un = z.GetEntry("uninstall.bat");
                Check(un is not null, "方案包里有 uninstall.bat");
                if (un is not null)
                {
                    using var s = un.Open();
                    using var r = new StreamReader(s, Encoding.UTF8);
                    string bat = r.ReadToEnd();
                    Check(bat.Contains("reg delete") && bat.Contains("Cursors\\Schemes"),
                        "uninstall.bat 删的是方案列表那一条");
                }

                File.Copy(zip, copy, overwrite: true);
            }

            // ---- 导出再导入，动画不能退化成单帧 ----
            var imported = Store.ImportPack(copy);
            var importedWait = imported.Peek("Wait");
            Check(imported.MissingImages.Count == 0, "导入方案包时源图一张没丢",
                string.Join("、", imported.MissingImages));
            Check(importedWait?.IsAnimated == true && importedWait.FrameCount == 3,
                "导入回来还是 3 帧的动画", $"FrameCount={importedWait?.FrameCount ?? 0}");
        }
        catch (Exception ex)
        {
            Fail("动画测试出错：" + ex);
        }
    }

    // ================================================================ B. .cur 格式

    private static void SectionCurFile()
    {
        Section("B. .cur 文件格式");

        string dir = Path.Combine(AppPaths.Root, "selftest");
        Directory.CreateDirectory(dir);

        using var source = TestImages.ArrowTipAt(6, 3, 64);
        string curPath = Path.Combine(dir, "test.cur");

        // 这些位图要活到写文件、甚至解码比对之后，不能放在循环里用 using——
        // using var 在每次迭代末尾就会把它们 Dispose 掉
        var owned = new List<Bitmap>();
        var frames = new List<CurImage>();
        try
        {
            foreach (int size in Store.CursorSizes)
            {
                var scaled = Renderer.Scale(source, size, true);
                owned.Add(scaled);
                var hot = Renderer.RescaleHotSpot(6, 3, 64, size);
                frames.Add(new CurImage(scaled, hot.X, hot.Y));
            }

            CurFile.Write(curPath, frames);

            Check(File.Exists(curPath), "写出了一个 .cur 文件");
            Info($"文件大小：{new FileInfo(curPath).Length} 字节");

            // --- 逐字段解析回来核对 ---
            CurInfo info;
            try
            {
                info = CurFile.Inspect(curPath);
                Pass("文件结构能被自己的解析器完整读回");
            }
            catch (Exception ex)
            {
                Fail("解析自己写出来的 .cur 失败：" + ex.Message);
                return;
            }

            Check(info.Type == 2, "idType = 2（光标，不是图标）", $"实际 {info.Type}");
            Check(info.Count == Store.CursorSizes.Length,
                $"包含 {Store.CursorSizes.Length} 个尺寸", $"实际 {info.Count}");
            Check(info.Entries.Select(e => e.Width).SequenceEqual(Store.CursorSizes),
                "尺寸档依次是 " + string.Join(" / ", Store.CursorSizes),
                string.Join(",", info.Entries.Select(e => e.Width)));

            // 源图 64，热点 (6,3)，各尺寸按比例换算
            var expectedHot = new[] { (32, 3, 2), (48, 4, 2), (64, 6, 3), (96, 9, 4) };
            for (int i = 0; i < Math.Min(expectedHot.Length, info.Entries.Count); i++)
            {
                var (sz, hx, hy) = expectedHot[i];
                var e = info.Entries[i];
                Check(e.HotX == hx && e.HotY == hy,
                    $"{sz} 尺寸的热点按比例换算成 ({hx},{hy})",
                    $"实际 ({e.HotX},{e.HotY})");
            }

            // --- 像素往返 ---
            try
            {
                using var decoded = CurFile.Decode(curPath, 0);
                Check(decoded.Width == 32 && decoded.Height == 32, "解码出来的 32 尺寸尺寸正确");

                bool match = true;
                string mismatch = "";
                for (int y = 0; y < 32 && match; y++)
                for (int x = 0; x < 32; x++)
                {
                    var a = frames[0].Bitmap.GetPixel(x, y);
                    var b = decoded.GetPixel(x, y);
                    if (a.ToArgb() != b.ToArgb())
                    {
                        match = false;
                        mismatch = $"({x},{y}) 原 {a.ToArgb():X8} vs 读出 {b.ToArgb():X8}";
                        break;
                    }
                }
                Check(match, "32×32 的像素逐点往返一致（没写反、没写歪）", mismatch);

                // DIB 是自下而上存的，"漏掉翻转"是这类代码最典型的错。
                // 挑两个只有翻转才会解释得了的点来验：
                //   源图从 (6,3) 起向右下填充，(10,1) 在填充区之外（x够但 y不够）
                //   竖直翻转后它就会落到填充区里，变成不透明
                using var dec64 = CurFile.Decode(curPath, 2);
                Check(dec64.GetPixel(10, 1).A < 40,
                    "填充区上方的像素读回来仍然透明（说明没有上下颠倒）",
                    $"alpha={dec64.GetPixel(10, 1).A}");
                Check(dec64.GetPixel(10, 40).A > 200,
                    "填充区内的像素读回来是不透明的",
                    $"alpha={dec64.GetPixel(10, 40).A}");
            }
            catch (Exception ex)
            {
                Fail("解码往返测试出错：" + ex.Message);
            }

            // --- 用新加的读取器把自己的文件读回来（系统指针也是用它量的）---
            try
            {
                var back = CurFile.ReadFrame(curPath, 64);
                Check(back is not null, "ReadFrame 能读回自己写出来的 .cur");

                if (back is not null)
                {
                    using (back.Image)
                    {
                        Check(back.Image.Width == 64 && back.Image.Height == 64,
                            "挑到的是 64 那一档", $"实际 {back.Image.Width}×{back.Image.Height}");
                        Check(back.HotX == 6 && back.HotY == 3,
                            "热点也读回来了", $"实际 ({back.HotX},{back.HotY})");

                        var want = frames[2].Bitmap.GetPixel(10, 40);
                        var got = back.Image.GetPixel(10, 40);
                        Check(want.ToArgb() == got.ToArgb(),
                            "像素值一致（读取器没把上下搞反）",
                            $"原 {want.ToArgb():X8} vs 读回 {got.ToArgb():X8}");
                    }
                }
            }
            catch (Exception ex)
            {
                Fail("读回测试出错：" + ex.Message);
            }
        }
        finally
        {
            foreach (var b in owned) b.Dispose();
        }

        // --- 让 Windows 自己来认，这是最硬的判据 ---
        IntPtr h = NativeLoad(curPath, 32);
        Check(h != IntPtr.Zero, "Windows 的 LoadImage 能加载这个 .cur（系统认它）");
        if (h != IntPtr.Zero) NativeDestroy(h);

        // 「试一试」区域用的是 LoadCursorFromFile，和 LoadImage 是两条不同的代码路径
        IntPtr live = Win32.LoadCursor(curPath);
        Check(live != IntPtr.Zero, "LoadCursorFromFile 也能加载它（「试一试」区域走的就是这条）");
        Win32.SafeDestroyCursor(live);

        foreach (int size in new[] { 48, 64 })
        {
            IntPtr hh = NativeLoad(curPath, size);
            Check(hh != IntPtr.Zero, $"Windows 能按 {size} 尺寸加载它");
            if (hh != IntPtr.Zero) NativeDestroy(hh);
        }

        // --- AND 掩码：老程序不看 alpha 通道，只看这一位 ---
        try
        {
            using var round = TestImages.TransparentCircle(64);
            using var rendered = Renderer.Scale(round, 32, true, 0.7);
            string maskPath = Path.Combine(dir, "mask.cur");
            CurFile.Write(maskPath, new[] { new CurImage(rendered, 16, 16) });

            var entry = CurFile.Inspect(maskPath).Entries[0];
            byte[] raw = File.ReadAllBytes(maskPath);
            int maskBase = entry.Offset + CurFile.HeaderSize + entry.Width * entry.Height * 4;
            int maskStride = (entry.Width + 31) / 32 * 4;

            int mismatch = 0;
            using (var decoded = CurFile.Decode(maskPath, 0))
            {
                for (int y = 0; y < entry.Height; y++)
                for (int x = 0; x < entry.Width; x++)
                {
                    // 掩码和 XOR 一样是自下而上存的
                    int fileRow = entry.Height - 1 - y;
                    bool maskTransparent =
                        (raw[maskBase + fileRow * maskStride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                    bool reallyTransparent = decoded.GetPixel(x, y).A <= 127;
                    if (maskTransparent != reallyTransparent) mismatch++;
                }
            }

            Check(mismatch == 0,
                "AND 掩码和 alpha 通道完全对齐（1 位透明度和 32 位透明度说的是同一件事）",
                $"{mismatch} 个像素对不上");
            Check((raw[maskBase + (entry.Height - 1) * maskStride] & 0x80) != 0,
                "左上角在掩码里被标成透明");
        }
        catch (Exception ex)
        {
            Fail("AND 掩码测试出错：" + ex.Message);
        }

        // --- 边界情况 ---
        using (var tiny = new Bitmap(32, 32))
        {
            try
            {
                CurFile.Write(Path.Combine(dir, "bad.cur"), new[] { new CurImage(tiny, 32, 0) });
                Fail("热点越界时应该抛异常，但没有");
            }
            catch (CurFormatException)
            {
                Pass("热点越界会被拦下并抛 CurFormatException");
            }
            catch (Exception ex)
            {
                Fail("热点越界抛的是别的异常类型：" + ex.GetType().Name);
            }
        }

        try
        {
            CurFile.Write(Path.Combine(dir, "empty.cur"), Array.Empty<CurImage>());
            Fail("空帧列表应该抛异常，但没有");
        }
        catch (ArgumentException) { Pass("空帧列表会被拦下"); }
        catch (Exception ex) { Fail("空帧列表抛的是别的异常：" + ex.GetType().Name); }

        // --- 256 的宽度字段要写成 0 ---
        try
        {
            using var big = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big)) g.Clear(Color.Blue);
            string p256 = Path.Combine(dir, "s256.cur");
            CurFile.Write(p256, new[] { new CurImage(big, 128, 128) });

            var i256 = CurFile.Inspect(p256);
            Check(i256.Entries[0].Width == 256, "256 尺寸能被正确解析回来（宽度字段写 0 的老规矩）");
        }
        catch (Exception ex)
        {
            Fail("256 尺寸处理出错：" + ex.Message);
        }
    }

    // ================================================================ C. 指针位表

    private static void SectionSlots()
    {
        Section("C. 指针位定义");

        Check(CursorSlots.All.Count == 17, "定义了 17 个指针位", $"实际 {CursorSlots.All.Count}");
        Check(CursorSlots.All.Select(s => s.RegName).Distinct().Count() == 17, "注册表值名没有重复");

        // 这几个名字必须和 Windows 完全一致，写错系统就不认
        string[] must = { "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen",
                          "No", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand" };
        var missing = must.Where(m => CursorSlots.ByRegName(m) is null).ToList();
        Check(missing.Count == 0, "Windows 标准指针位一个不少", string.Join(",", missing));

        // 和真实注册表对照：读得到当前值，而且那个文件真的在
        try
        {
            var current = CursorRegistry.ReadCurrent();
            string? arrow = current.GetValueOrDefault("Arrow");
            Check(!string.IsNullOrEmpty(arrow), "能读到注册表里 Arrow 的当前值", arrow ?? "(空)");

            if (!string.IsNullOrEmpty(arrow))
            {
                string expanded = Environment.ExpandEnvironmentVariables(arrow);
                Check(File.Exists(expanded), "Arrow 指向的 .cur 文件确实存在", expanded);
            }
        }
        catch (Exception ex)
        {
            Warn("读不到注册表，跳过对照：" + ex.Message);
        }
    }

    // ================================================================ D2. 系统自带指针实测

    /// <summary>
    /// 量一下系统自带的指针在 32×32 画布里到底占了多大一块。
    /// "导入的图别比原来的指针大太多"这件事需要一个客观基准，这就是那个基准。
    /// 顺便验证 .cur 读取器能读懂别人（微软）写的文件。
    /// </summary>
    private static void SectionStockCursors()
    {
        Section("D. 系统自带指针实测（用 .cur 读取器量内容框占比）");

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors");
        if (!Directory.Exists(dir))
        {
            Warn("找不到系统指针目录，跳过");
            return;
        }

        string[] probes =
        {
            "aero_arrow.cur", "aero_link.cur", "aero_helpsel.cur",
            "aero_move.cur", "aero_ns.cur", "aero_unavail.cur", "aero_up.cur",
        };

        var ratios = new List<double>();

        foreach (var file in probes)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;

            var frame = CurFile.ReadFrame(path, 32);
            if (frame is null)
            {
                Warn($"{file} 读不出来（可能是没覆盖到的存储格式）");
                continue;
            }

            using (frame.Image)
            {
                var ink = CurFile.InkBounds(frame.Image);
                if (ink.IsEmpty) { Warn($"{file} 内容框是空的"); continue; }

                double ratio = (double)Math.Max(ink.Width, ink.Height) / frame.Image.Width;
                ratios.Add(ratio);

                Info($"      {file,-22} 画布 {frame.Image.Width}×{frame.Image.Height}  " +
                     $"内容框 {ink.Width}×{ink.Height}  " +
                     $"占 {ratio * 100:F0}%  左上角 ({ink.X},{ink.Y})  热点 ({frame.HotX},{frame.HotY})");
            }
        }

        if (ratios.Count == 0)
        {
            Warn("一个系统指针都没量到");
            return;
        }

        Pass($"成功读取并测量了 {ratios.Count} 个系统自带指针");

        double avg = ratios.Average();
        double max = ratios.Max();
        Info($"      → 平均占比 {avg * 100:F0}%，最大占比 {max * 100:F0}%");

        // 这条是给"相对大小"默认值兜底的：系统指针的内容框普遍不超过画布的 9 成
        Check(max <= 1.0, "系统指针的内容框不会超出画布");
        Check(avg < 0.95,
            "系统指针普遍不会铺满整个画布（所以导入的图全铺满会显得偏大）",
            $"平均已经到 {avg * 100:F0}% 了");
    }

    // ================================================================ E. 方案包往返

    private static void SectionPack()
    {
        Section("D. 方案包导出 / 导入往返");

        try
        {
            using var demo = TestImages.DemoArrow(96);
            string src = Path.Combine(AppPaths.Root, "pack-src.png");
            demo.Save(src, ImageFormat.Png);
            string stored = Store.ImportImage(src);

            var ws = new Workspace { SchemeName = "往返测试" };

            var arrow = ws.For("Arrow");
            arrow.SourceImage = stored;
            arrow.HotX = 3;
            arrow.HotY = 2;
            arrow.Size = 32;

            var hand = ws.For("Hand");
            hand.SourceImage = stored;
            hand.RemoveBackground = true;
            hand.BackgroundKey = "#FFFFFF";
            hand.Tolerance = 35;
            hand.KeepAspect = false;

            using var cache = new RenderCache();
            var built = Store.BuildCursorFiles(ws, cache);
            Check(built.Files.Count == 2, "生成了 2 个 .cur 文件", $"实际 {built.Files.Count}");
            Check(built.Warnings.Count == 0, "生成过程没有警告", string.Join("；", built.Warnings));

            string zip = Path.Combine(AppPaths.Root, "pack.zip");
            Store.ExportPack(ws, zip, built);
            Check(File.Exists(zip), "导出 zip 成功");
            Info($"方案包大小：{new FileInfo(zip).Length} 字节");

            Workspace back;
            try
            {
                back = Store.ImportPack(zip);
                Pass("导入 zip 成功");
            }
            catch (Exception ex)
            {
                Fail("导入自己导出的方案包失败：" + ex.Message);
                return;
            }

            Check(back.SchemeName == "往返测试", "方案名跟着过去了", back.SchemeName);
            Check(back.ConfiguredCount == 2, "2 个指针位都回来了", $"实际 {back.ConfiguredCount}");
            Check(back.MissingImages.Count == 0, "没有丢失源图",
                string.Join("、", back.MissingImages));

            var bArrow = back.For("Arrow");
            Check(bArrow.HotX == 3 && bArrow.HotY == 2, "手动热点原样保留",
                $"实际 ({bArrow.HotX},{bArrow.HotY})");

            var bHand = back.For("Hand");
            Check(bHand.RemoveBackground && bHand.BackgroundKey == "#FFFFFF" && bHand.Tolerance == 35,
                "抠图参数原样保留",
                $"on={bHand.RemoveBackground} key={bHand.BackgroundKey} tol={bHand.Tolerance}");
            Check(!bHand.KeepAspect, "缩放方式原样保留");

            Check(bArrow.HasImage && File.Exists(bArrow.SourceImage!),
                "导入后源图落到了本机磁盘上", bArrow.SourceImage ?? "(空)");

            // 文件名是内容哈希，所以"内容一样"直接体现在文件名相同上
            using var cache2 = new RenderCache();
            var rebuilt = Store.BuildCursorFiles(back, cache2);
            Check(rebuilt.Files.Count == 2, "导入后再生成一次仍然是 2 个",
                $"实际 {rebuilt.Files.Count}");

            bool sameArrow = rebuilt.Files.GetValueOrDefault("Arrow") == built.Files.GetValueOrDefault("Arrow");
            Check(sameArrow, "往返之后 Arrow 的 .cur 逐字节一致",
                $"原来 {Path.GetFileName(built.Files.GetValueOrDefault("Arrow") ?? "?")} / " +
                $"现在 {Path.GetFileName(rebuilt.Files.GetValueOrDefault("Arrow") ?? "?")}");

            bool sameHand = rebuilt.Files.GetValueOrDefault("Hand") == built.Files.GetValueOrDefault("Hand");
            Check(sameHand, "往返之后 Hand 的 .cur 逐字节一致",
                $"原来 {Path.GetFileName(built.Files.GetValueOrDefault("Hand") ?? "?")} / " +
                $"现在 {Path.GetFileName(rebuilt.Files.GetValueOrDefault("Hand") ?? "?")}");
        }
        catch (Exception ex)
        {
            Fail("方案包往返测试出错：" + ex.Message);
        }
    }

    // ================================================================ E. 注册表实测

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr h);

    private static IntPtr NativeLoad(string path, int size) =>
        LoadImage(IntPtr.Zero, path, 2 /*IMAGE_CURSOR*/, size, size, 0x0010 /*LR_LOADFROMFILE*/);

    private static void NativeDestroy(IntPtr h) => DestroyCursor(h);

    private static void SectionRegistry()
    {
        Section("E. 注册表实测（会短暂改变系统指针，结束自动还原）");

        var before = CursorRegistry.Capture();
        Info($"测试前：方案「{before.SchemeName}」 Arrow={before.Values.GetValueOrDefault("Arrow")}");

        string dir = Path.Combine(AppPaths.Root, "selftest");
        Directory.CreateDirectory(dir);
        string curPath = Path.Combine(dir, "registry-probe.cur");

        using (var bmp = TestImages.Solid(32, 32, Color.FromArgb(255, 0, 200, 255)))
            CurFile.Write(curPath, new[] { new CurImage(bmp, 16, 16) });

        try
        {
            CursorRegistry.Apply(new Dictionary<string, string> { ["Arrow"] = curPath }, "CursorStudio 自检");

            var during = CursorRegistry.Capture();
            Check(during.Values.GetValueOrDefault("Arrow") == curPath,
                "Arrow 写进去了", $"实际 {during.Values.GetValueOrDefault("Arrow")}");
            Check(during.SchemeSource == 0, "方案来源标记成 0（自定义）", $"实际 {during.SchemeSource}");
            Check(during.SchemeName == "CursorStudio 自检", "方案名写进去了", $"实际 {during.SchemeName}");

            var others = CursorSlots.All.Where(s => s.RegName != "Arrow").ToList();
            var touched = others.Where(s =>
                during.Values.GetValueOrDefault(s.RegName) != before.Values.GetValueOrDefault(s.RegName)).ToList();
            Check(touched.Count == 0, "没有误改其他指针位", string.Join(",", touched.Select(s => s.RegName)));
        }
        catch (Exception ex)
        {
            Fail("应用注册表出错：" + ex.Message);
        }
        finally
        {
            CursorRegistry.Restore(before);
        }

        var after = CursorRegistry.Capture();
        Check(after.Values.GetValueOrDefault("Arrow") == before.Values.GetValueOrDefault("Arrow"),
            "还原后 Arrow 和测试前一致",
            $"测试前 {before.Values.GetValueOrDefault("Arrow")} / 还原后 {after.Values.GetValueOrDefault("Arrow")}");
        Check(after.SchemeSource == before.SchemeSource, "还原后 SchemeSource 一致",
            $"{before.SchemeSource} → {after.SchemeSource}");
        Check(after.SchemeName == before.SchemeName, "还原后方案名一致",
            $"{before.SchemeName} → {after.SchemeName}");

        var diffs = CursorSlots.All
            .Where(s => after.Values.GetValueOrDefault(s.RegName) != before.Values.GetValueOrDefault(s.RegName))
            .Select(s => s.RegName).ToList();
        Check(diffs.Count == 0, "全部 17 个指针位都还原如初", string.Join(",", diffs));

        // ---- 「恢复 Windows 默认」按钮 ----
        try
        {
            CursorRegistry.RestoreWindowsDefault();
            var restored = CursorRegistry.Capture();

            Check(restored.SchemeSource == 2, "方案来源被设回 2（系统方案）", $"实际 {restored.SchemeSource}");

            string? arrowPath = restored.Values.GetValueOrDefault("Arrow");
            Check(arrowPath is not null && File.Exists(arrowPath),
                "Arrow 指向了系统自带的 aero 指针文件", arrowPath ?? "(空)");

            string? handPath = restored.Values.GetValueOrDefault("Hand");
            Check(handPath is not null && File.Exists(handPath),
                "Hand 指向了系统自带的 aero 指针文件", handPath ?? "(空)");

            // 每个被写入的路径都必须真实存在，否则光标会变成透明的
            var broken = CursorSlots.All
                .Select(s => (s.RegName, Path: restored.Values.GetValueOrDefault(s.RegName)))
                .Where(x => !string.IsNullOrEmpty(x.Path) && !File.Exists(x.Path!))
                .Select(x => $"{x.RegName}={x.Path}")
                .ToList();
            Check(broken.Count == 0, "写进去的每个路径都真实存在", string.Join("；", broken));

            Check(restored.SchemeName == before.SchemeName || restored.SchemeName == "Windows 默认",
                "方案名是系统内置的名字", restored.SchemeName ?? "(空)");
        }
        catch (Exception ex)
        {
            Fail("恢复 Windows 默认出错：" + ex.Message);
        }
        finally
        {
            CursorRegistry.Restore(before);
        }

        var final = CursorRegistry.Capture();
        Check(final.Values.GetValueOrDefault("Arrow") == before.Values.GetValueOrDefault("Arrow"),
            "跑完「恢复 Windows 默认」之后仍然还原回了测试前的状态",
            $"测试前 {before.Values.GetValueOrDefault("Arrow")} / 现在 {final.Values.GetValueOrDefault("Arrow")}");
    }

    // ================================================================ E. 界面布局

    private static readonly (string Name, Size Size)[] LayoutProbes =
    {
        ("默认-1080x720",   new Size(1080, 720)),
        ("最小-940x640",    new Size(940, 640)),
        ("偏窄-940x900",    new Size(940, 900)),
        ("中等-1200x760",   new Size(1200, 760)),
        ("宽屏-1600x1000",  new Size(1600, 1000)),
    };

    private static void SectionLayout(string shotsDir)
    {
        Section("F. 界面布局");

        // 先看空状态，再看配好图的状态——只测空界面会漏掉一大半控件
        // （放大预览、实际大小、左列表图标、试一试里的真指针）
        try { File.Delete(AppPaths.StateFile); } catch { }
        RunLayoutProbes(shotsDir, "空状态");
        SeedDemoWorkspace(shadow: false);
        RunLayoutProbes(shotsDir, "已配图");
        SeedDemoWorkspace(shadow: true);
        RunLayoutProbes(shotsDir, "带投影");
    }

    /// <summary>造一张白底箭头图，塞满全部指针位，并把 Arrow 设成手动热点。</summary>
    private static void SeedDemoWorkspace(bool shadow)
    {
        try
        {
            using var demo = TestImages.DemoArrow(128);
            string src = Path.Combine(AppPaths.Root, "demo-arrow.png");
            demo.Save(src, ImageFormat.Png);

            string stored = Store.ImportImage(src);

            var ws = new Workspace { SchemeName = "自检演示" };
            foreach (var slot in CursorSlots.All)
            {
                var st = ws.For(slot.RegName);
                st.SourceImage = stored;
                st.Size = 32;
                st.KeepAspect = true;
                st.RemoveBackground = true;
                st.BackgroundKey = "#FFFFFF";
                st.Tolerance = 30;
                st.Shadow = shadow;
                st.HotX = -1;
                st.HotY = -1;
            }

            // 留一个手动热点的，好把"手动指定"那套控件的显示状态也覆盖到
            var arrow = ws.For("Arrow");
            arrow.HotX = 4;
            arrow.HotY = 2;

            Store.SaveState(ws);
            Info("已铺好演示数据（白底箭头套用到全部指针位）");
        }
        catch (Exception ex)
        {
            Fail("准备演示数据失败：" + ex.Message);
        }
    }

    private static void RunLayoutProbes(string shotsDir, string stage)
    {
        foreach (var (probe, size) in LayoutProbes)
        {
            string name = $"{stage}-{probe}";
            MainForm? form = null;
            try
            {
                form = new MainForm
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-4000, -4000),   // 挪到屏幕外，别干扰用户
                };
                form.Size = size;
                form.Show();
                Application.DoEvents();
                form.PerformLayout();
                Application.DoEvents();

                var problems = new List<string>();
                Audit(form, form, problems, "");

                var errors = problems.Where(p => p.StartsWith("E|")).ToList();
                var warns = problems.Where(p => p.StartsWith("W|")).ToList();

                if (errors.Count > 0)
                    Fail($"[{name}] 布局有 {errors.Count} 个硬错误：\n      " +
                         string.Join("\n      ", errors.Select(e => e[2..])));
                else
                    Pass($"[{name}] 无硬错误");

                foreach (var w in warns)
                    Warn($"[{name}] {w[2..]}");

                // 截图存档，人眼再看一遍
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                string shot = Path.Combine(shotsDir, name + ".png");
                bmp.Save(shot, ImageFormat.Png);
                Info($"      截图 → {shot}");
            }
            catch (Exception ex)
            {
                Fail($"[{name}] 建窗口时崩了：{ex.Message}");
            }
            finally
            {
                form?.Close();
                form?.Dispose();
                Application.DoEvents();
            }
        }
    }

    /// <summary>
    /// 遍历控件树找布局毛病。
    /// "E|" 开头是硬错误（控件塌成 0 尺寸、按钮文字被切掉），"W|" 是提醒。
    /// </summary>
    private static void Audit(Control root, Control c, List<string> problems, string path)
    {
        string me = path.Length == 0 ? c.GetType().Name : path + "/" + c.GetType().Name;

        // NumericUpDown 内部的 UpDownEdit / UpDownButtons 是它自己拆出来的子控件，
        // 宽度天生比外层小一圈（被上下箭头占掉），不是布局问题，跳过
        bool isSpinnerPart = c.Parent is NumericUpDown;

        if (c is Form or SplitContainer or SplitterPanel)
        {
            // 容器自己尺寸为 0 多半是还没布局完，单独判断没意义
        }
        else if (c.Visible && !isSpinnerPart)
        {
            // 文字为空的 Label 宽度就是 0，这是正常的占位，不是塌陷
            bool emptyLabel = c is Label { Text.Length: 0 };

            if (!emptyLabel && (c.Width <= 0 || c.Height <= 0))
                problems.Add($"E|{me} 尺寸塌成 {c.Width}×{c.Height}");

            if (c.Parent is { } p && p.ClientSize.Width > 0)
            {
                var pc = p.ClientRectangle;
                int overR = c.Right - pc.Right;
                int overB = c.Bottom - pc.Bottom;
                bool flows = p is FlowLayoutPanel;

                // 工具栏那种 WrapContents=false 的流式布局，放不下就是直接裁掉，
                // 按钮会凭空消失——所以这里也要查，不能因为父容器是 FlowLayoutPanel 就放过
                if ((overR > 2 || overB > 2) && c.Dock == DockStyle.None)
                    problems.Add($"{(flows ? "E" : "W")}|{me} 超出父容器 右{overR}px 下{overB}px");
            }

            // 按钮：文字放不下就是被切了
            if (c is Button b && b.Text.Length > 0)
            {
                var need = b.GetPreferredSize(Size.Empty);
                if (need.Width > b.Width + 1 || need.Height > b.Height + 1)
                    problems.Add($"E|{me} 文字「{b.Text}」放不下：需要 {need.Width}×{need.Height}，实际 {b.Width}×{b.Height}");
            }

            // 下拉框/输入框被压扁
            if (c is ComboBox or NumericUpDown or TextBox)
            {
                if (c.Height < 20)
                    problems.Add($"E|{me} 高度只有 {c.Height}px，控件被压扁了");
                if (c.Width < 40)
                    problems.Add($"E|{me} 宽度只有 {c.Width}px，太窄");
            }

            // 下拉框：选中项的文字放不下就会被右边的箭头盖住
            if (c is ComboBox cb && cb.DropDownStyle == ComboBoxStyle.DropDownList && cb.Items.Count > 0)
            {
                int longest = cb.Items.Cast<object>().Select(i => i?.ToString() ?? "").Max(t => t.Length);
                string widest = cb.Items.Cast<object>().Select(i => i?.ToString() ?? "").First(t => t.Length == longest);
                int need = TextRenderer.MeasureText(widest, cb.Font).Width + 26;  // 26 = 下拉箭头 + 内边距
                if (need > cb.Width)
                    problems.Add($"E|{me} 下拉项「{widest}」显示不全：需要 {need}px，实际 {cb.Width}px");
            }

            // Label 文字被截断（只检查单行的）
            if (c is Label l && l.Text.Length > 0 && !l.AutoSize && l.Height < 40)
            {
                var need = TextRenderer.MeasureText(l.Text, l.Font, new Size(l.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                if (need.Height > l.Height + 2)
                    problems.Add($"W|{me} 文字可能显示不全：需要 {need.Height}px 高，实际 {l.Height}px");
            }
        }

        foreach (Control child in c.Controls)
            Audit(root, child, problems, me);
    }
}

/// <summary>自检用的合成图，图案位置都是已知的，方便断言。</summary>
internal static class TestImages
{
    public static Bitmap Solid(int w, int h, Color c)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    /// <summary>
    /// 一帧转圈的扇形，用来造大动画。
    /// 每帧图形的位置都不一样，所以"所有帧共用一个热点"这件事是真的被考到了。
    /// </summary>
    public static Bitmap Spinner(int size, int frame, int total)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(255, 20, 120, 220), size * 0.16f);
        g.DrawArc(pen, size * 0.15f, size * 0.15f, size * 0.7f, size * 0.7f,
                  frame * (360 / Math.Max(1, total)), 270);
        return bmp;
    }

    /// <summary>把一张图存到自检的临时根目录里，返回路径。</summary>
    public static string SaveTemp(Bitmap bmp, string name)
    {
        string path = Path.Combine(AppPaths.Root, name);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>
    /// 手搓一个 3 帧的 GIF（红 / 绿 / 蓝，每帧 12/100 秒）。
    ///
    /// 为什么不用 GDI+ 的 GIF 编码器造测试数据：它写出来的多帧 GIF，
    /// **它自己的解码器只数得出 1 帧**——实测文件里明明有 6 个图形控制扩展，
    /// 但 GetFrameCount(FrameDimension.Time) 返回 1。自检要的是确定性，
    /// 所以按 GIF89a 规范直接拼字节，不依赖编码器。
    /// </summary>
    public static byte[] TinyAnimatedGif()
    {
        var ms = new MemoryStream();
        void U8(int v) => ms.WriteByte((byte)v);
        void U16(int v) { ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)((v >> 8) & 0xFF)); }
        void Ascii(string s) => ms.Write(Encoding.ASCII.GetBytes(s), 0, s.Length);

        Ascii("GIF89a");
        U16(2); U16(2);                 // 逻辑屏幕 2×2
        U8(0x80 | 0x01);                // 有全局色表，2^(1+1)=4 项
        U8(0); U8(0);                   // 背景色索引、像素宽高比
        U8(255); U8(0); U8(0);          // 0 红
        U8(0); U8(255); U8(0);          // 1 绿
        U8(0); U8(0); U8(255);          // 2 蓝
        U8(0); U8(0); U8(0);            // 3 黑

        // Netscape 循环扩展
        U8(0x21); U8(0xFF); U8(0x0B); Ascii("NETSCAPE2.0"); U8(0x03); U8(0x01); U16(0); U8(0x00);

        for (int f = 0; f < 3; f++)
        {
            // 图形控制扩展：处置方式 1（不处置），延时 12/100 秒
            U8(0x21); U8(0xF9); U8(0x04); U8(0x04); U16(12); U8(0); U8(0x00);
            U8(0x2C); U16(0); U16(0); U16(2); U16(2); U8(0x00);   // 图像描述符

            // LZW 最简流：clear=4，四个像素都是索引 f，end=5，定长 3 位
            var bits = new List<int>();
            void Code(int c, int n) { for (int b = 0; b < n; b++) bits.Add((c >> b) & 1); }
            Code(4, 3);
            Code(f, 3); Code(f, 3); Code(f, 3); Code(f, 3);
            Code(5, 3);

            var packed = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int v = 0;
                for (int b = 0; b < 8 && i + b < bits.Count; b++) v |= bits[i + b] << b;
                packed.Add((byte)v);
            }

            U8(2);                          // minCodeSize（GIF 最小就是 2）
            U8(packed.Count);
            foreach (var b in packed) U8(b);
            U8(0x00);                       // 子块结束
        }

        U8(0x3B);                           // trailer
        return ms.ToArray();
    }

    /// <summary>白底 + 正中间一个红圆。用来验证抠背景。</summary>
    public static Bitmap WhiteBgLogo(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(255, 220, 30, 30));
        // 半径小一点，保证四个角的采样块都落在纯白上
        g.FillEllipse(brush, size * 0.3f, size * 0.3f, size * 0.4f, size * 0.4f);
        return bmp;
    }

    /// <summary>四角透明、中间有色的图。</summary>
    public static Bitmap TransparentCircle(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(255, 20, 120, 220));
        g.FillEllipse(brush, size * 0.25f, size * 0.25f, size * 0.5f, size * 0.5f);
        return bmp;
    }

    /// <summary>四角颜色都不一样的渐变图，用来验证"不会误判成纯色底"。</summary>
    public static Bitmap Gradient(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, size, size),
            Color.FromArgb(255, 20, 40, 200),
            Color.FromArgb(255, 240, 220, 40),
            LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, 0, 0, size, size);
        return bmp;
    }

    /// <summary>
    /// 白底 + 深蓝实心圆，边缘开抗锯齿。
    /// 专门用来验"抠背景时抗锯齿边缘有没有把白底的颜色带出来"。
    /// </summary>
    public static Bitmap SoftEdgeShape(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(255, 24, 52, 140));
        g.FillEllipse(brush, size * 0.2f, size * 0.2f, size * 0.6f, size * 0.6f);
        return bmp;
    }

    /// <summary>
    /// 一个画在纯白底上的箭头，尖角在 (0.10·size, 0.06·size) 附近。
    /// 纯白底是为了让"自动抠背景"真的生效——布局截图里能直接看出白底被抠成了透明格。
    /// </summary>
    public static Bitmap DemoArrow(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var pts = new[]
        {
            new PointF(size * 0.10f, size * 0.06f),
            new PointF(size * 0.10f, size * 0.74f),
            new PointF(size * 0.30f, size * 0.55f),
            new PointF(size * 0.44f, size * 0.88f),
            new PointF(size * 0.58f, size * 0.82f),
            new PointF(size * 0.44f, size * 0.49f),
            new PointF(size * 0.68f, size * 0.45f),
        };

        using var brush = new SolidBrush(Color.FromArgb(255, 42, 62, 132));
        g.FillPolygon(brush, pts);
        using var pen = new Pen(Color.FromArgb(255, 14, 24, 70), Math.Max(1f, size / 40f));
        g.DrawPolygon(pen, pts);
        return bmp;
    }

    /// <summary>在 (tipX, tipY) 处放一个孤立的亮像素，其余全透明。用来验证尖角热点判定。</summary>
    public static Bitmap ArrowTipAt(int tipX, int tipY, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(255, 30, 30, 30));
        g.FillRectangle(brush, tipX, tipY, size - tipX, size - tipY);
        return bmp;
    }
}
