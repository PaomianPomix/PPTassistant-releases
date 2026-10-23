using System.IO;
using Microsoft.Win32;
using PPTAssistant.App;

namespace PPTAssistant.Tests;

public sealed class AutoStartTests
{
    private const string AppPath = @"C:\教学软件\PPT Assistant\PPTAssistant.exe";
    private const string AssemblyPath = @"D:\PPT Assistant\src\bin\PPTAssistant.dll";
    private const string DotnetPath = @"D:\PPT Assistant\tools\dotnet\dotnet.exe";
    private const string HelperPath = @"C:\Users\老师\AppData\Local\PPTAssistant\autostart.vbs";
    private const string ScriptHostPath = @"C:\Windows\System32\wscript.exe";
    private const string Command = "\"C:\\PPTAssistant\\PPTAssistant.exe\" --autostart";

    [Fact]
    public void SilentStartupFlagRequiresAnExactArgumentAndIgnoresCase()
    {
        Assert.False(Program.IsAutoStart([]));
        Assert.False(Program.IsAutoStart(["--autostart=false", "--autostart-extra", "autostart"]));
        Assert.True(Program.IsAutoStart(["--autostart"]));
        Assert.True(Program.IsAutoStart(["--other", "--AUTOSTART"]));
    }

    [Fact]
    public void AppHostQuotesAbsolutePathAndAddsOnlySilentStartupArgument()
    {
        var plan = BuildPlan(AppPath);

        Assert.Equal($"\"{AppPath}\" --autostart", plan.Command);
        Assert.Null(plan.HelperPath);
        Assert.Null(plan.HelperContents);
    }

    [Fact]
    public void DotnetStartupUsesHiddenScriptWithExplicitRepositoryRuntimeAndDll()
    {
        var plan = BuildPlan(DotnetPath);

        Assert.Equal($"\"{ScriptHostPath}\" //B //Nologo \"{HelperPath}\"", plan.Command);
        Assert.Equal(HelperPath, plan.HelperPath);
        Assert.Contains($"shell.Run \"\"\"{DotnetPath}\"\" \"\"{AssemblyPath}\"\" --autostart\", 0, False", plan.HelperContents);
        Assert.DoesNotContain("cmd.exe", plan.HelperContents);
    }

    [Theory]
    [InlineData("PPTAssistant.exe")]
    [InlineData(@"C:PPTAssistant.exe")]
    [InlineData("C:\\test\".exe")]
    [InlineData("C:\\test\r\n.exe")]
    [InlineData(null)]
    public void InvalidOrRelativeExecutableIsRejected(string? executable)
    {
        Assert.Throws<InvalidOperationException>(() => BuildPlan(executable));
    }

    [Fact]
    public void MissingRuntimeIsRejectedBeforeRegistration()
    {
        Assert.Throws<FileNotFoundException>(() => AutoStartService.BuildLaunchPlan(
            DotnetPath, AssemblyPath, HelperPath, ScriptHostPath, path => path != DotnetPath));
    }

    [Theory]
    [InlineData("PPTAssistant.dll")]
    [InlineData("C:\\test\r\n.dll")]
    [InlineData(null)]
    public void DotnetRequiresAbsoluteApplicationDll(string? assemblyPath)
    {
        Assert.Throws<InvalidOperationException>(() => AutoStartService.BuildLaunchPlan(
            DotnetPath, assemblyPath, HelperPath, ScriptHostPath, _ => true));
    }

    [Fact]
    public void DefaultFactoryCannotRegisterTestHost()
    {
        var store = new FakeStore();
        var service = new AutoStartService(store);

        var result = service.SetEnabled(true);

        Assert.False(result.Success);
        Assert.Contains("测试或诊断宿主", result.Error);
        Assert.Equal(0, store.WriteCalls);
        Assert.Null(store.Command);
    }

    [Fact]
    public void ReadAlwaysReflectsStoreInsteadOfRememberedChoice()
    {
        var store = new FakeStore();
        var service = new AutoStartService(store, () => Command);

        Assert.False(service.Read().Enabled);
        store.Command = Command;
        Assert.True(service.Read().Enabled);
        store.Command = null;
        Assert.False(service.Read().Enabled);
    }

