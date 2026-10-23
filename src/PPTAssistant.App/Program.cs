using System.Windows;
using System.Windows.Threading;

namespace PPTAssistant.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // ① 单实例闸门：抢不到就立刻退出，不做任何有副作用的初始化
        //    （不建可见窗口、不读配置、不碰注册表、不碰 COM）。
        if (!SingleInstance.TryAcquire(out var instance, out var mutexError))
        {
            // 唯一允许的例外是日志：没有控制台也没有窗口，若连一行日志都不写，
            // 用户双击图标「没反应」时将无从判断。日志只追加一个文本文件，无全局副作用。
            AppLog.Initialize();
            AppLog.Info($"已有实例在运行，本次启动忽略（{mutexError}）");
            return 0;
        }

        using (instance)
        {
            AppLog.Initialize();
            AppLog.Info("======== PPTAssistant 启动 ========");
            AppLog.Info($"版本={typeof(Program).Assembly.GetName().Version} 参数=[{string.Join(' ', args)}]");
            if (!string.IsNullOrEmpty(mutexError))
            {
                AppLog.Warn($"单实例闸门降级继续运行：{mutexError}");
            }

            var config = ConfigStore.Load();

            var application = new Application
            {
                // 待机时没有任何窗口，必须显式关闭，否则 WPF 会立即退出。
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            ApplicationIcon.SetWindowDefault();

            var host = new AppHost(application, config, IsAutoStart(args));
            HookGlobalExceptionHandlers(application);

            application.Startup += (_, _) =>
            {
                try
                {
                    host.Start();
                }
                catch (Exception ex)
                {
                    AppLog.Fatal("启动失败", ex);
                    application.Shutdown(1);
                }
            };

            application.Exit += (_, _) => host.Dispose();

            try
            {
                return application.Run();
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    internal static bool IsAutoStart(IEnumerable<string> args)
        => args.Any(arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));

    private static void HookGlobalExceptionHandlers(Application application)
    {
        application.DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("UI 线程未处理异常（已吞掉，程序继续运行）", e.Exception);

            // 界面回调出错不应中断课堂。真正的兜底是：日志 + 保持存活。
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.Fatal($"非 UI 线程未处理异常（进程{(e.IsTerminating ? "即将退出" : "继续运行")}）", ex);
            }
            else
            {
                AppLog.Fatal(
                    $"非 UI 线程未处理异常（进程{(e.IsTerminating ? "即将退出" : "继续运行")}）：{e.ExceptionObject}",
                    new InvalidOperationException("非异常对象"));
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }
}
