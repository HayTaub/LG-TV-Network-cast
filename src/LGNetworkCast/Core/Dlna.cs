using System;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LGNetworkCast.Core
{
    /// <summary>Drives the TV's built-in DLNA media renderer (UPnP AVTransport).</summary>
    public static class Dlna
    {
        public const string ContentFeatures = "DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

        public static async Task<string> GetAvTransportUrlAsync(string descriptionUrl)
        {
            using (var wc = new WebClient())
            {
                var xml = XDocument.Parse(await wc.DownloadStringTaskAsync(descriptionUrl).ConfigureAwait(false));
                var service = xml.Descendants().Where(e => e.Name.LocalName == "service")
                    .FirstOrDefault(s => s.Elements().Any(e => e.Name.LocalName == "serviceType" && e.Value.Contains("AVTransport")));
                var control = service?.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                if (control == null) throw new InvalidOperationException("TV has no DLNA AVTransport service");
                return new Uri(new Uri(descriptionUrl), control).ToString();
            }
        }

        public static async Task PlayAsync(string controlUrl, string mediaUrl)
        {
            var didl =
                "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"><item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
                "<dc:title>PC Screen</dc:title><upnp:class>object.item.videoItem</upnp:class>" +
                $"<res protocolInfo=\"http-get:*:video/mp2t:{ContentFeatures}\">{SecurityElement.Escape(mediaUrl)}</res></item></DIDL-Lite>";
            await SoapAsync(controlUrl, "Stop", "", ignoreErrors: true).ConfigureAwait(false);
            await SoapAsync(controlUrl, "SetAVTransportURI",
                $"<CurrentURI>{SecurityElement.Escape(mediaUrl)}</CurrentURI><CurrentURIMetaData>{SecurityElement.Escape(didl)}</CurrentURIMetaData>").ConfigureAwait(false);
            // LG answers Play only after its player app has started, sometimes with an error even though playback works.
            _ = SoapAsync(controlUrl, "Play", "<Speed>1</Speed>", ignoreErrors: true);
        }

        public static Task StopAsync(string controlUrl) => SoapAsync(controlUrl, "Stop", "", ignoreErrors: true);

        static async Task SoapAsync(string controlUrl, string action, string args, bool ignoreErrors = false)
        {
            var body =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                $"<u:{action} xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID>{args}</u:{action}>" +
                "</s:Body></s:Envelope>";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(controlUrl);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=\"utf-8\"";
                req.Headers.Add("SOAPAction", $"\"urn:schemas-upnp-org:service:AVTransport:1#{action}\"");
                req.Timeout = 40000;
                var bytes = Encoding.UTF8.GetBytes(body);
                using (var s = await req.GetRequestStreamAsync().ConfigureAwait(false))
                    await s.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                using (await req.GetResponseAsync().ConfigureAwait(false)) { }
            }
            catch (Exception ex)
            {
                if (!ignoreErrors) throw;
                Log.Info($"DLNA {action}: {ex.Message}");
            }
        }
    }
}
