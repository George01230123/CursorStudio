using System.Runtime.InteropServices;

namespace CursorStudio.Core;

/// <summary>用到的 Win32 调用都集中在这里。</summary>
internal static class Win32
{
    public const uint SPI_SETCURSORS = 0x0057;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    public const uint IMAGE_CURSOR = 2;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const uint LR_DEFAULTSIZE = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType,
                                           int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadCursorFromFile(string lpFileName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(IntPtr hCursor);

    /// <summary>改完注册表后广播一下，让已打开的窗口立刻换指针，不用重启 explorer。</summary>
    public static bool BroadcastCursorChange()
    {
        return SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    // ------------------------------------------------------------------
    // 直接换当前会话的系统光标
    // ------------------------------------------------------------------

    // SetSystemCursor 用的系统光标槽位编号（OCR_*）
    public const uint OCR_NORMAL = 32512, OCR_IBEAM = 32513, OCR_WAIT = 32514, OCR_CROSS = 32515,
                      OCR_UP = 32516, OCR_SIZENWSE = 32642, OCR_SIZENESW = 32643, OCR_SIZEWE = 32644,
                      OCR_SIZENS = 32645, OCR_SIZEALL = 32646, OCR_NO = 32648, OCR_HAND = 32649,
                      OCR_APPSTARTING = 32650, OCR_HELP = 32651;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    /// <summary>
    /// 把某个系统光标槽位直接换成这个文件里的指针——**当前会话立刻生效**，
    /// 不依赖 SPI_SETCURSORS 那一套广播。
    ///
    /// 为什么要多这一道：Windows 11 25H2 上有反馈说注册表和文件都写对了，
    /// 但当前会话的指针槽位不跟着更新（要注销重登才行）。多这一步就绕开了整个问题，
    /// 顺便也治了"有些已经开着的程序还在用旧指针"。
    ///
    /// 注意 <c>SetSystemCursor</c> **会接管并销毁传入的句柄**，所以每次都得重新 LoadImage 一个，
    /// 不能把同一个句柄用两次。
    /// </summary>
    public static bool SetSessionCursor(string path, uint ocrId)
    {
        if (!File.Exists(path)) return false;

        IntPtr h = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE);
        if (h == IntPtr.Zero) return false;

        if (SetSystemCursor(h, ocrId)) return true;   // 成功后句柄交给系统了，不能自己销毁

        DestroyCursor(h);
        return false;
    }

    /// <summary>
    /// 让 Windows 自己试着加载这个 .cur。返回 false 说明文件格式有问题——
    /// 这是比任何字节级校验都更硬的"系统认不认"的判据。
    /// </summary>
    public static bool CanWindowsLoad(string path, int size)
    {
        IntPtr h = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, size, size, LR_LOADFROMFILE);
        if (h == IntPtr.Zero) return false;
        DestroyCursor(h);
        return true;
    }

    /// <summary>拿到系统能用的光标句柄，用于把控件指针换成自定义图。失败返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr LoadCursor(string path)
    {
        if (!File.Exists(path)) return IntPtr.Zero;
        return LoadCursorFromFile(path);
    }

    /// <summary>
    /// 释放 <see cref="LoadCursor"/> 拿到的句柄。
    /// 必须手动放：<c>new Cursor(IntPtr)</c> 建出来的 Cursor 不接管句柄所有权，
    /// 它的 Dispose 不会去销毁这个 HCURSOR，光靠 Cursor 会漏。
    /// </summary>
    public static void SafeDestroyCursor(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        try { DestroyCursor(h); } catch { /* 句柄可能已被系统回收 */ }
    }
}
