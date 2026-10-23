using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PPTAssistant.Host.Native;

internal static class RotInterop
{
    /// <summary>按 ProgID 从 ROT 取活动对象；取不到时抛 <see cref="COMException"/>。</summary>
    public static object GetActiveObject(string progId)
    {
        if (TryFind(progId, null, out var active))
        {
            return active;
        }

        throw new COMException($"运行对象表里没有 {progId} 的实例（宿主未运行或尚未登记）。");
    }

    /// <summary>带步骤日志的挂接尝试（诊断用）：每一步的 HRESULT 都记下来。</summary>
    public static object? TryGetActiveObject(string progId, ICollection<string> log)
        => TryFind(progId, log, out var active) ? active : null;

    /// <summary>枚举 ROT 里所有登记名（诊断用）。</summary>
    public static IReadOnlyList<string> EnumerateRunningObjectNames()
    {
        var names = new List<string>();
        var rot = GetRot();
        IBindCtx? bindCtx = null;
        try
        {
            bindCtx = CreateBindContext();
            rot.EnumRunning(out var enumMoniker);
            if (enumMoniker is null)
            {
                return names;
            }

            var buffer = new IMoniker[1];
            while (enumMoniker.Next(1, buffer, IntPtr.Zero) == 0)
            {
                var moniker = buffer[0];
                if (moniker is null)
                {
                    break;
                }

                try
                {
                    moniker.GetDisplayName(bindCtx, null!, out var displayName);
                    names.Add(displayName ?? string.Empty);
                }
                catch (COMException ex)
                {
                    names.Add($"<GetDisplayName 失败：{ex.ErrorCode:X8}>");
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                    buffer[0] = null!;
                }
            }

            Marshal.ReleaseComObject(enumMoniker);
        }
        finally
        {
            if (bindCtx is not null)
            {
                Marshal.ReleaseComObject(bindCtx);
            }

            Marshal.ReleaseComObject(rot);
        }

        return names;
    }

    /// <summary>取窗口所属进程 ID。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static bool TryFind(string progId, ICollection<string>? log, out object active)
    {
        active = null!;

        var clsid = TryClsidFromProgId(progId, log);

        if (clsid != Guid.Empty && TryByOleAut(clsid, log, out active))
        {
            return true;
        }

        // ② 类 moniker：与 ① 同源，但走 ROT 直查。
        if (clsid != Guid.Empty && TryByClassMoniker(clsid, log, out active))
        {
            return true;
        }

        if (TryByMkParse(progId, clsid, log, out active))
        {
            return true;
        }

        // ④ 枚举 ROT + 名字匹配。
        return TryByEnumeration(progId, clsid, log, out active);
    }

    private static bool TryByOleAut(Guid clsid, ICollection<string>? log, out object active)
    {
        active = null!;
        try
        {
            // PreserveSig=false：失败直接抛 COMException（含 HRESULT），比手判返回值更少踩坑。
            OleAutGetActiveObject(clsid, IntPtr.Zero, out var obj);
            if (obj is null)
            {
                log?.Add($"① oleaut32!GetActiveObject({clsid:B}) → S_OK 但对象为 null");
                return false;
            }

            log?.Add($"① oleaut32!GetActiveObject({clsid:B}) → 成功（{Describe(obj)}）");
            active = obj;
            return true;
        }
        catch (Exception ex)
        {
            log?.Add($"① oleaut32!GetActiveObject({clsid:B}) → 0x{ex.HResult:X8} {ex.Message}");
            return false;
        }
    }

