using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Streaming;

/// <summary>Translates options removed in FFmpeg 9 without rewriting saved settings.</summary>
internal static class NvencOptions
{
    public static IReadOnlyList<(string? Key, string? Value)> Of(VideoSettingEntity setting, string encoder)
    {
        var options = setting.VideoSettingsOptions.Select(option => (option.Key, option.Value)).ToList();
        if (!encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        // Match the old FFmpeg aliases. An explicit modern tune or multipass wins over the
        // implied value of an old preset / rate-control mode.
        string? tune = null;
        string? multipass = null;
        string? rc = null;
        for (var index = 0; index < options.Count; index++)
        {
            var (key, value) = options[index];
            var name = key?.Trim().Split(':')[0];
            var text = value?.Trim().ToLowerInvariant();
            if (name == "preset")
            {
                var preset = text switch
                {
                    "default" or "ll" or "lossless" => "p4",
                    "hp" or "llhp" or "losslesshp" => "p1",
                    "hq" or "llhq" => "p7",
                    "bd" => "p5",
                    _ => value
                };
                tune = text switch
                {
                    "ll" or "llhp" or "llhq" => "ll",
                    "lossless" or "losslesshp" => "lossless",
                    _ => tune
                };
                options[index] = (key, preset);
            }
            else if (name == "rc")
            {
                var rate = text switch
                {
                    "vbr_minqp" or "vbr_2pass" or "vbr_hq" => "vbr",
                    "ll_2pass_quality" or "ll_2pass_size" or "cbr_ld_hq" or "cbr_hq" => "cbr",
                    _ => value
                };
                if (text is "vbr_2pass" or "vbr_hq" or "ll_2pass_quality" or "cbr_ld_hq" or "cbr_hq")
                {
                    multipass ??= "fullres";
                }
                else if (text == "ll_2pass_size")
                {
                    multipass ??= "qres";
                }
                options[index] = (key, rate);
            }
            else if (name is "2pass" or "cbr")
            {
                if (name == "2pass")
                {
                    multipass = text switch
                    {
                        "1" or "true" => "qres",
                        "0" or "false" => "disabled",
                        _ => multipass
                    };
                }
                else if (text is "1" or "true")
                {
                    rc = "cbr";
                }
                options.RemoveAt(index--);
            }
        }

        AddDefault("tune", tune);
        AddDefault("multipass", multipass);
        AddDefault("rc", rc);
        return options;

        void AddDefault(string key, string? value)
        {
            if (value is not null && !options.Any(option => option.Key?.Trim().Split(':')[0] == key))
            {
                options.Add((key, value));
            }
        }
    }
}
