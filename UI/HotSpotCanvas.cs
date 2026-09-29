using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CursorStudio.UI;

/// <summary>
/// 指针放大预览 + 热点编辑器。
/// 左边显示的是渲染后的真实像素（放大到整数倍、最近邻取样，看得清每一个像素），
/// 在上面点一下就把热点挪到那个像素。热点就是"点击真正生效的那个点"——
/// 箭头要在尖上，十字要在正中，放错地方会觉得鼠标"点不准"。
/// </summary>
public sealed class HotSpotCanvas : Control
{
    private Bitmap? _image;
    private Point _hot;
    /// <summary>
    /// 画布自己持有的那份位图副本。
    /// 不能直接引用渲染缓存里的那张：缓存在超出上限时会把它淘汰掉并 Dispose，
    /// 而画布还一直拿着引用——下次重绘就会对着一个已经释放的 Bitmap 画，直接崩。
    /// </summary>
    private Bitmap? _owned;
    private int _zoom = 8;
    private Rectangle _dest;
    private bool _dragging;
    private bool _hoverValid;

    /// <summary>用户点了画布改热点。e.X/e.Y 是新热点（图像像素坐标）。</summary>
    public event EventHandler<Point>? HotSpotPicked;

    public HotSpotCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(250, 250, 252);
        Cursor = Cursors.Cross;
    }

    /// <summary>传入的位图归调用方所有；本控件会复制一份自己拿着。</summary>
    public void SetImage(Bitmap? bmp, Point hot)
    {
        // 拖热点时渲染参数没变、缓存命中返回的还是同一张图，
        // 引用没变就不用反复复制了
        if (!ReferenceEquals(_image, bmp))
        {
            var old = _owned;
            _image = bmp;
            _owned = bmp is null ? null : new Bitmap(bmp);
            old?.Dispose();
        }

        // 图变小之后旧热点可能越界，夹一下
        if (_owned is not null)
            _hot = new Point(
                Math.Clamp(hot.X, 0, _owned.Width - 1),
                Math.Clamp(hot.Y, 0, _owned.Height - 1));
        else
            _hot = hot;

        Invalidate();
    }

    public void SetHotSpot(Point hot)
    {
        if (_hot == hot) return;
        _hot = hot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        DrawCheckerboard(g);

        if (_owned is null)
        {
            _dest = Rectangle.Empty;
            TextRenderer.DrawText(g, "还没有导入图片\r\n\r\n点左上角「导入图片」，\r\n再选一个指针位",
                Font, ClientRectangle, Color.FromArgb(150, 150, 160),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        int w = _owned.Width, h = _owned.Height;
        int pad = 16;
        _zoom = Math.Max(1, Math.Min(
            Math.Max(1, (ClientSize.Width - pad * 2) / w),
            Math.Max(1, (ClientSize.Height - pad * 2) / h)));
        _zoom = Math.Min(_zoom, 32);

        int dw = w * _zoom, dh = h * _zoom;
        _dest = new Rectangle(
            (ClientSize.Width - dw) / 2,
            (ClientSize.Height - dh) / 2,
            dw, dh);

        // 放大后的图用最近邻，像素边界清清楚楚
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_owned, _dest);

        if (_zoom >= 4) DrawPixelGrid(g, w, h);

        using (var pen = new Pen(Color.FromArgb(140, 30, 90, 200), 1))
        {
            g.DrawRectangle(pen, _dest);
        }

        DrawHotSpot(g);

        if (_hoverValid)
            TextRenderer.DrawText(g, $"{w}×{h} 像素 · 放大 {_zoom}×", Font,
                new Rectangle(6, ClientSize.Height - 22, ClientSize.Width - 12, 18),
                Color.FromArgb(120, 120, 130), TextFormatFlags.Right);
    }

    private void DrawCheckerboard(Graphics g)
    {
        const int cell = 10;
        using var light = new SolidBrush(Color.White);
        using var dark = new SolidBrush(Color.FromArgb(238, 238, 242));
        g.FillRectangle(light, ClientRectangle);
        for (int y = 0; y < ClientSize.Height; y += cell)
        for (int x = 0; x < ClientSize.Width; x += cell)
        {
            if (((x / cell) + (y / cell)) % 2 == 0) continue;
            g.FillRectangle(dark, x, y, cell, cell);
        }
    }

    private void DrawPixelGrid(Graphics g, int w, int h)
    {
        using var pen = new Pen(Color.FromArgb(38, 0, 0, 0), 1);
        for (int x = 1; x < w; x++)
        {
            int px = _dest.Left + x * _zoom;
            g.DrawLine(pen, px, _dest.Top, px, _dest.Bottom);
        }
        for (int y = 1; y < h; y++)
        {
            int py = _dest.Top + y * _zoom;
            g.DrawLine(pen, _dest.Left, py, _dest.Right, py);
        }
    }

    private void DrawHotSpot(Graphics g)
    {
        int hx = _dest.Left + _hot.X * _zoom;
        int hy = _dest.Top + _hot.Y * _zoom;

        // 红白双色画两遍，深浅背景上都看得见
        using var white = new Pen(Color.White, 3);
        using var red = new Pen(Color.FromArgb(230, 40, 40), 1);

        foreach (var pen in new[] { white, red })
        {
            g.DrawLine(pen, hx, 0, hx, ClientSize.Height);
            g.DrawLine(pen, 0, hy, ClientSize.Width, hy);
        }

        var box = new Rectangle(
            _dest.Left + _hot.X * _zoom - 1,
            _dest.Top + _hot.Y * _zoom - 1,
            _zoom + 2, _zoom + 2);

        using var solidW = new Pen(Color.White, 3);
        using var solidR = new Pen(Color.FromArgb(230, 40, 40), 2);
        g.DrawRectangle(solidW, box);
        g.DrawRectangle(solidR, box);
    }

    // ------------------------------------------------------------ 交互

    private bool TryPixelFrom(Point client, out Point pixel)
    {
        pixel = Point.Empty;
        if (_owned is null || _dest.IsEmpty || !_dest.Contains(client)) return false;

        int x = (client.X - _dest.Left) / _zoom;
        int y = (client.Y - _dest.Top) / _zoom;
        pixel = new Point(
            Math.Clamp(x, 0, _owned.Width - 1),
            Math.Clamp(y, 0, _owned.Height - 1));
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (!TryPixelFrom(e.Location, out var p)) return;

        _dragging = true;
        Capture = true;
        HotSpotPicked?.Invoke(this, p);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hoverValid) { _hoverValid = true; Invalidate(); }

        if (_dragging && TryPixelFrom(e.Location, out var p))
            HotSpotPicked?.Invoke(this, p);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _dragging = false;
        if (_hoverValid) { _hoverValid = false; Invalidate(); }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _owned?.Dispose();
            _owned = null;
            _image = null;
        }
        base.Dispose(disposing);
    }
}
