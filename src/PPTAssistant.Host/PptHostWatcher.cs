using System.Diagnostics;

namespace PPTAssistant.Host;

public sealed class PptHostWatcher : IDisposable
{
    private static readonly string[] HostProcessNames = ["POWERPNT", "wpp"];

    /// <summary>同类挂接失败日志的最小间隔（秒）。</summary>
    private const int AttachFailureLogIntervalSeconds = 30;

    private readonly Action<string>? _trace;
    private readonly TimeSpan _pollInterval;
    private readonly IUiDispatcher? _ui;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;
    private bool _hostProcessSeen;
    private int _pollPending;
    private DateTime _lastAttachFailureLogUtc = DateTime.MinValue;

    /// <param name="pollInterval">轮询间隔，默认 1 秒。</param>
    /// <param name="trace">日志回调（可为 null）。</param>
    /// <param name="ui">UI/STA 线程投递器；null = 就地执行（仅用于控制台探针，见类注释）。</param>
    public PptHostWatcher(TimeSpan? pollInterval = null, Action<string>? trace = null, IUiDispatcher? ui = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _trace = trace;
        _ui = ui;
    }

    /// <summary>当前已挂接的宿主（未挂接为 null）。</summary>
    public PptHost? Host { get; private set; }

    /// <summary>是否处于已挂接且存活状态。</summary>
    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return Host is { IsAlive: true };
            }
        }
    }

    /// <summary>挂接成功（换成新宿主实例时也会触发）。</summary>
    public event EventHandler<PptHost>? HostAttached;

    /// <summary>宿主退出 / 掉线，旧实例已释放。</summary>
    public event EventHandler? HostLost;

    /// <summary>开始监视（幂等）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, _pollInterval);
        }
    }

    /// <summary>停止监视（不释放已挂接的宿主）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// 立即尝试挂接一次（例如从设置页手动触发）。
    /// </summary>
    /// <returns>调用返回时是否处于已挂接状态。</returns>
    /// <remarks>注入 <see cref="IUiDispatcher"/> 时本方法**必须在 UI 线程调用**；
    /// 若不在 UI 线程则改为投递并返回当前状态（不阻塞等待）。</remarks>
    public bool TryAttachNow()
    {
        if (_disposed)
        {
            return false;
        }

        if (_ui is { IsCurrent: false } ui)
        {
            ui.Post(AttachNowCore);
            return IsAttached;
        }

        AttachNowCore();
        return IsAttached;
    }

    public void Dispose()
    {
        bool alreadyDisposed;
        lock (_gate)
        {
            alreadyDisposed = _disposed;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        if (alreadyDisposed)
        {
            return;
        }

        if (_ui is { IsCurrent: false } ui)
        {
            // 释放 COM 引用也要回到 STA 线程做（见类注释）。
            ui.Post(() =>
            {
                lock (_gate)
                {
                    DropHost();
                }
            });
            return;
        }

        lock (_gate)
        {
            DropHost();
        }
    }

    /// <summary>宿主进程扫描结果（必须区分「确实没有」与「扫不出来」）。</summary>
    private enum HostProcessScan
    {
        /// <summary>扫描失败：不掌握事实，绝不能据此判定宿主已死。</summary>
        Unknown = 0,

        /// <summary>确实有宿主进程在跑。</summary>
        Running = 1,

        /// <summary>扫描成功且一个宿主进程都没有。</summary>
        NotRunning = 2,
    }

    private void Poll()
    {
        // 计时器回调：绝不在这里发起 COM 调用（MTA）。只判断进程在不在。
        if (_disposed)
        {
            return;
        }

        var scan = ScanHostProcesses();
        var running = scan == HostProcessScan.Running;
        if (scan != HostProcessScan.Unknown && running != _hostProcessSeen)
        {
            _hostProcessSeen = running;
            _trace?.Invoke(running
                ? "[watcher] 检测到宿主进程（POWERPNT/wpp）"
                : "[watcher] 宿主进程已退出");
        }

        if (scan == HostProcessScan.Unknown)
        {
            // 枚举失败（权限/竞态）：不掌握事实就不动状态机，更不许误杀存活宿主。
            return;
        }

        if (!running)
        {
            // 进程都没了就没什么可挂接的；但**已挂接的实例必须收掉**（否则会一直留着一个死引用，
            // 而且 IsAlive 每次轮询都要去问一次不存在的进程）。
            if (HasHost)
            {
                PostOrRun(() => PollCore(hostProcessRunning: false));
            }

            return;
        }

        PostOrRun(() => PollCore(hostProcessRunning: true));
    }

    /// <summary>是否已持有宿主实例（**不触发 COM 存活判定**，见 <see cref="Poll"/>）。</summary>
    private bool HasHost
    {
        get
        {
            lock (_gate)
            {
                return Host is not null;
            }
        }
    }

    private void PostOrRun(Action action)
    {
        var ui = _ui;
        if (ui is null || ui.IsCurrent)
        {
            action();
            return;
        }

        // 合流：UI 线程繁忙时，最多只排一个待处理的轮询动作。
        if (Interlocked.Exchange(ref _pollPending, 1) == 1)
        {
            return;
        }

        ui.Post(() =>
        {
            Interlocked.Exchange(ref _pollPending, 0);
            action();
        });
    }

    /// <summary>轮询主体：只在 STA/UI 线程执行（注入 dispatcher 时）。</summary>
    /// <param name="hostProcessRunning">本次轮询时宿主进程是否存在（进程不在就不必去碰 ROT）。</param>
    private void PollCore(bool hostProcessRunning)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (Host is not null && (!hostProcessRunning || !Host.IsAlive))
                {
                    // ★ 两个条件缺一不可：
                    //   ① hostProcessRunning == false 是**权威信号**（进程扫描成功且一个都没有），
                    //      必须优先于 IsAlive —— 因为身份未解析（pid=0）时 IsAlive 刻意返回 true
                    //      （避免把 pid 0 的 System Idle Process 当宿主），只靠 IsAlive 会留下死引用：
                    //      会话永远停在 Attached、托盘一直显示「已挂接」，而且因为 Host 非空会提前
                    //      return，老师重新打开 PowerPoint 也再不会重挂。
                    //   ② IsAlive 负责「进程在、但已不是原来那个实例」的情形（换宿主）。
                    _trace?.Invoke(hostProcessRunning
                        ? "[watcher] 宿主实例已失效，放弃旧实例"
                        : "[watcher] 宿主进程已退出，放弃旧实例");
                    DropHost();
                    HostLost?.Invoke(this, EventArgs.Empty);
                }

                if (Host is not null)
                {

                    if (!Host.Info.IsResolved)
                    {
                        Host.RefreshIdentity();
                    }

                    return;
                }

                if (!hostProcessRunning)
                {
                    return;
                }

                AttachCore();
            }
        }
        catch (Exception ex)
        {
            // 后台定时器里的异常会终止整个进程（Timer 回调未捕获异常），必须兜住。
            _trace?.Invoke($"[watcher] 轮询异常：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void AttachNowCore()
    {
        lock (_gate)
        {
            if (_disposed || Host is { IsAlive: true })
            {
                return;
            }

            DropHost();
            AttachCore();
        }
    }

    private bool AttachCore()
    {
        if (!PptHost.TryAttach(_trace, out var host, out var error) || host is null)
        {
            LogAttachFailure(error);
            return false;
        }

        Host = host;
        _trace?.Invoke($"[watcher] 已挂接宿主：{host.Info}");
        HostAttached?.Invoke(this, host);
        return true;
    }

    /// <summary>挂接失败按「首次 + 每 30 秒一次」打印，防止日志刷屏。</summary>
    private void LogAttachFailure(string? error)
    {
        var now = DateTime.UtcNow;
        if (now - _lastAttachFailureLogUtc < TimeSpan.FromSeconds(AttachFailureLogIntervalSeconds))
        {
            return;
        }

        _lastAttachFailureLogUtc = now;
        _trace?.Invoke($"[watcher] 挂接未成功（后续同类失败 30 秒内不再重复）：{error}");
    }

    private void DropHost()
    {
        var old = Host;
        Host = null;
        if (old is not null)
        {
            try
            {
                old.Dispose();
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"[watcher] 释放宿主失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 扫描宿主进程。
    /// </summary>
    /// <remarks>
    /// <b>必须区分「确实没有」与「扫不出来」</b>：进程枚举在权限受限或竞态时会抛异常，
    /// 若把异常也当成「没有宿主进程」，就会杀掉一个其实还活着的宿主（并把放映中的浮层收掉）。
    /// 因此只要有一次枚举失败且没找到任何进程，就返回 <see cref="HostProcessScan.Unknown"/>。
    /// </remarks>
    private static HostProcessScan ScanHostProcesses()
    {
        var anyEnumerationSucceeded = false;

        foreach (var name in HostProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
                anyEnumerationSucceeded = true;
            }
            catch (Exception)
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return HostProcessScan.Running;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return anyEnumerationSucceeded ? HostProcessScan.NotRunning : HostProcessScan.Unknown;
    }
}
