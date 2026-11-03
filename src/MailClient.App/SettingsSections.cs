using MailClient.Core;
using MailClient.Core.Links;
using MailClient.Core.Retention;
using MailClient.Storage.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MailClient_App;

/// <summary>设置对话框里的「常规」「链接」「存储」页（「通知」页仍在 MainWindow 里）。</summary>
internal static class SettingsSections
{
    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = Secondary
    };

    private static void Id(DependencyObject element, string id, string? name = null)
    {
        element.SetValue(AutomationProperties.AutomationIdProperty, id);
        if (name is not null)
        {
            element.SetValue(AutomationProperties.NameProperty, name);
        }
    }

    // ───────── 常规 ─────────

    public static UIElement BuildGeneral(
        AppSettingsStore settings,
        Action<bool>? onBackgroundChanged = null,
        UpdateService? updates = null,
        Action<MailClient.Core.Updates.ReleaseInfo>? showUpdate = null)
    {
        var toggle = new ToggleSwitch
        {
            Header = "关闭主窗口时保留在后台",
            OnContent = "开：点关闭后藏到托盘，同步和通知继续",
            OffContent = "关：点关闭后退出软件",
            IsOn = settings.GetKeepRunningInBackground()
        };
        Id(toggle, "KeepRunningInBackgroundToggle", "关闭主窗口时保留在后台");
        toggle.Toggled += (_, _) =>
        {
            settings.Update(s => s.KeepRunningInBackground = toggle.IsOn);
            onBackgroundChanged?.Invoke(toggle.IsOn);
        };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(toggle);
        panel.Children.Add(Caption("开启后，点窗口右上角关闭只会把「邮件」藏到通知区域（托盘），收信、IDLE 和 Windows 通知都会继续。左键单击托盘图标可重新打开；右键菜单有「打开」「同步全部」「退出」。要真正退出请用托盘菜单里的「退出」。"));
        panel.Children.Add(Caption("再次从开始菜单启动时，如果软件已在后台运行，会直接唤起现有窗口，不会开第二个进程。"));
        if (updates is not null)
        {
            panel.Children.Add(BuildUpdates(settings, updates, showUpdate));
        }

        return panel;
    }

    /// <summary>「更新」小节：自动检查开关 + 检查更新按钮和结果。</summary>
    private static UIElement BuildUpdates(AppSettingsStore settings, UpdateService updates, Action<MailClient.Core.Updates.ReleaseInfo>? showUpdate)
    {
        var autoToggle = new ToggleSwitch
        {
            Header = "自动检查更新",
            OnContent = "开：启动后和之后每 6 小时检查一次 GitHub 上的新版本",
            OffContent = "关：只在点「检查更新」时才检查",
            IsOn = settings.GetAutoCheckUpdates()
        };
        Id(autoToggle, "AutoCheckUpdatesToggle", "自动检查更新");
        autoToggle.Toggled += (_, _) => settings.Update(s => s.AutoCheckUpdates = autoToggle.IsOn);

        var versionText = $"当前版本 {MailClient.Core.Updates.UpdateVersion.Display(updates.CurrentVersion)}（{MailClient.Core.Updates.EditionDetector.DisplayName(updates.Edition)}）";
        var version = new TextBlock { Text = versionText, TextWrapping = TextWrapping.Wrap };
        Id(version, "CurrentVersionText", versionText);

        var button = new Button { Content = "检查更新" };
        Id(button, "CheckUpdatesButton", "检查更新");
        var ring = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Id(result, "UpdateCheckResultText");
        var view = new HyperlinkButton { Content = "查看并更新", Padding = new Thickness(4, 0, 4, 0), Visibility = Visibility.Collapsed };
        Id(view, "ViewUpdateButton", "查看并更新");

        MailClient.Core.Updates.ReleaseInfo? found = null;
        void Show(MailClient.Core.Updates.UpdateCheckResult r)
        {
            var when = updates.LastChecked is { } t ? $"  ·  {t.LocalDateTime:HH:mm} 检查" : string.Empty;
            result.Text = r.ToDisplayText() + (r.IsFailure ? string.Empty : when);
            result.Foreground = r.IsFailure
                ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                : r.Status == MailClient.Core.Updates.UpdateCheckStatus.UpdateAvailable
                    ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                    : Secondary;
            found = r.Status == MailClient.Core.Updates.UpdateCheckStatus.UpdateAvailable ? r.Release : null;
            view.Visibility = found is not null && showUpdate is not null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (updates.LastResult is { } previous)
        {
            Show(previous);
        }

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            ring.IsActive = true;
            ring.Visibility = Visibility.Visible;
            result.Text = "正在检查…";
            result.Foreground = Secondary;
            view.Visibility = Visibility.Collapsed;
            try
            {
                Show(await updates.CheckAsync(manual: true));
            }
            catch (Exception ex)
            {
                result.Text = "检查失败：" + ex.Message;
            }
            finally
            {
                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;
                button.IsEnabled = true;
            }
        };
        view.Click += (_, _) =>
        {
            if (found is not null)
            {
                showUpdate?.Invoke(found);
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(button);
        row.Children.Add(ring);
        row.Children.Add(result);
        row.Children.Add(view);

        var heading = new TextBlock
        {
            Text = "更新",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 10, 0, 0)
        };
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 4, 0, 0)
        });
        section.Children.Add(heading);
        section.Children.Add(autoToggle);
        section.Children.Add(version);
        section.Children.Add(row);
        return section;
    }

    // ───────── 链接 ─────────

    public static UIElement BuildLinks(AppSettingsStore settings)
    {
        var radios = new RadioButtons { Header = "打开邮件中的链接" };
        radios.Items.Add("系统默认浏览器（默认）");
        radios.Items.Add("软件内置浏览器");
        radios.SelectedIndex = settings.GetLinkOpenMode() == LinkOpenMode.Internal ? 1 : 0;
        Id(radios, "LinkOpenModeRadio", "打开邮件中的链接");
        radios.SelectionChanged += (_, _) =>
        {
            var mode = radios.SelectedIndex == 1 ? LinkOpenMode.Internal : LinkOpenMode.System;
            settings.Update(s => s.LinkOpenMode = LinkRouter.Serialize(mode));
        };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(radios);
        panel.Children.Add(Caption("「系统默认浏览器」用你在 Windows 里设置的默认浏览器打开。「软件内置浏览器」会在一个独立窗口里打开：标题栏显示网址，带后退 / 前进 / 刷新，以及「在系统浏览器中打开」按钮。"));
        panel.Children.Add(Caption("无论哪种方式，邮件正文都不会运行脚本；mailto: 链接会直接打开写信窗口，javascript: 和 file: 等链接会被拦截。"));
        return panel;
    }

    // ───────── 存储 ─────────

    public static UIElement BuildStorage(AppSettingsStore settings, StorageMaintenanceService? service, Action<string> reportError)
    {
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(0, 8, 0, 8) };
        if (service is null)
        {
            panel.Children.Add(Caption("存储维护服务不可用。"));
            return panel;
        }

        var dbText = new TextBlock { Text = "数据库文件：计算中…", TextWrapping = TextWrapping.Wrap };
        var bodyText = new TextBlock { Text = "邮件正文缓存：计算中…", TextWrapping = TextWrapping.Wrap };
        var logText = new TextBlock { Text = "日志文件：计算中…", TextWrapping = TextWrapping.Wrap };
        Id(dbText, "StorageDbSize");
        Id(bodyText, "StorageBodySize");
        Id(logText, "StorageLogSize");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Secondary };
        Id(status, "StorageStatus");

        var clearButton = new Button { Content = "清理缓存" };
        Id(clearButton, "StorageClearCacheButton", "清理缓存");
        var compactButton = new Button { Content = "压缩数据库" };
        Id(compactButton, "StorageCompactButton", "压缩数据库");

        async Task RefreshAsync()
        {
            try
            {
                var snap = await service.GetSnapshotAsync();
                var u = snap.Usage;
                dbText.Text = $"数据库文件：{FormatBytes(u.DatabaseBytes)}（其中可回收的空闲空间 {FormatBytes(u.FreeBytes)}）";
                bodyText.Text = $"邮件正文缓存：{FormatBytes(u.BodyCacheBytes)}，共 {u.BodyCount} 封（其中可清理 {u.EvictableCount} 封，{FormatBytes(u.EvictableBytes)}）";
                logText.Text = $"日志文件：{FormatBytes(snap.LogBytes)}（每个日志约 1 MB 封顶，各保留 2 份旧文件）";
            }
            catch (Exception ex)
            {
                status.Text = "读取存储用量失败：" + ex.Message;
            }
        }

        void SetBusy(bool busy)
        {
            clearButton.IsEnabled = !busy;
            compactButton.IsEnabled = !busy;
        }

        async Task ApplyLimitsAsync()
        {
            SetBusy(true);
            status.Text = "正在按新上限整理…";
            try
            {
                var r = await service.RunAsync();
                status.Text = r.Eviction.Evicted > 0
                    ? $"已按新上限清理 {r.Eviction.Evicted} 封邮件的正文缓存，释放约 {FormatBytes(r.Eviction.FreedBytes)}。"
                    : "上限已保存；当前缓存没有超出，无需清理。";
            }
            catch (Exception ex)
            {
                status.Text = "整理失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
                await RefreshAsync();
            }
        }

        var current = settings.GetStorageOptions();
        var ageCombo = BuildLimitCombo(
            "StorageMaxAgeCombo", "正文缓存保留时间（距上次打开 / 下载）",
            new[] { 30, 90, 180, 365, 0 }, 90, current.MaxAgeDays, FormatDays,
            value =>
            {
                settings.Update(s => s.BodyCacheMaxAgeDays = value);
                _ = ApplyLimitsAsync();
            });
        var sizeCombo = BuildLimitCombo(
            "StorageMaxSizeCombo", "正文缓存大小上限",
            new[] { 100, 200, 500, 1024, 2048, 0 }, 200, current.MaxCacheMb, FormatMb,
            value =>
            {
                settings.Update(s => s.BodyCacheMaxMb = value);
                _ = ApplyLimitsAsync();
            });

        // 「清理缓存」二次确认：设置本身就在 ContentDialog 里，WinUI 一次只能开一个对话框，所以用内联 Flyout
        var confirmYes = new Button { Content = "确认清理", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Id(confirmYes, "StorageClearConfirmButton", "确认清理");
        var confirmPanel = new StackPanel { Spacing = 10, MaxWidth = 300 };
        confirmPanel.Children.Add(new TextBlock
        {
            Text = "清理全部已缓存的邮件正文？邮件标题、列表、草稿、发件箱和还没同步的操作都会保留；再次打开邮件时会自动重新下载正文。",
            TextWrapping = TextWrapping.Wrap
        });
        confirmPanel.Children.Add(confirmYes);
        var flyout = new Flyout { Content = confirmPanel };
        clearButton.Flyout = flyout;
        confirmYes.Click += async (_, _) =>
        {
            flyout.Hide();
            SetBusy(true);
            status.Text = "正在清理…";
            try
            {
                var r = await service.ClearCacheAsync();
                status.Text = $"已清理 {r.Eviction.Evicted} 封邮件的正文缓存，释放约 {FormatBytes(r.Eviction.FreedBytes)}；数据库回收 {FormatBytes(r.Database.ReclaimedBytes)}。";
            }
            catch (Exception ex)
            {
                status.Text = "清理失败：" + ex.Message;
                reportError(status.Text);
            }
            finally
            {
                SetBusy(false);
                await RefreshAsync();
            }
        };

        compactButton.Click += async (_, _) =>
        {
            SetBusy(true);
            status.Text = "正在压缩数据库（期间邮件列表可能短暂卡顿）…";
            try
            {
                var r = await service.CompactDatabaseAsync();
                status.Text = r.Skipped is { Length: > 0 } skipped
                    ? "未压缩：" + skipped
                    : $"已压缩数据库，回收 {FormatBytes(r.ReclaimedBytes)}。";
            }
            catch (Exception ex)
            {
                status.Text = "压缩失败：" + ex.Message;
                reportError(status.Text);
            }
            finally
            {
                SetBusy(false);
                await RefreshAsync();
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(clearButton);
        buttons.Children.Add(compactButton);

        panel.Children.Add(dbText);
        panel.Children.Add(bodyText);
        panel.Children.Add(logText);
        panel.Children.Add(ageCombo);
        panel.Children.Add(sizeCombo);
        panel.Children.Add(buttons);
        panel.Children.Add(status);
        panel.Children.Add(Caption("超出上限时，先清理最久没打开的邮件正文。邮件标题、草稿、发件箱、本地发出的副本和有待同步操作的邮件不会被清理。被清理的正文在再次打开时自动重新下载；清理期间这些邮件的正文暂时搜不到。「压缩数据库」会整理文件、把空闲空间还给磁盘，数据库较大时需要几秒。"));

        _ = RefreshAsync();
        return panel;
    }

    private static ComboBox BuildLimitCombo(string id, string header, int[] values, int defaultValue, int current,
        Func<int, string> label, Action<int> changed)
    {
        var combo = new ComboBox { Header = header, MinWidth = 260 };
        Id(combo, id, header);
        var all = values.ToList();
        if (!all.Contains(current))
        {
            all.Add(current);
            all.Sort((a, b) => a == 0 ? 1 : b == 0 ? -1 : a.CompareTo(b));
        }

        var selected = 0;
        for (var i = 0; i < all.Count; i++)
        {
            var text = label(all[i]) + (all[i] == defaultValue ? "（默认）" : string.Empty);
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = all[i] });
            if (all[i] == current)
            {
                selected = i;
            }
        }

        combo.SelectedIndex = selected;
        var last = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: int value } && value != last)
            {
                last = value;
                changed(value);
            }
        };
        return combo;
    }

    internal static string FormatDays(int days) => days <= 0 ? "不限制" : days % 365 == 0 && days >= 365 ? $"{days / 365} 年" : $"{days} 天";

    internal static string FormatMb(int mb) => mb <= 0 ? "不限制" : mb >= 1024 && mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb} MB";

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double v = bytes / 1024.0;
        if (v < 1024)
        {
            return $"{v:0.#} KB";
        }

        v /= 1024;
        return v < 1024 ? $"{v:0.#} MB" : $"{v / 1024:0.##} GB";
    }
}
