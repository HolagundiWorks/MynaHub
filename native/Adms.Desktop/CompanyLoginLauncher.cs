using System.Diagnostics;

namespace Adms.Desktop;

/// <summary>Starts the floating company login widget (O-Studio.CompanyLogin.exe).</summary>
internal static class CompanyLoginLauncher
{
    internal static bool TryLaunch(out string? error)
    {
        error = null;
        var exe = Path.Combine(AppContext.BaseDirectory, "launcher", "O-Studio.Launcher.exe");
        if (!File.Exists(exe))
            exe = Path.Combine(AppContext.BaseDirectory, "O-Studio.Launcher.exe");
        if (!File.Exists(exe))
            exe = Path.Combine(AppContext.BaseDirectory, "O-Studio.CompanyLogin.exe");
        if (!File.Exists(exe))
        {
            error = "O-Studio.Launcher.exe was not found beside O-Studio.exe. Build native/Adms.LoginWidget and publish it to the launcher folder here.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }
}
