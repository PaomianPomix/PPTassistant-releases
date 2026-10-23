using System.Windows.Threading;
using PPTAssistant.Host;

namespace PPTAssistant.App;

public sealed class DispatcherUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string>? _trace;

    public DispatcherUiDispatcher(Dispatcher dispatcher, Action<string>? trace = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _trace = trace;
    }

    /// <inheritdoc />
    public bool IsCurrent => _dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            // 投递失败的情形：Dispatcher 已在关闭流程中（ShutdownStarted / HasShutdownFinished）。
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[ui] 投递到 UI 线程失败：{ex.GetType().Name} {ex.Message}");
        }
    }
}
