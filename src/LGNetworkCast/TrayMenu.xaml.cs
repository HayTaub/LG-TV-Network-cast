using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LGNetworkCast.Core;
using Forms = System.Windows.Forms;

namespace LGNetworkCast
{
    /// <summary>Modern, rounded tray menu (replaces the classic WinForms context menu).</summary>
    public partial class TrayMenu : Window
    {
        static TrayMenu _open;
        static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8E));
        static readonly Brush RedText = new SolidColorBrush(Color.FromRgb(0xE0, 0x35, 0x2B));
        static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));

        static App AppRef => App.Current;
        static Settings S => App.Current.Settings;
        static CastController Cast => App.Current.Cast;

        TextBlock _volumeText;
        bool _closing;

        TrayMenu()
        {
            InitializeComponent();
            Build();
            Deactivated += (s, e) => SafeClose();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) SafeClose(); };
        }

        /// <summary>Opens the menu next to the mouse pointer (screen pixels), above the taskbar.</summary>
        public static void ShowAtCursor()
        {
            _open?.SafeClose();
            var menu = _open = new TrayMenu { Left = -10000, Top = -10000, Opacity = 0 };
            menu.Show();
            menu.UpdateLayout();

            var px = Forms.Cursor.Position;
            var work = Forms.Screen.FromPoint(px).WorkingArea;
            var toDip = PresentationSource.FromVisual(menu).CompositionTarget.TransformFromDevice;
            var cursor = toDip.Transform(new Point(px.X, px.Y));
            var topLeft = toDip.Transform(new Point(work.Left, work.Top));
            var bottomRight = toDip.Transform(new Point(work.Right, work.Bottom));

            const double shadow = 14;   // the Border margin around the visible card
            var w = menu.ActualWidth;
            var h = menu.ActualHeight;
            var left = cursor.X - w + shadow;
            var top = cursor.Y - h + shadow;
            if (left < topLeft.X - shadow) left = cursor.X - shadow;
            if (top < topLeft.Y - shadow) top = cursor.Y - shadow;
            left = Math.Min(left, bottomRight.X - w + shadow);
            top = Math.Min(top, bottomRight.Y - h + shadow);

            menu.Left = left;
            menu.Top = top;
            menu.Opacity = 1;
            menu.Activate();
            _ = menu.LoadVolumeAsync();
        }

        void SafeClose()
        {
            if (_closing) return;
            _closing = true;
            if (_open == this) _open = null;
            Close();
        }

        // ---- Building ---------------------------------------------------------------------------

        void Build()
        {
            var sharing = Cast.State != CastState.Idle;
            var tv = S.DefaultTv;
            var ready = tv != null && tv.IsPaired;

            Action("Show App", null, () => AppRef.ShowWindow());
            Separator();

            Action(sharing ? "Stop Sharing" : "Start Sharing", S.Hotkeys.ToggleShare, () => AppRef.Run(() => Cast.ToggleAsync()),
                enabled: ready, color: sharing ? RedText : null);
            Action(Cast.ClockShowing ? "Hide Clock" : "Show Clock on TV", S.Hotkeys.ToggleClock, () => AppRef.Run(() => Cast.ToggleClockAsync()), enabled: ready);
            Separator();

            Info(tv == null ? "No TV yet" : $"{tv.Name} · {TvState(tv)}");
            Action("Turn TV On", S.Hotkeys.TvOn, () => AppRef.Run(() => Cast.TurnOnAsync()), enabled: ready);
            Action("Turn TV Off", S.Hotkeys.TvOff, () => AppRef.Run(() => Cast.TurnOffAsync()), enabled: ready);
            if (ready) VolumeRow();

            var monitors = Monitors.List();
            if (monitors.Count > 1)
            {
                Separator();
                Info("Share display");
                for (var i = 0; i < monitors.Count; i++)
                {
                    var index = i;
                    Check(monitors[i].Label, monitors[i].Detail, S.MonitorIndex == i, () =>
                    {
                        S.MonitorIndex = index;
                        S.Save();
                        AppRef.RefreshWindow();
                    });
                }
            }

            if (S.Tvs.Count > 1)
            {
                Separator();
                Info("TV");
                foreach (var t in S.Tvs)
                {
                    var target = t;
                    Check(t.Name, null, S.DefaultTv == t, () =>
                    {
                        S.DefaultTvId = target.Id;
                        S.Save();
                        AppRef.RefreshWindow();
                    });
                }
            }

            if (AppRef.AvailableUpdate != null)
            {
                Separator();
                Action($"Update to {AppRef.AvailableUpdate.Version.ToString(3)}", null, () => _ = AppRef.InstallUpdateAsync(), color: Blue);
            }

            Separator();
            Action("Exit", null, () => AppRef.ExitApp());
        }

        static string TvState(TvDevice tv)
        {
            if (!tv.IsPaired) return "not paired";
            if (Cast.State == CastState.Sharing) return "sharing";
            if (Cast.ClockShowing) return "clock";
            return "ready";
        }

        void Action(string text, string hint, Action click, bool enabled = true, Brush color = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            if (color != null) label.Foreground = color;
            grid.Children.Add(label);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new TextBlock { Text = hint, Foreground = Gray, FontSize = 12.5, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(h, 1);
                grid.Children.Add(h);
            }
            var b = new Button { Style = (Style)Resources["MenuButton"], Content = grid, IsEnabled = enabled };
            b.Click += (s, e) => { SafeClose(); click(); };
            Items.Children.Add(b);
        }

        void Check(string text, string detail, bool isChecked, Action click)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 13,
                Foreground = Blue,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = isChecked ? 1 : 0,
            });
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            if (detail != null)
            {
                var d = new TextBlock { Text = detail, Foreground = Gray, FontSize = 12.5, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(d, 2);
                grid.Children.Add(d);
            }
            var b = new Button { Style = (Style)Resources["MenuButton"], Content = grid };
            b.Click += (s, e) => { SafeClose(); click(); };
            Items.Children.Add(b);
        }

        void Info(string text) => Items.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Gray,
            FontSize = 15,
            Margin = new Thickness(22, 8, 22, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
        });

        void Separator() => Items.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEB)), Margin = new Thickness(0, 7, 0, 7) });

        void VolumeRow()
        {
            var grid = new Grid { Margin = new Thickness(22, 6, 16, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = "Volume", FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), VerticalAlignment = VerticalAlignment.Center });
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(Round("", "Volume down", () => Cast.VolumeStepAsync(false)));
            _volumeText = new TextBlock { Text = "–", Width = 34, TextAlignment = TextAlignment.Center, FontSize = 14, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(_volumeText);
            controls.Children.Add(Round("", "Volume up", () => Cast.VolumeStepAsync(true)));
            var mute = Round("", "Mute", () => Cast.ToggleMuteAsync());
            mute.Margin = new Thickness(8, 0, 0, 0);
            controls.Children.Add(mute);
            Grid.SetColumn(controls, 1);
            grid.Children.Add(controls);
            Items.Children.Add(grid);
        }

        Button Round(string glyph, string tip, Func<Task> action)
        {
            var b = new Button
            {
                Style = (Style)Resources["RoundButton"],
                ToolTip = tip,
                Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)) },
            };
            // Volume buttons keep the menu open so you can press them repeatedly.
            b.Click += (s, e) => AppRef.Run(async () => { await action(); await LoadVolumeAsync(); });
            return b;
        }

        async Task LoadVolumeAsync()
        {
            if (_volumeText == null) return;
            var v = await Cast.GetVolumeAsync();
            if (v.HasValue) _volumeText.Text = v.Value.muted ? "Mute" : v.Value.volume.ToString();
        }
    }
}
