using System;
using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace 桌面整理工具;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 注册域全局异常捕获
        AppDomain.CurrentDomain.UnhandledException += (s, ev) => 
            LogCrash("AppDomain", ev.ExceptionObject as Exception);

        // 注册 WPF Dispatcher 线程全局异常捕获
        DispatcherUnhandledException += (s, ev) => {
            LogCrash("Dispatcher", ev.Exception);
            ev.Handled = true; // 尽量防止直接进程退出
        };

        base.OnStartup(e);
    }

    private static void LogCrash(string type, Exception? ex)
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string logPath = Path.Combine(dir, "crash_log.txt");
            string message = $"[{DateTime.Now}] [{type}] CRASH EXCEPTION:\n" +
                             $"Message: {ex?.Message}\n" +
                             $"Stack Trace:\n{ex?.StackTrace}\n" +
                             $"Inner Exception: {ex?.InnerException?.Message}\n" +
                             $"---------------------------------------------\n";
            File.AppendAllText(logPath, message);
        }
        catch { }
    }
}