    private static bool TryByClassMoniker(Guid clsid, ICollection<string>? log, out object active)
    {
        active = null!;
        IRunningObjectTable? rot = null;
        IMoniker? moniker = null;
        try
        {
            rot = GetRot();
            var hr = CreateClassMoniker(ref clsid, out moniker);
            if (hr != 0 || moniker is null)
            {
                log?.Add($"② CreateClassMoniker → 0x{hr:X8}");
                return false;
            }

            hr = rot.GetObject(moniker, out var obj);
            log?.Add($"② ROT.GetObject(类 moniker) → 0x{hr:X8}; obj={(obj is null ? "null" : Describe(obj))}");
            if (hr != 0 || obj is null)
            {
                return false;
            }

            active = obj;
            return true;
        }
        catch (Exception ex)
        {
            log?.Add($"② 类 moniker 路径异常：{ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (moniker is not null)
            {
                Marshal.ReleaseComObject(moniker);
            }

            if (rot is not null)
            {
                Marshal.ReleaseComObject(rot);
            }
        }
    }

    private static bool TryByEnumeration(string progId, Guid clsid, ICollection<string>? log, out object active)
    {
        active = null!;
        var classMonikerName = clsid == Guid.Empty ? null : "!" + clsid.ToString("B").ToUpperInvariant();

        IRunningObjectTable? rot = null;
        IBindCtx? bindCtx = null;
        try
        {
            rot = GetRot();
            bindCtx = CreateBindContext();
            rot.EnumRunning(out var enumMoniker);
            if (enumMoniker is null)
            {
                log?.Add("③ ROT.EnumRunning 返回空枚举器");
                return false;
            }

            var names = new List<string>();
            try
            {
                var buffer = new IMoniker[1];
                while (enumMoniker.Next(1, buffer, IntPtr.Zero) == 0)
                {
                    var moniker = buffer[0];
                    if (moniker is null)
                    {
                        break;
                    }

                    try
                    {
                        string? displayName = null;
                        try
                        {
                            moniker.GetDisplayName(bindCtx, null!, out displayName);
                        }
                        catch (COMException)
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(displayName))
                        {
                            continue;
                        }

                        names.Add(displayName);

                        // 类 moniker 名（"!{CLSID}"）是 Office 的实际登记形式；ProgID 子串匹配
                        // 只在「宿主用名字登记」的少数实现上成立，两者都认。
                        var matches = (classMonikerName is not null &&
                                       string.Equals(displayName, classMonikerName, StringComparison.OrdinalIgnoreCase)) ||
                                      displayName.Contains(progId, StringComparison.OrdinalIgnoreCase);
                        if (!matches)
                        {
                            continue;
                        }

                        log?.Add($"③ 命中候选名：{displayName}");
                        if (rot.GetObject(moniker, out var obj) == 0 && obj is not null)
                        {
                            active = obj;
                            log?.Add($"③ ROT.GetObject(枚举项) → 成功（{Describe(obj)}）");
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                        buffer[0] = null!;
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumMoniker);
            }

            log?.Add($"③ ROT 登记项 {names.Count} 个，未命中：{string.Join(" / ", names)}");
            return false;
        }
        catch (Exception ex)
        {
            log?.Add($"③ 枚举路径异常：{ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (bindCtx is not null)
            {
                Marshal.ReleaseComObject(bindCtx);
            }

            if (rot is not null)
            {
                Marshal.ReleaseComObject(rot);
            }
        }
    }

    private static bool TryByMkParse(string progId, Guid clsid, ICollection<string>? log, out object active)
    {
        active = null!;
        var candidates = new List<string> { progId };
        if (clsid != Guid.Empty)
        {
            candidates.Add("!" + clsid.ToString("B").ToUpperInvariant());
        }

        IRunningObjectTable? rot = null;
        IBindCtx? bindCtx = null;
        try
        {
            rot = GetRot();
            bindCtx = CreateBindContext();
            foreach (var name in candidates)
            {
                try
                {
                    var hr = MkParseDisplayName(bindCtx, name, out _, out var moniker);
                    if (hr != 0 || moniker is null)
                    {
                        log?.Add($"③ MkParseDisplayName(\"{name}\") → 0x{hr:X8}; moniker=null");
                        continue;
                    }

                    try
                    {
                        var getHr = rot.GetObject(moniker, out var obj);
                        log?.Add($"③ ROT.GetObject(\"{name}\") → 0x{getHr:X8}; obj={(obj is null ? "null" : Describe(obj))}");
                        if (getHr == 0 && obj is not null)
                        {
                            active = obj;
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }
                }
                catch (Exception ex)
                {
                    log?.Add($"③ MkParseDisplayName(\"{name}\") 异常：{ex.GetType().Name} {ex.Message}");
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            log?.Add($"③ MkParseDisplayName 路径异常：{ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (bindCtx is not null)
            {
                Marshal.ReleaseComObject(bindCtx);
            }

            if (rot is not null)
            {
                Marshal.ReleaseComObject(rot);
            }
        }
    }

    private static Guid TryClsidFromProgId(string progId, ICollection<string>? log)
    {
        try
        {
            var hr = CLSIDFromProgID(progId, out var clsid);
            if (hr != 0)
            {
                log?.Add($"CLSIDFromProgID(\"{progId}\") → 0x{hr:X8}");
                return Guid.Empty;
            }

            log?.Add($"CLSIDFromProgID(\"{progId}\") → {clsid:B}");
            return clsid;
        }
        catch (Exception ex)
        {
            log?.Add($"CLSIDFromProgID 异常：{ex.Message}");
            return Guid.Empty;
        }
    }

    private static string Describe(object comObject)
    {
        try
        {
            return comObject.GetType().FullName ?? comObject.GetType().Name;
        }
        catch (Exception)
        {
            return "<无法取得类型>";
        }
    }

    private static IRunningObjectTable GetRot()
    {
        var hr = GetRunningObjectTable(0, out var rot);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return rot ?? throw new COMException("GetRunningObjectTable 返回空表。", hr);
    }

    private static IBindCtx CreateBindContext()
    {
        var hr = CreateBindCtx(0, out var bindCtx);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return bindCtx ?? throw new COMException("CreateBindCtx 返回空。", hr);
    }

    [DllImport("oleaut32.dll", EntryPoint = "GetActiveObject", PreserveSig = false)]
    private static extern void OleAutGetActiveObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CreateClassMoniker(ref Guid rclsid, out IMoniker ppmk);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable pprot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int MkParseDisplayName(IBindCtx pbc, string szUserName, out uint pchEaten, out IMoniker ppmk);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);
}
