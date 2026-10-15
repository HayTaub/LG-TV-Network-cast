using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LGNetworkCast.Core
{
    /// <summary>
    /// Tiny HTTP + WebSocket server on a raw TcpListener (HttpListener would need admin URL reservations).
    ///   GET /            viewer page for the TV
    ///   GET /clock       flip clock page (runs entirely on the TV)
    ///   GET /ws          WebSocket: fragmented MP4 from ffmpeg (fast mode)
    ///   GET|HEAD /screen.ts   MPEG-TS stream (DLNA mode)
    /// </summary>
    public sealed class StreamServer : IDisposable
    {
        readonly Func<string> _fastArgs;
        readonly Func<string> _dlnaArgs;
        readonly Func<string> _codec;
        TcpListener _listener;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        ViewerConnection _viewer;
        readonly object _gate = new object();

        public int Port { get; }

        /// <summary>Raised when the TV viewer connects (true) or disconnects (false).</summary>
        public event Action<bool> ViewerChanged;

        public StreamServer(int port, Func<string> codec, Func<string> fastArgs, Func<string> dlnaArgs)
        {
            Port = port;
            _codec = codec;
            _fastArgs = fastArgs;
            _dlnaArgs = dlnaArgs;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => Handle(client));
            }
        }

        async Task Handle(TcpClient client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                var request = await ReadHeadersAsync(stream).ConfigureAwait(false);
                if (request == null) return;
                var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var parts = lines[0].Split(' ');
                var method = parts[0];
                var path = parts.Length > 1 ? parts[1].Split('?')[0] : "/";
                string key = null;
                foreach (var l in lines)
                    if (l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = l.Substring(18).Trim();

                if (path == "/ws" && key != null)
                {
                    await ServeWebSocket(client, stream, key).ConfigureAwait(false);
                    return;
                }
                if (path == "/screen.ts")
                {
                    await ServeTs(stream, method == "HEAD").ConfigureAwait(false);
                    return;
                }
                if (path == "/" || path == "/index.html")
                {
                    await WriteHtmlAsync(stream, Resource("viewer.html").Replace("{{CODEC}}", _codec())).ConfigureAwait(false);
                    return;
                }
                if (path == "/clock")
                {
                    var nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    await WriteHtmlAsync(stream, Resource("clock.html").Replace("{{NOW}}", nowMs.ToString())).ConfigureAwait(false);
                    return;
                }
                await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) Log.Info("HTTP client ended: " + ex.Message);
            }
            finally
            {
                client.Close();
            }
        }

        async Task ServeWebSocket(TcpClient client, NetworkStream stream, string key)
        {
            string accept;
            using (var sha = SHA1.Create())
                accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await WriteAsync(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                     $"Sec-WebSocket-Accept: {accept}\r\n\r\n").ConfigureAwait(false);

            var viewer = new ViewerConnection(stream);
            lock (_gate)
            {
                _viewer?.Dispose();    // only one viewer at a time
                _viewer = viewer;
            }
            ViewerChanged?.Invoke(true);
            Log.Info("Viewer connected from " + client.Client.RemoteEndPoint);

            var ffmpeg = Encoder.Start(_fastArgs());
            var readTask = viewer.ReadLoopAsync();
            try
            {
                var buf = new byte[256 * 1024];
                var output = ffmpeg.StandardOutput.BaseStream;
                while (!viewer.Closed && !_cts.IsCancellationRequested)
                {
                    var n = await output.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (n <= 0) break;
                    await viewer.SendAsync(buf, n, binary: true).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Info("Viewer stream ended: " + ex.Message);
            }
            finally
            {
                Kill(ffmpeg);
                bool wasCurrent;
                lock (_gate)
                {
                    wasCurrent = _viewer == viewer;
                    if (wasCurrent) _viewer = null;
                }
                viewer.Dispose();
                if (wasCurrent) ViewerChanged?.Invoke(false);
            }
            await Task.WhenAny(readTask, Task.Delay(200)).ConfigureAwait(false);
        }

        async Task ServeTs(NetworkStream stream, bool headOnly)
        {
            await WriteAsync(stream, "HTTP/1.0 200 OK\r\nContent-Type: video/mp2t\r\ntransferMode.dlna.org: Streaming\r\n" +
                                     $"contentFeatures.dlna.org: {Dlna.ContentFeatures}\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
            if (headOnly) return;
            var ffmpeg = Encoder.Start(_dlnaArgs());
            try
            {
                var buf = new byte[64 * 1024];
                var output = ffmpeg.StandardOutput.BaseStream;
                int n;
                while (!_cts.IsCancellationRequested && (n = await output.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                    await stream.WriteAsync(buf, 0, n).ConfigureAwait(false);
            }
            finally
            {
                Kill(ffmpeg);
            }
        }

        /// <summary>Tells the viewer page that sharing has ended.</summary>
        public async Task NotifyStopAsync()
        {
            ViewerConnection v;
            lock (_gate) v = _viewer;
            if (v == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes("stop");
                await v.SendAsync(bytes, bytes.Length, binary: false).ConfigureAwait(false);
            }
            catch
            {
                // viewer already gone
            }
        }

        static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch { /* already exited */ }
            p.Dispose();
        }

        static async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (sb.Length < 16384)
            {
                var n = await stream.ReadAsync(buf, 0, 1).ConfigureAwait(false);
                if (n == 0) return null;
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") return sb.ToString();
            }
            return null;
        }

        static Task WriteAsync(Stream s, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            return s.WriteAsync(b, 0, b.Length);
        }

        static async Task WriteHtmlAsync(Stream stream, string text)
        {
            var html = Encoding.UTF8.GetBytes(text);
            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\n" +
                                     "Cache-Control: no-store\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
            await stream.WriteAsync(html, 0, html.Length).ConfigureAwait(false);
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Resources =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        static string Resource(string name) => Resources.GetOrAdd(name, n =>
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(n))
            using (var r = new StreamReader(s))
                return r.ReadToEnd();
        });

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener?.Stop(); } catch { /* ignore */ }
            lock (_gate)
            {
                _viewer?.Dispose();
                _viewer = null;
            }
        }

        /// <summary>Server side of one WebSocket connection (server frames are never masked).</summary>
        sealed class ViewerConnection : IDisposable
        {
            readonly NetworkStream _stream;
            readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
            public bool Closed { get; private set; }

            public ViewerConnection(NetworkStream stream) { _stream = stream; }

            public async Task SendAsync(byte[] data, int count, bool binary)
            {
                var header = new byte[10];
                int hlen;
                header[0] = (byte)(0x80 | (binary ? 0x2 : 0x1));
                if (count < 126) { header[1] = (byte)count; hlen = 2; }
                else if (count <= ushort.MaxValue) { header[1] = 126; header[2] = (byte)(count >> 8); header[3] = (byte)count; hlen = 4; }
                else
                {
                    header[1] = 127;
                    for (var i = 0; i < 8; i++) header[9 - i] = (byte)((long)count >> (8 * i));
                    hlen = 10;
                }
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _stream.WriteAsync(header, 0, hlen).ConfigureAwait(false);
                    await _stream.WriteAsync(data, 0, count).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            /// <summary>Reads client frames: detects close and logs the viewer's latency reports.</summary>
            public async Task ReadLoopAsync()
            {
                try
                {
                    var h = new byte[2];
                    while (!Closed)
                    {
                        if (!await ReadExactly(h, 2)) break;
                        var opcode = h[0] & 0x0F;
                        long len = h[1] & 0x7F;
                        if (len == 126) { var e = new byte[2]; if (!await ReadExactly(e, 2)) break; len = (e[0] << 8) | e[1]; }
                        else if (len == 127) { var e = new byte[8]; if (!await ReadExactly(e, 8)) break; len = 0; for (var i = 0; i < 8; i++) len = (len << 8) | e[i]; }
                        var mask = new byte[4];
                        if ((h[1] & 0x80) != 0 && !await ReadExactly(mask, 4)) break;
                        var payload = new byte[len];
                        if (len > 0 && !await ReadExactly(payload, (int)len)) break;
                        for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                        if (opcode == 0x8) break;
                        if (opcode == 0x1) Log.Info("Viewer: " + Encoding.UTF8.GetString(payload));
                    }
                }
                catch
                {
                    // connection dropped
                }
                Closed = true;
            }

            async Task<bool> ReadExactly(byte[] buf, int count)
            {
                var off = 0;
                while (off < count)
                {
                    var n = await _stream.ReadAsync(buf, off, count - off).ConfigureAwait(false);
                    if (n == 0) return false;
                    off += n;
                }
                return true;
            }

            public void Dispose()
            {
                Closed = true;
                try { _stream.Close(); } catch { /* ignore */ }
            }
        }
    }
}
