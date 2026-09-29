using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.SystemInfo;

namespace Orbis.Stream.Core.Services;

/// <summary>Port of <c>com.orbis.stream.component.TaskManagerInfoComponent</c> and its controller.</summary>
public sealed class SystemInfoService
{
    private readonly ISystemInfoProvider _provider;
    private readonly Localizer _localizer;
    private readonly ILogger<SystemInfoService> _logger;

    public SystemInfoService(ISystemInfoProvider provider, Localizer localizer, ILogger<SystemInfoService> logger)
    {
        _provider = provider;
        _localizer = localizer;
        _logger = logger;
    }

    public IReadOnlyList<SystemInfoResponse> GetAllSystemInfo()
    {
        var response = new List<SystemInfoResponse>
        {
            new(SystemInfoField.Cpu.ToInfoName(), GetCpuPercent()),
            new(SystemInfoField.Ram.ToInfoName(), GetRamPercent()),
            new(SystemInfoField.Swap.ToInfoName(), GetSwapPercent()),
            new(SystemInfoField.CpuTemperature.ToInfoName(), GetCpuTemperature()),
            new(SystemInfoField.GpuTemperature.ToInfoName(), GetGpuTemperature()),
            new(SystemInfoField.AppCpu.ToInfoName(), GetAppCpuPercent()),
            new(SystemInfoField.AppGpu.ToInfoName(), GetAppGpuPercent()),
            new(SystemInfoField.AppRam.ToInfoName(), GetAppRamPercent()),
            new(SystemInfoField.AppDisk.ToInfoName(), GetAppDiskPercent()),
            new(SystemInfoField.AppNetwork.ToInfoName(), GetAppNetworkPercent())
        };

        _logger.LogTrace("{Message}", _localizer.PrintMessage("get.all.system.info"));
        return response;
    }

    public int GetCpuPercent()
    {
        var cpuPercent = _provider.GetCpuPercent();
        _logger.LogInformation("{Message}", _localizer.PrintMessage("get.cpu.info"));
        return cpuPercent;
    }

    public int GetRamPercent()
    {
        var ramPercent = _provider.GetRamPercent();
        _logger.LogInformation("{Message}", _localizer.PrintMessage("get.ram.info"));
        return ramPercent;
    }

    public int GetSwapPercent()
    {
        var swapPercent = _provider.GetSwapPercent();
        _logger.LogInformation("{Message}", _localizer.PrintMessage("get.swap.info"));
        return swapPercent;
    }

    public int GetCpuTemperature() => Temperature(_provider.GetCpuTemperature(), "get.cpu.temp.info", "cpu.temp.not.available");

    /// <summary>
    /// The card, not the processor: Windows has no API for it, so a machine whose driver ships no
    /// tool that answers has no sensor to show and the meter stays out of the page.
    /// </summary>
    public int GetGpuTemperature() => Temperature(_provider.GetGpuTemperature(), "get.gpu.temp.info", "gpu.temp.not.available");

    public int GetAppCpuPercent()
    {
        return _provider.GetAppCpuPercent();
    }

    public int GetAppGpuPercent()
    {
        return _provider.GetAppGpuPercent();
    }

    public int GetAppRamPercent()
    {
        return _provider.GetAppRamPercent();
    }

    public int GetAppDiskPercent()
    {
        return _provider.GetAppDiskPercent();
    }

    public int GetAppNetworkPercent()
    {
        return _provider.GetAppNetworkPercent();
    }

    /// <summary>-1 is how a provider says the machine has no sensor, the dash the meter would show.</summary>
    private int Temperature(int celsius, string read, string missing)
    {
        if (celsius < 0)
        {
            _logger.LogTrace("{Message}", _localizer.PrintMessage(missing));
        }
        else
        {
            _logger.LogInformation("{Message}", _localizer.PrintMessage(read));
        }

        return celsius;
    }

    /// <summary>
    /// What the machine is. Unlike the counters above, nothing here changes while the page is open,
    /// so the caller reads it once and keeps it out of the refresh of the meters.
    /// </summary>
    public IReadOnlyList<SystemFact> GetSystemFacts() => _provider.GetFacts();
}