    [Fact]
    public void WindowsDisabledItemIsNotReportedAsEnabledAndCanStillBeRemoved()
    {
        var store = new FakeStore { Command = Command, DisabledByWindows = true };
        var service = new AutoStartService(store, () => Command);

        var status = service.Read();
        Assert.False(status.Enabled);
        Assert.Contains("Windows 已禁用", status.Error);

        var enable = service.SetEnabled(true);
        Assert.False(enable.Enabled);
        Assert.False(enable.Success);
        Assert.Contains("Windows 设置", enable.Error);
        Assert.Equal(new AutoStartResult(false), service.SetEnabled(false));
        Assert.True(store.DisabledByWindows);
    }

    [Fact]
    public void EnablingAndDisablingAreIdempotentAndUseExactCommand()
    {
        var store = new FakeStore();
        var service = new AutoStartService(store, () => Command);

        Assert.Equal(new AutoStartResult(true), service.SetEnabled(true));
        Assert.Equal(Command, store.Command);
        Assert.Equal(new AutoStartResult(true), service.SetEnabled(true));
        Assert.Equal(new AutoStartResult(false), service.SetEnabled(false));
        Assert.Equal(new AutoStartResult(false), service.SetEnabled(false));
    }

    [Fact]
    public void DisableDoesNotRequireAValidStartupCommandOrRuntime()
    {
        var store = new FakeStore { Command = Command };
        var service = new AutoStartService(store, () => throw new FileNotFoundException("runtime removed"));

        Assert.Equal(new AutoStartResult(false), service.SetEnabled(false));
    }

