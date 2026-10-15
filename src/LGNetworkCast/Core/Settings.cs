using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace LGNetworkCast.Core
{
    public class TvDevice
    {
        public string Id { get; set; }          // UPnP UDN (stable per TV)
        public string Name { get; set; }        // user-editable display name
        public string Model { get; set; }
        public string Ip { get; set; }
        public List<string> Macs { get; set; } = new List<string>();
        public string ClientKey { get; set; }   // webOS pairing key
        public string DlnaDescriptionUrl { get; set; }
        public bool? WakeOnWifi { get; set; }   // last known "Turn on via Wi-Fi" setting on the TV

        public bool IsPaired => !string.IsNullOrEmpty(ClientKey);
        public override string ToString() => Name;
    }

    public enum StreamEngine { Fast, Dlna }

    public class HotkeySet
    {
        public string ToggleShare { get; set; } = "Ctrl+Alt+K";
        public string ToggleClock { get; set; } = "Ctrl+Alt+C";
        public string TvOn { get; set; } = "Ctrl+Alt+O";
        public string TvOff { get; set; } = "Ctrl+Alt+X";
        public string VolumeUp { get; set; } = "Ctrl+Alt+Up";
        public string VolumeDown { get; set; } = "Ctrl+Alt+Down";
        public string Mute { get; set; } = "Ctrl+Alt+M";
    }

    public class Settings
    {
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LGNetworkCast");
        static string FilePath => Path.Combine(Folder, "settings.json");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "LGNetworkCast";

        public List<TvDevice> Tvs { get; set; } = new List<TvDevice>();
        public string DefaultTvId { get; set; }
        public int MonitorIndex { get; set; }
        public StreamEngine Engine { get; set; } = StreamEngine.Fast;
        public int Height { get; set; } = 1080;          // 720, 1080 or 0 = native
        public int BitrateMbps { get; set; } = 10;
        public int Fps { get; set; } = 30;
        public int Port { get; set; } = 8770;
        public bool PowerOnWithShare { get; set; } = true;
        public bool PowerOffWithStop { get; set; }
        public HotkeySet Hotkeys { get; set; } = new HotkeySet();
        public bool AutoUpdate { get; set; } = true;
        public string UpdateRepo { get; set; }          // GitHub "owner/repo"; empty = built-in default
        public DateTime LastUpdateCheck { get; set; }

        public TvDevice DefaultTv => Tvs.FirstOrDefault(t => t.Id == DefaultTvId) ?? Tvs.FirstOrDefault();

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return Json.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to load settings", ex);
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(this));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save settings", ex);
            }
        }

        // Start-with-Windows lives in the registry, not in the JSON file, so it always reflects reality.
        public static bool StartWithWindows
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue(RunValue) != null;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(RunValue, $"\"{System.Reflection.Assembly.GetExecutingAssembly().Location}\" --tray");
                    else key.DeleteValue(RunValue, false);
                }
            }
        }
    }
}
