using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LGNetworkCast.Core
{
    /// <summary>Finds LG webOS TVs on the LAN using SSDP (UPnP) discovery.</summary>
    public static class Discovery
    {
        static readonly IPEndPoint Multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:MediaRenderer:1",
            "urn:lge-com:service:webos-second-screen:1",
        };

        public static async Task<List<TvDevice>> SearchAsync(TimeSpan duration, string unicastIp = null)
        {
            var locations = new Dictionary<string, string>();   // ip -> description URL
            var webOsIps = new HashSet<string>();
            var sockets = new List<UdpClient>();

            foreach (var local in LocalAddresses())
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(local, 0));
                    sockets.Add(udp);
                    foreach (var st in SearchTargets)
                    {
                        var msg = Encoding.ASCII.GetBytes(
                            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                            $"ST: {st}\r\n\r\n");
                        var target = unicastIp != null ? new IPEndPoint(IPAddress.Parse(unicastIp), 1900) : Multicast;
                        for (var i = 0; i < 2; i++) await udp.SendAsync(msg, msg.Length, target).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"SSDP send on {local} failed", ex);
                }
            }

            var deadline = DateTime.UtcNow + duration;
            var receives = sockets.ToDictionary(s => s, s => s.ReceiveAsync());
            while (receives.Count > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var done = await Task.WhenAny(receives.Values.Cast<Task>().Concat(new[] { Task.Delay(remaining) })).ConfigureAwait(false);
                var pair = receives.FirstOrDefault(kv => kv.Value == done);
                if (pair.Key == null) break;   // timeout
                try
                {
                    var res = pair.Value.Result;
                    var text = Encoding.ASCII.GetString(res.Buffer);
                    var ip = res.RemoteEndPoint.Address.ToString();
                    var headers = ParseHeaders(text);
                    var server = headers.TryGetValue("SERVER", out var sv) ? sv : "";
                    var st = headers.TryGetValue("ST", out var stv) ? stv : "";
                    if (st.Contains("webos-second-screen") || server.IndexOf("webos", StringComparison.OrdinalIgnoreCase) >= 0)
                        webOsIps.Add(ip);
                    if (st.Contains("MediaRenderer") && headers.TryGetValue("LOCATION", out var loc))
                        locations[ip] = loc;
                    receives[pair.Key] = pair.Key.ReceiveAsync();
                }
                catch
                {
                    receives.Remove(pair.Key);
                }
            }
            foreach (var s in sockets) s.Dispose();

            var tvs = new List<TvDevice>();
            foreach (var ip in webOsIps)
            {
                var tv = new TvDevice { Ip = ip, Id = "ip:" + ip, Name = "LG TV (" + ip + ")" };
                if (locations.TryGetValue(ip, out var location))
                {
                    tv.DlnaDescriptionUrl = location;
                    try
                    {
                        using (var wc = new System.Net.WebClient())
                        {
                            var xml = XDocument.Parse(await wc.DownloadStringTaskAsync(location).ConfigureAwait(false));
                            var dev = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                            string Get(string n) => dev?.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value;
                            tv.Id = Get("UDN") ?? tv.Id;
                            var friendly = Get("friendlyName");
                            if (!string.IsNullOrWhiteSpace(friendly)) tv.Name = friendly.Replace("[LG] ", "").Trim();
                            tv.Model = friendly?.Split(' ').LastOrDefault();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Could not read device description of {ip}", ex);
                    }
                }
                var mac = Arp.Lookup(ip);
                if (mac != null) tv.Macs.Add(mac);
                tvs.Add(tv);
            }
            return tvs;
        }

        static Dictionary<string, string> ParseHeaders(string response)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var i = line.IndexOf(':');
                if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return d;
        }

        public static IEnumerable<IPAddress> LocalAddresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address);

        /// <summary>The local address the OS would use to reach <paramref name="remoteIp"/>.</summary>
        public static string LocalIpFor(string remoteIp)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect(remoteIp, 9);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }
    }

    static class Arp
    {
        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref uint physicalAddrLen);

        public static string Lookup(string ip)
        {
            try
            {
                var mac = new byte[6];
                uint len = 6;
                if (SendARP(BitConverter.ToInt32(IPAddress.Parse(ip).GetAddressBytes(), 0), 0, mac, ref len) != 0) return null;
                return string.Join(":", mac.Take((int)len).Select(b => b.ToString("X2")));
            }
            catch
            {
                return null;
            }
        }
    }
}
