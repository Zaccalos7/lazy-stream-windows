using System.Windows;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Web.WebView2.Core;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.App;

/// <summary>
/// Desktop shell of the ported application: it starts the ASP.NET Core host in-process and
/// shows its Razor pages inside WebView2, like the JCEF window of the Java version did.
/// </summary>
public partial class App : Application
{
    private WebApplication? _host;
    private OrbisRuntimeOptions? _options;
    private Task<CoreWebView2Environment>? _browserEnvironment;
    private readonly HashSet<MainWindow> _windows = [];
    private bool _shuttingDown;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        OrbisRuntimeOptions options;
        try
        {
            options = OrbisHost.ResolveOptions(e.Args);
            _options = options;
            _host = OrbisHost.Create(options, e.Args);
            await _host.StartAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                AppText.StartError(exception.Message),
                "Orbis Stream",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!options.EmbeddedBrowser)
        {
            // Same as LAZY_STREAM_EMBEDDED_BROWSER=false: the server keeps running without a window.
            return;
        }

        OpenWindow();
    }

    /// <summary>All windows share one server and WebView2 environment, with independent pages.</summary>
    internal void OpenWindow(string? entryPoint = null)
    {
        if (_shuttingDown || _options is null)
        {
            return;
        }

        _browserEnvironment ??= CreateBrowserEnvironmentAsync(_options);
        var window = new MainWindow(_options, _browserEnvironment, entryPoint);
        _windows.Add(window);
        MainWindow ??= window;
        window.Closed += OnWindowClosed;
        window.Show();
    }

    private static async Task<CoreWebView2Environment> CreateBrowserEnvironmentAsync(OrbisRuntimeOptions options)
    {
        var userDataFolder = System.IO.Path.Combine(options.DataDirectory, "webview2");
        System.IO.Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder).ConfigureAwait(true);
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window || !_windows.Remove(window))
        {
            return;
        }

        if (MainWindow == window)
        {
            MainWindow = _windows.FirstOrDefault();
        }

        if (_windows.Count != 0 || _shuttingDown)
        {
            return;
        }

        // Keep the dispatcher alive until FFmpeg and Kestrel stop. Closing any earlier window,
        // including the first one, only disposes that window's browser.
        _shuttingDown = true;
        try
        {
            await ShutdownHostAsync().ConfigureAwait(true);
        }
        finally
        {
            Shutdown();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await ShutdownHostAsync().ConfigureAwait(true);
        base.OnExit(e);
    }

    private async Task ShutdownHostAsync()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            if (_host.Services.GetService(typeof(StreamingSessionRegistry)) is StreamingSessionRegistry sessions)
            {
                await sessions.StopAllAsync().ConfigureAwait(false);
            }

            if (_host.Services.GetService(typeof(StreamingService)) is StreamingService streaming)
            {
                streaming.Shutdown();
            }

            if (_host.Services.GetService(typeof(BackgroundTaskExecutor)) is BackgroundTaskExecutor executor)
            {
                await executor.DisposeAsync().ConfigureAwait(false);
            }

            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _host.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Shutdown error: {exception}");
        }
        finally
        {
            await _host.DisposeAsync().ConfigureAwait(true);
            _host = null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            AppText.UnexpectedError(e.Exception.Message),
            "Orbis Stream",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
