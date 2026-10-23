using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PPTAssistant.App;

namespace PPTAssistant.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData(null, UpdateCheckStatus.NotConfigured)]
    [InlineData("", UpdateCheckStatus.NotConfigured)]
    [InlineData("  ", UpdateCheckStatus.NotConfigured)]
    [InlineData("https://github.com/owner/repo", UpdateCheckStatus.InvalidRepository)]
    [InlineData("owner/repo/extra", UpdateCheckStatus.InvalidRepository)]
    [InlineData("owner/..", UpdateCheckStatus.InvalidRepository)]
    [InlineData("owner/repo?token=secret", UpdateCheckStatus.InvalidRepository)]
    [InlineData("owner/repo#fragment", UpdateCheckStatus.InvalidRepository)]
    public async Task MissingOrInvalidRepositoryNeverSendsARequest(string? repository, object expected)
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("Unexpected network request"));
        using var client = new HttpClient(handler);
        var checker = new UpdateChecker(client, "1.0.0");

        Assert.Equal(0, handler.RequestCount);
        var result = await checker.CheckAsync(repository);

        Assert.Equal((UpdateCheckStatus)expected, result.Status);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(result.ReleaseUri);
    }

    [Fact]
    public async Task ExplicitCheckRequestsOnlyTheConfiguredLatestReleaseWithGitHubHeaders()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Contains("PPTAssistant", request.Headers.UserAgent.ToString());
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/vnd.github+json");
            return Release("v1.2.0");
        });
        using var client = new HttpClient(handler);
        var checker = new UpdateChecker(client, "1.0.0");

        var result = await checker.CheckAsync(" owner/repo ");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("v1.2.0", result.LatestVersion);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.2.0", result.ReleaseUri!.AbsoluteUri);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("1.9.0", "v1.10.0", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("1.2", "1.2.0", UpdateCheckStatus.UpToDate)]
    [InlineData("1.2.0.0", "v1.2.0", UpdateCheckStatus.UpToDate)]
    [InlineData("1.2.0.1", "v1.2.0", UpdateCheckStatus.UpToDate)]
    [InlineData("1.2.0", "1.2.0.1", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("1.2.0+commit123", "1.2.0+different", UpdateCheckStatus.UpToDate)]
    [InlineData("1.2.0-rc.2+commit123", "v1.2.0", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("2.0.0-preview.1", "v1.9.0", UpdateCheckStatus.UpToDate)]
    public async Task ComparesReleaseVersionsWithoutLexicographicOrBuildMetadataErrors(
        string current, string latest, object expected)
    {
        var result = await Check(Release(latest), current);

        Assert.Equal((UpdateCheckStatus)expected, result.Status);
        if ((UpdateCheckStatus)expected == UpdateCheckStatus.UpToDate) Assert.Null(result.ReleaseUri);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo/releases/tag/v2.0.0")]
    [InlineData("https://github.com.evil.test/owner/repo/releases/tag/v2.0.0")]
    [InlineData("https://github.com/other/repo/releases/tag/v2.0.0")]
    [InlineData("https://github.com/owner/other/releases/tag/v2.0.0")]
    [InlineData("https://github.com/owner/repo/releases/tag/")]
    [InlineData("https://github.com/owner/repo/releases/download/v2.0.0/app.exe")]
    [InlineData("https://user@github.com/owner/repo/releases/tag/v2.0.0")]
    [InlineData("https://github.com:444/owner/repo/releases/tag/v2.0.0")]
    [InlineData("https://github.com/owner/repo/releases/tag/v2.0.0?redirect=evil")]
    [InlineData("file:///C:/temp/update.exe")]
    public async Task RejectsUntrustedOrNonReleaseDownloadLinks(string url)
    {
        var result = await Check(Release("v2.0.0", url));

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
        Assert.Null(result.ReleaseUri);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":42,\"html_url\":true}")]
    public async Task MalformedResponsesReturnInlineError(string json)
    {
        var result = await Check(JsonResponse(json));

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
        Assert.Null(result.ReleaseUri);
    }

    [Theory]
    [InlineData("latest", false, false)]
    [InlineData("v2.0.0-beta.1", false, false)]
    [InlineData("v2.0.0", true, false)]
    [InlineData("v2.0.0", false, true)]
    public async Task IgnoresDraftsPrereleasesAndUnparseableTags(string tag, bool draft, bool prerelease)
    {
        var result = await Check(Release(tag, draft: draft, prerelease: prerelease));

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
        Assert.Null(result.ReleaseUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, UpdateCheckStatus.NoReleases)]
    [InlineData(HttpStatusCode.Forbidden, UpdateCheckStatus.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateCheckStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateCheckStatus.NetworkError)]
    [InlineData(HttpStatusCode.MovedPermanently, UpdateCheckStatus.NetworkError)]
    public async Task HttpFailuresHaveActionableStatusAndNoLink(HttpStatusCode httpStatus, object expected)
    {
        var result = await Check(new HttpResponseMessage(httpStatus));

        Assert.Equal((UpdateCheckStatus)expected, result.Status);
        Assert.Null(result.ReleaseUri);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task OfflineNetworkFailureDoesNotThrow()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var client = new HttpClient(handler);

        var result = await new UpdateChecker(client, "1.0.0").CheckAsync("owner/repo");

        Assert.Equal(UpdateCheckStatus.NetworkError, result.Status);
    }

    [Fact]
    public async Task TimeoutIsReportedAsNetworkErrorRatherThanUserCancellation()
    {
        using var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        using var client = new HttpClient(handler);

        var result = await new UpdateChecker(client, "1.0.0").CheckAsync("owner/repo");

        Assert.Equal(UpdateCheckStatus.NetworkError, result.Status);
        Assert.Contains("超时", result.Message);
    }

    [Fact]
    public async Task CallerCancellationReturnsCancelledWithoutAReleaseLink()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var client = new HttpClient(handler);

        var result = await new UpdateChecker(client, "1.0.0").CheckAsync("owner/repo", cancellation.Token);

        Assert.Equal(UpdateCheckStatus.Cancelled, result.Status);
        Assert.Null(result.ReleaseUri);
    }

    [Fact]
    public async Task InvalidCurrentVersionDoesNotRequestOrInventAnUpdate()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("Unexpected network request"));
        using var client = new HttpClient(handler);

        var result = await new UpdateChecker(client, "development").CheckAsync("owner/repo");

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task OversizedMetadataIsRejected()
    {
        var result = await Check(JsonResponse(new string(' ', 1024 * 1024 + 1)));

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public void DefaultVersionComesFromTheProductAssembly()
    {
        var checker = new UpdateChecker();

        Assert.True(ReleaseVersion.TryParse(checker.CurrentVersion, out _));
        var assembly = typeof(AppConfig).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion;
        Assert.Equal(informational ?? assembly.GetName().Version!.ToString(), checker.CurrentVersion);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void PrereleaseOrderingFollowsSemVer(string lower, string higher)
    {
        Assert.True(ReleaseVersion.TryParse(lower, out var left));
        Assert.True(ReleaseVersion.TryParse(higher, out var right));

        Assert.True(left.CompareTo(right) < 0);
        Assert.True(right.CompareTo(left) > 0);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    [InlineData("999999999999.2.3")]
    public void InvalidVersionsAreNotCompared(string version)
    {
        Assert.False(ReleaseVersion.TryParse(version, out _));
    }

    private static async Task<UpdateCheckResult> Check(HttpResponseMessage response, string current = "1.0.0")
    {
        using var handler = new StubHandler(_ => response);
        using var client = new HttpClient(handler);
        return await new UpdateChecker(client, current).CheckAsync("owner/repo");
    }

    private static HttpResponseMessage Release(string tag, string? url = null, bool draft = false, bool prerelease = false) =>
        JsonResponse(JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = url ?? $"https://github.com/owner/repo/releases/tag/{tag}",
            draft,
            prerelease,
        }));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(respond(request));
        }
    }
}
