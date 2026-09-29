using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orbis.Stream.Core.SystemInfo;

/// <summary>
/// A fact of the machine, with the value already formatted. The label is not here: the view knows
/// it, in the language of the interface.
/// </summary>
public sealed record SystemFact(string Key, string Value)
{
    public const string Os = "os";
    public const string Architecture = "architecture";
    public const string Computer = "computer";
    public const string Cpu = "cpu";
    public const string CpuCores = "cpuCores";
    public const string CpuSpeed = "cpuSpeed";
    public const string RamTotal = "ramTotal";
    public const string RamUsed = "ramUsed";
    public const string Disk = "disk";
    public const string DiskFree = "diskFree";
    public const string Gpu = "gpu";
    public const string Uptime = "uptime";
    public const string AppUptime = "appUptime";
    public const string Dotnet = "dotnet";

    /// <summary>Bytes as Windows shows them, with the binary divisor and no locale in the way.</summary>
    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>How long something has been running, as <c>d:hh:mm:ss</c> so it needs no language.</summary>
    public static string FormatUptime(TimeSpan uptime) => uptime < TimeSpan.Zero
        ? TimeSpan.Zero.ToString("d\\.hh\\:mm\\:ss", System.Globalization.CultureInfo.InvariantCulture)
        : uptime.ToString("d\\.hh\\:mm\\:ss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Port of the OSHI calls behind <c>TaskManagerInfoComponent</c>. The contract (percentages as
/// integers, <c>-1</c> when a value is not available) is preserved for the React build.
/// </summary>
public interface ISystemInfoProvider
{
    int GetCpuPercent();

    int GetRamPercent();

    int GetSwapPercent();

    int GetCpuTemperature();

    /// <summary>
    /// Celsius of the graphics card, or -1 like the processor one when the machine has no sensor
    /// that answers it: Windows exposes no API for it, only the tool of the driver does.
    /// </summary>
    int GetGpuTemperature();

    int GetAppCpuPercent();

    int GetAppGpuPercent();

    int GetAppRamPercent();

    int GetAppDiskPercent();

    int GetAppNetworkPercent();

    /// <summary>What the machine is, as opposed to the counters above: read once per page.</summary>
    IReadOnlyList<SystemFact> GetFacts();
}

public static class SystemInfoProviderFactory
{
    public const int NotAvailable = -1;

    public static ISystemInfoProvider CreateDefault() =>
        OperatingSystem.IsWindows() ? new WindowsSystemInfoProvider() : new PortableSystemInfoProvider();
}

internal static class AppMetricsHelper
{
    private static ulong _lastNetworkBytes;
    private static DateTime _lastNetworkTime;
    private static readonly object _appNetworkLock = new();

    public static int GetAppNetworkMbps()
    {
        lock (_appNetworkLock)
        {
            try
            {
                ulong totalBytes = 0;
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        var stats = ni.GetIPStatistics();
                        totalBytes += (ulong)(stats.BytesReceived + stats.BytesSent);
                    }
                }

                var now = DateTime.UtcNow;
                if (_lastNetworkTime == default)
                {
                    _lastNetworkBytes = totalBytes;
                    _lastNetworkTime = now;
                    return 0;
                }

                var diff = totalBytes - _lastNetworkBytes;
                var elapsed = (now - _lastNetworkTime).TotalSeconds;
                _lastNetworkBytes = totalBytes;
                _lastNetworkTime = now;
                if (elapsed > 0)
                {
                    double mbps = (diff * 8 / elapsed) / 1_000_000.0;
                    return (int)Math.Min(100, Math.Round(mbps));
                }
            }
            catch { }
            return 0;
        }
    }
}

