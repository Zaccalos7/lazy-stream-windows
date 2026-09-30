namespace Orbis.Stream.Core.Domain;

/// <summary>Port of <c>com.orbis.stream.enums.SystemInfoEnum</c>; the names are part of the API contract.</summary>
public enum SystemInfoField
{
    Cpu,
    Ram,
    Swap,
    CpuTemperature,
    GpuTemperature,
    AppCpu,
    AppGpu,
    AppRam,
    AppDisk,
    AppNetwork
}

public static class SystemInfoFieldExtensions
{
    public static string ToInfoName(this SystemInfoField field) => field switch
    {
        SystemInfoField.Cpu => "CPU",
        SystemInfoField.Ram => "RAM",
        SystemInfoField.Swap => "SWAP",
        SystemInfoField.CpuTemperature => "TEMPERATURA CPU",
        SystemInfoField.GpuTemperature => "TEMPERATURA GPU",
        SystemInfoField.AppCpu => "APP CPU",
        SystemInfoField.AppGpu => "APP GPU",
        SystemInfoField.AppRam => "APP RAM",
        SystemInfoField.AppDisk => "APP DISK",
        SystemInfoField.AppNetwork => "APP NETWORK",
        _ => field.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// The name of the meter on the task manager page: the push channel sends each value under it
    /// and the card that shows it carries the same one, so this is the only place that pairs them.
    /// </summary>
    public static string ToMeterKey(this SystemInfoField field) => field switch
    {
        SystemInfoField.Cpu => "cpu",
        SystemInfoField.Ram => "ram",
        SystemInfoField.Swap => "swap",
        SystemInfoField.CpuTemperature => "cpu_temperature",
        SystemInfoField.GpuTemperature => "gpu_temperature",
        SystemInfoField.AppCpu => "app_cpu",
        SystemInfoField.AppGpu => "app_gpu",
        SystemInfoField.AppRam => "app_ram",
        SystemInfoField.AppDisk => "app_disk",
        SystemInfoField.AppNetwork => "app_network",
        _ => field.ToString().ToLowerInvariant()
    };
}

/// <summary>Port of <c>com.orbis.stream.enums.VideoExtensionEnum</c>.</summary>
public static class VideoExtensions
{
    private static readonly string[] Supported = ["mp4", "flv", "mov", "webm", "vp9", "mkv"];

    public static IReadOnlyList<string> All => Supported;

    public static bool IsVideoExtensionPresent(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return Supported.Any(supported => supported.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Content type of the file the preview serves. It is not the one the container would like:
    /// what matters is what the WebView can decode, and the values below are what Chromium
    /// answers to for these extensions.
    /// </summary>
    public static string ContentTypeOf(string? extension) => extension?.ToLowerInvariant() switch
    {
        "mp4" or "m4v" => "video/mp4",
        "mov" => "video/quicktime",
        "webm" or "vp9" => "video/webm",
        "flv" => "video/x-flv",
        "mkv" => "video/x-matroska",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Whether the WebView can play the container on its own. Matroska and FLV are the two the
    /// preview cannot show: they are still served, so the frame carries the reason instead of
    /// failing on something the user cannot see.
    /// </summary>
    public static bool IsBrowserPlayable(string? extension) =>
        extension?.ToLowerInvariant() is "mp4" or "m4v" or "mov" or "webm" or "vp9";
}
