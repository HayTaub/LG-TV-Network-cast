using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LGNetworkCast.Core;

namespace LGNetworkCast
{
    public partial class MainWindow : Window
    {
        static App AppRef => App.Current;
        static Settings S => App.Current.Settings;
        static CastController Cast => App.Current.Cast;

        readonly List<TvDevice> _found = new List<TvDevice>();
        readonly DispatcherTimer _volumePoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        readonly DispatcherTimer _volumeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        readonly DispatcherTimer _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        bool _updatingVolume, _updatingUi, _searching;
        string _renamingId, _pairingId, _error;
        HashSet<string> _failedHotkeys = new HashSet<string>();

        public MainWindow()
        {
            InitializeComponent();
            _volumePoll.Tick += async (s, e) => await LoadVolumeAsync();
            _volumeDebounce.Tick += (s, e) =>
            {
                _volumeDebounce.Stop();
                var v = (int)Math.Round(VolumeSlider.Value);
                AppRef.Run(() => Cast.SetVolumeAsync(v));
            };
            _errorTimer.Tick += (s, e) => { _errorTimer.Stop(); _error = null; Refresh(); };
            IsVisibleChanged += async (s, e) =>
            {
                if (IsVisible) { _volumePoll.Start(); await LoadVolumeAsync(); }
                else _volumePoll.Stop();
            };
            ManualIpBox.TextChanged += (s, e) => ManualIpHint.Visibility = ManualIpBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            AboutText.Text = $"LG Network Cast {typeof(App).Assembly.GetName().Version.ToString(3)} · MIT License\nScreen capture and encoding by FFmpeg";
            BuildHotkeyRows();
            Refresh();
        }

        // ---- Window chrome ----------------------------------------------------------------------

        void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
        void Minimize_Click(object sender, RoutedEventArgs e) => Hide();   // tray-only app: no taskbar button to minimize to
        void Close_Click(object sender, RoutedEventArgs e) => Hide();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Closing the window only hides it; the app keeps running in the tray.
            e.Cancel = true;
            Hide();
        }

        public void SelectTab(string tab)
        {
            TabCast.IsChecked = tab == "cast";
            TabTvs.IsChecked = tab == "tvs";
            TabSettings.IsChecked = tab == "settings";
            Tab_Click(null, null);
        }

        void Tab_Click(object sender, RoutedEventArgs e)
        {
            CastPage.Visibility = TabCast.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            TvsPage.Visibility = TabTvs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (TabTvs.IsChecked == true && S.Tvs.Count == 0 && _found.Count == 0 && !_searching) Search_Click(null, null);
        }

        void GoToTvs_Click(object sender, RoutedEventArgs e) => SelectTab("tvs");

        public void ShowError(string message)
        {
            _error = message;
            _errorTimer.Stop();
            _errorTimer.Start();
            Refresh();
        }

        // ---- Refresh ----------------------------------------------------------------------------

        public void Refresh()
        {
            _updatingUi = true;
            try
            {
                RefreshCast();
                RefreshTvs();
                RefreshSettings();
            }
            finally
            {
                _updatingUi = false;
            }
        }

