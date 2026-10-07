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
    [InlineData("tlh", "tlh")]
    [InlineData("hod", "hod")]
    [InlineData("la", "la")]
    [InlineData("ro-RO", "ro")]
    // Bengali was dropped: its bundle was the English text behind a "[bn]" tag,
    // which is not a language. It resolves to English like anything else the catalogue lacks.
    [InlineData("hi-IN", "hi")]
    [InlineData("da-DK", "da")]
    [InlineData("fil-PH", "fil")]
    [InlineData("tl-PH", "fil")]
    [InlineData("bn-BD", "en")]
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
        var catalog = CreateCatalog();

        Assert.Equal("Not valid field", catalog.GetMessage("ar-EG", "not.valid.input"));
        Assert.Equal("Not valid field", catalog.GetMessage("bn-BD", "not.valid.input"));
    }

    [Fact]
    public void EverySupportedLanguageIsReallyTranslated()
    {
        // A bundle that is a copy of the English one ships an interface in two languages at once:
        // nothing looks broken, every screen is simply in the wrong tongue. Portuguese, Korean and
        // Japanese sat that way until this caught them.
        var catalog = CreateCatalog();

        foreach (var language in catalog.SupportedLanguages.Where(code => code != "en"))
        {
            var bundle = PropertiesBundle.Parse(File.ReadAllText(BundlePath(language)));
            var english = PropertiesBundle.Parse(File.ReadAllText(BundlePath("en")));

            foreach (var (code, text) in english.Values)
            {
                Assert.True(bundle.TryGetValue(code, out var translated), $"messages_{language}.properties is missing '{code}'");
                Assert.False(text == translated, $"messages_{language}.properties leaves '{code}' in English");
                Assert.DoesNotMatch(@"^\s*[\[(]", translated);
            }
        }
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
        var english = PropertiesBundle.Parse(File.ReadAllText(BundlePath("en")));

        Assert.Equal(17, catalog.SupportedLanguages.Count);

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

    private static string BundlePath(string language) =>
        Path.Combine(AppContext.BaseDirectory, "Messages", $"messages_{language}.properties");
}
