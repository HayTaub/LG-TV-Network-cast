using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LGNetworkCast.Core
{
    public enum CastState { Idle, Starting, Sharing, Stopping }

    /// <summary>
    /// All TV and sharing operations, with self-healing: reconnects dropped TV connections, finds the TV
    /// again when its IP changes, re-pairs after a TV reset, retries the viewer and falls back to DLNA.
    /// UI-agnostic; raises <see cref="Changed"/> for the window and tray.
    /// </summary>
    public sealed class CastController : IDisposable
    {
        const int ViewerTimeoutMs = 12000;

        readonly Settings _settings;
        readonly SemaphoreSlim _tvLock = new SemaphoreSlim(1, 1);
        WebOsTv _tv;
        string _tvConnectedId;
        StreamServer _server;
        int _port;
        string _previousApp;
        string _dlnaControlUrl;
        StreamEngine _activeEngine;
        CancellationTokenSource _viewerWatch;
        TaskCompletionSource<bool> _viewerConnected;

        public CastState State { get; private set; } = CastState.Idle;
        public string Status { get; private set; } = "";
        public bool ClockShowing { get; private set; }

        public event Action Changed;

        public CastController(Settings settings) { _settings = settings; }

        void Set(CastState? state = null, string status = null)
        {
            if (state.HasValue) State = state.Value;
            if (status != null) Status = status;
            Changed?.Invoke();
        }

        TvDevice RequireTv()
        {
            var tv = _settings.DefaultTv;
            if (tv == null) throw new InvalidOperationException("No TV selected. Search for TVs first.");
            if (!tv.IsPaired) throw new InvalidOperationException($"\"{tv.Name}\" is not paired yet.");
            return tv;
        }

        static void RequireFirewallOpen()
        {
            if (Firewall.IsBlocked)
                throw new InvalidOperationException("Windows Firewall is blocking the TV from reaching this PC. Click Fix in the app window.");
        }

        MonitorInfo CurrentMonitor()
        {
            var monitors = Monitors.List();
            return monitors.ElementAtOrDefault(_settings.MonitorIndex) ?? monitors[0];
        }

        string BaseUrl(TvDevice tv) => $"http://{Discovery.LocalIpFor(tv.Ip)}:{_port}";

        // ---- Local server -----------------------------------------------------------------------

        /// <summary>Starts the local web server (viewer, stream and clock pages). Runs for the app's lifetime.</summary>
        public void StartServer()
        {
            if (_server != null) return;
            // Self-heal: if the configured port is taken, use the next free one.
            for (var port = _settings.Port; port < _settings.Port + 20; port++)
            {
                var server = new StreamServer(port,
                    () => Encoder.LevelFor(Encoder.OutputHeight(_settings, CurrentMonitor())).codec,
                    () => Encoder.FastArgs(_settings, CurrentMonitor()),
                    () => Encoder.DlnaArgs(_settings, CurrentMonitor()));
                server.ViewerChanged += OnViewerChanged;
                try
                {
                    server.Start();
                    _server = server;
                    _port = port;
                    if (port != _settings.Port) Log.Info($"Port {_settings.Port} busy, using {port}");
                    return;
                }
                catch (SocketException)
                {
                    server.Dispose();
                }
            }
            throw new InvalidOperationException($"No free network port near {_settings.Port}.");
        }

        // ---- TV connection (self-healing) -------------------------------------------------------

        /// <summary>If the TV does not answer at its saved IP, look for it on the network (DHCP may have moved it).</summary>
        async Task<bool> EnsureReachableAsync(TvDevice tv, int timeoutMs = 800)
        {
            if (WebOsTv.IsReachable(tv.Ip, timeoutMs)) return true;
            try
            {
                var found = (await Discovery.SearchAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)).FirstOrDefault(t => t.Id == tv.Id);
                if (found != null && found.Ip != tv.Ip)
                {
                    Log.Info($"{tv.Name} moved from {tv.Ip} to {found.Ip}");
                    tv.Ip = found.Ip;
                    tv.DlnaDescriptionUrl = found.DlnaDescriptionUrl ?? tv.DlnaDescriptionUrl;
                    _settings.Save();
                    DropConnection();
                    return WebOsTv.IsReachable(tv.Ip, timeoutMs);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Rediscovery failed", ex);
            }
            return false;
        }

        /// <summary>Returns a live, registered connection to the TV (reconnecting when needed).</summary>
        async Task<WebOsTv> TvAsync(TvDevice tv)
        {
            await _tvLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_tv != null && _tvConnectedId == tv.Id)
                {
                    try
                    {
                        await _tv.RequestAsync("com.webos.service.tvpower/power/getPowerState", null, 3000).ConfigureAwait(false);
                        return _tv;
                    }
                    catch
                    {
                        // stale connection - reconnect below
                    }
                }
                DropConnection();
                await EnsureReachableAsync(tv).ConfigureAwait(false);
                var c = new WebOsTv(tv.Ip, tv.ClientKey);
                try
                {
                    await c.ConnectAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The saved key was rejected (e.g. the TV was reset): the TV now shows a pairing prompt.
                    Set(status: "Accept the connection prompt on the TV…");
                    c.Dispose();
                    c = new WebOsTv(tv.Ip, null);   // fresh pairing request
                    await c.ConnectAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                }
                if (c.ClientKey != tv.ClientKey)
                {
                    tv.ClientKey = c.ClientKey;
                    _settings.Save();
                }
                _tv = c;
                _tvConnectedId = tv.Id;
                return c;
            }
            finally
            {
                _tvLock.Release();
            }
        }

        /// <summary>Runs a TV command; on a dropped connection, reconnects once and retries.</summary>
        async Task<T> WithTvAsync<T>(Func<WebOsTv, Task<T>> action)
        {
            var tv = RequireTv();
            try
            {
                return await action(await TvAsync(tv).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is System.Net.WebSockets.WebSocketException || ex is ObjectDisposedException)
            {
                Log.Info("TV command failed, reconnecting: " + ex.Message);
                DropConnection();
                return await action(await TvAsync(tv).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        Task WithTvAsync(Func<WebOsTv, Task> action) => WithTvAsync(async c => { await action(c).ConfigureAwait(false); return true; });

        /// <summary>Call after the PC resumes from sleep or the network changes.</summary>
        public void ResetConnection() => DropConnection();

        /// <summary>First connection to a TV: the TV shows an "allow" prompt.</summary>
        public async Task PairAsync(TvDevice tv)
        {
            using (var c = new WebOsTv(tv.Ip, tv.ClientKey))
            {
                await c.ConnectAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                tv.ClientKey = c.ClientKey;
                var macs = await c.GetMacAddressesAsync().ConfigureAwait(false);
                foreach (var m in macs) if (!tv.Macs.Contains(m)) tv.Macs.Add(m);
                await c.ToastAsync("Connected to LG Network Cast").ConfigureAwait(false);
            }
            _settings.Save();
        }

        /// <summary>Returns the TV to whatever it showed before (e.g. HDMI 1), otherwise closes the browser.</summary>
        async Task RestoreTvAsync(WebOsTv c)
        {
            try
            {
                if (_previousApp != null) await c.LaunchAppAsync(_previousApp).ConfigureAwait(false);
                else await c.CloseAppAsync(WebOsTv.BrowserAppId).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Log.Info("Could not return the TV to its previous app: " + ex.Message);
            }
        }

        async Task RememberForegroundAsync(WebOsTv c)
        {
            var foreground = await c.GetForegroundAppAsync().ConfigureAwait(false);
            _previousApp = foreground == WebOsTv.BrowserAppId ? null : foreground;
        }

        // ---- Power & volume --------------------------------------------------------------------

        public async Task TurnOnAsync()
        {
            var tv = RequireTv();
            if (!await EnsureReachableAsync(tv).ConfigureAwait(false))
            {
                Set(status: $"Turning on {tv.Name}…");
                if (tv.Macs.Count == 0) throw new InvalidOperationException("The TV is off and its MAC address is unknown. Turn it on once with the remote and pair again.");
                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (!WebOsTv.IsReachable(tv.Ip))
                {
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("The TV did not wake up. On the TV enable Settings > General > Devices > \"Turn on via Wi-Fi\" (or \"Mobile TV On\").");
                    WakeOnLan.Send(tv.Macs);
                    await Task.Delay(1500).ConfigureAwait(false);
                }
                await Task.Delay(2000).ConfigureAwait(false);   // let webOS finish starting its services
            }
            await WithTvAsync(async c =>
            {
                var state = await c.GetPowerStateAsync().ConfigureAwait(false);
                if (state == "Screen Off") await c.ScreenOnAsync().ConfigureAwait(false);
                else if (state != null && state != "Active") await c.PowerOnAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            Set(status: "");
        }

        public async Task TurnOffAsync()
        {
            var tv = RequireTv();
            if (State == CastState.Sharing) await StopAsync(powerOff: false, restoreTv: false).ConfigureAwait(false);
            ClockShowing = false;
            _previousApp = null;
            if (WebOsTv.IsReachable(tv.Ip))
            {
                await WithTvAsync(c => c.PowerOffAsync()).ConfigureAwait(false);
                DropConnection();
            }
            Set();
        }

        public Task VolumeStepAsync(bool up) => WithTvAsync(c => up ? c.VolumeUpAsync() : c.VolumeDownAsync());
        public Task SetVolumeAsync(int volume) => WithTvAsync(c => c.SetVolumeAsync(volume));
        public Task SetMuteAsync(bool mute) => WithTvAsync(c => c.SetMuteAsync(mute));

        public async Task<(int volume, bool muted)?> GetVolumeAsync()
        {
            var tv = _settings.DefaultTv;
            if (tv == null || !tv.IsPaired || !WebOsTv.IsReachable(tv.Ip, 400)) return null;
            try { return await (await TvAsync(tv).ConfigureAwait(false)).GetVolumeAsync().ConfigureAwait(false); }
            catch { return null; }
        }

        public async Task ToggleMuteAsync()
        {
            var v = await GetVolumeAsync().ConfigureAwait(false);
            if (v.HasValue) await SetMuteAsync(!v.Value.muted).ConfigureAwait(false);
        }

        // ---- Flip clock ------------------------------------------------------------------------

        public Task ToggleClockAsync() => ClockShowing ? HideClockAsync() : ShowClockAsync();

        /// <summary>Shows the flip clock on the TV. The page runs on the TV itself, so nothing is streamed.</summary>
        public async Task ShowClockAsync()
        {
            var tv = RequireTv();
            RequireFirewallOpen();
            if (State != CastState.Idle) await StopAsync(powerOff: false, restoreTv: false).ConfigureAwait(false);
            StartServer();
            await TurnOnAsync().ConfigureAwait(false);
            Set(status: $"Opening the clock on {tv.Name}…");
            await WithTvAsync(async c =>
            {
                if (!ClockShowing && _previousApp == null) await RememberForegroundAsync(c).ConfigureAwait(false);
                await c.OpenUrlAsync(BaseUrl(tv) + "/clock").ConfigureAwait(false);
            }).ConfigureAwait(false);
            ClockShowing = true;
            Set(status: $"Clock is showing on {tv.Name}");
        }

        public async Task HideClockAsync()
        {
            if (!ClockShowing) return;
            ClockShowing = false;
            var tv = _settings.DefaultTv;
            try
            {
                if (tv != null && WebOsTv.IsReachable(tv.Ip)) await WithTvAsync(RestoreTvAsync).ConfigureAwait(false);
            }
            finally
            {
                _previousApp = null;
                Set(status: "");
            }
        }

        // ---- Sharing ----------------------------------------------------------------------------

        public Task ToggleAsync() => State == CastState.Idle ? StartAsync() : StopAsync();

        public async Task StartAsync()
        {
            if (State != CastState.Idle) return;
            var tv = RequireTv();
            RequireFirewallOpen();
            Set(CastState.Starting, "Preparing…");
            try
            {
                if (_settings.PowerOnWithShare || !await EnsureReachableAsync(tv).ConfigureAwait(false)) await TurnOnAsync().ConfigureAwait(false);
                await Encoder.DetectAsync().ConfigureAwait(false);
                StartServer();
                var monitor = CurrentMonitor();
                var baseUrl = BaseUrl(tv);

                // If the clock was up, keep the app that was showing before it.
                await WithTvAsync(async c => { if (!ClockShowing) await RememberForegroundAsync(c).ConfigureAwait(false); }).ConfigureAwait(false);
                ClockShowing = false;

                _activeEngine = _settings.Engine;
                if (_activeEngine == StreamEngine.Fast && !await StartFastAsync(tv, baseUrl).ConfigureAwait(false))
                {
                    if (tv.DlnaDescriptionUrl == null) throw new TimeoutException("The TV opened the page but never connected. Check that the firewall allows LG Network Cast.");
                    Log.Info("Fast viewer did not connect; falling back to DLNA");
                    _activeEngine = StreamEngine.Dlna;
                }
                if (_activeEngine == StreamEngine.Dlna)
                {
                    if (tv.DlnaDescriptionUrl == null) throw new InvalidOperationException("DLNA is not available for this TV. Search for TVs again.");
                    Set(status: $"Opening on {tv.Name} (DLNA)…");
                    _dlnaControlUrl = await Dlna.GetAvTransportUrlAsync(tv.DlnaDescriptionUrl).ConfigureAwait(false);
                    await Dlna.PlayAsync(_dlnaControlUrl, baseUrl + "/screen.ts").ConfigureAwait(false);
                }
                var note = _activeEngine != _settings.Engine ? " (DLNA fallback)" : "";
                Set(CastState.Sharing, $"Sharing {monitor.Label} to {tv.Name}{note}");
            }
            catch
            {
                Set(CastState.Idle, "");
                throw;
            }
        }

        /// <summary>Opens the viewer and waits for it to connect; reopens it once if it doesn't.</summary>
        async Task<bool> StartFastAsync(TvDevice tv, string baseUrl)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                _viewerConnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(status: attempt == 1 ? $"Opening on {tv.Name}…" : $"Retrying on {tv.Name}…");
                await WithTvAsync(c => c.OpenUrlAsync(baseUrl + "/")).ConfigureAwait(false);
                if (await Task.WhenAny(_viewerConnected.Task, Task.Delay(ViewerTimeoutMs)).ConfigureAwait(false) == _viewerConnected.Task) return true;
                Log.Info($"Viewer did not connect (attempt {attempt})");
            }
            return false;
        }

        public async Task StopAsync(bool? powerOff = null, bool restoreTv = true)
        {
            if (State != CastState.Sharing && State != CastState.Starting) return;
            Set(CastState.Stopping, "Stopping…");
            var tv = _settings.DefaultTv;
            try
            {
                _viewerWatch?.Cancel();
                if (_server != null) await _server.NotifyStopAsync().ConfigureAwait(false);
                if (_dlnaControlUrl != null) await Dlna.StopAsync(_dlnaControlUrl).ConfigureAwait(false);

                if (restoreTv && tv != null && WebOsTv.IsReachable(tv.Ip))
                {
                    if (powerOff ?? _settings.PowerOffWithStop)
                    {
                        await WithTvAsync(c => c.PowerOffAsync()).ConfigureAwait(false);
                        DropConnection();
                    }
                    else if (_activeEngine == StreamEngine.Fast)
                    {
                        await WithTvAsync(RestoreTvAsync).ConfigureAwait(false);
                    }
                    else if (_previousApp != null)
                    {
                        await WithTvAsync(c => c.LaunchAppAsync(_previousApp)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error while stopping", ex);
            }
            finally
            {
                if (restoreTv) _previousApp = null;
                _dlnaControlUrl = null;
                Set(CastState.Idle, "");
            }
        }

        void OnViewerChanged(bool connected)
        {
            _viewerWatch?.Cancel();
            if (connected)
            {
                _viewerConnected?.TrySetResult(true);
                return;
            }
            if (State != CastState.Sharing || _activeEngine != StreamEngine.Fast) return;
            // The viewer reconnects by itself after network blips. If it stays away (someone pressed
            // Back or switched input with the remote), end the session.
            var cts = _viewerWatch = new CancellationTokenSource();
            Task.Delay(8000, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled && State == CastState.Sharing) _ = StopAsync(powerOff: false, restoreTv: false);
            });
        }

        void DropConnection()
        {
            _tv?.Dispose();
            _tv = null;
            _tvConnectedId = null;
        }

        public void Dispose()
        {
            _viewerWatch?.Cancel();
            _server?.Dispose();
            _server = null;
            DropConnection();
        }
    }
}
