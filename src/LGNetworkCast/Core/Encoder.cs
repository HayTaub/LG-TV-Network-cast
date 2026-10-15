using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LGNetworkCast.Core
{
    /// <summary>Builds ffmpeg command lines for desktop capture + hardware H.264 encoding.</summary>
    public static class Encoder
    {
        public static string FfmpegPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

        // Preferred order: hardware encoders first, software fallback last.
        static readonly string[] Candidates = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
        static string _best;

        public static string Best => _best ?? "libx264";

        /// <summary>Finds the first H.264 encoder that actually works on this machine.</summary>
        public static async Task<string> DetectAsync()
        {
            if (_best != null) return _best;
            foreach (var enc in Candidates)
            {
                var ok = await Task.Run(() =>
                {
                    try
                    {
                        using (var p = Process.Start(new ProcessStartInfo(FfmpegPath,
                            $"-hide_banner -loglevel error -f lavfi -i color=size=1280x720:rate=30 -frames:v 3 -c:v {enc} -f null -")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
                        {
                            p.StandardError.ReadToEnd();
                            return p.WaitForExit(15000) && p.ExitCode == 0;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }).ConfigureAwait(false);
                if (ok)
                {
                    Log.Info($"Using encoder {enc}");
                    return _best = enc;
                }
            }
            return _best = "libx264";
        }

        /// <summary>H.264 level and the matching MSE codec string for the output size.</summary>
        public static (string level, string codec) LevelFor(int height) =>
            height > 1088 ? ("5.1", "avc1.640033") : ("4.1", "avc1.640029");

        public static int OutputHeight(Settings s, MonitorInfo m) => s.Height <= 0 || s.Height >= m.Height ? m.Height : s.Height;

        static string Capture(Settings s, MonitorInfo m, string extraFilter = "")
        {
            var h = OutputHeight(s, m);
            var scale = h == m.Height ? "scale=trunc(iw/2)*2:trunc(ih/2)*2" : $"scale=-2:{h}:flags=fast_bilinear";
            // Adapter 0 is ddagrab's default; an explicit device is only needed for monitors on another GPU.
            var device = m.Adapter > 0 ? $"-init_hw_device d3d11va=dx:{m.Adapter} -filter_hw_device dx " : "";
            return device +
                   $"-filter_complex \"ddagrab=output_idx={m.Output}:framerate={s.Fps}:draw_mouse=1,hwdownload,format=bgra,{scale},format=nv12[v]{extraFilter}\" -map [v]";
        }

        static string VideoCodec(Settings s, MonitorInfo m)
        {
            var b = s.BitrateMbps;
            var (level, _) = LevelFor(OutputHeight(s, m));
            var common = $"-profile:v high -level {level} -b:v {b}M -maxrate {b}M -bufsize {Math.Max(1, b / 4)}M -g {s.Fps * 2} -bf 0";
            switch (Best)
            {
                case "h264_nvenc": return $"-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -rc cbr {common}";
                case "h264_qsv": return $"-c:v h264_qsv -preset veryfast -async_depth 1 -look_ahead 0 {common}";
                case "h264_amf": return $"-c:v h264_amf -usage ultralowlatency -quality speed -rc cbr {common}";
                default: return $"-c:v libx264 -preset ultrafast -tune zerolatency {common}";
            }
        }

        /// <summary>Fragmented MP4 for the low-latency viewer (Media Source Extensions on the TV).</summary>
        public static string FastArgs(Settings s, MonitorInfo m) =>
            $"-hide_banner -loglevel error {Capture(s, m)} {VideoCodec(s, m)} " +
            "-f mp4 -movflags frag_every_frame+empty_moov+default_base_moof pipe:1";

        /// <summary>MPEG-TS with a silent audio track for the TV's DLNA player.</summary>
        public static string DlnaArgs(Settings s, MonitorInfo m) =>
            $"-hide_banner -loglevel error {Capture(s, m, ";anullsrc=r=48000:cl=stereo[a]")} -map [a] {VideoCodec(s, m)} " +
            "-c:a aac -b:a 64k -f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1 pipe:1";

        public static Process Start(string args)
        {
            Log.Info("ffmpeg " + args);
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(FfmpegPath, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("ffmpeg: " + e.Data); };
            p.Start();
            p.BeginErrorReadLine();
            return p;
        }
    }
}
