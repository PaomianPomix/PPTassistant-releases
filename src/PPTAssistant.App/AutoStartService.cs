using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace PPTAssistant.App;

/// <summary>当前用户的可选自启。注册表是状态来源，配置文件不代替系统状态。</summary>
public sealed class AutoStartService
{
    internal const string ValueName = "PPTAssistant";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupApprovalKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly IAutoStartStore _store;
    private readonly Func<string> _commandFactory;

    public AutoStartService()
        : this(new RegistryAutoStartStore(), CreateCurrentCommand)
    {
    }

    internal AutoStartService(IAutoStartStore store, Func<string>? commandFactory = null)
    {
        _store = store;
        _commandFactory = commandFactory ?? CreateCurrentCommand;
    }

    public AutoStartResult Read()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_store.ReadCommand()))
            {
                return new AutoStartResult(false);
            }

            return _store.IsDisabledByWindows()
                ? new AutoStartResult(false, "Windows 已禁用此启动项，请在 Windows 设置 → 应用 → 启动中启用 PPTAssistant。")
                : new AutoStartResult(true);
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            return new AutoStartResult(false, $"无法读取开机自启状态：{ex.Message}");
        }
    }

    public AutoStartResult SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                // 构建命令/写入隐藏启动脚本失败时，不改动已有 Run 值。
                var command = _commandFactory();
                if (string.IsNullOrWhiteSpace(command) || command.Length > 260)
                {
                    throw new InvalidOperationException("自启命令为空或超过 Windows Run 项的 260 字符限制。");
                }

                _store.WriteCommand(command);
            }
            else
            {
                _store.DeleteCommand();
            }

            var actual = Read();
            if (!actual.Success || actual.Enabled == enabled)
            {
                return actual;
            }

            return actual with { Error = "开机自启状态未能保存，请检查当前用户的注册表权限。" };
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            // 写入失败时再次读取真实状态，使设置开关能够回到系统当前值。
            var actual = Read();
            var error = $"无法{(enabled ? "开启" : "关闭")}开机自启：{ex.Message}";
            return actual with { Error = actual.Error is null ? error : $"{error}；{actual.Error}" };
        }
    }

    private static bool IsExpectedError(Exception ex) => ex is
        IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or
        InvalidOperationException or ArgumentException or NotSupportedException;

    private static string CreateCurrentCommand()
    {
        var applicationAssembly = typeof(AutoStartService).Assembly;
        if (Assembly.GetEntryAssembly() != applicationAssembly)
        {
            throw new InvalidOperationException("请从 PPTAssistant 应用本身开启自启，不能注册测试或诊断宿主。");
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var plan = BuildLaunchPlan(
            Environment.ProcessPath,
            applicationAssembly.Location,
            Path.Combine(localData, "PPTAssistant", "autostart.vbs"),
            Path.Combine(systemDirectory, "wscript.exe"));

        return PrepareCommand(plan, WriteHelper);
    }

    /// <summary>
    /// 普通发布 apphost 可直接自启；dotnet 启动的开发副本需隐藏控制台，并保留仓库运行时路径。
    /// 不沿用当前命令行，避免把诊断参数或相对路径带到 Windows 登录时。
    /// </summary>
    internal static AutoStartLaunchPlan BuildLaunchPlan(
        string? processPath,
        string? assemblyPath,
        string helperPath,
        string scriptHostPath,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var executable = RequireAbsoluteFile(processPath, "应用程序", fileExists);
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("无法识别应用程序的 Windows 启动路径。");
        }

        if (!string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new AutoStartLaunchPlan($"\"{executable}\" --autostart");
        }

        var application = RequireAbsoluteFile(assemblyPath, "应用 DLL", fileExists);
        if (!string.Equals(Path.GetExtension(application), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("dotnet 自启需要应用 DLL 的绝对路径。");
        }

        var helper = RequireAbsolutePath(helperPath, "隐藏启动脚本");
        var scriptHost = RequireAbsoluteFile(scriptHostPath, "Windows 脚本宿主", fileExists);
        var applicationCommand = $"\"{executable}\" \"{application}\" --autostart";
        var script = "Option Explicit\r\n"
            + "Dim shell\r\n"
            + "Set shell = CreateObject(\"WScript.Shell\")\r\n"
            + $"shell.Run \"{applicationCommand.Replace("\"", "\"\"")}\", 0, False\r\n";

        return new AutoStartLaunchPlan($"\"{scriptHost}\" //B //Nologo \"{helper}\"", helper, script);
    }

    internal static string PrepareCommand(AutoStartLaunchPlan plan, Action<string, string> writeHelper)
    {
        if (plan.Command.Length > 260)
        {
            throw new InvalidOperationException("自启路径过长，超过 Windows Run 项的 260 字符限制。");
        }

        if (plan.HelperPath is not null && plan.HelperContents is not null)
        {
            writeHelper(plan.HelperPath, plan.HelperContents);
        }

        return plan.Command;
    }

    private static string RequireAbsoluteFile(string? path, string label, Func<string, bool> fileExists)
    {
        var absolutePath = RequireAbsolutePath(path, label);
        if (!fileExists(absolutePath))
        {
            throw new FileNotFoundException($"找不到{label}：{absolutePath}");
        }

        return absolutePath;
    }

    private static string RequireAbsolutePath(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\"', '\r', '\n', '\0']) >= 0
            || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{label}必须是有效的绝对路径。");
        }

        return Path.GetFullPath(path);
    }

    private static void WriteHelper(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // WScript 按 UTF-16 BOM 识别中文用户目录等非 ANSI 路径。
            File.WriteAllText(temporaryPath, contents, Encoding.Unicode);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不覆盖原来的写入错误；从不删除正式脚本或其他文件。
            }
        }
    }
}

