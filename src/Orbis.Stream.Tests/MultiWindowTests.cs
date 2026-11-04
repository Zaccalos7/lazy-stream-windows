using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Tests;

public sealed class MultiWindowTests : IClassFixture<ApplicationFixture>
{
    private readonly ApplicationFixture _fixture;

    public MultiWindowTests(ApplicationFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("/orbis/mainPreview?live=42")]
    [InlineData("/orbis/mainLive?start=1&platform=twitch")]
    [InlineData("/orbis/mainTaskManager")]
    public async Task NewWindowOpensTheCurrentPageIncludingItsSelection(string address)
    {
        using var response = await _fixture.Client.GetAsync(address);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var link = Regex.Match(html, "<a class=\"nav-new-window\"[^>]*>").Value;

        Assert.NotEmpty(link);
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(address)}\"", link);
        Assert.Contains("target=\"_blank\"", link);
        Assert.Contains("rel=\"noopener\"", link);
    }

    [Fact]
    public async Task ClosingOnePageLeavesOtherPagesReceivingUpdates()
    {
        var changes = _fixture.Services.GetRequiredService<LiveChangeNotifier>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var rowsAddress = $"/updates?watch=rows&since={changes.Version}";

        using var first = await _fixture.Client.GetAsync(rowsAddress, HttpCompletionOption.ResponseHeadersRead, token);
        using var second = await _fixture.Client.GetAsync(rowsAddress, HttpCompletionOption.ResponseHeadersRead, token);
        using var stats = await _fixture.Client.GetAsync("/updates?watch=stats", HttpCompletionOption.ResponseHeadersRead, token);
        using var firstReader = new StreamReader(await first.Content.ReadAsStreamAsync(token));
        using var secondReader = new StreamReader(await second.Content.ReadAsStreamAsync(token));
        using var statsReader = new StreamReader(await stats.Content.ReadAsStreamAsync(token));

        foreach (var reader in new[] { firstReader, secondReader, statsReader })
        {
            Assert.Equal("retry: 2000", await reader.ReadLineAsync(token));
            Assert.Equal(string.Empty, await reader.ReadLineAsync(token));
        }

        await ReadStatsAsync(statsReader, token);
        changes.Raise();
        await ReadChangeAsync(firstReader, changes.Version, token);
        await ReadChangeAsync(secondReader, changes.Version, token);

        firstReader.Dispose();
        first.Dispose();

        changes.Raise();
        await ReadChangeAsync(secondReader, changes.Version, token);
        await ReadStatsAsync(statsReader, token);

        // A third page can still navigate while the other two keep their push connections open.
        using var page = await _fixture.Client.GetAsync("/orbis/mainLive?start=1", token);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    private static async Task ReadChangeAsync(StreamReader reader, long version, CancellationToken token)
    {
        Assert.Equal($"id: {version}", await reader.ReadLineAsync(token));
        Assert.Equal("event: rows", await reader.ReadLineAsync(token));
        Assert.Equal($"data: {version}", await reader.ReadLineAsync(token));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(token));
    }

    private static async Task ReadStatsAsync(StreamReader reader, CancellationToken token)
    {
        Assert.Equal("event: stats", await reader.ReadLineAsync(token));
        Assert.StartsWith("data: {", await reader.ReadLineAsync(token));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(token));
    }
}
