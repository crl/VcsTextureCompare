using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VcsTextureCompare.Cli;

namespace VcsTextureCompare;

/// <summary>
/// 应用入口：解析启动参数（独立模式 / TortoiseSVN Diff）后打开主窗口。
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = StartupArgs.Parse(e.Args);
        var window = new MainWindow(args);
        window.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show($"未处理异常：{e.Exception.Message}", "SVN 贴图对比");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteCrashLog(ex);
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "VcsTextureCompare-crash.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n\n", Encoding.UTF8);
        }
        catch
        {
            // 写日志失败时忽略，避免二次崩溃
        }
    }
}
