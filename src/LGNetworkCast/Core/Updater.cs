using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace LGNetworkCast.Core
{
    public class UpdateInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string DownloadUrl { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// In-place updates from GitHub Releases: finds a newer release with a "*Setup*.exe" asset, downloads it
    /// and runs it silently over the current install (same AppId, so settings and paired TVs are kept), then
    /// restarts the app.
    /// </summary>
    public static class Updater
    {
        /// <summary>GitHub "owner/repo" that publishes releases. Can be overridden in settings.json (UpdateRepo).</summary>
        public const string DefaultRepo = "HayTaub/LG-TV-Network-cast";

        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

        public static string Repo(Settings s) => string.IsNullOrWhiteSpace(s.UpdateRepo) ? DefaultRepo : s.UpdateRepo.Trim();

        public static bool IsConfigured(Settings s) => Repo(s).Contains("/");

        /// <summary>Returns the newer release, or null when up to date.</summary>
        public static async Task<UpdateInfo> CheckAsync(Settings s)
        {
            if (!IsConfigured(s)) return null;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "LGNetworkCast/" + CurrentVersion;
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                string body;
                try
                {
                    body = await wc.DownloadStringTaskAsync($"https://api.github.com/repos/{Repo(s)}/releases/latest").ConfigureAwait(false);
                }
                catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;   // repository not public yet, or no release published
                }
                var json = Json.Parse(body);
                var tag = json.Str("tag_name") ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
                var asset = json.List("assets").FirstOrDefault(a =>
                {
                    var name = a.Str("name") ?? "";
                    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0;
                });
                if (asset == null || Normalize(version) <= Normalize(CurrentVersion)) return null;
                return new UpdateInfo { Version = version, Tag = tag, DownloadUrl = asset.Str("browser_download_url"), Notes = json.Str("body") };
            }
        }

        /// <summary>Downloads the installer and starts a silent upgrade that relaunches the app. The caller must then exit.</summary>
        public static async Task DownloadAndLaunchAsync(UpdateInfo update, IProgress<int> progress = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"LGNetworkCast-Setup-{update.Version}.exe");
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "LGNetworkCast/" + CurrentVersion;
                if (progress != null) wc.DownloadProgressChanged += (s, e) => progress.Report(e.ProgressPercentage);
                await wc.DownloadFileTaskAsync(update.DownloadUrl, path).ConfigureAwait(false);
            }
            Log.Info($"Installing update {update.Version}");
            var exe = Assembly.GetExecutingAssembly().Location;
            // The installer closes this app (CloseApplications=force); afterwards we start the new version in the tray.
            var cmd = $"/c \"\"{path}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART & start \"\" \"{exe}\" --tray\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = false, CreateNoWindow = true });
        }

        static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }
}