/// <summary>
/// The two temperatures in one reading, kept for a few seconds: a WMI query costs tens of
/// milliseconds and nvidia-smi is a process of its own, while the meters are pushed once a second.
/// A machine that has no sensor at all is asked far less often still, so a missing sensor costs
/// nothing once it has been found to be missing.
/// </summary>
internal sealed class TemperatureCache
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Missing = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private Reading? _reading;

    public (int Cpu, int Gpu) Read(Func<int> cpu, Func<int> gpu)
    {
        var now = DateTime.UtcNow;
        var reading = _reading;
        if (reading is not null && reading.IsUsableAt(now))
        {
            return (reading.Cpu, reading.Gpu);
        }

        lock (_lock)
        {
            // Another caller may have read it while this one waited for the lock.
            reading = _reading;
            if (reading is not null && reading.IsUsableAt(now))
            {
                return (reading.Cpu, reading.Gpu);
            }

            var values = (cpu(), gpu());
            _reading = new Reading(values.Item1, values.Item2, now);
            return values;
        }
    }

    private sealed record Reading(int Cpu, int Gpu, DateTime TakenAt)
    {
        public bool IsUsableAt(DateTime now) =>
            now - TakenAt < (Cpu == SystemInfoProviderFactory.NotAvailable && Gpu == SystemInfoProviderFactory.NotAvailable
                ? Missing
                : Fresh);
    }
}

