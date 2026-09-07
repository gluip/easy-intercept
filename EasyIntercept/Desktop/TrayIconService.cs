#if WINDOWS
using System.Drawing;
using System.Windows.Forms;
using EasyIntercept.Hosting;
using EasyIntercept.Proxy;

namespace EasyIntercept.Desktop;

/// <summary>
/// System-tray icon for the Windows build. Runs a WinForms message loop on its own STA thread
/// next to the ASP.NET host; "Exit" stops the host, and host shutdown removes the icon.
/// </summary>
public sealed class TrayIconService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SystemProxyService _systemProxy;
    private readonly BrowserLauncherService _browsers;
    private readonly AppPaths _paths;
    private readonly ILogger<TrayIconService> _logger;
    private readonly string _uiUrl;

    private Thread? _thread;
    private NotifyIcon? _icon;
    private Control? _invoker;   // hidden control used to marshal calls onto the UI thread
    private readonly ManualResetEventSlim _ready = new();

    public TrayIconService(
        IHostApplicationLifetime lifetime,
        SystemProxyService systemProxy,
        BrowserLauncherService browsers,
        AppPaths paths,
        IConfiguration config,
        ILogger<TrayIconService> logger)
    {
        _lifetime = lifetime;
        _systemProxy = systemProxy;
        _browsers = browsers;
        _paths = paths;
        _logger = logger;
        _uiUrl = StartupOptions.UiUrl(StartupOptions.GetUiPort(config));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(RunMessageLoop) { Name = "EasyIntercept tray", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var invoker = _invoker;
        if (invoker is not null && invoker.IsHandleCreated)
        {
            invoker.BeginInvoke(() =>
            {
                if (_icon is not null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
                Application.ExitThread();
            });
        }
        return Task.CompletedTask;
    }

    private void RunMessageLoop()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            _invoker = new Control();
            _ = _invoker.Handle; // force handle creation so BeginInvoke works from other threads

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = $"EasyIntercept — {_uiUrl}",
                ContextMenuStrip = BuildMenu(),
                Visible = true,
            };
            _icon.DoubleClick += (_, _) => Launcher.OpenUrl(_uiUrl);

            _ready.Set();
            Application.Run();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray icon failed; continuing without it");
            _ready.Set();
        }
        finally
        {
            _icon?.Dispose();
            _invoker?.Dispose();
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open EasyIntercept", null, (_, _) => Launcher.OpenUrl(_uiUrl))
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        };

        var proxyToggle = new ToolStripMenuItem("System proxy (127.0.0.1:9999)", null, (_, _) => ToggleSystemProxy());

        var launchBrowser = new ToolStripMenuItem("Launch proxied browser");
        launchBrowser.Click += (_, _) =>
        {
            if (launchBrowser.Tag is string browserId) LaunchBrowser(browserId); // single-browser case
        };
        var openSessions = new ToolStripMenuItem("Open sessions folder", null, (_, _) => Launcher.OpenFolder(_paths.Sessions));

        var exit = new ToolStripMenuItem("Exit", null, (_, _) => RequestExit());

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(proxyToggle);
        menu.Items.Add(launchBrowser);
        menu.Items.Add(openSessions);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        menu.Opening += (_, _) =>
        {
            try { proxyToggle.Checked = _systemProxy.IsEnabled(); }
            catch { proxyToggle.Checked = false; }
            RefreshBrowserItem(launchBrowser);
        };

        return menu;
    }

    // Same feature as the "Launch proxied …" button in the UI: an isolated browser profile that
    // routes only its own traffic through the proxy. One browser → direct item, several → submenu.
    private void RefreshBrowserItem(ToolStripMenuItem item)
    {
        item.DropDownItems.Clear();
        item.Tag = null;

        IReadOnlyList<DetectedBrowser> browsers;
        try { browsers = _browsers.DetectBrowsers(); }
        catch { browsers = Array.Empty<DetectedBrowser>(); }

        if (browsers.Count == 0)
        {
            item.Text = "Launch proxied browser (none found)";
            item.Enabled = false;
            return;
        }

        item.Enabled = true;
        if (browsers.Count == 1)
        {
            item.Text = $"Launch proxied {browsers[0].Name}";
            item.Tag = browsers[0].Id;
            return;
        }

        item.Text = "Launch proxied browser";
        foreach (var b in browsers)
            item.DropDownItems.Add(new ToolStripMenuItem(b.Name, null, (_, _) => LaunchBrowser(b.Id)));
    }

    private void LaunchBrowser(string browserId)
    {
        try
        {
            _browsers.Launch(browserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not launch proxied browser {BrowserId} from tray", browserId);
        }
    }

    private void RequestExit()
    {
        _logger.LogInformation("Exit requested from tray icon");
        if (_icon is not null) _icon.Visible = false; // immediate feedback; disposal happens in StopAsync
        _lifetime.StopApplication();

        // Watchdog: should graceful shutdown still hang (stuck proxy tunnel, misbehaving client),
        // terminate anyway rather than leaving a zombie process behind.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(8));
            _logger.LogWarning("Graceful shutdown did not complete in time; exiting forcefully");
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "EasyIntercept exit watchdog" }.Start();
    }

    private void ToggleSystemProxy()
    {
        try
        {
            if (_systemProxy.IsEnabled()) _systemProxy.Disable();
            else _systemProxy.Enable();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not toggle system proxy from tray");
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null)
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // fall through
        }
        return SystemIcons.Application;
    }
}
#endif
