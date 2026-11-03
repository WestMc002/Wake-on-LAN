// Wake on LAN – Geräte und ihre Speicherung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace WakeOnLan
{
    /// <summary>Wie der Status eines Geräts geprüft wird.</summary>
    internal enum StatusMode { Ping, Port, PingPort, Ipmi, Off }

    /// <summary>Ein Gerät mit seinen Weck-Arten (Weckpaket, Anstoßen, IPMI).</summary>
    internal sealed class Device
    {
        public const string DefaultIpmiTool = @"C:\Program Files (x86)\Dell\SysMgt\bmc\ipmitool.exe";
        public const int DefaultPort = 445;
        public const int DefaultStatusPort = 3389;

        public string Name = "";
        /// <summary>Feste IP-Adresse oder Rechnername: für Status, Anstoßen und Herunterfahren.</summary>
        public string Host = "";
        /// <summary>IP-Adresse automatisch über die MAC-Adresse ermitteln (ARP), wenn Host leer ist.</summary>
        public bool AutoIp;
        /// <summary>Zuletzt über die MAC-Adresse gefundene IP-Adresse.</summary>
        public string LastIp = "";
        /// <summary>Zuletzt online gesehen (null = noch nie).</summary>
        public DateTime? LastOnline;
        /// <summary>Gruppe (leer = ohne Gruppe). Eine Gruppe lässt sich als Ganzes wecken.</summary>
        public string Group = "";
        /// <summary>Gehört zu "Alle wecken".</summary>
        public bool InAll = true;

        // Status
        public StatusMode Status = StatusMode.Ping;
        public int StatusPort = DefaultStatusPort;

        // Herunterfahren/Neustart über Windows (Fernzugriff auf den Rechner)
        public bool AllowShutdown = true;
        /// <summary>Benutzer für das Herunterfahren (z. B. Büro-PC\Admin); leer = angemeldeter Windows-Benutzer.</summary>
        public string ShutdownUser = "";
        /// <summary>Kennwort dazu, mit Windows-Datenschutz (DPAPI, aktueller Benutzer) verschlüsselt.</summary>
        public string ShutdownPasswordProtected = "";

        // Weckpaket (Magic Packet)
        public bool UseMagic;
        public List<string> Macs = new List<string>();
        /// <summary>Zusätzliche Zieladresse für das Weckpaket (z. B. Broadcast eines anderen Netzes), sonst leer.</summary>
        public string Broadcast = "";

        // Anstoßen: Verbindungsversuch auf einen Port, weckt Netzwerkkarten mit "Wake on Pattern Match"
        public bool UsePort;
        public int Port = DefaultPort;

        // IPMI (Server-Fernwartung, z. B. Dell iDRAC) über ipmitool
        public bool UseIpmi;
        public string IpmiHost = "";
        public string IpmiUser = "";
        /// <summary>Kennwort, mit Windows-Datenschutz (DPAPI, aktueller Benutzer) verschlüsselt.</summary>
        public string IpmiPasswordProtected = "";
        public string IpmiTool = DefaultIpmiTool;

        public Device Clone()
        {
            var d = (Device)MemberwiseClone();
            d.Macs = new List<string>(Macs);
            return d;
        }

        /// <summary>Adresse, die gerade gilt: feste Adresse, sonst die über die MAC gefundene.</summary>
        public string CurrentHost
        {
            get
            {
                string h = Host.Trim();
                if (h.Length > 0) return h;
                return AutoIp ? (LastIp ?? "").Trim() : "";
            }
        }

        /// <summary>Kann eine Adresse über die MAC-Adresse gesucht werden?</summary>
        public bool CanFindIp
        {
            get { return AutoIp && Host.Trim().Length == 0 && Macs.Count > 0; }
        }

        public bool HasStatusTarget
        {
            get
            {
                switch (Status)
                {
                    case StatusMode.Off: return false;
                    case StatusMode.Ipmi: return UseIpmi;
                    default: return CurrentHost.Length > 0 || CanFindIp;
                }
            }
        }

        public void SetIpmiPassword(string plain)
        {
            IpmiPasswordProtected = Protect(plain);
        }

        /// <summary>Entschlüsseltes Kennwort, null wenn keins gespeichert ist oder es nicht lesbar ist (anderer Benutzer/PC).</summary>
        public string GetIpmiPassword()
        {
            return Unprotect(IpmiPasswordProtected);
        }

        public void SetShutdownPassword(string plain)
        {
            ShutdownPasswordProtected = Protect(plain);
        }

        /// <summary>Entschlüsseltes Kennwort fürs Herunterfahren, null wenn keins gespeichert oder nicht lesbar.</summary>
        public string GetShutdownPassword()
        {
            return Unprotect(ShutdownPasswordProtected);
        }

        internal static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        internal static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return null;
            }
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Wake on LAN · IPMI");

        /// <summary>Gleiche Gruppe (Groß-/Kleinschreibung egal).</summary>
        public static bool SameGroup(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>MAC-Adresse vereinheitlichen (AA:BB:CC:DD:EE:FF); null wenn ungültig. Erlaubt ':', '-', '.', Leerzeichen oder nichts.</summary>
        public static string NormalizeMac(string mac)
        {
            if (mac == null) return null;
            var hex = new StringBuilder();
            foreach (char c in mac.Trim())
            {
                if (Uri.IsHexDigit(c)) hex.Append(char.ToUpperInvariant(c));
                else if (c != ':' && c != '-' && c != '.' && c != ' ') return null;
            }
            if (hex.Length != 12) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < 12; i += 2)
            {
                if (i > 0) sb.Append(':');
                sb.Append(hex[i]).Append(hex[i + 1]);
            }
            return sb.ToString();
        }

        /// <summary>Ports, die Geräte mit "Wake on Pattern Match" wecken würden – für den Status gesperrt.</summary>
        public static bool IsWakingPort(int port)
        {
            return port == 445 || port == 139 || port == 137;
        }

        public static string StatusModeText(StatusMode m)
        {
            switch (m)
            {
                case StatusMode.Port: return "TCP-Port";
                case StatusMode.PingPort: return "Ping, sonst TCP-Port";
                case StatusMode.Ipmi: return "IPMI (Einschaltzustand)";
                case StatusMode.Off: return "Aus";
                default: return "Ping";
            }
        }
    }

    /// <summary>
    /// Speichert die Geräte als XML unter %LOCALAPPDATA%\Wake on LAN\Geräte.xml
    /// (außerhalb des Programmordners, damit dort nur die Programmdateien liegen).
    /// </summary>
    internal sealed class DeviceStore
    {
        public readonly List<Device> Devices = new List<Device>();
        public int WindowWidth, WindowHeight;
        /// <summary>Stand des Fensteraufbaus, zu dem die gespeicherte Fenstergröße gehört.</summary>
        public int LayoutVersion;
        public const int CurrentLayout = 2;
        /// <summary>Windows-Benachrichtigungen (z. B. "Büro-PC ist online").</summary>
        public bool Notify = true;
        /// <summary>Zugeklappte Gruppen in der Liste ("" = Ohne Gruppe).</summary>
        public readonly HashSet<string> Collapsed = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        /// <summary>Eigene Reihenfolge der Gruppen (neue Gruppen kommen alphabetisch dahinter).</summary>
        public readonly List<string> GroupOrder = new List<string>();
        public readonly string FilePath;
        /// <summary>Nur lesen (Beispieldaten, Import): Save() schreibt nichts.</summary>
        public bool ReadOnly;
        /// <summary>Steuerung per Handy (wird nicht exportiert).</summary>
        public RemoteSettings Remote = new RemoteSettings();

        public DeviceStore()
        {
            FilePath = Path.Combine(DataFolder, "Geräte.xml");
        }

        /// <summary>Geräteliste aus einer anderen Datei (Beispieldaten, Import), nur lesen.</summary>
        public DeviceStore(string filePath)
        {
            FilePath = filePath;
            ReadOnly = true;
        }

        /// <summary>%LOCALAPPDATA%\Wake on LAN (Geräteliste, Verlauf, Fehler.log).</summary>
        public static string DataFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wake on LAN"); }
        }

        public string Folder { get { return Path.GetDirectoryName(FilePath); } }

        public bool Exists { get { return File.Exists(FilePath); } }

        public void Load()
        {
            Devices.Clear();
            Collapsed.Clear();
            GroupOrder.Clear();
            Remote = new RemoteSettings();
            if (!File.Exists(FilePath)) return;
            var doc = XDocument.Load(FilePath);
            var root = doc.Root;
            if (root == null) return;
            Notify = Bool(root, "Benachrichtigungen", true);
            var win = root.Element("Fenster");
            if (win != null)
            {
                WindowWidth = Int(win, "Breite", 0);
                WindowHeight = Int(win, "Hoehe", 0);
                LayoutVersion = Int(win, "Layout", 1);
            }
            var remote = root.Element("Fernsteuerung");
            if (remote != null)
            {
                Remote.Enabled = Bool(remote, "Aktiv", false);
                Remote.Port = Int(remote, "Port", RemoteSettings.DefaultPort);
                Remote.Address = Str(remote, "Adresse");
                Remote.Background = Bool(remote, "Hintergrund", true);
                Remote.PinHash = Str(remote, "Passwort"); // frühere PIN ("Pin") wird bewusst nicht übernommen
                Remote.SecretProtected = Str(remote, "Schluessel");
                Remote.Https = Bool(remote, "Https", false);
                Remote.Internet = Bool(remote, "Internet", false);
                Remote.CertFile = Str(remote, "Zertifikat");
                Remote.KeyFile = Str(remote, "PrivaterSchluessel");
                Remote.CertPasswordProtected = Str(remote, "ZertifikatKennwort");
                Remote.CertMode = Str(remote, "ZertifikatQuelle") == "Speicher" ? "Speicher" : "Datei";
                Remote.CertName = Str(remote, "ZertifikatDomain");
            }
            foreach (var z in root.Elements("Zugeklappt")) Collapsed.Add(Str(z, "Gruppe"));
            foreach (var o in root.Elements("Gruppe")) if (Str(o, "Name").Length > 0) GroupOrder.Add(Str(o, "Name"));
            foreach (var x in root.Elements("Geraet"))
            {
                var d = new Device
                {
                    Name = Str(x, "Name"),
                    Host = Str(x, "Adresse"),
                    Group = Str(x, "Gruppe"),
                    InAll = Bool(x, "Alle", true),
                    AutoIp = Bool(x, "IpAutomatisch", false),
                    LastIp = Str(x, "LetzteIp"),
                    AllowShutdown = Bool(x, "Herunterfahren", true)
                };
                DateTime seen;
                if (DateTime.TryParse(Str(x, "ZuletztOnline"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out seen)) d.LastOnline = seen;
                var m = x.Element("Weckpaket");
                if (m != null)
                {
                    d.UseMagic = Bool(m, "Aktiv", true);
                    d.Broadcast = Str(m, "Broadcast");
                    foreach (var mac in m.Elements("Mac"))
                    {
                        string n = Device.NormalizeMac(mac.Value);
                        if (n != null) d.Macs.Add(n);
                    }
                }
                var p = x.Element("Anstossen");
                if (p != null)
                {
                    d.UsePort = Bool(p, "Aktiv", true);
                    d.Port = Int(p, "Port", Device.DefaultPort);
                }
                var i = x.Element("Ipmi");
                if (i != null)
                {
                    d.UseIpmi = Bool(i, "Aktiv", true);
                    d.IpmiHost = Str(i, "Adresse");
                    d.IpmiUser = Str(i, "Benutzer");
                    d.IpmiPasswordProtected = Str(i, "Kennwort");
                    string tool = Str(i, "Programm");
                    d.IpmiTool = tool.Length > 0 ? tool : Device.DefaultIpmiTool;
                }
                var login = x.Element("Anmeldung");
                if (login != null)
                {
                    d.ShutdownUser = Str(login, "Benutzer");
                    d.ShutdownPasswordProtected = Str(login, "Kennwort");
                }
                var s = x.Element("Status");
                if (s != null)
                {
                    d.Status = ParseMode(Str(s, "Art"));
                    d.StatusPort = Int(s, "Port", Device.DefaultStatusPort);
                }
                else if (d.UseIpmi && d.Host.Length == 0)
                    d.Status = StatusMode.Ipmi; // bisheriges Verhalten: IPMI-Geräte ohne IP zeigen den Einschaltzustand
                if (d.Name.Length > 0) Devices.Add(d);
            }
        }

        public void Save()
        {
            if (ReadOnly) return;
            Write(FilePath, true);
        }

        /// <summary>Geräteliste in eine andere Datei schreiben (ohne IPMI-Kennwörter – die sind nur unter diesem Benutzer lesbar).</summary>
        public void Export(string path)
        {
            Write(path, false);
        }

        private void Write(string path, bool full)
        {
            var root = new XElement("WakeOnLan", new XAttribute("Version", 2));
            if (full)
            {
                root.Add(new XAttribute("Benachrichtigungen", Notify));
                if (WindowWidth > 0 && WindowHeight > 0)
                    root.Add(new XElement("Fenster", new XAttribute("Breite", WindowWidth), new XAttribute("Hoehe", WindowHeight), new XAttribute("Layout", LayoutVersion)));
                foreach (var g in Collapsed) root.Add(new XElement("Zugeklappt", new XAttribute("Gruppe", g)));
                if (Remote.Enabled || Remote.PinHash.Length > 0 || Remote.CertFile.Length > 0 || Remote.CertName.Length > 0)
                    root.Add(new XElement("Fernsteuerung",
                        new XAttribute("Aktiv", Remote.Enabled),
                        new XAttribute("Port", Remote.Port),
                        new XAttribute("Adresse", Remote.Address),
                        new XAttribute("Hintergrund", Remote.Background),
                        new XAttribute("Passwort", Remote.PinHash),
                        new XAttribute("Schluessel", Remote.SecretProtected),
                        new XAttribute("Https", Remote.Https),
                        new XAttribute("Internet", Remote.Internet),
                        new XAttribute("Zertifikat", Remote.CertFile),
                        new XAttribute("PrivaterSchluessel", Remote.KeyFile),
                        new XAttribute("ZertifikatKennwort", Remote.CertPasswordProtected),
                        new XAttribute("ZertifikatQuelle", Remote.CertMode),
                        new XAttribute("ZertifikatDomain", Remote.CertName)));
            }
            foreach (var g in Groups()) root.Add(new XElement("Gruppe", new XAttribute("Name", g))); // Reihenfolge
            foreach (var d in Devices)
            {
                var x = new XElement("Geraet", new XAttribute("Name", d.Name), new XAttribute("Alle", d.InAll));
                if (d.Host.Length > 0) x.Add(new XAttribute("Adresse", d.Host));
                if (d.Group.Trim().Length > 0) x.Add(new XAttribute("Gruppe", d.Group.Trim()));
                if (d.AutoIp) x.Add(new XAttribute("IpAutomatisch", true));
                if (full && d.LastIp.Length > 0) x.Add(new XAttribute("LetzteIp", d.LastIp));
                if (full && d.LastOnline.HasValue) x.Add(new XAttribute("ZuletztOnline", d.LastOnline.Value.ToString("o", CultureInfo.InvariantCulture)));
                if (!d.AllowShutdown) x.Add(new XAttribute("Herunterfahren", false));
                x.Add(new XElement("Status", new XAttribute("Art", ModeKey(d.Status)), new XAttribute("Port", d.StatusPort)));
                if (d.ShutdownUser.Length > 0)
                    x.Add(new XElement("Anmeldung", new XAttribute("Benutzer", d.ShutdownUser),
                        new XAttribute("Kennwort", full ? d.ShutdownPasswordProtected : "")));
                if (d.UseMagic || d.Macs.Count > 0)
                {
                    var m = new XElement("Weckpaket", new XAttribute("Aktiv", d.UseMagic));
                    if (d.Broadcast.Length > 0) m.Add(new XAttribute("Broadcast", d.Broadcast));
                    foreach (var mac in d.Macs) m.Add(new XElement("Mac", mac));
                    x.Add(m);
                }
                if (d.UsePort)
                    x.Add(new XElement("Anstossen", new XAttribute("Aktiv", true), new XAttribute("Port", d.Port)));
                if (d.UseIpmi || d.IpmiHost.Length > 0)
                    x.Add(new XElement("Ipmi",
                        new XAttribute("Aktiv", d.UseIpmi),
                        new XAttribute("Adresse", d.IpmiHost),
                        new XAttribute("Benutzer", d.IpmiUser),
                        new XAttribute("Kennwort", full ? d.IpmiPasswordProtected : ""),
                        new XAttribute("Programm", d.IpmiTool)));
                root.Add(x);
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // erst in eine neue Datei schreiben, dann austauschen: bei Fehlern bleibt die alte Datei erhalten
            string tmp = path + ".neu";
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(tmp);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static string ModeKey(StatusMode m)
        {
            switch (m)
            {
                case StatusMode.Port: return "Port";
                case StatusMode.PingPort: return "PingPort";
                case StatusMode.Ipmi: return "Ipmi";
                case StatusMode.Off: return "Aus";
                default: return "Ping";
            }
        }

        private static StatusMode ParseMode(string s)
        {
            switch (s)
            {
                case "Port": return StatusMode.Port;
                case "PingPort": return StatusMode.PingPort;
                case "Ipmi": return StatusMode.Ipmi;
                case "Aus": return StatusMode.Off;
                default: return StatusMode.Ping;
            }
        }

        /// <summary>
        /// Alle Gruppen (ohne leere) in der eigenen Reihenfolge (GroupOrder), noch nicht eingeordnete alphabetisch dahinter.
        /// Bei unterschiedlicher Schreibweise zählt die erste.
        /// </summary>
        public List<string> Groups()
        {
            var list = new List<string>();
            foreach (var d in Devices)
            {
                string g = d.Group.Trim();
                if (g.Length > 0 && !list.Any(x => Device.SameGroup(x, g))) list.Add(g);
            }
            // leere Gruppen (angelegt, aber noch ohne Geräte) aus der Reihenfolge
            foreach (string o in GroupOrder)
                if (o.Trim().Length > 0 && !list.Any(x => Device.SameGroup(x, o))) list.Add(o.Trim());
            return list
                .OrderBy(g => { int i = GroupOrder.FindIndex(o => Device.SameGroup(o, g)); return i < 0 ? int.MaxValue : i; })
                .ThenBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Gruppe entfernen (Reihenfolge und Zuklapp-Zustand); die Geräte behandelt der Aufrufer.</summary>
        public void RemoveGroup(string group)
        {
            GroupOrder.RemoveAll(o => Device.SameGroup(o, group));
            Collapsed.Remove(group);
        }

        /// <summary>Neue Reihenfolge der Gruppen festlegen.</summary>
        public void SetGroupOrder(IEnumerable<string> order)
        {
            var list = order.ToList();
            GroupOrder.Clear();
            GroupOrder.AddRange(list);
        }

        public Device Find(string name)
        {
            return Devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string Str(XElement x, string name)
        {
            var a = x.Attribute(name);
            return a == null ? "" : a.Value.Trim();
        }

        private static int Int(XElement x, string name, int def)
        {
            int v;
            return int.TryParse(Str(x, name), out v) ? v : def;
        }

        private static bool Bool(XElement x, string name, bool def)
        {
            bool v;
            return bool.TryParse(Str(x, name), out v) ? v : def;
        }
    }
}
