using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LGNetworkCast.Core
{
    public static class WakeOnLan
    {
        public static void Send(IEnumerable<string> macs)
        {
            var targets = new List<IPAddress> { IPAddress.Broadcast };
            targets.AddRange(SubnetBroadcasts());
            using (var udp = new UdpClient { EnableBroadcast = true })
            {
                foreach (var mac in macs.Where(m => !string.IsNullOrWhiteSpace(m)))
                {
                    var bytes = mac.Split(':', '-').Select(h => Convert.ToByte(h, 16)).ToArray();
                    if (bytes.Length != 6) continue;
                    var packet = Enumerable.Repeat((byte)0xFF, 6).Concat(Enumerable.Repeat(bytes, 16).SelectMany(b => b)).ToArray();
                    foreach (var ip in targets)
                        foreach (var port in new[] { 9, 7 })
                            try { udp.Send(packet, packet.Length, new IPEndPoint(ip, port)); } catch { /* best effort */ }
                }
            }
        }

        static IEnumerable<IPAddress> SubnetBroadcasts()
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    yield return new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
                }
        }
    }
}
