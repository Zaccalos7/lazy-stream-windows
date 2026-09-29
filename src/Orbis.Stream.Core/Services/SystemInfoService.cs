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
            new(SystemInfoField.CpuTemperature.ToInfoName(), GetCpuTemperature())
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

    public int GetCpuTemperature()
    {
        var value = _provider.GetCpuTemperature();
        if (value == 0)
        {
            _logger.LogWarning("{Message}", _localizer.PrintMessage("cpu.temp.not.available"));
        }
        else
        {
            _logger.LogInformation("{Message}", _localizer.PrintMessage("get.cpu.temp.info"));
        }

        return value;
    }

    /// <summary>
    /// What the machine is. Unlike the counters above, nothing here changes while the page is open,
    /// so the caller reads it once and keeps it out of the refresh of the meters.
    /// </summary>
    public IReadOnlyList<SystemFact> GetSystemFacts() => _provider.GetFacts();
}
