import re

with open("src/Orbis.Stream.Tests/DomainAndPagingTests.cs", "r") as f:
    content = f.read()

domain_and_paging_1 = r"""    public void StartAsync_SeedsTikTokStandingUp\(\)"""
content = re.sub(domain_and_paging_1, '    public void StartAsync_SeedsVerticalPlatformsStandingUp()', content, count=1)

domain_and_paging_2 = r"""        var low = settings.FindByTitleAndPlatform\("Default Low TikTok", "TikTok"\);
        Assert.NotNull\(low\);
        Assert.False\(low.IsDefaultConfiguration\);
        Assert.Equal\(\(720, 1280, 2_500_000\), \(low.VideoWidth, low.VideoHeight, low.VideoBitrate\)\);
    \}"""
domain_and_paging_2_new = """        var low = settings.FindByTitleAndPlatform("Default Low TikTok", "TikTok");
        Assert.NotNull(low);
        Assert.False(low.IsDefaultConfiguration);
        Assert.Equal((720, 1280, 2_500_000), (low.VideoWidth, low.VideoHeight, low.VideoBitrate));

        var instagram = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Instagram"));
        Assert.Equal("Default Instagram", instagram.Title);
        Assert.Equal((1080, 1920, 30d), (instagram.VideoWidth, instagram.VideoHeight, instagram.FrameRate));
        Assert.Equal(4_500_000, instagram.VideoBitrate);

        var lowInstagram = settings.FindByTitleAndPlatform("Default Low Instagram", "Instagram");
        Assert.NotNull(lowInstagram);
        Assert.False(lowInstagram.IsDefaultConfiguration);
        Assert.Equal((720, 1280, 2_500_000), (lowInstagram.VideoWidth, lowInstagram.VideoHeight, lowInstagram.VideoBitrate));
    }"""
content = re.sub(domain_and_paging_2, domain_and_paging_2_new, content, count=1)

domain_and_paging_3 = r"""        foreach \(var platform in new\[\] \{ "Twitch", "Youtube", "Kick", "Facebook Gaming", "TikTok" \}\)"""
content = re.sub(domain_and_paging_3, '        foreach (var platform in new[] { "Twitch", "Youtube", "Kick", "Facebook Gaming", "TikTok", "Instagram" })', content, count=1)

domain_and_paging_4 = r"""    private const string TikTok = "rtmp://push-rtmp-l11-va01.tiktokcdn.com/stage";"""
content = re.sub(domain_and_paging_4, '    private const string TikTok = "rtmp://push-rtmp-l11-va01.tiktokcdn.com/stage";\n    private const string Instagram = "rtmps://live-upload.instagram.com:443/rtmp/";', content, count=1)

domain_and_paging_5 = r"""    \[InlineData\(TikTok, "tiktok"\)\]"""
content = re.sub(domain_and_paging_5, '    [InlineData(TikTok, "tiktok")]\n    [InlineData(Instagram, "instagram")]', content, count=1)

domain_and_paging_6 = r"""    public void UrlOf_TikTokIsTheLiveOfTheAccount\(\)
    \{
        Assert.Equal\("https://www.tiktok.com/@repro.channel/live", LiveLinkView.UrlOf\(TikTok, "@repro.channel", "tiktok"\)\);
        Assert.Equal\("https://www.tiktok.com/@repro/live", LiveLinkView.UrlOf\("rtmp://push.tiktokcdn-us.com/live", "repro", null\)\);
    \}"""
domain_and_paging_6_new = """    public void UrlOf_TikTokIsTheLiveOfTheAccount()
    {
        Assert.Equal("https://www.tiktok.com/@repro.channel/live", LiveLinkView.UrlOf(TikTok, "@repro.channel", "tiktok"));
        Assert.Equal("https://www.tiktok.com/@repro/live", LiveLinkView.UrlOf("rtmp://push.tiktokcdn-us.com/live", "repro", null));
    }

    [Fact]
    public void UrlOf_InstagramIsTheAccountPage()
    {
        Assert.Equal("https://www.instagram.com/repro.channel", LiveLinkView.UrlOf(Instagram, "@repro.channel", "instagram"));
        Assert.Equal("https://www.instagram.com/repro", LiveLinkView.UrlOf("rtmps://live-upload.instagram.com:443/rtmp/", "repro", null));
    }"""
content = re.sub(domain_and_paging_6, domain_and_paging_6_new, content, count=1)

domain_and_paging_7 = r"""        Assert.Equal\("TikTok", LiveLinkView.LabelOf\("tiktok"\)\);"""
content = re.sub(domain_and_paging_7, '        Assert.Equal("TikTok", LiveLinkView.LabelOf("tiktok"));\n        Assert.Equal("Instagram", LiveLinkView.LabelOf("instagram"));', content, count=1)

domain_and_paging_8 = r"""        Assert.StartsWith\("M12.525.02", LiveLinkView.MarkupOf\("tiktok"\), StringComparison.Ordinal\);"""
content = re.sub(domain_and_paging_8, '        Assert.StartsWith("M12.525.02", LiveLinkView.MarkupOf("tiktok"), StringComparison.Ordinal);\n        Assert.StartsWith("M12 2.163", LiveLinkView.MarkupOf("instagram"), StringComparison.Ordinal);', content, count=1)

domain_and_paging_9 = r"""        var marks = new\[\] \{ "twitch", "youtube", "kick", "facebook", "tiktok", null \}\.Select\(LiveLinkView.MarkupOf\).ToList\(\);"""
content = re.sub(domain_and_paging_9, '        var marks = new[] { "twitch", "youtube", "kick", "facebook", "tiktok", "instagram", null }.Select(LiveLinkView.MarkupOf).ToList();', content, count=1)

domain_and_paging_10 = r"""        Assert.Equal\("platform-tiktok", LiveLinkView.ClassOf\("tiktok"\)\);"""
content = re.sub(domain_and_paging_10, '        Assert.Equal("platform-tiktok", LiveLinkView.ClassOf("tiktok"));\n        Assert.Equal("platform-instagram", LiveLinkView.ClassOf("instagram"));', content, count=1)

with open("src/Orbis.Stream.Tests/DomainAndPagingTests.cs", "w") as f:
    f.write(content)

with open("src/Orbis.Stream.Tests/EncoderQualityTests.cs", "r") as f:
    content = f.read()

encoder_quality_1 = r"""            .Where\(setting => setting.DefaultPlatformConfiguration is "Twitch" or "Youtube" or "Kick" or "Facebook Gaming" or "TikTok"\)"""
content = re.sub(encoder_quality_1, '            .Where(setting => setting.DefaultPlatformConfiguration is "Twitch" or "Youtube" or "Kick" or "Facebook Gaming" or "TikTok" or "Instagram")', content, count=1)

with open("src/Orbis.Stream.Tests/EncoderQualityTests.cs", "w") as f:
    f.write(content)
