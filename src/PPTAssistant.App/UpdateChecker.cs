using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PPTAssistant.App;

internal enum UpdateCheckStatus
{
    NotConfigured,
    InvalidRepository,
    UpdateAvailable,
    UpToDate,
    NoReleases,
    InvalidResponse,
    NetworkError,
    RateLimited,
    Cancelled,
}

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    Uri? ReleaseUri,
    string Message);

internal sealed class UpdateChecker
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private static readonly Regex RepositoryPattern = new(
        @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}\z",
        RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient;

    public UpdateChecker(HttpClient? httpClient = null, string? currentVersion = null)
    {
        _httpClient = httpClient ?? SharedClient;
        CurrentVersion = currentVersion ?? ReadCurrentVersion();
    }

    public string CurrentVersion { get; }

    public static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var value = repository.Trim();
        return RepositoryPattern.IsMatch(value) && value.Split('/')[1] is not ("." or "..");
    }

    public async Task<UpdateCheckResult> CheckAsync(string? repository, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return Result(UpdateCheckStatus.NotConfigured, "尚未配置 GitHub 发布仓库，请填写 owner/repo 后检查更新。");
        if (!IsValidRepository(repository))
            return Result(UpdateCheckStatus.InvalidRepository, "仓库格式应为 owner/repo，例如组织名/仓库名。");
        if (!ReleaseVersion.TryParse(CurrentVersion, out var current))
            return Result(UpdateCheckStatus.InvalidResponse, "无法识别当前程序版本，暂不能比较更新。");

        var normalizedRepository = repository.Trim();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{normalizedRepository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("PPTAssistant-UpdateCheck/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Result(UpdateCheckStatus.NoReleases, "未找到正式发布，请确认仓库公开且已有 GitHub Release。");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Result(UpdateCheckStatus.RateLimited, "GitHub 暂时限制访问或请求过于频繁，请稍后重试。");
            if (!response.IsSuccessStatusCode)
                return Result(UpdateCheckStatus.NetworkError, "GitHub 暂时无法提供更新信息，请稍后重试。");

            // 发布元数据足够小；拒绝异常的大响应，避免错误代理页面占用过多内存。
            const int maximumResponseBytes = 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumResponseBytes)
                return InvalidRelease();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new System.IO.MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > maximumResponseBytes) return InvalidRelease();
                buffer.Write(bytes, 0, read);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            var release = document.RootElement;
            if (release.ValueKind != JsonValueKind.Object ||
                !TryReadString(release, "tag_name", out var tag) ||
                !TryReadString(release, "html_url", out var releaseUrl) ||
                !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
                !ReleaseVersion.TryParse(tag, out var latest) || latest.IsPrerelease ||
                !TryGetReleaseUri(releaseUrl, normalizedRepository, out var releaseUri))
                return InvalidRelease();

            return latest.CompareTo(current) > 0
                ? new(UpdateCheckStatus.UpdateAvailable, CurrentVersion, tag, releaseUri,
                    $"发现新版本 {tag}，可前往 GitHub 发布页自行下载。")
                : new(UpdateCheckStatus.UpToDate, CurrentVersion, tag, null,
                    "当前版本已是最新正式版，或比最新正式版更新。");
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? Result(UpdateCheckStatus.Cancelled, "已取消更新检查。")
                : Result(UpdateCheckStatus.NetworkError, "更新检查超时，请检查网络后重试。");
        }
        catch (HttpRequestException)
        {
            return Result(UpdateCheckStatus.NetworkError, "无法连接 GitHub，请检查网络后重试。");
        }
        catch (System.IO.IOException)
        {
            return Result(UpdateCheckStatus.NetworkError, "读取更新信息失败，请稍后重试。");
        }
        catch (JsonException)
        {
            return InvalidRelease();
        }
    }

    private UpdateCheckResult Result(UpdateCheckStatus status, string message) =>
        new(status, CurrentVersion, null, null, message);

    private UpdateCheckResult InvalidRelease() =>
        Result(UpdateCheckStatus.InvalidResponse, "发布信息或版本号无效，暂不能确认是否有更新。");

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return value.Length != 0;
    }

    private static bool TryGetReleaseUri(string value, string repository, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort || candidate.UserInfo.Length != 0 ||
            candidate.Query.Length != 0 || candidate.Fragment.Length != 0)
            return false;

        var prefix = $"/{repository}/releases/tag/";
        if (!candidate.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            candidate.AbsolutePath.Length <= prefix.Length)
            return false;
        uri = candidate;
        return true;
    }

    private static string ReadCurrentVersion()
    {
        var assembly = typeof(UpdateChecker).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ReleaseVersion.TryParse(informational, out _) ? informational!
            : assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}

/// <summary>兼容 v 前缀、2–4 段程序集版本，以及 SemVer 预发行/构建元数据。</summary>
internal sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(
        @"\A[vV]?(?<core>[0-9]+\.[0-9]+(?:\.[0-9]+){0,2})(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant);
    private readonly int[] _numbers;
    private readonly string[] _prerelease;

    private ReleaseVersion(int[] numbers, string[] prerelease)
    {
        _numbers = numbers;
        _prerelease = prerelease;
    }

    public bool IsPrerelease => _prerelease.Length != 0;

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        var match = Pattern.Match(value.Trim());
        if (!match.Success) return false;
        var segments = match.Groups["core"].Value.Split('.');
        var numbers = new int[4];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        // SemVer 的数字预发行标识不能以 0 开头（单个 0 除外）。
        if (prerelease.Any(item => IsNumeric(item) && item.Length > 1 && item[0] == '0')) return false;
        version = new ReleaseVersion(numbers, prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < _numbers.Length; i++)
        {
            var result = _numbers[i].CompareTo(other._numbers[i]);
            if (result != 0) return result;
        }
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            var result = leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1)
                : leftNumeric && left.Length != right.Length ? left.Length.CompareTo(right.Length)
                : string.Compare(left, right, StringComparison.Ordinal);
            if (result != 0) return result;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool IsNumeric(string value) => value.All(c => c is >= '0' and <= '9');
}
