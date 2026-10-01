using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace CursorStudio.Core;

/// <summary>要写进 .cur 的一帧：已经渲染到最终像素尺寸的位图 + 热点。</summary>
public sealed record CurImage(Bitmap Bitmap, int HotX, int HotY);

public sealed record CurEntryInfo(int Width, int Height, int HotX, int HotY, int Bytes, int Offset);

public sealed record CurInfo(int Type, int Count, IReadOnlyList<CurEntryInfo> Entries);

public sealed class CurFormatException(string message) : Exception(message);

/// <summary>
/// .cur 文件读写。
///
/// 格式说明（省得以后忘了）：
///   .cur 就是 .ico，只差一个字节——ICONDIR.idType 是 2 而不是 1。
///   而且 ICONDIRENTRY 里本来放"颜色平面数"和"位深"的 4 个字节，
///   在光标格式里被换成热点坐标 X/Y。这是整个格式里唯一的坑。
///
///   图像数据本身是标准 DIB：BITMAPINFOHEADER + XOR 位图 + AND 掩码。
///   注意 biHeight 要填高度的两倍——因为 XOR 和 AND 两张图是上下拼在一起的。
///   AND 掩码按 alpha 逐位生成（1 = 透明），理由见下面 BuildDib 里的注释。
/// </summary>
public static class CurFile
{
    private const int DirSize = 6;
    private const int EntrySize = 16;
    /// <summary>BITMAPINFOHEADER 的固定长度。</summary>
    internal const int HeaderSize = 40;

