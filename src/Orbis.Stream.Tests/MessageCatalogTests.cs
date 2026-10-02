using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Tests;

public sealed class MessageCatalogTests
{
    private static MessageCatalog CreateCatalog()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Messages");
        return new MessageCatalog(directory, NullLogger<MessageCatalog>.Instance);
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData("it-IT", "it")]
    [InlineData("IT_it", "it")]
    [InlineData("zh-CN", "zh")]
    [InlineData("en-US", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData("ko-KR", "ko")]
    [InlineData("ar-EG", "en")]
    [InlineData("hi-IN", "hi")]
    [InlineData("bn-BD", "bn")]
    [InlineData(null, "en")]
    public void ResolveLanguage_MapsToAnAvailableBundle(string? requested, string expected)
    {
        Assert.Equal(expected, CreateCatalog().ResolveLanguage(requested));
    }

    [Fact]
    public void GetMessage_UsesTheRequestedLanguage()
    {
        var catalog = CreateCatalog();

        Assert.Equal("Live started", catalog.GetMessage("en", "live.started"));
        Assert.Equal("Live avviata correttemente", catalog.GetMessage("it", "live.started"));
    }

    [Fact]
    public void GetMessage_FallsBackToEnglishForUnsupportedLanguages()
    {
        Assert.Equal("Not valid field", CreateCatalog().GetMessage("pt-BR", "not.valid.input"));
    }

    [Fact]
    public void GetMessage_ThrowsWhenTheCodeDoesNotExist()
    {
        Assert.Throws<MessageNotFoundException>(() => CreateCatalog().GetMessage("en", "code.that.does.not.exist"));
    }

    [Fact]
    public void GetMessage_SubstitutesTheMessageFormatParameters()
    {
        Assert.Equal(
            "Video /tmp/clip.mp4 not valid, no extension found.",
            CreateCatalog().GetMessage("en", "video.not.valid", ["/tmp/clip.mp4"]));
    }

    [Fact]
    public void EveryBundleDefinesTheSameCodes()
    {
        var catalog = CreateCatalog();
        var english = PropertiesBundle.Parse(File.ReadAllText(EnglishPath()));

        Assert.Equal(15, catalog.SupportedLanguages.Count);

        foreach (var language in catalog.SupportedLanguages)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Messages", $"messages_{language}.properties");
            var bundle = PropertiesBundle.Parse(File.ReadAllText(path));

            foreach (var code in english.Values.Keys)
            {
                Assert.True(bundle.TryGetValue(code, out _), $"messages_{language}.properties is missing '{code}'");
            }
        }
    }

    private static string EnglishPath() =>
        Path.Combine(AppContext.BaseDirectory, "Messages", "messages_en.properties");
}