    [Fact]
    public void ReadFailureIsVisibleAndNeverReportedAsSuccessfulDisable()
    {
        var store = new FakeStore { ReadError = new UnauthorizedAccessException("read denied") };
        var service = new AutoStartService(store, () => Command);

        var result = service.Read();

        Assert.False(result.Success);
        Assert.Contains("read denied", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteFailureReturnsTheActualUnchangedState(bool initiallyEnabled)
    {
        var store = new FakeStore
        {
            Command = initiallyEnabled ? Command : null,
            WriteError = new UnauthorizedAccessException("write denied"),
        };
        var service = new AutoStartService(store, () => Command);

        var result = service.SetEnabled(!initiallyEnabled);

        Assert.False(result.Success);
        Assert.Equal(initiallyEnabled, result.Enabled);
        Assert.Contains("write denied", result.Error);
    }

    [Fact]
    public void SilentWriteFailureIsDetectedByReadingTheStore()
    {
        var store = new FakeStore { IgnoreWrites = true };
        var service = new AutoStartService(store, () => Command);

        var result = service.SetEnabled(true);

        Assert.False(result.Success);
        Assert.False(result.Enabled);
    }

    [Fact]
    public void HelperWriteFailurePreservesExistingRunRegistration()
    {
        var store = new FakeStore { Command = Command };
        var plan = BuildPlan(DotnetPath);
        var service = new AutoStartService(store, () => AutoStartService.PrepareCommand(
            plan, (_, _) => throw new IOException("script denied")));

        var result = service.SetEnabled(true);

        Assert.False(result.Success);
        Assert.True(result.Enabled);
        Assert.Contains("script denied", result.Error);
        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(Command, store.Command);
    }

    [Fact]
    public void HelperIsPreparedBeforeItsRunRegistration()
    {
        var store = new FakeStore();
        var plan = BuildPlan(DotnetPath);
        var writes = new List<(string Path, string Contents)>();
        var service = new AutoStartService(store, () => AutoStartService.PrepareCommand(plan, (path, content) =>
        {
            Assert.Null(store.Command);
            writes.Add((path, content));
        }));

        Assert.True(service.SetEnabled(true).Success);
        Assert.Equal((HelperPath, plan.HelperContents), Assert.Single(writes));
        Assert.Equal(plan.Command, store.Command);
    }

    [Fact]
    public void RegistryStoreOnlyTouchesApplicationValueInIsolatedTestKey()
    {
        var path = @"Software\PPTAssistant.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("AnotherApplication", "keep me");
            }

            var service = new AutoStartService(new RegistryAutoStartStore(path, path + @"\StartupApproved"), () => Command);
            Assert.False(service.Read().Enabled);
            Assert.True(service.SetEnabled(true).Enabled);
            using (var key = Registry.CurrentUser.OpenSubKey(path)!)
            {
                Assert.Equal(Command, key.GetValue("PPTAssistant"));
                Assert.Equal(RegistryValueKind.String, key.GetValueKind("PPTAssistant"));
            }

            Assert.Equal(new AutoStartResult(false), service.SetEnabled(false));
            using var remaining = Registry.CurrentUser.OpenSubKey(path)!;
            Assert.Null(remaining.GetValue("PPTAssistant"));
            Assert.Equal("keep me", remaining.GetValue("AnotherApplication"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    [InlineData(99, false)]
    public void WindowsStartupApprovalIsReadWithoutChangingItsValue(int state, bool expectedEnabled)
    {
        var path = @"Software\PPTAssistant.Tests\" + Guid.NewGuid().ToString("N");
        var approvalPath = path + @"\StartupApproved";
        var bytes = new byte[12];
        BitConverter.GetBytes(state).CopyTo(bytes, 0);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("PPTAssistant", Command);
            }

            using (var approval = Registry.CurrentUser.CreateSubKey(approvalPath))
            {
                approval.SetValue("PPTAssistant", bytes, RegistryValueKind.Binary);
            }

            var service = new AutoStartService(new RegistryAutoStartStore(path, approvalPath), () => Command);
            var status = service.Read();
            Assert.Equal(expectedEnabled, status.Enabled);
            Assert.Equal(expectedEnabled, status.Success);

            var enable = service.SetEnabled(true);
            Assert.Equal(expectedEnabled, enable.Enabled);
            Assert.Equal(expectedEnabled, enable.Success);

            Assert.Equal(new AutoStartResult(false), service.SetEnabled(false));
            using var remainingApproval = Registry.CurrentUser.OpenSubKey(approvalPath)!;
            Assert.Equal(bytes, Assert.IsType<byte[]>(remainingApproval.GetValue("PPTAssistant")));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedRegistryValuesReturnAnErrorAndCanStillBeDisabled(bool corruptApproval)
    {
        var path = @"Software\PPTAssistant.Tests\" + Guid.NewGuid().ToString("N");
        var approvalPath = path + @"\StartupApproved";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                if (corruptApproval) key.SetValue("PPTAssistant", Command);
                else key.SetValue("PPTAssistant", 1, RegistryValueKind.DWord);
            }

            using (var approval = Registry.CurrentUser.CreateSubKey(approvalPath))
            {
                approval.SetValue("PPTAssistant", new byte[] { 2 }, RegistryValueKind.Binary);
            }

            var service = new AutoStartService(new RegistryAutoStartStore(path, approvalPath), () => Command);
            var status = service.Read();
            Assert.False(status.Enabled);
            Assert.False(status.Success);
            Assert.Contains("无法读取", status.Error);
            Assert.Equal(new AutoStartResult(false), service.SetEnabled(false));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    private static AutoStartLaunchPlan BuildPlan(string? executable) =>
        AutoStartService.BuildLaunchPlan(executable, AssemblyPath, HelperPath, ScriptHostPath, _ => true);

    private sealed class FakeStore : IAutoStartStore
    {
        public string? Command { get; set; }
        public Exception? ReadError { get; init; }
        public Exception? WriteError { get; init; }
        public bool IgnoreWrites { get; init; }
        public bool DisabledByWindows { get; init; }
        public int WriteCalls { get; private set; }

        public string? ReadCommand()
        {
            if (ReadError is not null) throw ReadError;
            return Command;
        }

        public bool IsDisabledByWindows() => DisabledByWindows;

        public void WriteCommand(string command)
        {
            WriteCalls++;
            if (WriteError is not null) throw WriteError;
            if (!IgnoreWrites) Command = command;
        }

        public void DeleteCommand()
        {
            if (WriteError is not null) throw WriteError;
            if (!IgnoreWrites) Command = null;
        }
    }
}
