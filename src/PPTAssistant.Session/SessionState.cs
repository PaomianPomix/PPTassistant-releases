namespace PPTAssistant.Session;

public enum SessionState
{
    /// <summary>阶段 1：已启动，尚未挂接宿主（待机、完全隐形）。</summary>
    Idle = 0,

    /// <summary>阶段 2/3：已挂接宿主，没有放映（完全隐形：不建窗口、不消费输入）。</summary>
    Attached = 1,

    /// <summary>阶段 4/5：门控通过，覆盖层与工具栏就位，正在授课循环。</summary>
    Presenting = 2,
}