public sealed record AutoStartResult(bool Enabled, string? Error = null)
{
    public bool Success => Error is null;
}

internal sealed record AutoStartLaunchPlan(string Command, string? HelperPath = null, string? HelperContents = null);

internal interface IAutoStartStore
{
    string? ReadCommand();
    bool IsDisabledByWindows();
    void WriteCommand(string command);
    void DeleteCommand();
}

internal sealed class RegistryAutoStartStore(
    string keyPath = AutoStartService.RunKeyPath,
    string startupApprovalKeyPath = AutoStartService.StartupApprovalKeyPath) : IAutoStartStore
{
    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        var value = key?.GetValue(AutoStartService.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => null,
            string command => command,
            _ => throw new InvalidDataException("PPTAssistant 自启项不是有效的字符串。"),
        };
    }

    public bool IsDisabledByWindows()
    {
        // Task Manager 可以保留 Run 命令并单独禁用它。这里只读其已知状态，
        // 不写入未文档化的 StartupApproved 格式，也不覆盖用户在 Windows 中的选择。
        using var key = Registry.CurrentUser.OpenSubKey(startupApprovalKeyPath, writable: false);
        var value = key?.GetValue(AutoStartService.ValueName);
        if (value is null)
        {
            return false;
        }

        if (value is byte[] bytes && bytes.Length >= 12)
        {
            switch (BitConverter.ToUInt32(bytes, 0))
            {
                case 2:
                case 6:
                    return false;
                case 3:
                case 7:
                    return true;
            }
        }

        throw new InvalidDataException("无法识别 Windows 启动项状态，请在 Windows 设置 → 应用 → 启动中检查 PPTAssistant。");
    }

    public void WriteCommand(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new IOException("无法打开当前用户的自启注册表项。");
        key.SetValue(AutoStartService.ValueName, command, RegistryValueKind.String);
    }

    public void DeleteCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(AutoStartService.ValueName, throwOnMissingValue: false);
    }
}
