using System.Runtime.InteropServices;
using System.Text;
using CursorStudio.UI;

namespace CursorStudio;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run(Environment.CurrentDirectory);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// WinExe 默认不带控制台，--selftest 的输出会掉进虚空。
    /// 这里贴到调用方的控制台上，再重开一个 StreamWriter 接管 Console.Out。
    /// </summary>
    internal static void AttachToParentConsole()
    {
        const int ATTACH_PARENT_PROCESS = -1;
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;

        var stdout = Console.OpenStandardOutput();
        Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
        var stderr = Console.OpenStandardError();
        Console.SetError(new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true });
    }
}
