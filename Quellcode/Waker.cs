// Wake on LAN – Wecken (Weckpaket, Anstoßen, IPMI) und Status prüfen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WakeOnLan
{
    internal enum DeviceState { Unknown, Checking, Online, Offline, PowerOff, Error }

    internal sealed class WakeResult
    {
        public bool Ok = true;
        /// <summary>Über die MAC-Adresse gefundene IP (sonst null).</summary>
        public string FoundIp;
        public readonly List<string> Parts = new List<string>();

        public string Message { get { return string.Join(" · ", Parts); } }

        public void Fail(string text)
        {
            Ok = false;
            Parts.Add(text);
        }
    }

    internal sealed class StatusResult
    {
        public DeviceState State;
        public string Info = "";
        /// <summary>Über die MAC-Adresse gefundene IP (sonst null).</summary>
        public string FoundIp;
    }

    internal static class Waker
    {
        private const int MagicPort = 9;
        private const int PortTimeoutMs = 3000;
        private const int PingTimeoutMs = 1500;
        private const int IpmiTimeoutMs = 20000;

        // ================================================================== Wecken

        /// <summary>Weckt ein Gerät mit allen eingestellten Arten. Läuft im Hintergrund (kein UI-Zugriff).</summary>
        public static WakeResult Wake(Device d)
        {
            var r = new WakeResult();
            // Adresse über die MAC suchen (für Anstoßen); nur die ARP-Tabelle, ohne Netzsuche – das Weckpaket braucht keine IP
            if (d.UsePort && d.CanFindIp)
            {
                string ip = Arp.FindIp(d.Macs, false);
                if (ip != null) { d.LastIp = ip; r.FoundIp = ip; }
            }
            if (d.UseMagic) SendMagic(d, r);
            if (d.UsePort) Nudge(d, r);
            if (d.UseIpmi) IpmiPowerOn(d, r);
            if (!d.UseMagic && !d.UsePort && !d.UseIpmi) r.Fail("keine Weck-Art eingestellt");
            return r;
        }

        /// <summary>Weckpaket: 6 × FF, dann 16 × die MAC-Adresse, per UDP-Broadcast über jede aktive Netzwerkkarte.</summary>
        private static void SendMagic(Device d, WakeResult r)
        {
            if (d.Macs.Count == 0) { r.Fail("keine MAC-Adresse"); return; }
            var targets = Targets();
            int adapters = 0;
            foreach (string mac in d.Macs)
            {
                byte[] packet = MagicPacket(mac);
                if (packet == null) { r.Fail("MAC " + mac + " ungültig"); continue; }
                int sent = 0;
                foreach (var t in targets)
                {
                    try
                    {
                        using (var u = new UdpClient(new IPEndPoint(t.Local, 0)))
                        {
                            u.EnableBroadcast = true;
                            u.Send(packet, packet.Length, new IPEndPoint(t.Broadcast, MagicPort));
                            u.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, MagicPort));
                            sent++;
                        }
                    }
                    catch { }
                }
                if (targets.Count == 0)
                {
                    // keine Netzwerkkarte erkannt: Windows entscheidet über den Weg
                    try
                    {
                        using (var u = new UdpClient())
                        {
                            u.EnableBroadcast = true;
                            u.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, MagicPort));
                            sent++;
                        }
                    }
                    catch { }
                }
                // zusätzliche Zieladresse (z. B. Broadcast eines anderen Netzes)
                IPAddress extra;
                if (d.Broadcast.Length > 0 && IPAddress.TryParse(d.Broadcast, out extra))
                {
                    try
                    {
                        using (var u = new UdpClient())
                        {
                            u.EnableBroadcast = true;
                            u.Send(packet, packet.Length, new IPEndPoint(extra, MagicPort));
                            sent++;
                        }
                    }
                    catch { }
                }
                if (sent == 0) r.Fail("Weckpaket an " + mac + " konnte nicht gesendet werden");
                adapters = Math.Max(adapters, sent);
            }
            if (r.Ok)
            {
                string what = d.Macs.Count == 1 ? "Weckpaket gesendet" : d.Macs.Count + " Weckpakete gesendet";
                r.Parts.Add(what + " (" + (targets.Count == 1 ? "1 Netzwerkkarte" : Math.Max(1, targets.Count) + " Netzwerkkarten") + ")");
            }
        }

        internal static byte[] MagicPacket(string mac)
        {
            string n = Device.NormalizeMac(mac);
            if (n == null) return null;
            byte[] m = n.Split(':').Select(h => Convert.ToByte(h, 16)).ToArray();
            var p = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) p[i] = 0xFF;
            for (int i = 0; i < 16; i++) Buffer.BlockCopy(m, 0, p, 6 + i * 6, 6);
            return p;
        }

        private sealed class Target
        {
            public IPAddress Local;
            public IPAddress Broadcast;
        }

        /// <summary>Alle aktiven IPv4-Netzwerkkarten mit ihrer Broadcast-Adresse.</summary>
        private static List<Target> Targets()
        {
            var list = new List<Target>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        if (ip[0] == 169 && ip[1] == 254) continue; // keine Adresse vom DHCP (APIPA)
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        list.Add(new Target { Local = ua.Address, Broadcast = new IPAddress(b) });
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Anstoßen: Verbindungsversuch auf den Port (Standard 445). Weckt Netzwerkkarten, die auf Zugriffe reagieren.</summary>
        private static void Nudge(Device d, WakeResult r)
        {
            string host = d.CurrentHost;
            if (host.Length == 0) { r.Fail("Anstoßen: keine IP-Adresse" + (d.AutoIp ? " (über die MAC nicht gefunden)" : "")); return; }
            bool answered;
            try
            {
                answered = TryConnect(host, d.Port, PortTimeoutMs);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
                {
                    r.Fail("Anstoßen: " + host + " nicht gefunden");
                    return;
                }
                answered = false;
            }
            r.Parts.Add("Port " + d.Port + " angestoßen" + (answered ? " (antwortet)" : ""));
        }

        internal static bool TryConnect(string host, int port, int timeoutMs)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
                ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip == null) throw new SocketException((int)SocketError.HostNotFound);
            using (var c = new TcpClient(AddressFamily.InterNetwork))
            {
                var ar = c.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                try { c.EndConnect(ar); return true; }
                catch { return false; }
            }
        }

        /// <summary>IPMI: erst Status abfragen, bei "aus" einschalten.</summary>
        private static void IpmiPowerOn(Device d, WakeResult r)
        {
            string output;
            int code = RunIpmi(d, "chassis power status", out output);
            if (code == 0 && output.IndexOf("is on", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Parts.Add("IPMI: läuft bereits");
                return;
            }
            if (code < 0) { r.Fail("IPMI: " + output); return; }
            code = RunIpmi(d, "chassis power on", out output);
            if (code == 0) r.Parts.Add("IPMI: eingeschaltet");
            else r.Fail("IPMI: " + (output.Length > 0 ? output : "Einschalten fehlgeschlagen"));
        }

        /// <summary>
        /// Startet ipmitool. Das Kennwort wird über die Umgebungsvariable IPMI_PASSWORD übergeben (Schalter -E),
        /// nicht auf der Befehlszeile. Rückgabe: Exit-Code, -1 wenn ipmitool nicht startet.
        /// </summary>
        internal static int RunIpmi(Device d, string command, out string output)
        {
            output = "";
            if (!File.Exists(d.IpmiTool)) { output = "ipmitool nicht gefunden (" + d.IpmiTool + ")"; return -1; }
            if (d.IpmiHost.Length == 0) { output = "keine BMC-Adresse"; return -1; }
            string pw = d.GetIpmiPassword();
            if (pw == null) { output = "kein Kennwort gespeichert (Gerät bearbeiten)"; return -1; }
            var psi = new ProcessStartInfo(d.IpmiTool,
                "-I lanplus -H " + Quote(d.IpmiHost) + " -U " + Quote(d.IpmiUser) + " -E " + command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            };
            psi.EnvironmentVariables["IPMI_PASSWORD"] = pw;
            try
            {
                using (var p = Process.Start(psi))
                {
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(IpmiTimeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = "keine Antwort vom BMC " + d.IpmiHost;
                        return -1;
                    }
                    p.WaitForExit();
                    string o = outTask.Result.Trim();
                    string e2;
                    lock (err) e2 = err.ToString().Trim();
                    output = p.ExitCode == 0 ? o : FirstLine(e2.Length > 0 ? e2 : o);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        private static string Quote(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "") + "\"";
        }

        private static string FirstLine(string s)
        {
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        // ================================================================== Status

        /// <summary>
        /// Status je nach Einstellung: Ping, TCP-Port, Ping und dann Port, IPMI-Einschaltzustand oder aus.
        /// Kein Port, der Geräte mit "Wake on Pattern Match" wecken würde (445, 139, 137).
        /// </summary>
        public static StatusResult Check(Device d)
        {
            var r = new StatusResult();
            if (d.Status == StatusMode.Off) { r.State = DeviceState.Unknown; return r; }
            if (d.Status == StatusMode.Ipmi)
            {
                if (!d.UseIpmi) { r.State = DeviceState.Unknown; return r; }
                IpmiState(d, r, false);
                return r;
            }

            string host = d.CurrentHost;
            if (d.CanFindIp)
            {
                // aktuelle IP über die MAC-Adresse (ARP-Tabelle, bei Bedarf kurze Suche im eigenen Netz)
                string ip = Arp.FindIp(d.Macs, true);
                if (ip != null) { host = ip; r.FoundIp = ip; }
            }
            if (host.Length == 0)
            {
                r.State = DeviceState.Offline;
                r.Info = d.AutoIp ? "IP nicht gefunden" : "";
                return r;
            }

            bool usePing = d.Status == StatusMode.Ping || d.Status == StatusMode.PingPort;
            bool usePort = d.Status == StatusMode.Port || d.Status == StatusMode.PingPort;
            if (usePing)
            {
                try
                {
                    using (var ping = new Ping())
                    {
                        var reply = ping.Send(host, PingTimeoutMs);
                        if (reply != null && reply.Status == IPStatus.Success)
                        {
                            r.State = DeviceState.Online;
                            r.Info = reply.RoundtripTime + " ms";
                            return r;
                        }
                    }
                }
                catch (PingException ex)
                {
                    var se = ex.InnerException as SocketException;
                    if (se != null && (se.SocketErrorCode == SocketError.HostNotFound || se.SocketErrorCode == SocketError.NoData))
                    {
                        r.State = DeviceState.Error;
                        r.Info = "Name nicht gefunden";
                        return r;
                    }
                }
                catch { }
            }
            if (usePort)
            {
                if (Device.IsWakingPort(d.StatusPort))
                {
                    r.State = DeviceState.Error;
                    r.Info = "Port " + d.StatusPort + " würde das Gerät wecken";
                    return r;
                }
                try
                {
                    if (TryConnect(host, d.StatusPort, PingTimeoutMs))
                    {
                        r.State = DeviceState.Online;
                        r.Info = "Port " + d.StatusPort;
                        return r;
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
                    {
                        r.State = DeviceState.Error;
                        r.Info = "Name nicht gefunden";
                        return r;
                    }
                }
                catch { }
            }
            // keine Antwort: bei IPMI-Geräten zusätzlich den Einschaltzustand zeigen
            if (d.UseIpmi) { IpmiState(d, r, true); return r; }
            r.State = DeviceState.Offline;
            return r;
        }

        /// <summary>Einschaltzustand über IPMI. noAnswer = Ping/Port hat nicht geantwortet.</summary>
        private static void IpmiState(Device d, StatusResult r, bool noAnswer)
        {
            string output;
            int code = RunIpmi(d, "chassis power status", out output);
            if (code == 0 && output.IndexOf("is on", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // eingeschaltet, aber (noch) keine Antwort: Gerät startet gerade
                r.State = noAnswer ? DeviceState.Offline : DeviceState.Online;
                r.Info = noAnswer ? "eingeschaltet, keine Antwort" : "eingeschaltet";
            }
            else if (code == 0 && output.IndexOf("is off", StringComparison.OrdinalIgnoreCase) >= 0)
                r.State = DeviceState.PowerOff;
            else
            {
                r.State = DeviceState.Error;
                r.Info = output;
            }
        }

        // ================================================================== Herunterfahren

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class NetResource
        {
            public int Scope;
            public int Type;
            public int DisplayType;
            public int Usage;
            public string LocalName;
            public string RemoteName;
            public string Comment;
            public string Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(NetResource resource, string password, string user, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool InitiateSystemShutdownEx(string machine, string message, int timeout, bool forceAppsClosed, bool reboot, uint reason);

        /// <summary>
        /// Windows-PC herunterfahren bzw. neu starten (wie shutdown /m \\Rechner). Mit hinterlegtem Benutzer meldet sich
        /// das Programm vorher an \\Rechner\IPC$ an und trennt die Verbindung danach wieder.
        /// Der Benutzer braucht auf dem Ziel Administratorrechte.
        /// </summary>
        public static WakeResult ShutdownWindows(Device d, bool restart)
        {
            var r = new WakeResult();
            string host = d.CurrentHost;
            if (d.CanFindIp)
            {
                string ip = Arp.FindIp(d.Macs, false);
                if (ip != null) { host = ip; r.FoundIp = ip; }
            }
            if (host.Length == 0) { r.Fail("keine IP-Adresse"); return r; }
            string machine = "\\\\" + host;
            string ipc = machine + "\\IPC$";
            bool connected = false;
            try
            {
                if (d.ShutdownUser.Length > 0)
                {
                    string pw = d.GetShutdownPassword();
                    if (pw == null) { r.Fail("kein Kennwort gespeichert (Gerät bearbeiten)"); return r; }
                    var res = new NetResource { Type = 0 /* RESOURCETYPE_ANY */, RemoteName = ipc };
                    int code = WNetAddConnection2(res, pw, d.ShutdownUser, 0);
                    if (code == 1219) // ERROR_SESSION_CREDENTIAL_CONFLICT: schon mit anderen Daten verbunden
                    {
                        WNetCancelConnection2(ipc, 0, true);
                        code = WNetAddConnection2(res, pw, d.ShutdownUser, 0);
                    }
                    if (code != 0 && code != 85) // 85 = schon verbunden
                    {
                        r.Fail("Anmeldung als " + d.ShutdownUser + " fehlgeschlagen: " + new System.ComponentModel.Win32Exception(code).Message);
                        return r;
                    }
                    connected = code == 0;
                }
                // Grund: geplant, Sonstiges (SHTDN_REASON_FLAG_PLANNED | MAJOR_OTHER | MINOR_OTHER)
                if (InitiateSystemShutdownEx(machine, "Wake on LAN", 0, false, restart, 0x80000000))
                    r.Parts.Add(restart ? "wird neu gestartet" : "wird heruntergefahren");
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    string text = new System.ComponentModel.Win32Exception(err).Message;
                    if (err == 5) text += d.ShutdownUser.Length > 0 ? " (Benutzer braucht Administratorrechte auf dem Ziel)" : " (Benutzer und Kennwort unter „Gerät bearbeiten“ hinterlegen)";
                    r.Fail(text);
                }
            }
            catch (Exception ex)
            {
                r.Fail(ex.Message);
            }
            finally
            {
                if (connected) { try { WNetCancelConnection2(ipc, 0, true); } catch { } }
            }
            return r;
        }

        /// <summary>IPMI: sanft herunterfahren (ACPI, wie kurzer Druck auf den Einschaltknopf).</summary>
        public static WakeResult ShutdownIpmi(Device d)
        {
            var r = new WakeResult();
            string output;
            int code = RunIpmi(d, "chassis power soft", out output);
            if (code == 0) r.Parts.Add("IPMI: wird heruntergefahren");
            else r.Fail("IPMI: " + (output.Length > 0 ? output : "Herunterfahren fehlgeschlagen"));
            return r;
        }
    }

    /// <summary>
    /// IP-Adresse zu einer MAC-Adresse: zuerst die ARP-Tabelle von Windows, bei Bedarf eine kurze ARP-Suche im
    /// eigenen Netz (höchstens alle 2 Minuten). ARP weckt keine Geräte; viele Netzwerkkarten antworten auch im Standby.
    /// Funktioniert nur im eigenen Netz (nicht über Router oder VPN).
    /// </summary>
    internal static class Arp
    {
        [DllImport("iphlpapi.dll")]
        private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

        [DllImport("iphlpapi.dll")]
        private static extern int SendARP(int destIp, int srcIp, byte[] mac, ref int macLength);

        private static readonly object Sync = new object();
        private static DateTime _lastSweep = DateTime.MinValue;
        private const int SweepMinutes = 2;

        public static string FindIp(IEnumerable<string> macs, bool allowSweep)
        {
            var wanted = macs.Select(Device.NormalizeMac).Where(m => m != null).ToList();
            if (wanted.Count == 0) return null;
            string ip = Lookup(wanted);
            if (ip != null || !allowSweep) return ip;
            lock (Sync)
            {
                if ((DateTime.Now - _lastSweep).TotalMinutes < SweepMinutes) return Lookup(wanted);
                _lastSweep = DateTime.Now;
                Sweep();
            }
            return Lookup(wanted);
        }

        private static string Lookup(List<string> wanted)
        {
            var table = Table();
            foreach (string mac in wanted)
            {
                string ip;
                if (table.TryGetValue(mac, out ip)) return ip;
            }
            return null;
        }

        /// <summary>ARP-Tabelle: MAC (AA:BB:…) → IP.</summary>
        public static Dictionary<string, string> Table()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int size = 0;
            GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return result;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buffer, ref size, false) != 0) return result;
                int count = Marshal.ReadInt32(buffer);
                const int rowSize = 24; // dwIndex, dwPhysAddrLen, bPhysAddr[8], dwAddr, dwType
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + i * rowSize);
                    int macLen = Marshal.ReadInt32(row, 4);
                    int type = Marshal.ReadInt32(row, 20);
                    if (macLen != 6 || type == 2) continue; // 2 = ungültig
                    var sb = new StringBuilder();
                    for (int b = 0; b < 6; b++)
                    {
                        if (b > 0) sb.Append(':');
                        sb.Append(Marshal.ReadByte(row, 8 + b).ToString("X2"));
                    }
                    var ipBytes = new byte[4];
                    for (int b = 0; b < 4; b++) ipBytes[b] = Marshal.ReadByte(row, 16 + b);
                    string mac = sb.ToString();
                    if (mac == "00:00:00:00:00:00" || mac == "FF:FF:FF:FF:FF:FF") continue;
                    result[mac] = new IPAddress(ipBytes).ToString();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        /// <summary>Alle Adressen der eigenen Netze (bis /22) per ARP anfragen; füllt die ARP-Tabelle von Windows.</summary>
        private static void Sweep()
        {
            var targets = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        uint ip = ToUInt(ua.Address), mask = ToUInt(ua.IPv4Mask);
                        if ((ip >> 16) == ((169u << 8) | 254u)) continue; // APIPA
                        uint hosts = ~mask;
                        if (hosts < 2 || hosts > 1024) continue; // nur kleine Netze (bis /22)
                        uint net = ip & mask;
                        for (uint h = 1; h < hosts; h++)
                        {
                            uint a = net | h;
                            if (a != ip) targets.Add(FromUInt(a));
                        }
                    }
                }
            }
            catch { }
            if (targets.Count == 0) return;
            Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 64 }, t =>
            {
                try
                {
                    var mac = new byte[6];
                    int len = mac.Length;
                    SendARP(BitConverter.ToInt32(t.GetAddressBytes(), 0), 0, mac, ref len);
                }
                catch { }
            });
        }

        private static uint ToUInt(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint v)
        {
            return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        }
    }
}
