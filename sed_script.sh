sed -i 's/private readonly Lock _appCpuLock = new();/private readonly object _appCpuLock = new();/g' src/Orbis.Stream.Core/SystemInfo/SystemInfoProviders.cs
