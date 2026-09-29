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
}
