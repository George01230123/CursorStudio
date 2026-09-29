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
