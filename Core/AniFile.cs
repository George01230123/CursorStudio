using System.Drawing;
using System.Text;

namespace CursorStudio.Core;

/// <summary><c>anih</c> 块里的信息。</summary>
public sealed record AniInfo(
    int FrameCount,
    int Steps,
    int Width,
    int Height,
    int BitCount,
    int Planes,
    int FrameDelayMs,
    int Attributes,
    int IconCount)
{
    /// <summary>含 AF_SEQUENCE：帧的播放顺序由 <c>seq</c> 块单独给，不是 0,1,2…</summary>
    public bool HasSequence => (Attributes & AniFile.AF_SEQUENCE) != 0;
}

/// <summary>从 .ani 里解出来的一帧。图归调用方，用完 Dispose 掉。</summary>
public sealed record AniFrame(Bitmap Image, int HotX, int HotY, int DelayMs) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// .ani（动画指针）读写。
///
/// 格式说明（对着系统自带的 aero_busy.ani 一个字节一个字节核过）：
///
///   RIFF &lt;size&gt; "ACON"
///     "anih" 36 &lt;9 个 DWORD&gt;
///     "LIST" &lt;size&gt; "fram"
///       "icon" &lt;size&gt; &lt;一整个 .cur 文件&gt;   ← 每帧都是一个完整的 .cur，不是裸 DIB
///       "icon" ...
///
///   九个 DWORD 依次是：cbSize / nFrames / nSteps / iWidth / iHeight /
///   iBitCount / nPlanes / iDispRate / bfAttributes。
///
/// 几个从 aero_busy.ani 上实测到的关键点：
///
/// 1. **每帧就是一个完整的 .cur**，里面照样能带多个尺寸。系统自带的那个是
///    18 帧 × 每帧 64/48/32 三个尺寸，热点按尺寸等比缩放（32,32 / 24,24 / 16,16）。
///    所以本程序不用为动画另做一套渲染——每帧走原来的 CurFile.BuildBytes 就行。
/// 2. **iDispRate 的单位是 1/60 秒**（jiffies），实测值是 3，也就是 50ms 一帧、20fps。
/// 3. 系统自带的文件里 **iWidth / iHeight 填的是 0**，也没有 rate / seq 块。
///    0 表示"按帧自己的尺寸来"。本程序填真实尺寸，语义上更明确，自检里会用
///    Windows 的 LoadImage 真加载一遍来确认系统认这个写法。
/// 4. 块长度是奇数时要补一个字节凑偶，这是 RIFF 的通用规矩，漏了后面所有块都会错位。
/// </summary>
public static class AniFile
{
    public const int AF_ICON = 0x1;
    public const int AF_SEQUENCE = 0x2;

    /// <summary>
    /// 帧数上限。一帧是四个尺寸的完整 .cur（约 68KB），60 帧就是 4MB 上下，
    /// 再往上文件太大、系统加载也慢，收益很小。
    /// </summary>
    public const int MaxFrames = 60;

    /// <summary>没有指定帧间隔时的默认值：50ms，和系统自带 aero_busy.ani 的 iDispRate=3 一致。</summary>
    public const int DefaultDelayMs = 50;

    public const int MinDelayMs = 20;
    public const int MaxDelayMs = 1000;