/// <summary>Windows 11 implementation: kernel32 counters plus the ACPI thermal zone through WMI.</summary>
public sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    private const int CpuSampleDelayMilliseconds = 500;
    private const int NvidiaSmiTimeoutMilliseconds = 2000;

    /// <summary>Zones named like the processor win over the others, which are usually the board.</summary>
    private static readonly string[] ProcessorZones = ["cpu", "package", "tcpu"];

    private readonly TemperatureCache _temperatures = new();

    private readonly object _appCpuLock = new();
    private TimeSpan _lastAppCpuTime;
    private DateTime _lastAppCpuReadTime;

    public int GetCpuPercent()
    {
        if (!TryReadSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        Thread.Sleep(CpuSampleDelayMilliseconds);

        if (!TryReadSystemTimes(out var idleAfter, out var kernelAfter, out var userAfter))
        {
            return 0;
        }

        var totalDelta = (kernelAfter - kernel) + (userAfter - user);
        if (totalDelta <= 0)
        {
            return 0;
        }

        var idleDelta = idleAfter - idle;
        var load = (totalDelta - idleDelta) * 100.0 / totalDelta;
        return (int)load;
    }

    public int GetRamPercent()
    {
        if (!TryGetMemoryStatus(out var memory))
        {
            return 0;
        }

        var total = memory.TotalPhys;
        var available = memory.AvailablePhys;
        if (total <= 0)
        {
            return 0;
        }

        var used = total - available;
        return (int)((used * 100.0) / total);
    }

    public int GetSwapPercent()
    {
        if (!TryGetMemoryStatus(out var memory))
        {
            return 0;
        }

        var total = memory.TotalPageFile;
        if (total <= 0)
        {
            return 0;
        }

        var used = total - memory.AvailablePageFile;
        if (used < 0)
        {
            used = 0;
        }

        return (int)((used * 100.0) / total);
    }

    public int GetCpuTemperature() => Temperatures().Cpu;

    public int GetGpuTemperature() => Temperatures().Gpu;

    public int GetAppCpuPercent()
    {
        lock (_appCpuLock)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var currentAppCpuTime = process.TotalProcessorTime;
                var currentReadTime = DateTime.UtcNow;

                if (_lastAppCpuReadTime == default)
                {
                    _lastAppCpuTime = currentAppCpuTime;
                    _lastAppCpuReadTime = currentReadTime;
                    return 0;
                }

                var elapsedAppCpu = (currentAppCpuTime - _lastAppCpuTime).TotalMilliseconds;
                var elapsedTime = (currentReadTime - _lastAppCpuReadTime).TotalMilliseconds;

                _lastAppCpuTime = currentAppCpuTime;
                _lastAppCpuReadTime = currentReadTime;

                if (elapsedTime <= 0)
                {
                    return 0;
                }

                var load = (elapsedAppCpu / (Environment.ProcessorCount * elapsedTime)) * 100.0;
                return (int)Math.Max(0, Math.Min(100, load));
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public int GetAppGpuPercent()
    {
        try
        {
            int processId = Process.GetCurrentProcess().Id;
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery($"SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE 'pid_{processId}_%'"));
            using var collection = searcher.Get();
            int total = 0;
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    if (item["UtilizationPercentage"] is ulong val) total += (int)val;
                    else if (item["UtilizationPercentage"] is uint val2) total += (int)val2;
                }
            }
            return Math.Min(100, total);
        }
        catch { return 0; }
    }

    public int GetAppRamPercent()
    {
        try
        {
            if (!TryGetMemoryStatus(out var memory))
            {
                return 0;
            }

            var total = memory.TotalPhys;
            if (total <= 0)
            {
                return 0;
            }

            using var process = Process.GetCurrentProcess();
            var workingSet = process.WorkingSet64;
            return (int)((workingSet * 100.0) / total);
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount;
        public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    private ulong _lastIoTransferCount;
    private DateTime _lastIoReadTime;
    private readonly object _appIoLock = new();

    public int GetAppDiskPercent()
    {
        lock (_appIoLock)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                if (GetProcessIoCounters(process.Handle, out var counters))
                {
                    ulong currentTransfer = counters.ReadTransferCount + counters.WriteTransferCount;
                    var currentTime = DateTime.UtcNow;
                    if (_lastIoReadTime == default) { _lastIoTransferCount = currentTransfer; _lastIoReadTime = currentTime; return 0; }
                    var elapsedSecs = (currentTime - _lastIoReadTime).TotalSeconds;
                    var diff = currentTransfer - _lastIoTransferCount;
                    _lastIoTransferCount = currentTransfer;
                    _lastIoReadTime = currentTime;
                    if (elapsedSecs > 0) { double mbPerSec = (diff / elapsedSecs) / (1024 * 1024); return (int)Math.Min(100, Math.Round(mbPerSec)); }
                }
            }
            catch { }
            return 0;
        }
    }

    public int GetAppNetworkPercent() => AppMetricsHelper.GetAppNetworkMbps();

    /// <summary>
    /// The two in one reading: they are read together because the same round trip answers both, and
    /// the cache hands the same reading to whoever asks for it next.
    /// </summary>
    private (int Cpu, int Gpu) Temperatures() => OperatingSystem.IsWindows()
        ? _temperatures.Read(ReadProcessorTemperature, ReadGraphicsTemperature)
        : (SystemInfoProviderFactory.NotAvailable, SystemInfoProviderFactory.NotAvailable);

    /// <summary>
    /// The processor is an ACPI thermal zone, so it is read from the two classes Windows publishes
    /// it through: the one of the hardware namespace, and the same sensors as the performance
    /// counters see them. A machine that exposes neither answers -1, the same way OSHI answered
    /// when the sensor was missing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int ReadProcessorTemperature()
    {
        // MSAcpi_ThermalZoneTemperature only lives in the WMI namespace of the hardware, not in the
        // default one: without the scope the query finds no class at all and the card stays empty.
        var celsius = ReadThermalZone(
            new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\wmi"),
                new ObjectQuery("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature")));

        if (celsius != SystemInfoProviderFactory.NotAvailable)
        {
            return celsius;
        }

        // The same sensors of the thermal zone, as the performance counters of Windows see them.
        return ReadThermalZone(
            new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation")));
    }

    /// <summary>
    /// The card has no Windows API: the driver does, through the tool it installs. It answers from
    /// user space, so a per-user installation needs no elevation, and a machine with another
    /// manufacturer has no nvidia-smi and simply reports no sensor.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int ReadGraphicsTemperature()
    {
        foreach (var tool in NvidiaSmiLocations())
        {
            if (Path.IsPathRooted(tool) && !File.Exists(tool))
            {
                continue;
            }

            if (Capture(tool, "--query-gpu=temperature.gpu --format=csv,noheader,nounits") is not { } output)
            {
                continue;
            }

            // One line per card, and the hottest of them is the one worth a meter.
            var hottest = SystemInfoProviderFactory.NotAvailable;
            foreach (var line in output.Split('\n'))
            {
                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var celsius)
                    && IsTemperatureOfSomething(celsius))
                {
                    hottest = Math.Max(hottest, celsius);
                }
            }

            if (hottest != SystemInfoProviderFactory.NotAvailable)
            {
                return hottest;
            }
        }

        return SystemInfoProviderFactory.NotAvailable;
    }

    /// <summary>Where the driver puts the tool: the system folders and whatever is on the path.</summary>
    private static string[] NvidiaSmiLocations()
    {
        var windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        var locations = new List<string>();

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            locations.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
        }

        locations.Add(Path.Combine(windows, "System32", "nvidia-smi.exe"));
        locations.Add(Path.Combine(windows, "Sys32", "nvidia-smi.exe"));
        locations.Add(Path.Combine(windows, "SysArm32", "nvidia-smi.exe"));

        // Not a path: the search the shell would do, for a driver that installed the tool elsewhere.
        locations.Add("nvidia-smi");
        return [.. locations];
    }

    /// <summary>What the tool of the driver printed, or null when it is missing, hangs or fails.</summary>
    [SupportedOSPlatform("windows")]
    private static string? Capture(string executable, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return null;
            }

            // The answer is a couple of numbers, so reading it to the end cannot fill the pipe.
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(NvidiaSmiTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // No driver, no tool, no permission: the card has no sensor to show.
            return null;
        }
    }

    /// <summary>
    /// Celsius of the thermal zone that names the processor, or of the first one that reads a
    /// plausible temperature: a board zone is still the closest thing to the processor on a
    /// machine that names none.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int ReadThermalZone(ManagementObjectSearcher searcher)
    {
        try
        {
            using (searcher)
            {
                using var collection = searcher.Get();
                var fallback = SystemInfoProviderFactory.NotAvailable;

                foreach (ManagementBaseObject item in collection)
                {
                    using (item)
                    {
                        // Both classes report tenths of a kelvin, under a different property name.
                        var raw = NumberOf(item, "CurrentTemperature") ?? NumberOf(item, "Temperature");
                        if (raw is null)
                        {
                            continue;
                        }

                        var celsius = (int)Math.Round((raw.Value / 10.0) - 273.15);
                        if (!IsTemperatureOfSomething(celsius))
                        {
                            // A thermal zone is not always the processor, and a machine reporting an
                            // impossible value has no sensor worth showing.
                            continue;
                        }

                        // The performance counters name the zone in Name, the ACPI class in InstanceName.
                        var zone = (item["InstanceName"] ?? item["Name"])?.ToString() ?? string.Empty;
                        if (ProcessorZones.Any(name => zone.Contains(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            return celsius;
                        }

                        if (fallback == SystemInfoProviderFactory.NotAvailable)
                        {
                            fallback = celsius;
                        }
                    }
                }

                return fallback;
            }
        }
        catch (Exception)
        {
            // Temperature is optional: OSHI returned NaN (-1 here) when the sensor is missing.
        }

        return SystemInfoProviderFactory.NotAvailable;
    }

    /// <summary>A reading no machine of this size reports is a sensor that is not really one.</summary>
    internal static bool IsTemperatureOfSomething(int celsius) => celsius is >= 1 and <= 120;

    /// <summary>A WMI property as a number, whatever unsigned type the schema chose for it.</summary>
    [SupportedOSPlatform("windows")]
    private static double? NumberOf(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property] switch
            {
                ulong value => value,
                uint value => value,
                ushort value => value,
                int value => value,
                double value => value,
                _ => null
            };
        }
        catch (Exception)
        {
            // A class that does not have the property: the other class carries it.
            return null;
        }
    }

    /// <summary>
    /// The properties of the first object of a WMI class, in one query: every round trip to WMI
    /// costs tens of milliseconds, and the panel asks for a dozen properties.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyDictionary<string, string> FirstOf(string scope, string className, params string[] properties)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(scope),
                new ObjectQuery($"SELECT {string.Join(", ", properties)} FROM {className}"));
            using var collection = searcher.Get();

            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    var values = new Dictionary<string, string>(properties.Length, StringComparer.OrdinalIgnoreCase);
                    foreach (var property in properties)
                    {
                        var value = item[property]?.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values[property] = value.Trim();
                        }
                    }

                    if (values.Count > 0)
                    {
                        return values;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Windows without the class (a stripped image, a remote session): the fact is left out.
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Of(IReadOnlyDictionary<string, string> values, string property) =>
        values.TryGetValue(property, out var value) ? value : null;

    /// <summary>What the machine is, read once per page: unlike the counters above, it does not move.</summary>
    [SupportedOSPlatform("windows")]
    public IReadOnlyList<SystemFact> GetFacts()
    {
        const string wmi = @"\\.\root\cimv2";
        var facts = new List<SystemFact>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new SystemFact(key, value.Trim()));
            }
        }

        var operatingSystem = FirstOf(wmi, "Win32_OperatingSystem", "Caption", "Version", "OSArchitecture", "LastBootUpTime");
        var caption = Of(operatingSystem, "Caption");
        var version = Of(operatingSystem, "Version");
        Add(SystemFact.Os, version is null ? caption : $"{caption} {version}");
        Add(SystemFact.Architecture, Of(operatingSystem, "OSArchitecture"));

        var computer = Environment.MachineName;
        var system = FirstOf(wmi, "Win32_ComputerSystem", "Manufacturer", "Model");
        var manufacturer = Of(system, "Manufacturer");
        var model = Of(system, "Model");
        Add(SystemFact.Computer, manufacturer is null && model is null
            ? computer
            : $"{computer} ({manufacturer} {model})");

        var processor = FirstOf(wmi, "Win32_Processor", "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed");
        Add(SystemFact.Cpu, Of(processor, "Name"));

        var cores = Of(processor, "NumberOfCores");
        var threads = Of(processor, "NumberOfLogicalProcessors");
        Add(SystemFact.CpuCores, cores is null && threads is null
            ? null
            : $"{threads ?? "–"} thread / {cores ?? "–"} core");

        Add(SystemFact.CpuSpeed, Gigahertz(Of(processor, "MaxClockSpeed")));

        if (TryGetMemoryStatus(out var memory))
        {
            Add(SystemFact.RamTotal, SystemFact.FormatBytes(memory.TotalPhys));
            Add(SystemFact.RamUsed, SystemFact.FormatBytes(memory.TotalPhys - Math.Min(memory.AvailablePhys, memory.TotalPhys)));
        }

        var boot = Of(operatingSystem, "LastBootUpTime");
        if (boot is not null && TryReadWmiTime(boot, out var bootedAt))
        {
            Add(SystemFact.Uptime, SystemFact.FormatUptime(DateTime.Now - bootedAt));
        }

        var drive = SystemDrive();
        if (drive is not null)
        {
            Add(SystemFact.Disk, $"{drive.Name} {SystemFact.FormatBytes((ulong)drive.TotalSize)}");
            Add(SystemFact.DiskFree, SystemFact.FormatBytes((ulong)drive.AvailableFreeSpace));
        }

        Add(SystemFact.Gpu, Of(FirstOf(wmi, "Win32_VideoController", "Name"), "Name"));
        Add(SystemFact.AppUptime, SystemFact.FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        Add(SystemFact.Dotnet, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

        return facts;
    }

    /// <summary>Clock of the processor as Windows reports it, in the unit a human reads.</summary>
    private static string? Gigahertz(string? megahertz)    {
        if (megahertz is null || !int.TryParse(megahertz, out var hertz))
        {
            return null;
        }

        return hertz >= 1000
            ? (hertz / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GHz"
            : $"{hertz} MHz";
    }

    private static DriveInfo? SystemDrive()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            return new DriveInfo(root);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// WMI answers a date in its own DMTF form, whose offset counts minutes, not hours: it is
    /// <c>20260928101500.000000+060</c> for a machine one hour ahead of UTC. Parsing it as a plain
    /// date would read that offset as six hours.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryReadWmiTime(string value, out DateTime moment)
    {
        try
        {
            moment = ManagementDateTimeConverter.ToDateTime(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            moment = default;
            return false;
        }
    }

    private static bool TryReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = 0;
        kernel = 0;
        user = 0;

        if (!GetSystemTimes(out idle, out kernel, out user))
        {
            return false;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailablePhys;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static bool TryGetMemoryStatus(out MemoryStatusEx status)
    {
        status = default;
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref status);
    }
}

/// <summary>
/// Linux/macOS implementation so the server and the test-suite can run on any developer machine;
/// the Windows build always uses <see cref="WindowsSystemInfoProvider"/>.
/// </summary>
public sealed class PortableSystemInfoProvider : ISystemInfoProvider
{
    private const int CpuSampleDelayMilliseconds = 500;

    private readonly TemperatureCache _temperatures = new();

    private readonly object _appCpuLock = new();
    private TimeSpan _lastAppCpuTime;
    private DateTime _lastAppCpuReadTime;

    public int GetCpuPercent()
    {
        if (!TryReadLinuxCpuTimes(out var idle, out var total))
        {
            return 0;
        }

        Thread.Sleep(CpuSampleDelayMilliseconds);

        if (!TryReadLinuxCpuTimes(out var idleAfter, out var totalAfter))
        {
            return 0;
        }

        var totalDelta = totalAfter - total;
        if (totalDelta <= 0)
        {
            return 0;
        }

        var idleDelta = idleAfter - idle;
        return (int)(((totalDelta - idleDelta) * 100.0) / totalDelta);
    }

    public int GetRamPercent()
    {
        if (!TryReadLinuxMemory(out var totalKilobytes, out var availableKilobytes))
        {
            return 0;
        }

        if (totalKilobytes <= 0)
        {
            return 0;
        }

        var used = totalKilobytes - availableKilobytes;
        return (int)((used * 100.0) / totalKilobytes);
    }

    public int GetSwapPercent()
    {
        if (!TryReadLinuxMemory(out var totalKilobytes, out var availableKilobytes, swap: true))
        {
            return 0;
        }

        if (totalKilobytes <= 0)
        {
            return 0;
        }

        var used = totalKilobytes - availableKilobytes;
        if (used < 0)
        {
            used = 0;
        }

        return (int)((used * 100.0) / totalKilobytes);
    }

    public int GetCpuTemperature() => _temperatures.Read(ReadCpuTemperature, ReadGpuTemperature).Cpu;

    public int GetGpuTemperature() => _temperatures.Read(ReadCpuTemperature, ReadGpuTemperature).Gpu;

    public int GetAppCpuPercent()
    {
        lock (_appCpuLock)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var currentAppCpuTime = process.TotalProcessorTime;
                var currentReadTime = DateTime.UtcNow;

                if (_lastAppCpuReadTime == default)
                {
                    _lastAppCpuTime = currentAppCpuTime;
                    _lastAppCpuReadTime = currentReadTime;
                    return 0;
                }

                var elapsedAppCpu = (currentAppCpuTime - _lastAppCpuTime).TotalMilliseconds;
                var elapsedTime = (currentReadTime - _lastAppCpuReadTime).TotalMilliseconds;

                _lastAppCpuTime = currentAppCpuTime;
                _lastAppCpuReadTime = currentReadTime;

                if (elapsedTime <= 0)
                {
                    return 0;
                }

                var load = (elapsedAppCpu / (Environment.ProcessorCount * elapsedTime)) * 100.0;
                return (int)Math.Max(0, Math.Min(100, load));
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public int GetAppGpuPercent() => 0;

    public int GetAppRamPercent()
    {
        try
        {
            if (!TryReadLinuxMemory(out var totalKilobytes, out var availableKilobytes))
            {
                return 0;
            }

            if (totalKilobytes <= 0)
            {
                return 0;
            }

            using var process = Process.GetCurrentProcess();
            var workingSetKilobytes = process.WorkingSet64 / 1024;
            return (int)((workingSetKilobytes * 100.0) / totalKilobytes);
        }
        catch
        {
            return 0;
        }
    }

    private ulong _lastLinuxDiskBytes;
    private DateTime _lastLinuxDiskTime;
    private readonly object _appLinuxDiskLock = new();

    public int GetAppDiskPercent()
    {
        lock (_appLinuxDiskLock)
        {
            try
            {
                var lines = File.ReadAllLines("/proc/self/io");
                ulong readBytes = 0, writeBytes = 0;
                foreach (var line in lines)
                {
                    if (line.StartsWith("read_bytes:", StringComparison.Ordinal)) readBytes = ulong.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                    if (line.StartsWith("write_bytes:", StringComparison.Ordinal)) writeBytes = ulong.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                }
                ulong total = readBytes + writeBytes;
                var now = DateTime.UtcNow;
                if (_lastLinuxDiskTime == default) { _lastLinuxDiskBytes = total; _lastLinuxDiskTime = now; return 0; }
                var diff = total - _lastLinuxDiskBytes;
                var elapsed = (now - _lastLinuxDiskTime).TotalSeconds;
                _lastLinuxDiskBytes = total;
                _lastLinuxDiskTime = now;
                if (elapsed > 0)
                {
                    double mbPerSec = (diff / elapsed) / (1024 * 1024);
                    return (int)Math.Min(100, Math.Round(mbPerSec));
                }
            }
            catch { }
            return 0;
        }
    }

    public int GetAppNetworkPercent() => AppMetricsHelper.GetAppNetworkMbps();

    private static int ReadCpuTemperature()
    {
        foreach (var sensor in ThermalSensors())
        {
            if (TryReadMilliCelsius(sensor) is { } celsius)
            {
                return celsius;
            }
        }

        return SystemInfoProviderFactory.NotAvailable;
    }

    private static int ReadGpuTemperature()
    {
        // The kernels without a thermal zone of their own keep the sensor of the card in the hwmon
        // folder of the device, so a machine of this kind answers what the driver publishes.
        foreach (var sensor in DeviceSensors())
        {
            if (TryReadMilliCelsius(Path.Combine(sensor, "temp1_input")) is { } celsius)
            {
                return celsius;
            }
        }

        return SystemInfoProviderFactory.NotAvailable;
    }

    private static IReadOnlyList<string> ThermalSensors()
    {
        // Only the thermal zones of the top level are read: the entries below them are symlinks to
        // the device tree, and a recursive enumeration walks the whole sysfs graph.
        try
        {
            return Directory.Exists("/sys/class/thermal")
                ? [.. Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*")
                    .Select(zone => Path.Combine(zone, "temp"))]
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A machine whose sysfs is not readable has no sensor to show, which is not an error.
            return [];
        }
    }

    /// <summary>The hwmon folder of the card, which the driver of an AMD or Intel graphics publishes.</summary>
    private static IReadOnlyList<string> DeviceSensors()
    {
        var sensors = new List<string>();

        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                var hwmon = Path.Combine(card, "device", "hwmon");
                if (!Directory.Exists(hwmon))
                {
                    continue;
                }

                sensors.AddRange(Directory.EnumerateDirectories(hwmon, "hwmon*"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A machine whose sysfs is not readable has no sensor to show, which is not an error.
        }

        return sensors;
    }

    private static int? TryReadMilliCelsius(string sensor)
    {
        try
        {
            var raw = File.ReadAllText(sensor).Trim();
            if (long.TryParse(raw, out var milliCelsius) && WindowsSystemInfoProvider.IsTemperatureOfSomething((int)(milliCelsius / 1000)))
            {
                return (int)(milliCelsius / 1000);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignore unreadable sensors.
        }

        return null;
    }

    /// <summary>The same facts of the Windows one, from the sources a developer machine has.</summary>
    public IReadOnlyList<SystemFact> GetFacts()
    {
        var facts = new List<SystemFact>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new SystemFact(key, value.Trim()));
            }
        }

        Add(SystemFact.Os, RuntimeInformation.OSDescription);
        Add(SystemFact.Architecture, RuntimeInformation.OSArchitecture.ToString());
        Add(SystemFact.Computer, Environment.MachineName);
        Add(SystemFact.Cpu, CpuModelName());
        Add(SystemFact.CpuCores, $"{Environment.ProcessorCount} thread");

        var kilobytes = LinuxMemory();
        if (kilobytes is { } memory && memory.Total > 0)
        {
            Add(SystemFact.RamTotal, SystemFact.FormatBytes((ulong)memory.Total * 1024));
            Add(SystemFact.RamUsed, SystemFact.FormatBytes((ulong)Math.Max(memory.Total - memory.Available, 0) * 1024));
        }

        Add(SystemFact.Uptime, LinuxUptime());
        Add(SystemFact.AppUptime, SystemFact.FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        Add(SystemFact.Dotnet, RuntimeInformation.FrameworkDescription);

        return facts;
    }

    private static string? CpuModelName()
    {
        foreach (var line in ReadLines("/proc/cpuinfo"))
        {
            foreach (var field in new[] { "model name", "Model" })
            {
                var prefix = field + "\t";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line[prefix.Length..].Trim();
                }
            }
        }

        return null;
    }

    private static string? LinuxUptime()
    {
        if (ReadLines("/proc/uptime").FirstOrDefault() is not { } line
            || !double.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return SystemFact.FormatUptime(TimeSpan.FromSeconds(seconds));
    }

    private static (long Total, long Available)? LinuxMemory()
    {
        if (!File.Exists("/proc/meminfo"))
        {
            return null;
        }

        long total = 0;
        long available = 0;
        foreach (var line in ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal", StringComparison.Ordinal))
            {
                total = ParseKilobytes(line);
            }
            else if (line.StartsWith("MemAvailable", StringComparison.Ordinal))
            {
                available = ParseKilobytes(line);
            }
        }

        return total > 0 ? (total, available) : null;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.ReadLines(path).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool TryReadLinuxCpuTimes(out ulong idle, out ulong total)
    {
        idle = 0;
        total = 0;
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault() ?? string.Empty;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu")
            {
                return false;
            }

            for (var index = 1; index < parts.Length; index++)
            {
                if (!ulong.TryParse(parts[index], out var value))
                {
                    continue;
                }

                total += value;
                if (index is 4 or 5)
                {
                    idle += value;
                }
            }

            return total > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryReadLinuxMemory(out long totalKilobytes, out long availableKilobytes, bool swap = false)
    {
        totalKilobytes = 0;
        availableKilobytes = 0;
        var totalKey = swap ? "SwapTotal" : "MemTotal";
        var availableKey = swap ? "SwapFree" : "MemAvailable";

        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith(totalKey, StringComparison.Ordinal))
                {
                    totalKilobytes = ParseKilobytes(line);
                }
                else if (line.StartsWith(availableKey, StringComparison.Ordinal))
                {
                    availableKilobytes = ParseKilobytes(line);
                }
            }

            return totalKilobytes > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static long ParseKilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var value) ? value : 0;
    }
}
