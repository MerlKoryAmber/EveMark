namespace JitaWatch;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly WebApplication _app;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _notifyItem;
    private int _shouted;
    private bool _notifyEarn = true;
    private bool _loadingNotify;

    public TrayContext(WebApplication app)
    {
        _app = app;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
        _icon = new NotifyIcon
        {
            Icon = new Icon(iconPath),
            Visible = true,
            Text = "MK Jitawatch"
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => OpenPanel());
        menu.Items.Add("Сигнал", null, (_, _) => OpenSignal());
        _notifyItem = new ToolStripMenuItem("Оповещения о заработке")
        {
            CheckOnClick = true,
            Checked = true
        };
        _notifyItem.CheckedChanged += (_, _) =>
        {
            if (_loadingNotify)
                return;
            _notifyEarn = _notifyItem.Checked;
            if (_notifyEarn)
                _shouted = 0;
            _ = SaveNotifyAsync(_notifyEarn);
            if (!_notifyEarn)
                _icon.Text = "MK Jitawatch";
        };
        menu.Items.Add(_notifyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenPanel();
        _icon.BalloonTipClicked += (_, _) => OpenSignal();

        _timer = new System.Windows.Forms.Timer { Interval = 8_000 };
        _timer.Tick += (_, _) =>
        {
            if (_timer.Interval != 300_000)
                _timer.Interval = 300_000;
            _ = PollSignalAsync();
        };
        _timer.Start();
        _ = LoadNotifyAsync();
    }

    private async Task LoadNotifyAsync()
    {
        try
        {
            var db = _app.Services.GetRequiredService<MarketDb>();
            var on = await db.GetTrayNotifyAsync();
            void apply() => ApplyNotifyFlag(on);
            if (_icon.ContextMenuStrip is { InvokeRequired: true } strip)
                strip.BeginInvoke(apply);
            else
                apply();
        }
        catch
        {
            // дефолт включено
        }
    }

    private void ApplyNotifyFlag(bool on)
    {
        _loadingNotify = true;
        try
        {
            _notifyEarn = on;
            if (_notifyItem.Checked != on)
                _notifyItem.Checked = on;
        }
        finally
        {
            _loadingNotify = false;
        }
    }

    private async Task SaveNotifyAsync(bool enabled)
    {
        try
        {
            var db = _app.Services.GetRequiredService<MarketDb>();
            await db.SetTrayNotifyAsync(enabled);
        }
        catch
        {
        }
    }

    private async Task PollSignalAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var signal = await http.GetFromJsonAsync<SignalDto>("http://127.0.0.1:8741/api/signals");
            if (signal is null)
                return;
            var shout = signal.Tone == "shout";
            if (!_notifyEarn)
            {
                _icon.Text = "MK Jitawatch";
                _shouted = shout ? signal.Shown : 0;
                return;
            }

            _icon.Text = shout ? "Есть шанс заработать" : "MK Jitawatch";
            if (shout && signal.Shown > _shouted)
            {
                _icon.ShowBalloonTip(8000, "MK Jitawatch", "Есть шанс заработать. Иди покупай.", ToolTipIcon.Info);
            }

            _shouted = shout ? signal.Shown : 0;
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    public static void OpenSignal() => Open("http://127.0.0.1:8741/#signal");

    public static void OpenPanel() => Open("http://127.0.0.1:8741/");

    private static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _app.Lifetime.StopApplication();
        base.ExitThreadCore();
    }
}
