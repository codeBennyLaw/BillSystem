using Microsoft.Win32;

namespace BillSystem.Services;

/// <summary>开机自启（写 HKCU 的 Run 项，只在用户主动勾选时才动）。</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BillSystem";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySet(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) { error = "打不开注册表 Run 项"; return false; }

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) { error = "拿不到程序路径"; return false; }
                // --tray：开机时静默启动，只出托盘和任务栏组件，不弹主窗口
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
