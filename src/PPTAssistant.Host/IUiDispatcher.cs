namespace PPTAssistant.Host;

/// <summary>
/// 把工作送回「发起 COM 调用的那条线程」执行的最小抽象。
/// </summary>
/// <remarks>
/// <para><b>为什么挂接层需要它</b>：PowerPoint / WPS 的自动化对象是 <b>STA</b> 组件。
/// 若在计时器线程（线程池 = MTA）上从 ROT 取对象，拿到的是 MTA 侧的代理，
/// 之后所有 <c>dynamic</c> 调用都要跨公寓封送；更糟的是事件回调线程与对象所属公寓不一致，
/// 让「谁在什么时候释放 COM 引用」变得不可推理。工程上最稳的做法只有一条：
/// <b>COM 的生命周期操作全部发生在同一条 STA 线程上</b>（本案即 WPF UI 线程）。</para>
/// <para><b>为什么是接口而不是 <c>Dispatcher</c></b>：挂接层不引用 WPF（它是个普通类库，
/// 控制台探针也要用）。实现放在 App 层（<c>DispatcherUiDispatcher</c>），
/// 测试与无 UI 场景用 <see cref="InlineUiDispatcher"/>。</para>
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>当前线程是否就是目标线程（是则可以直接执行，省一次投递）。</summary>
    bool IsCurrent { get; }

    /// <summary>异步投递到目标线程。实现必须保证：投递失败不抛出（记日志即可）。</summary>
    void Post(Action action);
}

/// <summary>
/// 就地执行的 <see cref="IUiDispatcher"/>：<see cref="Post"/> 直接同步调用。
/// </summary>
/// <remarks>
/// 用途：① 单元测试（无 UI 线程）；② 控制台探针（主线程就是 STA 且自己驱动循环，
/// 不需要真正的 Dispatcher）。**不要**在多线程环境里用它替代真正的 Dispatcher——
/// 那就退化成「谁调用谁执行」，正是本抽象要避免的情形。
/// </remarks>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    /// <summary>共享实例（无状态）。</summary>
    public static readonly InlineUiDispatcher Instance = new();

    private InlineUiDispatcher()
    {
    }

    /// <inheritdoc />
    public bool IsCurrent => true;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
