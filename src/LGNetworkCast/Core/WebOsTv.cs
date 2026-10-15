using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LGNetworkCast.Core
{
    /// <summary>
    /// Minimal client for the LG webOS "SSAP" second-screen protocol (WebSocket on port 3000/3001).
    /// </summary>
    public sealed class WebOsTv : IDisposable
    {
        public const string BrowserAppId = "com.webos.app.browser";

        readonly string _ip;
        ClientWebSocket _ws;
        int _nextId;
        readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        CancellationTokenSource _cts;

        public string ClientKey { get; private set; }

        static WebOsTv()
        {
            // Newer webOS firmware only offers wss:// with a self-signed certificate.
            ServicePointManager.ServerCertificateValidationCallback += (s, cert, chain, errors) =>
                s is HttpWebRequest req && (req.RequestUri.Port == 3001);
        }

        public WebOsTv(string ip, string clientKey)
        {
            _ip = ip;
            ClientKey = clientKey;
        }

        public static bool IsReachable(string ip, int timeoutMs = 800)
        {
            foreach (var port in new[] { 3000, 3001 })
            {
                try
                {
                    using (var c = new TcpClient())
                    {
                        var t = c.ConnectAsync(ip, port);
                        if (t.Wait(timeoutMs) && c.Connected) return true;
                    }
                }
                catch
                {
                    // try next port
                }
            }
            return false;
        }

        /// <summary>Connects and registers. If not yet paired, the TV shows a prompt; waits up to <paramref name="pairTimeout"/>.</summary>
        public async Task ConnectAsync(TimeSpan pairTimeout)
        {
            Exception last = null;
            foreach (var uri in new[] { $"ws://{_ip}:3000", $"wss://{_ip}:3001" })
            {
                try
                {
                    _ws = new ClientWebSocket();
                    _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await _ws.ConnectAsync(new Uri(uri), cts.Token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _ws.Dispose();
                    _ws = null;
                }
            }
            if (_ws == null) throw new IOException($"Cannot connect to TV at {_ip}", last);

            _cts = new CancellationTokenSource();
            var registered = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending["register_0"] = registered;
            _ = Task.Run(ReceiveLoop);

            await SendAsync(new Dictionary<string, object>
            {
                ["type"] = "register",
                ["id"] = "register_0",
                ["payload"] = RegistrationPayload(ClientKey),
            }).ConfigureAwait(false);

            var done = await Task.WhenAny(registered.Task, Task.Delay(pairTimeout)).ConfigureAwait(false);
            if (done != registered.Task) throw new TimeoutException("The TV did not accept the connection. Accept the prompt on the TV screen.");
            var payload = await registered.Task.ConfigureAwait(false);
            ClientKey = payload.Str("client-key") ?? ClientKey;
        }

        async Task ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var res = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    if (!res.EndOfMessage) continue;
                    var msg = Json.Parse(sb.ToString());
                    sb.Clear();
                    var id = msg.Str("id");
                    var type = msg.Str("type");
                    if (id == null || !_pending.TryGetValue(id, out var tcs)) continue;
                    // Registration first answers "response" (prompt shown) and later "registered".
                    if (id == "register_0" && type == "response") continue;
                    _pending.TryRemove(id, out _);
                    if (type == "error") tcs.TrySetException(new InvalidOperationException(msg.Str("error") ?? "TV error"));
                    else tcs.TrySetResult(msg.Obj("payload") ?? new Dictionary<string, object>());
                }
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) Log.Error("webOS receive loop ended", ex);
            }
            foreach (var p in _pending.Values) p.TrySetException(new IOException("Connection to TV closed"));
        }

        async Task SendAsync(object message)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Serialize(message));
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<Dictionary<string, object>> RequestAsync(string uri, object payload = null, int timeoutMs = 8000)
        {
            var id = "req_" + Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await SendAsync(new Dictionary<string, object>
            {
                ["id"] = id,
                ["type"] = "request",
                ["uri"] = "ssap://" + uri,
                ["payload"] = payload ?? new Dictionary<string, object>(),
            }).ConfigureAwait(false);
            if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false) != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException($"TV did not answer {uri}");
            }
            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>Fire-and-forget request (used for power off, where the TV may not answer).</summary>
        public Task SendCommandAsync(string uri, object payload = null) => SendAsync(new Dictionary<string, object>
        {
            ["id"] = "cmd_" + Interlocked.Increment(ref _nextId),
            ["type"] = "request",
            ["uri"] = "ssap://" + uri,
            ["payload"] = payload ?? new Dictionary<string, object>(),
        });

        // ---- High level commands ------------------------------------------------------------

        public Task PowerOffAsync() => SendCommandAsync("system/turnOff");
        public Task PowerOnAsync() => RequestAsync("system/turnOn");
        public Task ScreenOnAsync() => RequestAsync("com.webos.service.tvpower/power/turnOnScreen", new Dictionary<string, object> { ["standbyMode"] = "active" });

        public async Task<string> GetPowerStateAsync()
        {
            try { return (await RequestAsync("com.webos.service.tvpower/power/getPowerState").ConfigureAwait(false)).Str("state"); }
            catch { return null; }
        }

        public Task VolumeUpAsync() => RequestAsync("audio/volumeUp");
        public Task VolumeDownAsync() => RequestAsync("audio/volumeDown");
        public Task SetVolumeAsync(int volume) => RequestAsync("audio/setVolume", new Dictionary<string, object> { ["volume"] = volume });
        public Task SetMuteAsync(bool mute) => RequestAsync("audio/setMute", new Dictionary<string, object> { ["mute"] = mute });

        public async Task<(int volume, bool muted)> GetVolumeAsync()
        {
            var r = await RequestAsync("audio/getVolume").ConfigureAwait(false);
            var status = r.Obj("volumeStatus") ?? r;   // webOS 5+ nests the values
            return (status.Int("volume") ?? r.Int("volume") ?? 0, status.Bool("muteStatus") ?? r.Bool("muted") ?? false);
        }

        /// <summary>
        /// Opens a URL full screen in the TV's web engine and waits until it is in front.
        /// Launching with params.target (rather than "open") makes the browser session ours, so
        /// <see cref="CloseAppAsync"/> can close it later. Launch does not navigate a browser that is
        /// already running, so a running browser is closed first.
        /// </summary>
        public async Task OpenUrlAsync(string url)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (await GetForegroundAppAsync().ConfigureAwait(false) == BrowserAppId)
                {
                    try
                    {
                        await CloseAppAsync(BrowserAppId).ConfigureAwait(false);
                        await Task.Delay(1200).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // A browser session we don't own: navigate it instead.
                        await RequestAsync("system.launcher/open", new Dictionary<string, object> { ["target"] = url }).ConfigureAwait(false);
                        return;
                    }
                }
                await RequestAsync("system.launcher/launch", new Dictionary<string, object>
                {
                    ["id"] = BrowserAppId,
                    ["params"] = new Dictionary<string, object> { ["target"] = url },
                }).ConfigureAwait(false);

                for (var i = 0; i < 16; i++)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    if (await GetForegroundAppAsync().ConfigureAwait(false) == BrowserAppId) return;
                }
                Log.Info($"Browser did not come to the front (attempt {attempt})");
            }
            throw new TimeoutException("The TV did not open the page. Is the TV on and showing a picture?");
        }
        public Task LaunchAppAsync(string appId) => RequestAsync("system.launcher/launch", new Dictionary<string, object> { ["id"] = appId });
        /// <summary>
        /// Closes an app. webOS refuses close-by-id (403) for apps we did not launch, but accepts the
        /// session id, which is simply the base64 of the app id.
        /// </summary>
        public async Task CloseAppAsync(string appId)
        {
            try
            {
                await RequestAsync("system.launcher/close", new Dictionary<string, object>
                {
                    ["sessionId"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(appId)),
                }).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await RequestAsync("system.launcher/close", new Dictionary<string, object> { ["id"] = appId }).ConfigureAwait(false);
            }
        }
        public Task ToastAsync(string message) => RequestAsync("system.notifications/createToast", new Dictionary<string, object> { ["message"] = message });

        public async Task<string> GetForegroundAppAsync()
        {
            try { return (await RequestAsync("com.webos.applicationManager/getForegroundAppInfo").ConfigureAwait(false)).Str("appId"); }
            catch { return null; }
        }

        public async Task<List<string>> GetMacAddressesAsync()
        {
            var macs = new List<string>();
            try
            {
                var r = await RequestAsync("com.webos.service.connectionmanager/getinfo").ConfigureAwait(false);
                foreach (var k in new[] { "wiredInfo", "wifiInfo" })
                {
                    var mac = r.Obj(k).Str("macAddress");
                    if (!string.IsNullOrEmpty(mac)) macs.Add(mac.ToUpperInvariant());
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read TV MAC addresses", ex);
            }
            return macs;
        }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
                if (_ws?.State == WebSocketState.Open)
                    _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(500);
            }
            catch
            {
                // ignore
            }
            _ws?.Dispose();
        }

        static Dictionary<string, object> RegistrationPayload(string clientKey)
        {
            var payload = new Dictionary<string, object>
            {
                ["forcePairing"] = false,
                ["pairingType"] = "PROMPT",
                ["manifest"] = new Dictionary<string, object>
                {
                    ["manifestVersion"] = 1,
                    ["appVersion"] = "1.1",
                    ["permissions"] = new[]
                    {
                        "APP_TO_APP", "CLOSE", "CONTROL_AUDIO", "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK",
                        "CONTROL_INPUT_MEDIA_PLAYBACK", "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_TEXT",
                        "CONTROL_INPUT_TV", "CONTROL_MOUSE_AND_KEYBOARD", "CONTROL_POWER", "CONTROL_TV_SCREEN",
                        "LAUNCH", "LAUNCH_WEBAPP", "READ_APP_STATUS", "READ_COUNTRY_INFO", "READ_CURRENT_CHANNEL",
                        "READ_INPUT_DEVICE_LIST", "READ_INSTALLED_APPS", "READ_LGE_SDX", "READ_LGE_TV_INPUT_EVENTS",
                        "READ_NETWORK_STATE", "READ_NOTIFICATIONS", "READ_POWER_STATE", "READ_RUNNING_APPS",
                        "READ_SETTINGS", "READ_TV_CHANNEL_LIST", "READ_TV_CURRENT_TIME", "READ_UPDATE_INFO", "SEARCH",
                        "TEST_OPEN", "TEST_PROTECTED", "TEST_SECURE", "UPDATE_FROM_REMOTE_APP",
                        "WRITE_NOTIFICATION_ALERT", "WRITE_NOTIFICATION_TOAST", "WRITE_SETTINGS",
                    },
                },
            };
            if (!string.IsNullOrEmpty(clientKey)) payload["client-key"] = clientKey;
            return payload;
        }
    }
}
