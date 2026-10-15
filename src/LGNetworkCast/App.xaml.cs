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
                // Already running: ask the other instance to show its window. This process was just started
                // by the user, so it may hand its right to take the foreground to the running instance.
                AllowSetForegroundWindow(-1);   // ASFW_ANY
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
            _tray = new Forms.NotifyIcon { Icon = _idleIcon, Text = "LG Network Cast", Visible = true };
            _tray.MouseClick += (s, ev) =>
            {
                if (ev.Button == Forms.MouseButtons.Left) ToggleWindow();
                else if (ev.Button == Forms.MouseButtons.Right) TrayMenu.ShowAtCursor();
            };

            // Windows 11 hides new tray icons behind "^". Show ours the first time; later user choices are kept.
            Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => PromoteTrayIcon());

            _hotkeys = new HotkeyManager();
            RegisterHotkeys();

            _ = Task.Run(async () =>
            {
                await Encoder.DetectAsync();
                await Dispatcher.BeginInvoke(new Action(() => _window?.Refresh()));
            });

            // Updates: check shortly after start, then every 6 hours.
            var updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            updateTimer.Tick += (s, ev) => _ = CheckForUpdatesAsync(userInitiated: false);
            updateTimer.Start();
            Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => _ = CheckForUpdatesAsync(false))));

            var startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            if (!startHidden || Settings.Tvs.Count == 0) ShowWindow(Settings.Tvs.Count == 0 ? "tvs" : null);
        }

        /// <summary>
        /// Marks this app's tray icon as "always shown" if the user has never chosen (no IsPromoted value yet).
        /// Windows creates the entry once the icon has been shown, keyed by executable path.
        /// </summary>
        static void PromoteTrayIcon()
        {
            try
            {
                var exe = Assembly.GetExecutingAssembly().Location;
                using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return;
                    foreach (var name in root.GetSubKeyNames())
                        using (var key = root.OpenSubKey(name, writable: true))
                        {
                            if (key == null || !string.Equals(key.GetValue("ExecutablePath") as string, exe, StringComparison.OrdinalIgnoreCase)) continue;
                            if (key.GetValue("IsPromoted") == null) key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not promote tray icon", ex);
            }
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

        // ---- Updates -----------------------------------------------------------------------------

        public UpdateInfo AvailableUpdate { get; private set; }
        public string UpdateStatus { get; private set; } = "";

        /// <summary>Checks GitHub for a newer version. Installs it automatically when allowed and nothing is on the TV.</summary>
        public async Task CheckForUpdatesAsync(bool userInitiated)
        {
            if (!Updater.IsConfigured(Settings)) { UpdateStatus = "Update source not configured"; _window?.Refresh(); return; }
            try
            {
                UpdateStatus = "Checking…";
                _window?.Refresh();
                AvailableUpdate = await Updater.CheckAsync(Settings);
                Settings.LastUpdateCheck = DateTime.Now;
                Settings.Save();
                UpdateStatus = AvailableUpdate == null ? "You're up to date" : $"Version {AvailableUpdate.Version.ToString(3)} is available";
                if (AvailableUpdate != null)
                {
                    var idle = Cast.State == CastState.Idle && !Cast.ClockShowing;
                    if (Settings.AutoUpdate && idle && !userInitiated) await InstallUpdateAsync();
                    else if (!userInitiated)
                        _tray.ShowBalloonTip(6000, "LG Network Cast", $"Version {AvailableUpdate.Version.ToString(3)} is available. Open the app to update.", Forms.ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Update check failed", ex);
                UpdateStatus = userInitiated ? "Could not check for updates: " + ex.Message : "";
            }
            _window?.Refresh();
        }

        public async Task InstallUpdateAsync()
        {
            if (AvailableUpdate == null) return;
            try
            {
                UpdateStatus = "Downloading update…";
                _window?.Refresh();
                await Updater.DownloadAndLaunchAsync(AvailableUpdate, new Progress<int>(p =>
                {
                    UpdateStatus = $"Downloading update… {p}%";
                    _window?.Refresh();
                }));
                ExitApp();   // the installer replaces the files and starts the new version
            }
            catch (Exception ex)
            {
                Log.Error("Update failed", ex);
                UpdateStatus = "Update failed: " + ex.Message;
                _window?.Refresh();
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

        public void RefreshWindow() => _window?.Refresh();

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
            // There is no taskbar button, so the window must come above everything else.
            _window.Topmost = true;
            _window.Activate();
            _window.Topmost = false;
            _window.Focus();
            _window.Refresh();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

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
