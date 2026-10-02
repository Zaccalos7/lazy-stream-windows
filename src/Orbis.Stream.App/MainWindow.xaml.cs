using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Web.WebView2.Core;
using Orbis.Stream.Core.Configuration;

namespace Orbis.Stream.App;

/// <summary>
/// WebView2 host window: it shows the Razor pages served by the in-process Kestrel host,
/// replacing the JCEF window of the Java version.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// The width, in DIPs, the pages were laid out for. A window wider than this gets the pages
    /// zoomed by the difference, so a 2K or 4K screen shows the same layout a Full HD one does
    /// instead of the same small text spread over twice the room.
    /// </summary>
    private const double DesignWidth = 1920d;

    /// <summary>A 4K screen at 100% scaling is twice the design width: past it nothing is gained.</summary>
    private const double MaxZoom = 2d;

    private readonly OrbisRuntimeOptions _options;
    private readonly WebApplication _host;

    public MainWindow(OrbisRuntimeOptions options, WebApplication host)
    {
        _options = options;
        _host = host;

        InitializeComponent();

        // Full HD when restored, never more than the screen has room for: 1920x1080 DIPs is
        // 2400x1350 pixels at 125% scaling, and a window that size cannot be centered on anything.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);

        Loaded += OnLoaded;

        // Every change of size counts: maximizing, restoring, and moving to a monitor with another
        // resolution or another scaling all arrive here, because the width is measured in DIPs.
        SizeChanged += (_, _) => FitZoomToWidth();
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(_options.DataDirectory, "webview2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder)
                .ConfigureAwait(true);

            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "orbis.local",
                _options.WebRootPath,
                CoreWebView2HostResourceAccessKind.Allow);
            Browser.CoreWebView2.Navigate(_options.ApplicationEntryPoint);
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        }
        catch (Exception exception)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Impossibile inizializzare il browser: {exception.Message}";
        }
    }

    /// <summary>
    /// Zooms the pages so they fill a wide window as they fill a Full HD one. The width is already
    /// in DIPs, so the scaling of Windows is counted first: 4K at 200% is 1920 DIPs and is left
    /// alone, 4K at 150% or 2K at 100% is 2560 and is zoomed by a third. A window narrower than the
    /// design width is never shrunk: the pages have their own narrow layout for that.
    /// </summary>
    private void FitZoomToWidth()
    {
        var zoom = Math.Round(Math.Clamp(ActualWidth / DesignWidth, 1d, MaxZoom), 2);
        if (Math.Abs(Browser.ZoomFactor - zoom) > 0.001)
        {
            Browser.ZoomFactor = zoom;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            Browser.Visibility = Visibility.Visible;
            Splash.Visibility = Visibility.Collapsed;
        }
        else if (Browser.Visibility != Visibility.Visible)
        {
            // Only the first load reports on the splash: once the app is visible, a failed or
            // cancelled navigation (a quick double click) must not hide it again.
            LoadingBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Il browser non riesce a raggiungere {_options.ApplicationUrl}.";
        }
    }

    /// <summary>
    /// A link that opens in another window (the page of a live on Twitch or YouTube, the coffee
    /// page): the WebView would replace the whole application with the site, so the browser of the
    /// system takes it. Only http and https are handed over: the URI reaches a shell that runs
    /// whatever it is given.
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            // The window is on top of the application, so there is nowhere to report it: the link
            // simply does nothing. A failure here must not take the application down.
            System.Diagnostics.Debug.WriteLine($"Impossibile aprire {uri.AbsoluteUri}: {exception.Message}");
        }
    }

    /// <summary>
    /// A file dropped on a <c>data-drop-path</c> input (site.js): the page only gets a File without
    /// its path, WebView2 resolves it on the host side and the absolute path is sent back.
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // WebMessageAsJson never throws, unlike TryGetWebMessageAsString on a non-string message.
        if (e.WebMessageAsJson != "\"dropPath\"")
        {
            return;
        }

        var path = e.AdditionalObjects?.OfType<CoreWebView2File>().FirstOrDefault()?.Path;
        if (path is not null)
        {
            Browser.CoreWebView2.PostWebMessageAsString(path);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Browser.CoreWebView2?.Stop();
    }
}