        void RefreshCast()
        {
            FirewallBanner.Visibility = Firewall.IsBlocked ? Visibility.Visible : Visibility.Collapsed;
            var tv = S.DefaultTv;
            TvNameText.Text = tv?.Name ?? "No TV yet";
            if (tv == null) SetTvStatus("Tap to search for TVs", "Tertiary");
            else if (!tv.IsPaired) SetTvStatus("Not paired — tap to pair", "Orange");
            else if (Cast.State == CastState.Sharing) SetTvStatus("Sharing", "Red");
            else if (Cast.ClockShowing) SetTvStatus("Showing the clock", "Blue");
            else SetTvStatus($"Ready · {tv.Ip}", "Green");

            var busy = Cast.State == CastState.Starting || Cast.State == CastState.Stopping;
            var sharing = Cast.State == CastState.Sharing;
            ShareButton.IsEnabled = tv != null && tv.IsPaired && !busy;
            ShareButton.Background = (Brush)FindResource(sharing ? "Red" : "Blue");
            ShareText.Text = busy ? (Cast.State == CastState.Starting ? "Starting…" : "Stopping…") : sharing ? "Stop Sharing" : "Start Sharing";
            ShareGlyph.Text = sharing ? "" : "";
            ClockButton.IsEnabled = tv != null && tv.IsPaired && !busy;
            ClockText.Text = Cast.ClockShowing ? "Hide Clock" : "Show Clock on TV";

            if (_error != null)
            {
                StatusText.Text = _error;
                StatusText.Foreground = (Brush)FindResource("Red");
            }
            else
            {
                var hint = string.IsNullOrEmpty(S.Hotkeys.ToggleShare) ? "" : $"Shortcut: {S.Hotkeys.ToggleShare}";
                StatusText.Text = string.IsNullOrEmpty(Cast.Status) ? hint : Cast.Status;
                StatusText.Foreground = (Brush)FindResource("Secondary");
            }

            // Display picker
            var monitors = Monitors.List();
            if (S.MonitorIndex >= monitors.Count) S.MonitorIndex = 0;
            if (MonitorSegments.Children.Count != monitors.Count)
            {
                MonitorSegments.Children.Clear();
                for (var i = 0; i < monitors.Count; i++)
                {
                    var index = i;
                    var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "Monitor", Content = monitors[i].Label };
                    rb.Click += (s, e) =>
                    {
                        S.MonitorIndex = index;
                        S.Save();
                        Refresh();
                    };
                    MonitorSegments.Children.Add(rb);
                }
            }
            for (var i = 0; i < MonitorSegments.Children.Count; i++)
                ((RadioButton)MonitorSegments.Children[i]).IsChecked = i == S.MonitorIndex;
            var m = monitors[S.MonitorIndex];
            MonitorDetail.Text = $"{m.Detail} → sent as {Encoder.OutputHeight(S, m)}p";
        }

        void SetTvStatus(string text, string color)
        {
            TvStatusText.Text = text;
            TvStatusDot.Fill = (Brush)FindResource(color);
        }

        async Task LoadVolumeAsync()
        {
            var v = await Cast.GetVolumeAsync();
            _updatingVolume = true;
            if (v.HasValue)
            {
                VolumeSlider.IsEnabled = true;
                if (!VolumeSlider.IsMouseCaptureWithin && !_volumeDebounce.IsEnabled) VolumeSlider.Value = v.Value.volume;
                VolumeText.Text = ((int)VolumeSlider.Value).ToString();
                MuteGlyph.Text = v.Value.muted ? "" : "";
                MuteGlyph.Foreground = (Brush)FindResource(v.Value.muted ? "Red" : "Secondary");
            }
            else
            {
                VolumeSlider.IsEnabled = false;
                VolumeText.Text = "–";
            }
            _updatingVolume = false;
        }

        // ---- Cast tab actions -------------------------------------------------------------------

        void FixFirewall_Click(object sender, RoutedEventArgs e)
        {
            if (!Firewall.Fix()) ShowError("The firewall was not changed. Approve the Windows prompt to allow LG Network Cast.");
            Refresh();
        }

        void Share_Click(object sender, RoutedEventArgs e) => AppRef.Run(() => Cast.ToggleAsync());
        void Clock_Click(object sender, RoutedEventArgs e) => AppRef.Run(() => Cast.ToggleClockAsync());
        void TvOn_Click(object sender, RoutedEventArgs e) => AppRef.Run(async () => { await Cast.TurnOnAsync(); await LoadVolumeAsync(); });
        void TvOff_Click(object sender, RoutedEventArgs e) => AppRef.Run(async () => { await Cast.TurnOffAsync(); await LoadVolumeAsync(); });
        void Mute_Click(object sender, RoutedEventArgs e) => AppRef.Run(async () => { await Cast.ToggleMuteAsync(); await LoadVolumeAsync(); });

        void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeText == null) return;
            VolumeText.Text = ((int)Math.Round(e.NewValue)).ToString();
            if (_updatingVolume) return;
            _volumeDebounce.Stop();
            _volumeDebounce.Start();
        }

        // ---- TVs tab ----------------------------------------------------------------------------

        void RefreshTvs()
        {
            MyTvsPanel.Children.Clear();
            if (S.Tvs.Count == 0)
                MyTvsPanel.Children.Add(new TextBlock
                {
                    Text = "No TVs yet. Search below — your TV must be on and on the same network.",
                    Foreground = (Brush)FindResource("Secondary"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 14, 16, 14),
                });
            for (var i = 0; i < S.Tvs.Count; i++)
            {
                if (i > 0) MyTvsPanel.Children.Add(new Border { Style = (Style)FindResource("RowSeparator"), Margin = new Thickness(52, 0, 0, 0) });
                MyTvsPanel.Children.Add(TvRow(S.Tvs[i]));
            }

            FoundPanel.Children.Clear();
            foreach (var tv in _found.Where(f => !S.Tvs.Any(t => t.Id == f.Id || t.Ip == f.Ip)))
            {
                var row = new Grid { Margin = new Thickness(16, 10, 10, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Tile("", "Blue"));
                var text = TwoLine(tv.Name, $"{tv.Model} · {tv.Ip}");
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                var add = new Button { Style = (Style)FindResource("TintButton"), Content = "Add", Height = 32, Padding = new Thickness(6, 0, 6, 0) };
                var found = tv;
                add.Click += (s, e) => AddTv(found);
                Grid.SetColumn(add, 2);
                row.Children.Add(add);
                FoundPanel.Children.Add(row);
                FoundPanel.Children.Add(new Border { Style = (Style)FindResource("RowSeparator") });
            }
            SearchText.Text = _searching ? "Searching…" : "Search for TVs";
            SearchButton.IsEnabled = !_searching;
        }

        UIElement TvRow(TvDevice tv)
        {
            var isDefault = S.DefaultTv == tv;
            var grid = new Grid { Margin = new Thickness(16, 10, 8, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new TextBlock
            {
                Style = (Style)FindResource("Glyph"),
                Text = "",
                Width = 24,
                Margin = new Thickness(0, 0, 12, 0),
                Foreground = (Brush)FindResource("Blue"),
                Opacity = isDefault ? 1 : 0,
            };
            grid.Children.Add(check);

            FrameworkElement middle;
            if (_renamingId == tv.Id)
            {
                var box = new TextBox { Style = (Style)FindResource("IosTextBox"), Text = tv.Name, FontWeight = FontWeights.SemiBold };
                void Commit()
                {
                    if (_renamingId != tv.Id) return;
                    _renamingId = null;
                    if (!string.IsNullOrWhiteSpace(box.Text)) tv.Name = box.Text.Trim();
                    S.Save();
                    Refresh();
                }
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) Commit();
                    if (e.Key == Key.Escape) { _renamingId = null; Refresh(); }
                };
                box.LostKeyboardFocus += (s, e) => Commit();
                box.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
                middle = box;
            }
            else
            {
                var status = _pairingId == tv.Id ? "Accept the prompt on the TV…"
                    : tv.IsPaired ? $"{tv.Model} · {tv.Ip}".TrimStart(' ', '·') : "Not paired";
                middle = TwoLine(tv.Name, status);
            }
            Grid.SetColumn(middle, 1);
            grid.Children.Add(middle);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!tv.IsPaired)
            {
                var pair = new Button { Style = (Style)FindResource("TintButton"), Content = "Pair", Height = 32, Margin = new Thickness(0, 0, 4, 0), IsEnabled = _pairingId == null };
                pair.Click += (s, e) => Pair(tv);
                actions.Children.Add(pair);
            }
            actions.Children.Add(IconButton("", "Rename", "Blue", () => { _renamingId = tv.Id; Refresh(); }));
            actions.Children.Add(IconButton("", "Remove", "Red", () =>
            {
                if (MessageBox.Show(this, $"Remove \"{tv.Name}\"?", "LG Network Cast", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                S.Tvs.Remove(tv);
                if (S.DefaultTvId == tv.Id) S.DefaultTvId = S.Tvs.FirstOrDefault()?.Id;
                S.Save();
                Refresh();
            }));
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            var row = new Button { Style = (Style)FindResource("RowButton"), Content = grid };
            row.Click += (s, e) =>
            {
                if (_renamingId != null) return;
                S.DefaultTvId = tv.Id;
                S.Save();
                Refresh();
            };
            return row;
        }

        FrameworkElement TwoLine(string title, string detail)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = detail, FontSize = 12.5, Foreground = (Brush)FindResource("Secondary"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            return sp;
        }

        Border Tile(string glyph, string color) => new Border
        {
            Style = (Style)FindResource("IconTile"),
            Background = (Brush)FindResource(color),
            Child = new TextBlock { Style = (Style)FindResource("Glyph"), Text = glyph, Foreground = Brushes.White, FontSize = 14 },
        };

        Button IconButton(string glyph, string tip, string color, Action click)
        {
            var b = new Button
            {
                Style = (Style)FindResource("PlainButton"),
                Width = 34,
                Height = 34,
                ToolTip = tip,
                Content = new TextBlock { Style = (Style)FindResource("Glyph"), Text = glyph, FontSize = 15, Foreground = (Brush)FindResource(color) },
            };
            b.Click += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        async void Search_Click(object sender, RoutedEventArgs e)
        {
            if (_searching) return;
            _searching = true;
            Refresh();
            try
            {
                var tvs = await Discovery.SearchAsync(TimeSpan.FromSeconds(3));
                foreach (var tv in tvs)
                {
                    // Refresh the IP of TVs we already know (DHCP may have moved them).
                    var known = S.Tvs.FirstOrDefault(t => t.Id == tv.Id);
                    if (known != null)
                    {
                        known.Ip = tv.Ip;
                        known.DlnaDescriptionUrl = tv.DlnaDescriptionUrl ?? known.DlnaDescriptionUrl;
                        foreach (var mac in tv.Macs) if (!known.Macs.Contains(mac)) known.Macs.Add(mac);
                    }
                    else if (!_found.Any(f => f.Id == tv.Id)) _found.Add(tv);
                }
                S.Save();
                if (tvs.Count == 0) ShowError("No LG TVs found. Make sure the TV is on and on the same network.");
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                _searching = false;
                Refresh();
            }
        }

        void AddTv(TvDevice tv)
        {
            S.Tvs.Add(tv);
            _found.Remove(tv);
            if (S.DefaultTvId == null || S.Tvs.Count == 1) S.DefaultTvId = tv.Id;
            S.Save();
            Pair(tv);
        }

        async void Pair(TvDevice tv)
        {
            _pairingId = tv.Id;
            Refresh();
            try
            {
                await Cast.PairAsync(tv);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                _pairingId = null;
                Refresh();
                await LoadVolumeAsync();
            }
        }

        void ManualIp_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ManualAdd_Click(null, null); }

        async void ManualAdd_Click(object sender, RoutedEventArgs e)
        {
            var ip = ManualIpBox.Text.Trim();
            if (!IPAddress.TryParse(ip, out _)) { ShowError("Enter a valid IP address, e.g. 192.168.1.50"); return; }
            if (!WebOsTv.IsReachable(ip, 1500)) { ShowError($"No LG webOS TV answers at {ip}. Is it on?"); return; }
            var tvs = await Discovery.SearchAsync(TimeSpan.FromSeconds(2), ip);
            var tv = tvs.FirstOrDefault(t => t.Ip == ip) ?? new TvDevice { Id = "ip:" + ip, Ip = ip, Name = "LG TV" };
            if (tv.Macs.Count == 0)
            {
                var mac = Arp.Lookup(ip);
                if (mac != null) tv.Macs.Add(mac);
            }
            ManualIpBox.Text = "";
            if (S.Tvs.Any(t => t.Id == tv.Id || t.Ip == tv.Ip)) { ShowError("This TV is already in the list."); return; }
            AddTv(tv);
        }

        // ---- Settings tab -----------------------------------------------------------------------

        void RefreshSettings()
        {
            PowerOnToggle.IsChecked = S.PowerOnWithShare;
            PowerOffToggle.IsChecked = S.PowerOffWithStop;
            AutostartToggle.IsChecked = Settings.StartWithWindows;
            ModeFast.IsChecked = S.Engine == StreamEngine.Fast;
            ModeDlna.IsChecked = S.Engine == StreamEngine.Dlna;
            Res720.IsChecked = S.Height == 720;
            Res1080.IsChecked = S.Height == 1080;
            ResNative.IsChecked = S.Height <= 0;
            Rate6.IsChecked = S.BitrateMbps <= 6;
            Rate10.IsChecked = S.BitrateMbps > 6 && S.BitrateMbps <= 12;
            Rate20.IsChecked = S.BitrateMbps > 12;
            EncoderText.Text = EncoderName(Encoder.Best);
            RefreshHotkeyRows();
        }

        static string EncoderName(string enc)
        {
            switch (enc)
            {
                case "h264_nvenc": return "NVIDIA NVENC";
                case "h264_qsv": return "Intel Quick Sync";
                case "h264_amf": return "AMD AMF";
                default: return "Software (x264)";
            }
        }

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingUi) return;
            S.PowerOnWithShare = PowerOnToggle.IsChecked == true;
            S.PowerOffWithStop = PowerOffToggle.IsChecked == true;
            try { Settings.StartWithWindows = AutostartToggle.IsChecked == true; }
            catch (Exception ex) { ShowError("Could not change startup setting: " + ex.Message); }
            S.Save();
        }

        void Stream_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingUi) return;
            S.Engine = ModeDlna.IsChecked == true ? StreamEngine.Dlna : StreamEngine.Fast;
            S.Height = Res720.IsChecked == true ? 720 : ResNative.IsChecked == true ? 0 : 1080;
            S.BitrateMbps = Rate6.IsChecked == true ? 6 : Rate20.IsChecked == true ? 20 : 10;
            S.Save();
            Refresh();
        }

        // Hotkey rows: (property name, label, icon, color)
        static readonly (string key, string label, string glyph, string color)[] HotkeyDefs =
        {
            (nameof(HotkeySet.ToggleShare), "Start / stop sharing", "", "Blue"),
            (nameof(HotkeySet.ToggleClock), "Show / hide clock", "", "Blue"),
            (nameof(HotkeySet.TvOn), "Turn TV on", "", "Green"),
            (nameof(HotkeySet.TvOff), "Turn TV off", "", "Red"),
            (nameof(HotkeySet.VolumeUp), "Volume up", "", "Orange"),
            (nameof(HotkeySet.VolumeDown), "Volume down", "", "Orange"),
            (nameof(HotkeySet.Mute), "Mute / unmute", "", "Orange"),
        };
        readonly Dictionary<string, TextBox> _hotkeyBoxes = new Dictionary<string, TextBox>();

        void BuildHotkeyRows()
        {
            HotkeyPanel.Children.Clear();
            for (var i = 0; i < HotkeyDefs.Length; i++)
            {
                var def = HotkeyDefs[i];
                if (i > 0) HotkeyPanel.Children.Add(new Border { Style = (Style)FindResource("RowSeparator"), Margin = new Thickness(57, 0, 0, 0) });
                var grid = new Grid { Margin = new Thickness(16, 8, 12, 8) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(Tile(def.glyph, def.color));
                var label = new TextBlock { Style = (Style)FindResource("RowText"), Text = def.label };
                Grid.SetColumn(label, 1);
                grid.Children.Add(label);
                var box = new TextBox { Style = (Style)FindResource("KeyPill"), Tag = def.key };
                box.PreviewKeyDown += HotkeyBox_PreviewKeyDown;
                box.GotKeyboardFocus += (s, e) => { box.Text = "Press keys…"; };
                box.LostKeyboardFocus += (s, e) => RefreshHotkeyRows();
                Grid.SetColumn(box, 2);
                grid.Children.Add(box);
                _hotkeyBoxes[def.key] = box;
                HotkeyPanel.Children.Add(grid);
            }
        }

        void RefreshHotkeyRows()
        {
            foreach (var kv in _hotkeyBoxes)
            {
                if (kv.Value.IsKeyboardFocused) continue;
                var value = (string)typeof(HotkeySet).GetProperty(kv.Key).GetValue(S.Hotkeys);
                kv.Value.Text = string.IsNullOrEmpty(value) ? "None" : value;
                kv.Value.Foreground = (Brush)FindResource(_failedHotkeys.Contains(kv.Key) ? "Red" : "Label");
                kv.Value.ToolTip = _failedHotkeys.Contains(kv.Key) ? "Already used by Windows or another app" : null;
            }
            HotkeyFooter.Text = _failedHotkeys.Count > 0
                ? "Shortcuts in red are taken by Windows or another app — pick different keys."
                : "Click a shortcut, then press the new keys. Backspace clears it. Shortcuts work everywhere, even when this window is closed.";
            HotkeyFooter.Foreground = (Brush)FindResource(_failedHotkeys.Count > 0 ? "Red" : "Secondary");
        }

        void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var box = (TextBox)sender;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return;

            var mods = Keyboard.Modifiers;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
            string text;
            if ((key == Key.Back || key == Key.Delete) && mods == ModifierKeys.None) text = "";
            else if (mods == ModifierKeys.None && !(key >= Key.F1 && key <= Key.F24)) return;   // require a modifier (except F-keys)
            else text = HotkeyManager.Format(mods, key);

            typeof(HotkeySet).GetProperty((string)box.Tag).SetValue(S.Hotkeys, text);
            S.Save();
            _failedHotkeys = AppRef.RegisterHotkeys();
            Keyboard.ClearFocus();
            Refresh();
        }

        void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(Settings.Folder);
            Process.Start("explorer.exe", Settings.Folder);
        }
    }
}
