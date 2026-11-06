using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BorgionSystemInfo
{
    public sealed class InfoItem
    {
        public InfoItem(string label, string value) { Label = label; Value = value; }
        public string Label { get; }
        public string Value { get; }
    }

    public sealed class InfoSection
    {
        public InfoSection(string title) { Title = title; }
        public string Title { get; }
        public List<InfoItem> Items { get; } = new List<InfoItem>();

        /// <summary>Voegt een regel toe. Fouten of lege waarden worden "Onbekend".</summary>
        public void Add(string label, Func<string> get) => AddCore(label, get, optional: false);

        /// <summary>Voegt een regel alleen toe als er een waarde is.</summary>
        public void AddOptional(string label, Func<string> get) => AddCore(label, get, optional: true);

        void AddCore(string label, Func<string> get, bool optional)
        {
            string v;
            try { v = get()?.Trim(); } catch { v = null; }
            if (string.IsNullOrEmpty(v))
            {
                if (optional) return;
                v = "Onbekend";
            }
            Items.Add(new InfoItem(label, v));
        }
    }

    /// <summary>
    /// Haalt algemene Windows-systeeminformatie op via WMI, het register en .NET.
    /// Alles werkt zonder administratorrechten. Moet op een STA-thread draaien (Shell-COM voor BitLocker).
    /// </summary>
    public static class SystemInfoCollector
    {
        static readonly CultureInfo NL = CultureInfo.GetCultureInfo("nl-NL");

        public static List<InfoSection> Collect()
        {
            return new List<InfoSection>
            {
                Computer(),
                OperatingSystem(),
                Hardware(),
                Network(),
                User(),
                Region(),
                Security(),
            };
        }

        // ------------------------------------------------------------------ Secties

        static InfoSection Computer()
        {
            var s = new InfoSection("Computer");
            var cs = First("SELECT * FROM Win32_ComputerSystem");
            var bios = First("SELECT * FROM Win32_BIOS");

            s.Add("Computernaam", () => Environment.MachineName);
            s.Add("Volledige naam (FQDN)", () =>
            {
                var p = IPGlobalProperties.GetIPGlobalProperties();
                return string.IsNullOrEmpty(p.DomainName) ? p.HostName : p.HostName + "." + p.DomainName;
            });
            s.Add("Domein / werkgroep", () =>
                (bool)cs["PartOfDomain"] ? "Domein: " + cs["Domain"] : "Werkgroep: " + cs["Workgroup"]);
            s.Add("Fabrikant", () => Str(cs, "Manufacturer"));
            s.Add("Model", () => Str(cs, "Model"));
            s.Add("Serienummer", () => Str(bios, "SerialNumber"));
            s.Add("BIOS", () =>
            {
                var date = bios["ReleaseDate"] is string d ? " (" + Date(ManagementDateTimeConverter.ToDateTime(d)) + ")" : "";
                return Str(bios, "Manufacturer") + " " + Str(bios, "SMBIOSBIOSVersion") + date;
            });
            s.Add("Firmware", () =>
            {
                if (!GetFirmwareType(out var t)) return null;
                return t == 2 ? "UEFI" : t == 1 ? "BIOS (Legacy)" : null;
            });
            return s;
        }

        static InfoSection OperatingSystem()
        {
            var s = new InfoSection("Besturingssysteem");
            var os = First("SELECT * FROM Win32_OperatingSystem");
            const string cv = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

            s.Add("Windows", () => Str(os, "Caption").Replace("Microsoft ", ""));
            s.Add("Versie", () => (Registry.GetValue(cv, "DisplayVersion", null) ?? Registry.GetValue(cv, "ReleaseId", null))?.ToString());
            s.Add("Build", () =>
            {
                var build = Registry.GetValue(cv, "CurrentBuild", null)?.ToString() ?? Str(os, "BuildNumber");
                return Registry.GetValue(cv, "UBR", null) is int ubr ? build + "." + ubr : build;
            });
            s.Add("Architectuur", () => Str(os, "OSArchitecture"));
            s.Add("Geïnstalleerd op", () => DateTimeText(ManagementDateTimeConverter.ToDateTime(Str(os, "InstallDate"))));
            s.Add("Laatst opgestart", () =>
            {
                var boot = ManagementDateTimeConverter.ToDateTime(Str(os, "LastBootUpTime"));
                return DateTimeText(boot) + "  (" + Duration(DateTime.Now - boot) + " geleden)";
            });
            s.Add("Laatste update", LastHotfix);
            s.Add(".NET Framework", () =>
            {
                var r = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null);
                if (!(r is int rel)) return null;
                return rel >= 533320 ? "4.8.1" : rel >= 528040 ? "4.8" : rel >= 461808 ? "4.7.2" : "4.x (" + rel + ")";
            });
            s.Add("Windows-map", () => Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return s;
        }

        static InfoSection Hardware()
        {
            var s = new InfoSection("Hardware");
            var cs = First("SELECT * FROM Win32_ComputerSystem");
            var os = First("SELECT * FROM Win32_OperatingSystem");

            s.Add("Processor", () =>
            {
                var cpus = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").ToList();
                var name = Collapse(Str(cpus[0], "Name"));
                int cores = cpus.Sum(c => Convert.ToInt32(c["NumberOfCores"]));
                int threads = cpus.Sum(c => Convert.ToInt32(c["NumberOfLogicalProcessors"]));
                var count = cpus.Count > 1 ? cpus.Count + "× " : "";
                return $"{count}{name}\n{cores} cores, {threads} threads";
            });
            s.Add("Geheugen (RAM)", () =>
            {
                double total = Convert.ToDouble(cs["TotalPhysicalMemory"]);
                double free = Convert.ToDouble(os["FreePhysicalMemory"]) * 1024;
                return $"{Size(total)}  ({Size(free)} vrij)";
            });
            s.Add("Videokaart", () => string.Join("\n",
                Query("SELECT Name FROM Win32_VideoController").Select(v => Str(v, "Name")).Where(n => n != "").Distinct()));

            int n = 0;
            foreach (var scr in Forms.Screen.AllScreens.OrderByDescending(x => x.Primary))
            {
                n++;
                var screen = scr;
                s.Add(Forms.Screen.AllScreens.Length > 1 ? "Beeldscherm " + n : "Beeldscherm",
                    () => $"{screen.Bounds.Width} × {screen.Bounds.Height}" + (screen.Primary ? "  (primair)" : ""));
            }

            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var drive = d;
                s.Add("Schijf " + drive.Name.TrimEnd('\\'), () =>
                {
                    var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "" : "  · " + drive.VolumeLabel;
                    return $"{Size(drive.TotalFreeSpace)} vrij van {Size(drive.TotalSize)}  ({drive.DriveFormat}){label}";
                });
            }

            s.AddOptional("Batterij", () =>
            {
                var b = Query("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery").FirstOrDefault();
                if (b == null) return null;
                var status = Convert.ToInt32(b["BatteryStatus"]);
                return b["EstimatedChargeRemaining"] + "%" + (status == 2 ? "  (netstroom)" : status == 1 ? "  (op batterij)" : "");
            });
            return s;
        }

        static InfoSection Network()
        {
            var s = new InfoSection("Netwerk");
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .ToList();

            if (nics.Count == 0) s.Add("Netwerk", () => "Geen actieve netwerkverbinding");

            foreach (var nic in nics)
            {
                var ni = nic;
                s.Add(ni.Name, () =>
                {
                    var p = ni.GetIPProperties();
                    var sb = new StringBuilder();
                    var v4 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address + "/" + a.PrefixLength).ToList();
                    sb.AppendLine("IPv4: " + (v4.Count > 0 ? string.Join(", ", v4) : "-"));
                    var gw = p.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                    if (gw.Count > 0) sb.AppendLine("Gateway: " + string.Join(", ", gw));
                    var dns = p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                    if (dns.Count > 0) sb.AppendLine("DNS: " + string.Join(", ", dns));
                    if (!string.IsNullOrEmpty(p.DnsSuffix)) sb.AppendLine("DNS-achtervoegsel: " + p.DnsSuffix);
                    try { sb.AppendLine("DHCP: " + (p.GetIPv4Properties()?.IsDhcpEnabled == true ? "ja" : "nee")); } catch { }
                    var mac = ni.GetPhysicalAddress().GetAddressBytes();
                    if (mac.Length > 0) sb.AppendLine("MAC: " + string.Join("-", mac.Select(b => b.ToString("X2"))));
                    if (ni.Speed > 0) sb.AppendLine("Snelheid: " + Speed(ni.Speed));
                    sb.Append(ni.Description);
                    return sb.ToString();
                });
            }
            return s;
        }

        static InfoSection User()
        {
            var s = new InfoSection("Gebruiker");
            var id = WindowsIdentity.GetCurrent();

            s.Add("Aangemeld als", () => id.Name);
            s.Add("Lokale beheerder", () =>
            {
                const string admins = "S-1-5-32-544";
                if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return "Ja (met verhoogde rechten)";
                bool denyOnly = id.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == admins);
                return denyOnly ? "Ja (zonder verhoogde rechten)" : "Nee";
            });
            s.Add("Aanmeldserver", () => Environment.GetEnvironmentVariable("LOGONSERVER")?.TrimStart('\\'));
            s.Add("Profielmap", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return s;
        }

        static InfoSection Region()
        {
            var s = new InfoSection("Regio en taal");
            s.Add("Weergavetaal", () => Culture(CultureInfo.CurrentUICulture));
            s.Add("Landinstellingen", () => Culture(CultureInfo.CurrentCulture));
            s.Add("Datum- en tijdnotatie", () =>
            {
                var c = CultureInfo.CurrentCulture;
                return DateTime.Now.ToString(c.DateTimeFormat.ShortDatePattern + " " + c.DateTimeFormat.ShortTimePattern, c)
                       + "  (" + c.DateTimeFormat.ShortDatePattern + ")";
            });
            s.Add("Tijdzone", () => TimeZoneInfo.Local.DisplayName);
            s.Add("Toetsenbord", () => string.Join("\n",
                Forms.InputLanguage.InstalledInputLanguages.Cast<Forms.InputLanguage>()
                    .Select(l => l.LayoutName + "  (" + l.Culture.Name + ")" + (l.Equals(Forms.InputLanguage.DefaultInputLanguage) ? "  · standaard" : ""))));
            return s;
        }

        static InfoSection Security()
        {
            var s = new InfoSection("Beveiliging");
            s.Add("Antivirus", Antivirus);
            s.Add("Windows Firewall", () =>
            {
                const string root = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
                const string pol = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\WindowsFirewall\";
                string P(string key, string name)
                {
                    var v = Registry.GetValue(pol + key, "EnableFirewall", null) ?? Registry.GetValue(root + key, "EnableFirewall", null);
                    return name + ": " + (v is int i ? (i == 1 ? "aan" : "uit") : "?");
                }
                return P("DomainProfile", "Domein") + " · " + P("StandardProfile", "Privé") + " · " + P("PublicProfile", "Openbaar");
            });
            s.Add("BitLocker (" + SystemDrive() + ")", BitLocker);
            s.Add("Secure Boot", () =>
            {
                var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
                return v is int i ? (i == 1 ? "Aan" : "Uit") : "Niet ondersteund";
            });
            s.Add("Gebruikersaccountbeheer (UAC)", () =>
            {
                var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", null);
                return v is int i && i == 0 ? "Uit" : "Aan";
            });
            return s;
        }

        // ------------------------------------------------------------------ Losse onderdelen

        static string LastHotfix()
        {
            var formats = new[] { "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "d-M-yyyy", "dd-MM-yyyy" };
            var latest = Query("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering")
                .Select(h =>
                {
                    var raw = Str(h, "InstalledOn");
                    DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d);
                    return new { Id = Str(h, "HotFixID"), Date = d };
                })
                .Where(h => h.Date > DateTime.MinValue)
                .OrderByDescending(h => h.Date)
                .FirstOrDefault();
            return latest == null ? null : $"{Date(latest.Date)}  ({latest.Id})";
        }

        static string Antivirus()
        {
            // Werkstations: Beveiligingscentrum
            try
            {
                var products = Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2")
                    .Select(p =>
                    {
                        int state = Convert.ToInt32(p["productState"]);
                        bool on = ((state >> 12) & 0xF) == 1;
                        bool upToDate = ((state >> 4) & 0xF) == 0;
                        return Str(p, "displayName") + "  (" + (on ? "actief" : "uit") + (on ? upToDate ? ", up-to-date" : ", verouderd" : "") + ")";
                    }).ToList();
                if (products.Count > 0) return string.Join("\n", products);
            }
            catch { }

            // Servers: Microsoft Defender rechtstreeks
            var mp = Query("SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus",
                @"root\Microsoft\Windows\Defender").FirstOrDefault();
            if (mp == null) return null;
            bool av = mp["AntivirusEnabled"] is bool a && a;
            bool rt = mp["RealTimeProtectionEnabled"] is bool r && r;
            var sig = mp["AntivirusSignatureLastUpdated"] is string sd ? ", definities " + Date(ManagementDateTimeConverter.ToDateTime(sd)) : "";
            return "Microsoft Defender  (" + (av ? "actief" : "uit") + (rt ? ", realtime aan" : ", realtime uit") + sig + ")";
        }

        static string BitLocker()
        {
            // Via de Shell-eigenschap: werkt zonder administratorrechten.
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                var item = shell.NameSpace(17).ParseName(SystemDrive()); // 17 = Deze pc
                object v = item?.ExtendedProperty("System.Volume.BitLockerProtection");
                if (v == null) return "Niet beschikbaar";
                switch (Convert.ToInt32(v))
                {
                    case 1: return "Aan";
                    case 2: return "Uit";
                    case 3: return "Bezig met versleutelen";
                    case 4: return "Bezig met ontsleutelen";
                    case 5: return "Onderbroken";
                    case 6: return "Aan (vergrendeld)";
                    case 8: return "Wacht op activering";
                    default: return "Niet beschikbaar";
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        // ------------------------------------------------------------------ Helpers

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool GetFirmwareType(out int firmwareType); // 1 = BIOS, 2 = UEFI

        static IEnumerable<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
        {
            using (var searcher = new ManagementObjectSearcher(scope, wql))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(10);
                return searcher.Get().Cast<ManagementBaseObject>().ToList();
            }
        }

        static ManagementBaseObject First(string wql)
        {
            try { return Query(wql).FirstOrDefault(); } catch { return null; }
        }

        static string Str(ManagementBaseObject o, string prop) => o?[prop]?.ToString()?.Trim() ?? "";

        static string Collapse(string s) => string.Join(" ", s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        static string SystemDrive() => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

        static string Culture(CultureInfo c) => c.NativeName + "  (" + c.Name + ")";

        static string Date(DateTime d) => d.ToString("d MMMM yyyy", NL);

        static string DateTimeText(DateTime d) => d.ToString("d MMMM yyyy, HH:mm", NL);

        static string Duration(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} u";
            if (t.TotalHours >= 1) return $"{t.Hours} u {t.Minutes} min";
            return $"{Math.Max(1, t.Minutes)} min";
        }

        static string Size(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
            return bytes.ToString(i >= 3 ? "0.0" : "0", NL) + " " + units[i];
        }

        static string Speed(long bps)
        {
            if (bps >= 1_000_000_000) return (bps / 1e9).ToString("0.#", NL) + " Gbit/s";
            return (bps / 1e6).ToString("0", NL) + " Mbit/s";
        }

        // ------------------------------------------------------------------ Tekst voor klembord

        public static string ToText(IEnumerable<InfoSection> sections, bool header = true)
        {
            var sb = new StringBuilder();
            var list = sections.ToList();
            if (header)
            {
                sb.AppendLine("Borgion System Information - " + Environment.MachineName);
                sb.AppendLine("Opgehaald: " + DateTimeText(DateTime.Now));
                sb.AppendLine();
            }
            int width = Math.Max(22, list.SelectMany(x => x.Items).Select(i => i.Label.Length).DefaultIfEmpty(0).Max()) + 1;
            foreach (var sec in list)
            {
                sb.AppendLine("== " + sec.Title + " ==");
                foreach (var item in sec.Items)
                    sb.AppendLine(ItemText(item, width));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        static string ItemText(InfoItem item, int width)
        {
            var lines = item.Value.Replace("\r", "").Split('\n');
            var indent = new string(' ', width + 2);
            var sb = new StringBuilder(item.Label.PadRight(width) + ": " + lines[0]);
            foreach (var l in lines.Skip(1)) sb.Append(Environment.NewLine + indent + l);
            return sb.ToString();
        }
    }
}
