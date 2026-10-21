namespace GameOptimizer.App.ViewModels;

using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

/// <summary>
/// WPF 侧的「文件夹选择 / 打开资源管理器」适配器。
///
/// 单独放在 App 层的原因：<see cref="GameOptimizer.Core.Download.DownloadService"/>
/// 与下载页 ViewModel 都不该引用 WPF 或直接弹窗，
/// 这样下载逻辑可以脱离界面单测，宿主也能换成 CLI / 自动化测试。
/// </summary>
public static class DirectoryPicker
{
    /// <summary>
    /// 弹出文件夹选择器。取消时返回 null。
    /// 用 <see cref="OpenFolderDialog"/>（.NET 8 起内置），而不是 WinForms 的 FolderBrowserDialog：
    /// 后者是树形单选的老界面，与本项目的深色风格完全不搭。
    /// </summary>
    public static string? AskForFolder(string? initial)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择下载文件的保存位置",
                Multiselect = false,
            };

            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                dialog.InitialDirectory = initial;

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法打开文件夹选择器：{ex.Message}",
                "游戏优化助手 — 下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>在资源管理器里选中文件。文件不存在时退回打开它所在的目录。</summary>
    public static void OpenInExplorer(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            MessageBox.Show(
                "文件与所在文件夹都不存在，可能已被移动或删除。",
                "游戏优化助手 — 下载",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"打开文件夹失败：{ex.Message}",
                "游戏优化助手 — 下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
