using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Tests;

public sealed class MessageFormatterTests
{
    [Fact]
    public void Format_SubstitutesPositionalArguments()
    {
        Assert.Equal(
            "Error during streaming video /tmp/a.mp4 with videoLiveHistoryPkid = 7",
            MessageFormatter.Format(
                "Error during streaming video {0} with videoLiveHistoryPkid = {1}",
                "/tmp/a.mp4",
                7));
    }

    [Fact]
    public void Format_UnescapesDoubledSingleQuotesLikeJavaMessageFormat()
    {
        Assert.Equal("Estensione non trovata!", MessageFormatter.Format("Estensione non trovata!"));
        Assert.Equal("it's fine", MessageFormatter.Format("it''s fine"));
    }

    [Fact]
    public void Format_KeepsQuotedLiteralsVerbatim()
    {
        Assert.Equal("a {literal} b", MessageFormatter.Format("a '{literal}' b"));
    }

    [Fact]
    public void Format_WithoutArgumentsReturnsThePatternUnchanged()
    {
        Assert.Equal("Live started", MessageFormatter.Format("Live started"));
    }
}

public sealed class PropertiesBundleTests
{
    [Fact]
    public void Parse_ReadsKeysValuesCommentsAndContinuations()
    {
        var bundle = PropertiesBundle.Parse(
            """
            # a comment
            ! another comment
            first=one
            second:two
            third=three \
                  continued
            empty=
            """);

        Assert.True(bundle.TryGetValue("first", out var first));
        Assert.Equal("one", first);
        Assert.True(bundle.TryGetValue("second", out var second));
        Assert.Equal("two", second);
        Assert.True(bundle.TryGetValue("third", out var third));
        Assert.Equal("three continued", third);
        Assert.True(bundle.TryGetValue("empty", out var empty));
        Assert.Equal(string.Empty, empty);
    }

    [Fact]
    public void Parse_UnescapesColonAndEqualsInsideValues()
    {
        var bundle = PropertiesBundle.Parse("""

            url=https://localhost:1200?a=1
            """);

        Assert.True(bundle.TryGetValue("url", out var url));
        Assert.Equal("https://localhost:1200?a=1", url);
    }
}
