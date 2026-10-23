using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace PPTAssistant.App;

/// <summary>只响应显式操作；关闭设置页/进入放映时取消正在进行的更新检查。</summary>
internal sealed class MaintenanceSettingsPanel : StackPanel, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _checking;

    internal MaintenanceSettingsPanel(
        AutoStartResult autoStartState,
        Func<bool, AutoStartResult> onAutoStart,
        string currentVersion,
        Func<CancellationToken, Task<UpdateCheckResult>> onCheckUpdate,
        Action<Uri> onOpenRelease,
        Action<string>? onSaved = null)
    {
        var startup = new CheckBox
        {
            IsChecked = autoStartState.Enabled,
            IsEnabled = autoStartState.Success,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
        };
        SettingsUi.Identify(startup, "AutoStartToggle", "开机自启");
        AutomationProperties.SetHelpText(startup, "登录 Windows 后自动启动 PPTAssistant，并驻留在系统托盘。");

        var startupHeading = SettingsUi.Label("登录 Windows 后自动启动", semibold: true);
        startupHeading.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(SettingRow(startupHeading, startup));

        var startupDescription = SettingsUi.Description("开启后安静地驻留在系统托盘，需要时即可开始使用。");
        startupDescription.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(startupDescription);

        var startupStatus = SettingsUi.Description(string.Empty);
        startupStatus.Margin = new Thickness(0, 10, 0, 0);
        SettingsUi.Identify(startupStatus, "AutoStartStatus", "自启状态");
        AutomationProperties.SetLiveSetting(startupStatus, AutomationLiveSetting.Polite);
        Children.Add(startupStatus);

        void ShowStartupState(AutoStartResult result)
        {
            startup.IsChecked = result.Enabled;
            startupStatus.Text = result.Error ?? (result.Enabled ? "已开启 · 下次登录时自动启动" : "已关闭 · 需要时手动启动");
            startupStatus.Foreground = result.Success
                ? result.Enabled ? SettingsUi.Success : SettingsUi.Muted
                : SettingsUi.Error;
        }

        ShowStartupState(autoStartState);
        var savedStartupEnabled = autoStartState.Enabled;
        // Click 只在操作时触发；载入和失败后的勾选回退不会再次写启动项。
        startup.Click += (_, _) =>
        {
            if (_disposed) return;
            try
            {
                var result = onAutoStart(startup.IsChecked == true);
                savedStartupEnabled = result.Enabled;
                ShowStartupState(result);
                if (result.Success) onSaved?.Invoke(result.Enabled ? "已开启开机自启" : "已关闭开机自启");
            }
            catch (Exception ex)
            {
                AppLog.Error("设置开机自启失败", ex);
                ShowStartupState(new AutoStartResult(savedStartupEnabled, "未能保存启动设置，请重试。"));
            }
        };

        var divider = SettingsUi.Divider();
        divider.Margin = new Thickness(0, 28, 0, 24);
        Children.Add(divider);

        var version = new StackPanel();
        version.Children.Add(SettingsUi.Label("版本与更新", semibold: true));
        var versionText = SettingsUi.Description($"当前版本  {currentVersion}");
        versionText.Margin = new Thickness(0, 6, 0, 0);
        SettingsUi.Identify(versionText, "CurrentVersion", "当前版本");
        version.Children.Add(versionText);

        var check = new Button
        {
            Content = "检查更新",
            MinWidth = 108,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
        };
        SettingsUi.Identify(check, "CheckForUpdates", "检查更新");
        AutomationProperties.SetHelpText(check, "连接 GitHub 检查正式版本。有新版本时，由你选择是否打开下载页面。");
        Children.Add(SettingRow(version, check));

        var updateDescription = SettingsUi.Description("仅在你点击时检查更新。有新版本时，可自行前往下载页面。");
        updateDescription.Margin = new Thickness(0, 14, 0, 0);
        Children.Add(updateDescription);

        var status = SettingsUi.Description("尚未检查更新");
        status.Margin = new Thickness(0);
        SettingsUi.Identify(status, "UpdateStatus", "更新状态");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var linkText = SettingsUi.Description(string.Empty);
        linkText.Margin = new Thickness(0, 10, 0, 0);
        linkText.Visibility = Visibility.Collapsed;
        var updateDetails = new StackPanel();
        updateDetails.Children.Add(status);
        updateDetails.Children.Add(linkText);
        Children.Add(new Border
        {
            Background = SettingsUi.Subtle,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 16, 0, 0),
            Child = updateDetails,
        });

        check.Click += async (_, _) =>
        {
            if (_checking || _disposed) return;
            _checking = true;
            check.IsEnabled = false;
            check.Content = "正在检查…";
            status.Text = "正在连接 GitHub，获取最新版本信息…";
            status.Foreground = SettingsUi.Muted;
            linkText.Visibility = Visibility.Collapsed;
            linkText.Inlines.Clear();
            var shouldRetry = false;
            try
            {
                var result = await onCheckUpdate(_lifetime.Token);
                if (_disposed) return;
                status.Text = result.Message;
                shouldRetry = result.Status is not (UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.Cancelled);
                status.Foreground = result.Status switch
                {
                    UpdateCheckStatus.UpToDate => SettingsUi.Success,
                    UpdateCheckStatus.UpdateAvailable => SettingsUi.Accent,
                    UpdateCheckStatus.Cancelled => SettingsUi.Muted,
                    _ => SettingsUi.Error,
                };
                if (result.Status == UpdateCheckStatus.UpdateAvailable && result.ReleaseUri is { } uri)
                {
                    var link = new Hyperlink(new Run($"前往 GitHub 下载 {result.LatestVersion} ↗"))
                    {
                        NavigateUri = uri,
                        Foreground = SettingsUi.Accent,
                        Focusable = true,
                    };
                    SettingsUi.Identify(link, "ReleaseDownloadLink", "前往 GitHub 下载新版本");
                    KeyboardNavigation.SetIsTabStop(link, true);
                    link.RequestNavigate += (_, e) =>
                    {
                        e.Handled = true;
                        if (_disposed) return;
                        try { onOpenRelease(uri); }
                        catch (Exception ex)
                        {
                            AppLog.Error("打开更新页面失败", ex);
                            status.Text = $"无法打开浏览器，请手动访问：{uri.AbsoluteUri}";
                            status.Foreground = SettingsUi.Error;
                        }
                    };
                    linkText.Inlines.Add(link);
                    linkText.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AppLog.Error("检查更新失败", ex);
                shouldRetry = true;
                if (!_disposed)
                {
                    status.Text = "检查失败，请检查网络后重试。";
                    status.Foreground = SettingsUi.Error;
                }
            }
            finally
            {
                _checking = false;
                if (!_disposed)
                {
                    check.Content = shouldRetry ? "重试检查" : "检查更新";
                    check.IsEnabled = true;
                }
                else
                {
                    _lifetime.Dispose();
                }
            }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (!_checking) _lifetime.Dispose();
    }

    private static Grid SettingRow(FrameworkElement description, FrameworkElement action)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(action, 1);
        row.Children.Add(description);
        row.Children.Add(action);
        return row;
    }
}
