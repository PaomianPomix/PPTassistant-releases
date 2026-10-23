namespace PPTAssistant.App;

public sealed class SingleInstance : IDisposable
{
    /// <summary>互斥体名。</summary>
    public const string MutexName = @"Local\PPTAssistant.SingleInstance";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>尝试成为唯一实例。</summary>
    /// <param name="instance">成功时的实例（失败为 null）。</param>
    /// <param name="error">失败原因（诊断用）。</param>
    /// <returns>是否抢到了唯一实例身份。</returns>
    public static bool TryAcquire(out SingleInstance? instance, out string? error)
    {
        instance = null;
        error = null;

        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch (Exception ex)
        {
            // 互斥体创建失败（权限/内核对象耗尽）时**选择继续运行**：
            // 「多开一份」的代价远小于「工具在教室里起不来」。
            error = $"创建互斥体失败：{ex.GetType().Name} {ex.Message}";
            return true;
        }

        try
        {
            if (mutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                instance = new SingleInstance(mutex);
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例崩溃退出（没有释放互斥体）——我们接管它，属于正常情况。
            instance = new SingleInstance(mutex);
            return true;
        }
        catch (Exception ex)
        {
            error = $"等待互斥体失败：{ex.GetType().Name} {ex.Message}";
            mutex.Dispose();
            return true;
        }

        mutex.Dispose();
        error = "已有实例在运行";
        return false;
    }

    /// <summary>释放唯一实例身份（退出时调用）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception)
        {
            // 未持有 / 已被放弃：无所谓，进程马上退出。
        }

        _mutex.Dispose();
    }
}
