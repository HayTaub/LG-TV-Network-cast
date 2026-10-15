using System;
using System.Diagnostics;
using System.Reflection;

namespace LGNetworkCast.Core
{
    /// <summary>
    /// Detects whether Windows Firewall blocks incoming connections to this app (the TV must reach our
    /// web server), and offers a one-click fix that runs elevated after the user approves the UAC prompt.
    /// </summary>
    public static class Firewall
    {
        static string ExePath => Assembly.GetExecutingAssembly().Location;
        static DateTime _checkedAt;
        static bool _blocked;

        /// <summary>True when an enabled inbound rule blocks this exe (cached for a few seconds).</summary>
        public static bool IsBlocked
        {
            get
            {
                if ((DateTime.UtcNow - _checkedAt).TotalSeconds < 5) return _blocked;
                _checkedAt = DateTime.UtcNow;
                _blocked = Check();
                return _blocked;
            }
        }

        static bool Check()
        {
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null) return false;
                dynamic policy = Activator.CreateInstance(type);
                if (!(bool)policy.FirewallEnabled[(int)policy.CurrentProfileTypes]) return false;
                // A block rule wins over allow rules, so any enabled inbound block rule for this exe means trouble.
                foreach (dynamic rule in policy.Rules)
                {
                    string app = rule.ApplicationName;
                    if (app == null || !string.Equals(app, ExePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(bool)rule.Enabled || (int)rule.Direction != 1) continue;   // 1 = inbound
                    if (((int)rule.Profiles & (int)policy.CurrentProfileTypes) == 0) continue;
                    if ((int)rule.Action == 0) return true;                          // 0 = block
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Firewall check failed", ex);
                return false;
            }
        }

        /// <summary>Replaces this app's firewall rules with an allow rule. Shows a UAC prompt.</summary>
        public static bool Fix()
        {
            var exe = ExePath;
            var cmd = $"/c netsh advfirewall firewall delete rule name=all program=\"{exe}\" & " +
                      $"netsh advfirewall firewall add rule name=\"LG Network Cast\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    p?.WaitForExit(20000);
                _checkedAt = DateTime.MinValue;
                return !IsBlocked;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;   // the user declined the UAC prompt
            }
        }
    }
}