    /// <summary>把若干"已经是完整 .cur 字节"的帧写成一个 .ani。</summary>
    public static void Write(string path, IReadOnlyList<byte[]> curFrames, int size, int frameDelayMs)
    {
        if (curFrames.Count == 0)
            throw new ArgumentException("至少要有一帧", nameof(curFrames));
        if (curFrames.Count > MaxFrames)
            throw new ArgumentException($"一个 .ani 最多 {MaxFrames} 帧", nameof(curFrames));
        if (size is <= 0 or > 256)
            throw new ArgumentException($"尺寸 {size} 超出 1..256 的范围", nameof(size));

        int rate = DelayToRate(frameDelayMs);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            // ---- anih ----
            w.Write(ChunkId("anih"));
            w.Write(36);                    // 块长度：9 个 DWORD
            w.Write(36);                    // cbSize
            w.Write(curFrames.Count);       // nFrames
            w.Write(curFrames.Count);       // nSteps：没有 seq 块时就是帧数
            w.Write(size);                  // iWidth
            w.Write(size);                  // iHeight
            w.Write(32);                    // iBitCount
            w.Write(1);                     // nPlanes
            w.Write(rate);                  // iDispRate（1/60 秒）
            w.Write(AF_ICON);               // bfAttributes：帧是以整个 .cur/图标的形式存的

            // ---- LIST fram ----
            int listSize = 4;               // "fram" 这四个字节也算在 LIST 的长度里
            foreach (var f in curFrames) listSize += 8 + f.Length + (f.Length % 2);

            w.Write(ChunkId("LIST"));
            w.Write(listSize);
            w.Write(ChunkId("fram"));

            foreach (var f in curFrames)
            {
                w.Write(ChunkId("icon"));
                w.Write(f.Length);
                w.Write(f);
                if (f.Length % 2 == 1) w.Write((byte)0);   // RIFF 要求块凑偶
            }
        }

        byte[] body = ms.ToArray();

        using var outMs = new MemoryStream();
        using (var w = new BinaryWriter(outMs, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(ChunkId("RIFF"));
            w.Write(4 + body.Length);       // 后面跟的字节数：ACON + body
            w.Write(ChunkId("ACON"));
            w.Write(body);
        }

        byte[] bytes = outMs.ToArray();

        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

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

    /// <summary>毫秒 → iDispRate（1/60 秒为单位）。至少为 1，填 0 系统会当成"没设"。</summary>
    public static int DelayToRate(int delayMs)
    {
        int ms = Math.Clamp(delayMs, MinDelayMs, MaxDelayMs);
        return Math.Clamp((int)Math.Round(ms * 60.0 / 1000.0), 1, 1000);
    }

    /// <summary>iDispRate → 毫秒。和 <see cref="DelayToRate"/> 是一对，自检里验它俩能对上。</summary>
    public static int RateToDelay(int rate) =>
        rate <= 0 ? DefaultDelayMs : (int)Math.Round(rate * 1000.0 / 60.0);

    // ------------------------------------------------------------------
    // 读
    // ------------------------------------------------------------------

    /// <summary>只读头部信息，不解码像素。读不出来返回 null。</summary>
    public static AniInfo? Inspect(string path)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch { return null; }

        return TryParse(raw, out var p) ? p.ToInfo() : null;
    }

    /// <summary>
    /// 解出所有帧。按 <c>seq</c> 块给的顺序（如果有），每帧的延时就近取 <c>rate</c> 块的值。
    /// 读不出来返回 null。
    /// </summary>
    public static List<AniFrame>? ReadFrames(string path, int targetSize = 32, int maxFrames = 0)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch { return null; }

        if (!TryParse(raw, out var p) || p.Icons.Count == 0) return null;

        int limit = maxFrames > 0 ? Math.Min(maxFrames, p.Icons.Count) : p.Icons.Count;
        var result = new List<AniFrame>(limit);

        for (int step = 0; step < limit; step++)
        {
            int idx = p.Sequence.Count > 0 ? p.Sequence[step % p.Sequence.Count] : step;
            if (idx < 0 || idx >= p.Icons.Count) idx = step % p.Icons.Count;

            var frame = CurFile.ReadFrame(p.Icons[idx], targetSize);
            if (frame is null)
            {
                foreach (var f in result) f.Image.Dispose();
                return null;
            }

            int delay = p.Rates.Count > 0
                ? RateToDelay(p.Rates[Math.Min(idx, p.Rates.Count - 1)])
                : RateToDelay(p.DispRate);

            result.Add(new AniFrame(frame.Image, frame.HotX, frame.HotY, delay));
        }