    /// <summary>把一个或多个尺寸写成一个 .cur 文件。多个尺寸时 Windows 会挑最接近当前指针大小的那个用。</summary>
    public static void Write(string path, IReadOnlyList<CurImage> images)
    {
        byte[] bytes = BuildBytes(images);

        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // 先写临时文件再原子替换：中途失败也不会留半个坏文件在硬盘上
        string tmp = full + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不该盖住真正的错 */ }
            throw;
        }
    }

    /// <summary>
    /// 只拼字节不落盘。
    /// 写 .ani 时每一帧内部装的都是一个完整的 .cur，直接拿这里的结果塞进 icon 块就行，
    /// 不必为了取字节再走一遍临时文件。
    /// </summary>
    public static byte[] BuildBytes(IReadOnlyList<CurImage> images)
    {
        if (images.Count == 0)
            throw new ArgumentException("至少要有一帧", nameof(images));
        if (images.Count > 255)
            throw new ArgumentException("单个 .cur 最多 255 帧", nameof(images));

        var blobs = new byte[images.Count][];
        for (int i = 0; i < images.Count; i++)
        {
            Validate(images[i]);
            blobs[i] = BuildDib(images[i].Bitmap);
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0);            // idReserved
            w.Write((ushort)2);            // idType：2 = 光标（图标是 1）
            w.Write((ushort)images.Count); // idCount

            int offset = DirSize + EntrySize * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                var bmp = images[i].Bitmap;
                // 256 要写成 0，这是格式里"用 1 个字节表示 256"的老规矩
                w.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width));
                w.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
                w.Write((byte)0);                        // bColorCount，真彩色填 0
                w.Write((byte)0);                        // bReserved
                w.Write((ushort)images[i].HotX);         // ← 光标格式：这里其实是热点 X
                w.Write((ushort)images[i].HotY);         // ← 光标格式：这里其实是热点 Y
                w.Write((uint)blobs[i].Length);
                w.Write((uint)offset);
                offset += blobs[i].Length;
            }

            foreach (var b in blobs) w.Write(b);
        }

        return ms.ToArray();
    }

    private static void Validate(CurImage img)
    {
        int w = img.Bitmap.Width, h = img.Bitmap.Height;
        if (w is <= 0 or > 256 || h is <= 0 or > 256)
            throw new CurFormatException($"尺寸 {w}×{h} 超出 1..256 的范围");
        if (img.HotX < 0 || img.HotX >= w || img.HotY < 0 || img.HotY >= h)
            throw new CurFormatException($"热点 ({img.HotX},{img.HotY}) 落在 {w}×{h} 图像之外");
    }

    private static byte[] BuildDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int xorSize = w * h * 4;
        int maskStride = (w + 31) / 32 * 4;   // 1bpp，每行补齐到 4 字节
        int maskSize = maskStride * h;
        var buf = new byte[HeaderSize + xorSize + maskSize];

        // ---- BITMAPINFOHEADER ----
        PutI32(buf, 0, HeaderSize);
        PutI32(buf, 4, w);
        PutI32(buf, 8, h * 2);              // ← 高度翻倍：XOR 图 + AND 掩码叠在一起
        PutI16(buf, 12, 1);                 // biPlanes
        PutI16(buf, 14, 32);                // biBitCount
        PutI32(buf, 16, 0);                 // biCompression = BI_RGB
        PutI32(buf, 20, xorSize + maskSize);
        // 后面 16 字节保持 0

        // ---- XOR 位图：BGRA、自下而上 ----
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int absStride = Math.Abs(data.Stride);
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                // Stride 为负说明这张位图本身就是自下而上存的，源行号要反过来数
                int srcRow = data.Stride >= 0 ? y : h - 1 - y;
                Marshal.Copy(IntPtr.Add(data.Scan0, srcRow * absStride), row, 0, row.Length);
                // DIB 里第 0 行是图像的**最后**一行
                Buffer.BlockCopy(row, 0, buf, HeaderSize + (h - 1 - y) * w * 4, row.Length);
            }

            // ---- AND 掩码：按 alpha 生成 1 位透明度 ----
            // 32 位色的指针，现代 Windows 只认 alpha 通道，AND 掩码随便填都不影响观感。
            // 但老程序（一些 Win32 老工具、部分远程桌面/终端客户端）不看 alpha，只看这个掩码——
            // 全填 0 的话它们会把整个方框都画出来，包括本该透明的部分。所以老老实实按
            // alpha > 127 逐位生成。Bibata 的 anicursorgen 也是这么做的。
            int maskBase = HeaderSize + w * h * 4;
            for (int y = 0; y < h; y++)
            {
                int srcRow = data.Stride >= 0 ? y : h - 1 - y;
                int maskRow = maskBase + (h - 1 - y) * maskStride;
                for (int x = 0; x < w; x++)
                {
                    // 位 = 1 表示"透明"，靠左往右填，高位在前
                    if (Marshal.ReadByte(data.Scan0, srcRow * absStride + x * 4 + 3) <= 127)
                        buf[maskRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return buf;
    }

    private static void PutI32(byte[] b, int off, int v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    private static void PutI16(byte[] b, int off, int v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
    }

    // ------------------------------------------------------------------
    // 读：解析别人写的 .cur（系统自带的那套也要能读）
    // ------------------------------------------------------------------

    public sealed record CurFrame(Bitmap Image, int HotX, int HotY, int Width, int Height);

    /// <summary>
    /// 读一个 .cur，挑最接近 <paramref name="targetSize"/> 的那一帧。
    /// 支持 32 位色 DIB（系统自带指针都是这个）和 PNG 内嵌两种存储方式。
    /// 读不了就返回 null——这是给"参考系统指针"用的，读不到不该让程序崩。
    /// </summary>
    public static CurFrame? ReadFrame(string path, int targetSize = 32)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch { return null; }
        return ReadFrame(raw, targetSize);
    }

    /// <summary>
    /// 从内存里的字节读一帧。.ani 的每一帧内部就是一个完整 .cur，
    /// 解帧时用这个重载，免得先落一次盘。
    /// </summary>
    public static CurFrame? ReadFrame(byte[] raw, int targetSize = 32)
    {
        if (raw.Length < DirSize + EntrySize) return null;
        if (ReadU16(raw, 0) != 0) return null;

        int type = ReadU16(raw, 2);
        if (type is not (1 or 2)) return null;

        int count = ReadU16(raw, 4);
        if (count <= 0 || raw.Length < DirSize + EntrySize * count) return null;

        int best = -1, bestScore = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int e = DirSize + EntrySize * i;
            int w = raw[e]; if (w == 0) w = 256;
            int score = Math.Abs(w - targetSize);
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return null;

        int entry = DirSize + EntrySize * best;
        int width = raw[entry]; if (width == 0) width = 256;
        int height = raw[entry + 1]; if (height == 0) height = 256;
        // 图标（type 1）里这两个字段是颜色平面数和位深，不是热点
        int hotX = type == 2 ? ReadU16(raw, entry + 4) : 0;
        int hotY = type == 2 ? ReadU16(raw, entry + 6) : 0;
        int bytes = (int)ReadU32(raw, entry + 8);
        int offset = (int)ReadU32(raw, entry + 12);

        if (offset < 0 || bytes <= 0 || offset + bytes > raw.Length) return null;

        var image = DecodePayload(raw, offset, bytes, width, height);
        return image is null ? null : new CurFrame(image, hotX, hotY, width, height);
    }

    private static Bitmap? DecodePayload(byte[] raw, int offset, int bytes, int width, int height)
    {
        // PNG 内嵌（Vista 以后的大尺寸图标会用）
        if (bytes > 8 && raw[offset] == 0x89 && raw[offset + 1] == 0x50
                      && raw[offset + 2] == 0x4E && raw[offset + 3] == 0x47)
        {
            try
            {
                using var ms = new MemoryStream(raw, offset, bytes, writable: false);
                using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
                return new Bitmap(img);
            }
            catch { return null; }
        }

        // 32 位色 DIB
        if (offset + HeaderSize > raw.Length) return null;
        int biSize = ReadI32(raw, offset);
        int dibW = ReadI32(raw, offset + 4);
        int dibH = ReadI32(raw, offset + 8);
        int bpp = ReadU16(raw, offset + 14);
        int compression = ReadI32(raw, offset + 16);

        if (biSize < HeaderSize) return null;
        if (bpp != 32 || compression != 0) return null;   // 其它位深系统指针也不用，不折腾

        int w = dibW > 0 ? dibW : width;
        int h = dibH > 0 ? dibH / 2 : height;             // biHeight 存的是两倍
        if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;

        int dataOffset = offset + biSize;
        if (dataOffset + (long)w * h * 4 > raw.Length) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var dst = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int absStride = Math.Abs(dst.Stride);
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                // 文件里是自下而上的
                Buffer.BlockCopy(raw, dataOffset + (h - 1 - y) * w * 4, row, 0, row.Length);
                int dstRow = dst.Stride >= 0 ? y : h - 1 - y;
                Marshal.Copy(row, 0, IntPtr.Add(dst.Scan0, dstRow * absStride), row.Length);
            }
        }
        catch
        {
            bmp.Dispose();
            return null;
        }
        finally
        {
            bmp.UnlockBits(dst);
        }

        return bmp;
    }

    /// <summary>图像不透明部分的包围盒。用来量"这个指针实际占了画布多大一块"。</summary>
    public static Rectangle InkBounds(Bitmap bmp, int alphaThreshold = 24)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int len = stride * bmp.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                int rowStart = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (buf[rowStart + x * 4 + 3] <= alphaThreshold) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0) return Rectangle.Empty;
            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ------------------------------------------------------------------
    // 下面这些只给自检用：把写出来的文件重新解析一遍，逐字段核对。
    // ------------------------------------------------------------------

    public static CurInfo Inspect(string path) => Inspect(File.ReadAllBytes(path));

    /// <summary>同 <see cref="Inspect(string)"/>，但直接吃内存里的字节（.ani 的帧就是这么检查的）。</summary>
    public static CurInfo Inspect(byte[] raw)
    {
        if (raw.Length < DirSize + EntrySize)
            throw new CurFormatException($"文件只有 {raw.Length} 字节，连目录都装不下");

        int type = ReadU16(raw, 2);
        if (ReadU16(raw, 0) != 0)
            throw new CurFormatException("idReserved 应该是 0");
        if (type != 2)
            throw new CurFormatException($"idType 是 {type}，应该是 2（光标）");

        int count = ReadU16(raw, 4);
        if (count == 0)
            throw new CurFormatException("idCount 是 0");
        if (raw.Length < DirSize + EntrySize * count)
            throw new CurFormatException("目录项数量对不上文件长度");

        var entries = new List<CurEntryInfo>(count);
        for (int i = 0; i < count; i++)
        {
            int e = DirSize + EntrySize * i;
            int w = raw[e]; if (w == 0) w = 256;
            int h = raw[e + 1]; if (h == 0) h = 256;
            int hotX = ReadU16(raw, e + 4);
            int hotY = ReadU16(raw, e + 6);
            int bytes = (int)ReadU32(raw, e + 8);
            int offset = (int)ReadU32(raw, e + 12);

            if (offset < DirSize + EntrySize * count || offset + bytes > raw.Length)
                throw new CurFormatException($"第 {i} 帧的数据区 [{offset}, {offset + bytes}) 越界");
            if (hotX >= w || hotY >= h)
                throw new CurFormatException($"第 {i} 帧热点 ({hotX},{hotY}) 落在 {w}×{h} 之外");

            // DIB 头部自检
            if (ReadI32(raw, offset) != HeaderSize)
                throw new CurFormatException($"第 {i} 帧的 biSize 不是 40");
            int dibW = ReadI32(raw, offset + 4);
            int dibH = ReadI32(raw, offset + 8);
            if (dibW != w || dibH != h * 2)
                throw new CurFormatException($"第 {i} 帧 DIB 尺寸 {dibW}×{dibH} 和目录里的 {w}×{h * 2} 对不上");
            if (ReadU16(raw, offset + 14) != 32)
                throw new CurFormatException($"第 {i} 帧不是 32 位色");
            if (ReadI32(raw, offset + 16) != 0)
                throw new CurFormatException($"第 {i} 帧的压缩方式不是 BI_RGB");

            int expect = HeaderSize + w * h * 4 + (w + 31) / 32 * 4 * h;
            if (bytes != expect)
                throw new CurFormatException($"第 {i} 帧长度 {bytes}，按 {w}×{h} 应该正好是 {expect}");

            entries.Add(new CurEntryInfo(w, h, hotX, hotY, bytes, offset));
        }

        return new CurInfo(type, count, entries);
    }

    /// <summary>把 .cur 里的某一个尺寸解码回 Bitmap，用来核对像素有没有写反/写歪。</summary>
    public static Bitmap Decode(string path, int index)
    {
        byte[] raw = File.ReadAllBytes(path);
        var info = Inspect(path);
        if (index < 0 || index >= info.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var e = info.Entries[index];
        var bmp = new Bitmap(e.Width, e.Height, PixelFormat.Format32bppArgb);
        var dst = bmp.LockBits(new Rectangle(0, 0, e.Width, e.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int absStride = Math.Abs(dst.Stride);
            var row = new byte[e.Width * 4];
            for (int y = 0; y < e.Height; y++)
            {
                // 文件里是自下而上的，读回来要翻一次
                Buffer.BlockCopy(raw, e.Offset + HeaderSize + (e.Height - 1 - y) * e.Width * 4, row, 0, row.Length);
                int dstRow = dst.Stride >= 0 ? y : e.Height - 1 - y;
                Marshal.Copy(row, 0, IntPtr.Add(dst.Scan0, dstRow * absStride), row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(dst);
        }

        return bmp;
    }

    private static int ReadU16(byte[] b, int off) => b[off] | (b[off + 1] << 8);

    private static int ReadI32(byte[] b, int off) =>
        b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);

    private static uint ReadU32(byte[] b, int off) => (uint)ReadI32(b, off);
}
