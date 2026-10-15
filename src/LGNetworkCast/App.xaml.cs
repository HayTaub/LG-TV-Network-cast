using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LGNetworkCast.Core;
using Forms = System.Windows.Forms;

namespace LGNetworkCast
{
    public partial class App : Application
    {
        const string MutexName = "LGNetworkCast.SingleInstance";
        const string ShowEventName = "LGNetworkCast.ShowWindow";

        Mutex _mutex;
        EventWaitHandle _showEvent;
        Forms.NotifyIcon _tray;
        Icon _idleIcon, _liveIcon;
        HotkeyManager _hotkeys;
        MainWindow _window;

        public Settings Settings { get; private set; }
        public CastController Cast { get; private set; }

        public static new App Current => (App)Application.Current;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, MutexName, out var first);
            if (!first)
            {
                // Already running: ask the other instance to show its window.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(new Action(() => ShowWindow()));
            }) { IsBackground = true }.Start();

            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Error("Unhandled UI exception", ex.Exception);
                ex.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

            Settings = Settings.Load();
            Cast = new CastController(Settings);
            Cast.Changed += () => Dispatcher.BeginInvoke(new Action(OnCastChanged));
            try { Cast.StartServer(); }
            catch (Exception ex) { Log.Error("Server start failed", ex); }

            // Self-heal after sleep or network changes: the TV connection is re-established on next use.
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, ev) =>
            {
                if (ev.Mode == Microsoft.Win32.PowerModes.Resume) { Log.Info("Resumed from sleep"); Cast.ResetConnection(); }
            };
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, ev) => Cast.ResetConnection();

            _idleIcon = LoadIcon("tray-idle.ico");
            _liveIcon = LoadIcon("tray-live.ico");
            _tray = new Forms.NotifyIcon { Icon = _idleIcon, Text = "LG Network Cast", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
            _tray.ContextMenuStrip.Opening += (s, ev) => BuildTrayMenu();
            _tray.MouseClick += (s, ev) => { if (ev.Button == Forms.MouseButtons.Left) ToggleWindow(); };

            _hotkeys = new HotkeyManager();
            RegisterHotkeys();

            _ = Task.Run(async () =>
            {
                await Encoder.DetectAsync();
                await Dispatcher.BeginInvoke(new Action(() => _window?.Refresh()));
            });

            var startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            if (!startHidden || Settings.Tvs.Count == 0) ShowWindow(Settings.Tvs.Count == 0 ? "tvs" : null);
        }

        static Icon LoadIcon(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                return new Icon(s, Forms.SystemInformation.SmallIconSize);
        }

        // ---- Hotkeys ----------------------------------------------------------------------------

        /// <summary>(Re)registers all hotkeys. Returns the names of hotkeys that could not be registered.</summary>
        public HashSet<string> RegisterHotkeys()
        {
            _hotkeys.UnregisterAll();
            var failed = new HashSet<string>();
            var h = Settings.Hotkeys;
            void Reg(string name, string keys, Func<Task> action)
            {
                if (string.IsNullOrWhiteSpace(keys)) return;
                if (!_hotkeys.Register(keys, () => Run(action))) failed.Add(name);
            }
            Reg(nameof(h.ToggleShare), h.ToggleShare, () => Cast.ToggleAsync());
            Reg(nameof(h.ToggleClock), h.ToggleClock, () => Cast.ToggleClockAsync());
            Reg(nameof(h.TvOn), h.TvOn, () => Cast.TurnOnAsync());
            Reg(nameof(h.TvOff), h.TvOff, () => Cast.TurnOffAsync());
            Reg(nameof(h.VolumeUp), h.VolumeUp, () => Cast.VolumeStepAsync(true));
            Reg(nameof(h.VolumeDown), h.VolumeDown, () => Cast.VolumeStepAsync(false));
            Reg(nameof(h.Mute), h.Mute, () => Cast.ToggleMuteAsync());
            return failed;
        }

        // ---- Running actions ---------------------------------------------------------------------

        /// <summary>Runs a TV action and reports failures in the window and as a notification.</summary>
        public async void Run(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Error("Action failed", ex);
                var message = ex is AggregateException ae ? ae.InnerException?.Message ?? ex.Message : ex.Message;
                _window?.ShowError(message);
                if (_window == null || !_window.IsVisible) _tray.ShowBalloonTip(5000, "LG Network Cast", message, Forms.ToolTipIcon.Warning);
            }
        }

        void OnCastChanged()
        {
            var live = Cast.State == CastState.Sharing;
            _tray.Icon = live ? _liveIcon : _idleIcon;
            var text = live ? Cast.Status : Cast.ClockShowing ? "Clock on TV" : "LG Network Cast";
            _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            _window?.Refresh();
        }

        // ---- Tray -------------------------------------------------------------------------------

        void BuildTrayMenu()
        {
            var m = _tray.ContextMenuStrip;
            m.Items.Clear();
            var sharing = Cast.State != CastState.Idle;

            var share = new Forms.ToolStripMenuItem(sharing ? "Stop Sharing" : "Start Sharing", null, (s, e) => Run(() => Cast.ToggleAsync()))
            { Font = new Font(m.Font, System.Drawing.FontStyle.Bold), ShortcutKeyDisplayString = Settings.Hotkeys.ToggleShare };
            m.Items.Add(share);
            m.Items.Add(new Forms.ToolStripMenuItem(Cast.ClockShowing ? "Hide Clock" : "Show Clock on TV", null, (s, e) => Run(() => Cast.ToggleClockAsync()))
            { ShortcutKeyDisplayString = Settings.Hotkeys.ToggleClock });

            var displays = new Forms.ToolStripMenuItem("Display");
            var monitors = Monitors.List();
            for (var i = 0; i < monitors.Count; i++)
            {
                var index = i;
                displays.DropDownItems.Add(new Forms.ToolStripMenuItem($"{monitors[i].Label}  ({monitors[i].Detail})", null, (s, e) =>
                {
                    Settings.MonitorIndex = index;
                    Settings.Save();
                    _window?.Refresh();
                }) { Checked = Settings.MonitorIndex == i });
            }
            m.Items.Add(displays);

            if (Settings.Tvs.Count > 1)
            {
                var tvs = new Forms.ToolStripMenuItem("TV");
                foreach (var tv in Settings.Tvs)
                {
                    var t = tv;
                    tvs.DropDownItems.Add(new Forms.ToolStripMenuItem(tv.Name, null, (s, e) =>
                    {
                        Settings.DefaultTvId = t.Id;
                        Settings.Save();
                        _window?.Refresh();
                    }) { Checked = Settings.DefaultTv == tv });
                }
                m.Items.Add(tvs);
            }

            m.Items.Add(new Forms.ToolStripSeparator());
            m.Items.Add(new Forms.ToolStripMenuItem("Turn TV On", null, (s, e) => Run(() => Cast.TurnOnAsync())) { ShortcutKeyDisplayString = Settings.Hotkeys.TvOn });
            m.Items.Add(new Forms.ToolStripMenuItem("Turn TV Off", null, (s, e) => Run(() => Cast.TurnOffAsync())) { ShortcutKeyDisplayString = Settings.Hotkeys.TvOff });
            var volume = new Forms.ToolStripMenuItem("Volume");
            volume.DropDownItems.Add(new Forms.ToolStripMenuItem("Volume Up", null, (s, e) => Run(() => Cast.VolumeStepAsync(true))) { ShortcutKeyDisplayString = Settings.Hotkeys.VolumeUp });
            volume.DropDownItems.Add(new Forms.ToolStripMenuItem("Volume Down", null, (s, e) => Run(() => Cast.VolumeStepAsync(false))) { ShortcutKeyDisplayString = Settings.Hotkeys.VolumeDown });
            volume.DropDownItems.Add(new Forms.ToolStripMenuItem("Mute / Unmute", null, (s, e) => Run(() => Cast.ToggleMuteAsync())) { ShortcutKeyDisplayString = Settings.Hotkeys.Mute });
            m.Items.Add(volume);
            m.Items.Add(new Forms.ToolStripSeparator());
            m.Items.Add(new Forms.ToolStripMenuItem("Open LG Network Cast", null, (s, e) => ShowWindow()));
            m.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));
        }

        // ---- Window -----------------------------------------------------------------------------

        public void ShowWindow(string tab = null)
        {
            if (_window == null)
            {
                _window = new MainWindow();
                _window.Closed += (s, e) => _window = null;
                // Open above the tray, like a flyout.
                var area = SystemParameters.WorkArea;
                _window.Left = area.Right - _window.Width;
                _window.Top = area.Bottom - _window.Height;
            }
            if (tab != null) _window.SelectTab(tab);
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Refresh();
        }

        void ToggleWindow()
        {
            if (_window != null && _window.IsVisible && _window.IsActive) _window.Hide();
            else ShowWindow();
        }

        public void ExitApp()
        {
            try
            {
                if (Cast.State != CastState.Idle) Cast.StopAsync().Wait(5000);
                else if (Cast.ClockShowing) Cast.HideClockAsync().Wait(5000);
            }
            catch (Exception ex)
            {
                Log.Error("Stop on exit failed", ex);
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _hotkeys?.Dispose();
            Cast?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