        return result;
    }

    /// <summary>把当前系统里的 .ani 拷一份出来做参考用（自检里量尺寸档）。</summary>
    public static byte[]? ReadIconBlob(string path, int index)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch { return null; }

        if (!TryParse(raw, out var p)) return null;
        return index >= 0 && index < p.Icons.Count ? p.Icons[index] : null;
    }

    private sealed class Parsed
    {
        public int FrameCount = -1, Steps = -1, Width, Height, BitCount, Planes, DispRate = 3, Attributes = AF_ICON;
        public readonly List<byte[]> Icons = new();
        public readonly List<int> Rates = new();
        public readonly List<int> Sequence = new();

        public AniInfo ToInfo() => new(FrameCount, Steps, Width, Height, BitCount, Planes,
                                        RateToDelay(DispRate), Attributes, Icons.Count);
    }

    private static bool TryParse(byte[] raw, out Parsed p)
    {
        p = new Parsed();
        if (raw.Length < 12) return false;
        if (Id(raw, 0) != "RIFF" || Id(raw, 8) != "ACON") return false;

        // RIFF 头里那个长度字段 Windows 自己根本不看（实测：写 0、写小了、写大了都照常加载），
        // 所以这里也别拿它当边界——有些第三方工具写出来的值就是错的，
        // 认它反而会把好好的文件判成坏文件。下面每个块都按实际文件长度做了越界检查。
        int end = raw.Length;

        bool sawAnih = false;
        int pos = 12;
        while (pos + 8 <= end)
        {
            string id = Id(raw, pos);
            int size = (int)ReadU32(raw, pos + 4);
            int payload = pos + 8;
            if (size < 0 || payload + size > end) break;   // 坏块：就此打住，不当成致命错误

            if (id == "anih" && size >= 36)
            {
                p.FrameCount = (int)ReadU32(raw, payload + 4);
                p.Steps = (int)ReadU32(raw, payload + 8);
                p.Width = (int)ReadU32(raw, payload + 12);
                p.Height = (int)ReadU32(raw, payload + 16);
                p.BitCount = (int)ReadU32(raw, payload + 20);
                p.Planes = (int)ReadU32(raw, payload + 24);
                p.DispRate = (int)ReadU32(raw, payload + 28);
                p.Attributes = (int)ReadU32(raw, payload + 32);
                sawAnih = true;
            }
            else if (id == "rate")
            {
                for (int o = 0; o + 4 <= size; o += 4) p.Rates.Add((int)ReadU32(raw, payload + o));
            }
            else if (id == "seq " || id == "seq")
            {
                for (int o = 0; o + 4 <= size; o += 4) p.Sequence.Add((int)ReadU32(raw, payload + o));
            }
            else if (id == "LIST" && size >= 4 && Id(raw, payload) == "fram")
            {
                int sp = payload + 4;
                int send = payload + size;
                while (sp + 8 <= send)
                {
                    string sid = Id(raw, sp);
                    int ssize = (int)ReadU32(raw, sp + 4);
                    int spayload = sp + 8;
                    if (ssize < 0 || spayload + ssize > send) break;
                    if (sid == "icon" && ssize > 0)
                    {
                        var blob = new byte[ssize];
                        Buffer.BlockCopy(raw, spayload, blob, 0, ssize);
                        p.Icons.Add(blob);
                    }
                    sp = spayload + ssize + (ssize % 2);
                }
            }

            pos = payload + size + (size % 2);
        }

        if (!sawAnih) return false;
        if (p.FrameCount < 0) p.FrameCount = p.Icons.Count;
        if (p.Steps < 0) p.Steps = p.FrameCount;
        return true;
    }

    private static string Id(byte[] b, int off) => Encoding.ASCII.GetString(b, off, 4);

    private static uint ReadU32(byte[] b, int off) =>
        (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static byte[] ChunkId(string s) => Encoding.ASCII.GetBytes(s);
}
