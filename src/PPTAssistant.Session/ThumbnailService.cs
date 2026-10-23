using System.IO;
using PPTAssistant.Host;

namespace PPTAssistant.Session;

public sealed class ThumbnailService : IDisposable
{
    private readonly string _sessionDirectory;
    private readonly Action<string>? _trace;
    private readonly HashSet<int> _exported = [];
    private readonly List<string> _createdFiles = [];
    private bool _disposed;

    /// <param name="rootDirectory">缩略图根目录（null = 默认 <c>%LOCALAPPDATA%\PPTAssistant\thumbs</c>）。</param>
    /// <param name="trace">日志回调。</param>
    public ThumbnailService(string? rootDirectory = null, Action<string>? trace = null)
    {
        _trace = trace;
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PPTAssistant",
            "thumbs");

        CleanupStaleSessions(RootDirectory, _trace);

        // 会话子目录：不同运行之间不会互相删文件（也避免多实例干扰，虽然产品本身是单实例）。
        _sessionDirectory = Path.Combine(
            RootDirectory,
            $"session-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }

    /// <summary>缩略图根目录。</summary>
    public string RootDirectory { get; }

    public static int CleanupStaleSessions(string rootDirectory, Action<string>? trace = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return 0;
        }

        var removed = 0;
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(rootDirectory);
        }
        catch (Exception ex)
        {
            trace?.Invoke($"[thumbs] 枚举历史会话目录失败（忽略）：{ex.GetType().Name} {ex.Message}");
            return 0;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (!IsSessionDirectoryName(name))
            {
                // 不是我们建的目录 → 一律不碰。
                continue;
            }

            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    if (IsThumbnailFileName(Path.GetFileName(file)))
                    {
                        File.Delete(file);
                    }
                }

                // 只有「确实空了」才删目录：里面若混着别的东西（子目录/未知文件），就留着不动。
                if (Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                    removed++;
                }
                else
                {
                    trace?.Invoke($"[thumbs] 历史会话目录含非缩略图内容，保留不删：{directory}");
                }
            }
            catch (Exception ex)
            {
                trace?.Invoke($"[thumbs] 清理历史会话目录失败（忽略）：{ex.GetType().Name} {ex.Message}");
            }
        }

        if (removed > 0)
        {
            trace?.Invoke($"[thumbs] 已清理 {removed} 个历史会话目录");
        }

        return removed;
    }

    /// <summary>目录名是否是我们自己的会话目录（<c>session-&lt;pid&gt;-&lt;yyyyMMdd-HHmmss&gt;</c>）。</summary>
    private static bool IsSessionDirectoryName(string name)
    {
        if (!name.StartsWith("session-", StringComparison.Ordinal))
        {
            return false;
        }

        // 严格形状：session-<数字>-<8位日期>-<6位时间>
        var parts = name.Split('-');
        return parts.Length == 4 &&
               parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit) &&
               parts[2].Length == 8 && parts[2].All(char.IsAsciiDigit) &&
               parts[3].Length == 6 && parts[3].All(char.IsAsciiDigit);
    }

    /// <summary>文件名是否是我们自己的缩略图（<c>slide-NNN.png</c>）。</summary>
    private static bool IsThumbnailFileName(string name)
        => name.StartsWith("slide-", StringComparison.Ordinal) &&
           name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
           name.Length == "slide-000.png".Length &&
           name.AsSpan(6, 3).ToString().All(char.IsAsciiDigit);

    /// <summary>本会话的缩略图目录。</summary>
    public string SessionDirectory => _sessionDirectory;

    /// <summary>已成功导出的页号集合。</summary>
    public IReadOnlyCollection<int> ExportedSlides => _exported;

    /// <summary>
    /// 确保某页的缩略图已导出，返回文件路径（失败返回 null）。
    /// </summary>
    public string? EnsureThumbnail(PptHost host, int slideIndex, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_disposed || slideIndex < 1)
        {
            return null;
        }

        var path = Path.Combine(_sessionDirectory, $"slide-{slideIndex:D3}.png");
        if (_exported.Contains(slideIndex) && File.Exists(path))
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(_sessionDirectory);
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[thumbs] 创建缩略图目录失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }

        if (!File.Exists(path))
        {
            try
            {
                _createdFiles.Add(path);
            }
            catch (Exception)
            {
            }
        }

        if (!host.ExportSlideThumbnail(slideIndex, path, pixelWidth, pixelHeight))
        {
            _exported.Remove(slideIndex);
            return null;
        }

        if (!File.Exists(path))
        {
            // 有些实现会静默不产出文件（例如受保护的演示文稿）：如实当作失败，别让 UI 显示坏图。
            _trace?.Invoke($"[thumbs] 第 {slideIndex} 页导出返回成功但没有产出文件");
            _exported.Remove(slideIndex);
            return null;
        }

        _exported.Add(slideIndex);
        _trace?.Invoke($"[thumbs] 第 {slideIndex} 页缩略图就绪（{new FileInfo(path).Length / 1024} KB）");
        return path;
    }

    /// <summary>清空记录（不删文件）——换课件时调用，好让同一页号能重新导出。</summary>
    public void ResetSlideCache() => _exported.Clear();

    /// <summary>逐个删除本会话产生的文件，然后（仅在空目录时）删除会话目录。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var file in _createdFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"[thumbs] 删除缩略图失败（忽略）：{ex.GetType().Name} {ex.Message}");
            }
        }

        _createdFiles.Clear();
        _exported.Clear();

        try
        {
            // 只在「确实是空目录」时才删目录：不做递归删除，不碰任何不属于本会话的东西。
            if (Directory.Exists(_sessionDirectory) && Directory.GetFileSystemEntries(_sessionDirectory).Length == 0)
            {
                Directory.Delete(_sessionDirectory);
            }
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[thumbs] 清理会话目录失败（忽略）：{ex.GetType().Name} {ex.Message}");
        }
    }
}
