using BillSystem.Models;
using BillSystem.Services;
using BillSystem.UI;

namespace BillSystem;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        int shot = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (shot >= 0)
        {
            ApplicationConfiguration.Initialize();
            string dir = shot + 1 < args.Length && !args[shot + 1].StartsWith('-')
                ? args[shot + 1]
                : Path.Combine(Path.GetTempPath(), "billsystem-shots");
            Environment.ExitCode = DevShot.Run(dir);
            return;
        }

        using var mutex = new Mutex(true, @"Local\BillSystem.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("宿舍电费助手已经在运行了（看任务栏左侧或托盘图标）。", "宿舍电费助手",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // 让托盘气泡以"宿舍电费助手"的身份进 Windows 通知中心
        Interop.Win32.SetAppId("BillSystem.WyuElectricity");

        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);

        bool silent = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayContext(silent));
    }

    private static void Crash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            File.AppendAllText(Path.Combine(AppConfig.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 记不下来就算了
        }

        MessageBox.Show($"出错了：{ex.Message}\n\n详细信息写到了 {AppConfig.DataDir}\\error.log",
            "宿舍电费助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
