// Wake on LAN – Steuerung per Handy: eingebaute Webseite (HTTP/HTTPS) mit Passwort-Anmeldung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace WakeOnLan
{
    /// <summary>Einstellungen der Handy-Steuerung (in Geräte.xml, Element "Fernsteuerung"; nicht im Export).</summary>
    internal sealed class RemoteSettings
    {
        public const int DefaultPort = 8770;

        public bool Enabled;
        public int Port = DefaultPort;
        /// <summary>Adresse, unter der das Handy den PC erreicht (Rechnername, Domain aus dem Zertifikat oder IP).</summary>
        public string Address = "";
        /// <summary>Beim Schließen des Fensters im Infobereich weiterlaufen.</summary>
        public bool Background = true;
        public bool Https;
        /// <summary>
        /// Auch aus dem Internet erreichbar (Portfreigabe im Router, z. B. mit DynDNS-Namen). Nur zusammen mit HTTPS wirksam;
        /// sonst nimmt die Webseite nur Anfragen aus dem Heimnetz bzw. per VPN an.
        /// </summary>
        public bool Internet;
        public string CertFile = "", KeyFile = "";
        /// <summary>Herkunft des Zertifikats: "Datei" (Standard) oder "Speicher" (Windows-Zertifikatspeicher, nach Domain).</summary>
        public string CertMode = "Datei";
        /// <summary>Domain des Zertifikats im Windows-Zertifikatspeicher (z. B. von win-acme abgelegt).</summary>
        public string CertName = "";

        public bool CertFromStore { get { return CertMode == "Speicher"; } }

        /// <summary>Zertifikat laden – aus Datei bzw. dem Windows-Zertifikatspeicher.</summary>
        public X509Certificate2 LoadCertificate(string keyName = null)
        {
            if (CertFromStore) return CertLoader.FromStore(CertName);
            return keyName == null ? CertLoader.Load(CertFile, KeyFile, CertPassword) : CertLoader.Load(CertFile, KeyFile, CertPassword, keyName);
        }
        /// <summary>Kennwort der .pfx/.p12-Datei, mit DPAPI verschlüsselt.</summary>
        public string CertPasswordProtected = "";
        /// <summary>Passwort als PBKDF2-Prüfwert "Salz:Hash" (Base64), nie im Klartext.</summary>
        public string PinHash = "";
        /// <summary>Anmeldeschlüssel für die Handys (Cookie), mit DPAPI verschlüsselt. Neu = alle Handys abgemeldet.</summary>
        public string SecretProtected = "";

        public RemoteSettings Clone()
        {
            return (RemoteSettings)MemberwiseClone();
        }

        public bool HasPin { get { return PinHash.Length > 0; } }

        /// <summary>Mindestlänge des Passworts: 8 im Heimnetz, 12 bei Zugriff aus dem Internet.</summary>
        public int MinPinLength { get { return Internet ? 12 : 8; } }

        /// <summary>Zugriff aus dem Internet ist wirklich offen (nur mit HTTPS).</summary>
        public bool InternetActive { get { return Internet && Https; } }

        public void SetPin(string pin)
        {
            var salt = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(salt);
            PinHash = Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Derive(pin, salt));
            NewSecret();
        }

        public bool CheckPin(string pin)
        {
            try
            {
                int i = PinHash.IndexOf(':');
                if (i <= 0 || pin == null) return false;
                byte[] salt = Convert.FromBase64String(PinHash.Substring(0, i));
                byte[] hash = Convert.FromBase64String(PinHash.Substring(i + 1));
                return Same(hash, Derive(pin, salt));
            }
            catch { return false; }
        }

        private static byte[] Derive(string pin, byte[] salt)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(pin), salt, 20000)) return kdf.GetBytes(32);
        }

        /// <summary>Neuer Anmeldeschlüssel: alle angemeldeten Handys müssen das Passwort neu eingeben.</summary>
        public void NewSecret()
        {
            var s = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(s);
            SecretProtected = Device.Protect(Convert.ToBase64String(s));
        }

        /// <summary>Anmeldeschlüssel als Cookie-Wert (null = nicht lesbar).</summary>
        public string Token()
        {
            string s = Device.Unprotect(SecretProtected);
            if (string.IsNullOrEmpty(s)) return null;
            return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public string CertPassword
        {
            get { return Device.Unprotect(CertPasswordProtected) ?? ""; }
            set { CertPasswordProtected = string.IsNullOrEmpty(value) ? "" : Device.Protect(value); }
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Adresse fürs Handy, z. B. https://wol.example.de:8770/ (Standard-Ports ohne Angabe).</summary>
        public string Url(string address)
        {
            string host = string.IsNullOrEmpty(address) ? "localhost" : address.Trim();
            if (host.Contains(":") && !host.StartsWith("[")) host = "[" + host + "]"; // IPv6
            bool std = (Https && Port == 443) || (!Https && Port == 80);
            return (Https ? "https://" : "http://") + host + (std ? "" : ":" + Port) + "/";
        }

        /// <summary>IPv4-Adressen dieses PCs im Heimnetz (für den Vorschlag der Adresse).</summary>
        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue; // virtuelle Adapter ohne Gateway auslassen
                    foreach (var a in props.UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && WebServer.IsLocalNetwork(a.Address))
                            list.Add(a.Address.ToString());
                }
            }
            catch { }
            return list;
        }
    }

    /// <summary>
    /// Anmeldung per QR-Code (wie in Sync Pilot): Einmal-Code im QR-Code, 10 Minuten und nur einmal gültig.
    /// Das Handy ist danach angemeldet, ohne das Passwort einzugeben.
    /// </summary>
    internal static class Pairing
    {
        private static readonly object Lock = new object();
        private static byte[] _hash;
        private static DateTime _until;

        private static byte[] Hash(string code)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes((code ?? "").Trim().ToUpperInvariant()));
        }

        /// <summary>Neuer Code (ersetzt einen älteren).</summary>
        public static string NewCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var raw = new byte[20];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(raw);
            var sb = new StringBuilder();
            foreach (byte b in raw) sb.Append(chars[b % chars.Length]);
            string code = sb.ToString();
            lock (Lock)
            {
                _hash = Hash(code);
                _until = DateTime.UtcNow.AddMinutes(10);
            }
            return code;
        }

        /// <summary>Code ungültig machen (Dialog geschlossen – der QR-Code ist nicht mehr zu sehen).</summary>
        public static void Clear()
        {
            lock (Lock) _hash = null;
        }

        /// <summary>Code einlösen: true, wenn er stimmt und noch gilt (danach ungültig).</summary>
        public static bool Use(string code)
        {
            lock (Lock)
            {
                bool ok = _hash != null && DateTime.UtcNow < _until && RemoteSettings.Same(_hash, Hash(code));
                if (ok) _hash = null;
                return ok;
            }
        }
    }

    /// <summary>Verbindung zwischen Webseite und Hauptfenster (Aufrufe kommen aus Hintergrund-Threads).</summary>
    internal interface IRemoteHost
    {
        /// <summary>Geräte, Gruppen und Status als JSON.</summary>
        string Snapshot();
        /// <summary>Aktion ausführen (wake, group, all, shutdown) – Antwort als JSON {"ok":…,"text":…}.</summary>
        string Act(string action, string name, string mode);
        /// <summary>Akzentfarben für die Webseite als CSS.</summary>
        string ThemeCss();
        void Log(string text);
    }

    /// <summary>
    /// Kleiner Webserver für die Handy-Steuerung: liefert die Webseite (in die EXE eingebettet) und eine JSON-Schnittstelle.
    /// Nur aus dem Heimnetz (private Adressen, VPN) erreichbar, Anmeldung per Passwort (Cookie), wahlweise HTTPS mit eigenem Zertifikat.
    /// Ein erneuertes Zertifikat (z. B. Let's Encrypt) wird beim nächsten Verbindungsaufbau automatisch übernommen.
    /// </summary>
    internal sealed class WebServer : IDisposable
    {
        private const int MaxConnections = 32;
        private const int MaxFails = 5;
        private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(10);
        /// <summary>Aus dem Internet: höchstens so viele falsche Passwörter je Stunde (alle Adressen zusammen), sonst Anmeldung von außen 1 Stunde gesperrt.</summary>
        private const int MaxInternetFails = 15;

        private readonly RemoteSettings _settings;
        private readonly IRemoteHost _host;
        private readonly string _token;
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _stop;
        private int _connections;
        private long _lastRequest;

        private readonly object _certLock = new object();
        private X509Certificate2 _cert;
        private DateTime _certTime, _keyTime, _lastCertCheck;

        private readonly object _failLock = new object();
        private readonly Dictionary<string, int> _fails = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _locked = new Dictionary<string, DateTime>();
        private readonly List<DateTime> _internetFails = new List<DateTime>();
        private DateTime _internetLockedUntil = DateTime.MinValue;

        private static readonly object IconLock = new object();
        private static readonly Dictionary<int, byte[]> Icons = new Dictionary<int, byte[]>();

        public WebServer(RemoteSettings settings, IRemoteHost host)
        {
            _settings = settings.Clone();
            _host = host;
            _token = _settings.Token();
        }

        public int Port { get { return _settings.Port; } }

        /// <summary>Gerade ein Handy verbunden (letzte Anfrage vor weniger als 2 Minuten) – dann laufen die Statusprüfungen auch im Hintergrund.</summary>
        public bool RecentlyUsed
        {
            get { return DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastRequest) < TimeSpan.FromMinutes(2).Ticks; }
        }

        /// <summary>Server starten. Fehler (Port belegt, Zertifikat) werden als Ausnahme mit verständlichem Text gemeldet.</summary>
        public void Start()
        {
            if (_token == null || !_settings.HasPin) throw new InvalidOperationException("Bitte zuerst ein Passwort festlegen.");
            if (_settings.Https)
            {
                _cert = _settings.LoadCertificate();
                _certTime = Stamp(_settings.CertFile);
                _keyTime = Stamp(_settings.KeyFile);
                _lastCertCheck = DateTime.UtcNow;
            }
            try
            {
                // IPv4 und IPv6 zugleich, sonst nur IPv4
                try
                {
                    _listener = new TcpListener(IPAddress.IPv6Any, _settings.Port);
                    _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27 /* IPV6_V6ONLY */, 0);
                    _listener.Start(50);
                }
                catch (SocketException)
                {
                    if (_listener != null) { try { _listener.Stop(); } catch { } }
                    _listener = new TcpListener(IPAddress.Any, _settings.Port);
                    _listener.Start(50);
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied)
                    throw new InvalidOperationException("Port " + _settings.Port + " ist schon belegt (anderes Programm oder Dienst). Bitte einen anderen Port wählen.");
                throw new InvalidOperationException("Port " + _settings.Port + " kann nicht geöffnet werden: " + ex.Message);
            }
            if (_stop)
            {
                // inzwischen neu gestartet bzw. ausgeschaltet: Port sofort wieder freigeben
                _listener.Stop();
                throw new InvalidOperationException("abgebrochen");
            }
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Wake on LAN Webserver" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        private static DateTime Stamp(string file)
        {
            try { return string.IsNullOrEmpty(file) ? DateTime.MinValue : File.GetLastWriteTimeUtc(file); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Aktuelles Zertifikat; wurde die Datei erneuert, wird sie neu geladen (höchstens alle 30 Sekunden geprüft).
        /// Aus dem Windows-Zertifikatspeicher alle 30 Minuten neu (Verlängerungen, z. B. durch win-acme).
        /// </summary>
        private X509Certificate2 Certificate()
        {
            lock (_certLock)
            {
                if (_settings.CertFromStore)
                {
                    if ((DateTime.UtcNow - _lastCertCheck).TotalMinutes >= 30)
                    {
                        _lastCertCheck = DateTime.UtcNow;
                        try
                        {
                            var fresh = _settings.LoadCertificate();
                            if (fresh.Thumbprint != _cert.Thumbprint) _host.Log("Zertifikat neu geladen: " + CertLoader.Describe(fresh));
                            _cert = fresh;
                        }
                        catch (Exception ex)
                        {
                            _host.Log("Zertifikat aus dem Zertifikatspeicher konnte nicht neu geladen werden: " + ex.Message);
                        }
                    }
                    return _cert;
                }
                if ((DateTime.UtcNow - _lastCertCheck).TotalSeconds >= 30)
                {
                    _lastCertCheck = DateTime.UtcNow;
                    DateTime c = Stamp(_settings.CertFile), k = Stamp(_settings.KeyFile);
                    if (c != _certTime || k != _keyTime)
                    {
                        try
                        {
                            var fresh = CertLoader.Load(_settings.CertFile, _settings.KeyFile, _settings.CertPassword);
                            _cert = fresh;
                            _host.Log("Zertifikat neu geladen: " + CertLoader.Describe(fresh));
                        }
                        catch (Exception ex)
                        {
                            _host.Log("Erneuertes Zertifikat konnte nicht geladen werden: " + ex.Message);
                        }
                        _certTime = c;
                        _keyTime = k;
                    }
                }
                return _cert;
            }
        }

        // ================================================================== Verbindungen

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch
                {
                    if (_stop) return;
                    Thread.Sleep(200);
                    continue;
                }
                if (Interlocked.Increment(ref _connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref _connections);
                    try { client.Close(); } catch { }
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(client); }
                    catch { }
                    finally
                    {
                        Interlocked.Decrement(ref _connections);
                        try { client.Close(); } catch { }
                    }
                });
            }
        }

        // ---------- letzter Zugriff von einem anderen Gerät (seit Programmstart) ----------
        private static readonly object ClientLock = new object();
        private static string _lastClient;
        private static DateTime _lastClientTime, _ownRead;
        private static HashSet<string> _own;

        /// <summary>
        /// Adresse und Zeit des letzten Zugriffs von einem anderen Gerät, null wenn es noch keinen gab.
        /// Das ist der Beweis, dass die Firewalls dieses PCs Verbindungen durchlassen – ein Test vom PC selbst zeigt das nicht.
        /// </summary>
        public static string LastClient(out DateTime time)
        {
            lock (ClientLock) { time = _lastClientTime; return _lastClient; }
        }

        /// <summary>Gehört die Adresse zu diesem PC (Loopback oder eine seiner Netzwerkkarten)?</summary>
        public static bool IsOwnAddress(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            lock (ClientLock)
            {
                if (_own == null || (DateTime.UtcNow - _ownRead).TotalSeconds > 60)
                {
                    var set = new HashSet<string>();
                    try
                    {
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                                set.Add(new IPAddress(a.Address.GetAddressBytes()).ToString()); // ohne Zonen-Kennung
                    }
                    catch { }
                    _own = set;
                    _ownRead = DateTime.UtcNow;
                }
                return _own.Contains(new IPAddress(ip.GetAddressBytes()).ToString());
            }
        }

        private static void NoteClient(IPAddress ip)
        {
            if (IsOwnAddress(ip)) return;
            lock (ClientLock) { _lastClient = ip.ToString(); _lastClientTime = DateTime.Now; }
        }

        /// <summary>Nur Heimnetz: private IPv4-Bereiche, CGNAT/VPN (100.64/10), Link-Local, Loopback; IPv6 ULA/Link-Local.</summary>
        public static bool IsLocalNetwork(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                return ip.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
            }
            return false;
        }

        private void Handle(TcpClient client)
        {
            var remote = (IPEndPoint)client.Client.RemoteEndPoint;
            var ip = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            bool local = IsLocalNetwork(ip);
            if (!local && !_settings.InternetActive) return; // aus dem Internet (ohne Freigabe): Verbindung sofort schließen
            client.NoDelay = true;
            client.ReceiveTimeout = 20000;
            client.SendTimeout = 20000;
            Stream stream = client.GetStream();
            bool https = _settings.Https;
            if (https)
            {
                // Erstes Byte: 0x16 = TLS-Handshake, sonst normale HTTP-Anfrage (wird auf https umgeleitet)
                int first = stream.ReadByte();
                if (first < 0) return;
                var prefixed = new PrefixStream(stream, (byte)first);
                if (first == 0x16)
                {
                    var ssl = new SslStream(prefixed, false);
                    ssl.AuthenticateAsServer(Certificate(), false, SslProtocols.Tls12, false);
                    stream = ssl;
                }
                else
                {
                    var reader = new HttpReader(prefixed);
                    var req = reader.ReadRequest();
                    if (req == null) return;
                    string host = SafeHost(req.Header("Host"));
                    var w = new Response(prefixed, false);
                    w.Redirect("https://" + (host.Length > 0 ? host : "localhost:" + _settings.Port) + "/");
                    return;
                }
            }
            using (stream)
            {
                var r = new HttpReader(stream);
                for (int n = 0; n < 200 && !_stop; n++)
                {
                    var req = r.ReadRequest();
                    if (req == null) return;
                    Interlocked.Exchange(ref _lastRequest, DateTime.UtcNow.Ticks);
                    if (n == 0) NoteClient(ip);
                    bool keep = !string.Equals(req.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                    var res = new Response(stream, keep) { Secure = https, Head = req.Method == "HEAD" };
                    Route(req, res, ip.ToString(), https, local);
                    if (!keep) return;
                }
            }
        }

        private static string SafeHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            foreach (char c in host)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == ':' || c == '[' || c == ']')) return "";
            return host;
        }

        // ================================================================== Seiten und Schnittstelle

        private void Route(HttpRequest req, Response res, string ip, bool https, bool local)
        {
            string path = req.Path;
            bool get = req.Method == "GET" || req.Method == "HEAD";
            if (get)
            {
                switch (path)
                {
                    case "/":
                    case "/index.html": res.Send(200, "text/html; charset=utf-8", Resource("Web.index.html"), false); return;
                    case "/app.js": res.Send(200, "text/javascript; charset=utf-8", Resource("Web.app.js"), false); return;
                    case "/app.css": res.Send(200, "text/css; charset=utf-8", Resource("Web.app.css"), false); return;
                    case "/theme.css": res.Send(200, "text/css; charset=utf-8", Encoding.UTF8.GetBytes(_host.ThemeCss()), false); return;
                    case "/sw.js": res.Send(200, "text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(ServiceWorker), false); return;
                    case "/manifest.webmanifest": res.Send(200, "application/manifest+json; charset=utf-8", Encoding.UTF8.GetBytes(Manifest()), false); return;
                    case "/logo.png": res.Send(200, "image/png", Resource("GS-Logo.png"), true); return;
                    case "/icon-192.png": res.Send(200, "image/png", Icon(192), true); return;
                    case "/icon-512.png": res.Send(200, "image/png", Icon(512), true); return;
                    case "/apple-touch-icon.png": res.Send(200, "image/png", Icon(180), true); return;
                    case "/favicon.ico":
                    case "/favicon.png": res.Send(200, "image/png", Icon(32), true); return;
                    case "/api/status":
                        if (!Authorized(req)) { res.Json(401, "{\"error\":\"login\"}"); return; }
                        res.Json(200, _host.Snapshot());
                        return;
                }
                res.Send(404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Nicht gefunden"), false);
                return;
            }
            if (req.Method != "POST") { res.Send(405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Nicht erlaubt"), false); return; }

            // Schutz gegen Anfragen fremder Webseiten: eigener Header (erzwingt CORS-Vorabprüfung) und passender Origin
            if (req.Header("X-WOL") != "1" || !SameOrigin(req, https)) { res.Json(403, "{\"error\":\"Nicht erlaubt\"}"); return; }
            var form = req.Form();
            switch (path)
            {
                case "/api/login": Login(req, res, ip, local, form); return;
                case "/api/pair": Pair(res, ip, local, form); return;
                case "/api/logout":
                    res.Cookie = CookieValue("", 0);
                    res.Json(200, "{\"ok\":true}");
                    return;
            }
            if (!Authorized(req)) { res.Json(401, "{\"error\":\"login\"}"); return; }
            string name = Get(form, "name"), mode = Get(form, "mode");
            switch (path)
            {
                case "/api/wake": res.Json(200, _host.Act("wake", name, mode)); return;
                case "/api/group": res.Json(200, _host.Act("group", name, mode)); return;
                case "/api/all": res.Json(200, _host.Act("all", name, mode)); return;
                case "/api/shutdown": res.Json(200, _host.Act("shutdown", name, mode)); return;
            }
            res.Json(404, "{\"error\":\"Unbekannte Aktion\"}");
        }

        private static string Get(Dictionary<string, string> form, string key)
        {
            string v;
            return form.TryGetValue(key, out v) ? v : "";
        }

        private bool SameOrigin(HttpRequest req, bool https)
        {
            string origin = req.Header("Origin");
            if (string.IsNullOrEmpty(origin)) return true; // ältere Browser schicken bei gleicher Seite keinen Origin
            string host = req.Header("Host");
            return string.Equals(origin, (https ? "https://" : "http://") + host, StringComparison.OrdinalIgnoreCase);
        }

        private bool Authorized(HttpRequest req)
        {
            string cookie = req.Cookie("wol");
            return _token != null && cookie != null && RemoteSettings.Same(Encoding.ASCII.GetBytes(cookie), Encoding.ASCII.GetBytes(_token));
        }

        private string CookieValue(string value, int maxAge)
        {
            return "wol=" + value + "; Path=/; Max-Age=" + maxAge + "; HttpOnly; SameSite=Strict" + (_settings.Https ? "; Secure" : "");
        }

        private void Login(HttpRequest req, Response res, string ip, bool local, Dictionary<string, string> form)
        {
            if (Blocked(res, ip, local)) return;
            if (_settings.CheckPin(Get(form, "passwort")))
            {
                LoggedIn(res, ip, local, "Handy angemeldet");
                return;
            }
            Thread.Sleep(1000); // Raten bremsen
            Failed(res, ip, local, "Das Passwort stimmt nicht.", "falsches Passwort");
        }

        /// <summary>Anmeldung per QR-Code (Einmal-Code aus dem Dialog "Steuerung per Handy").</summary>
        private void Pair(Response res, string ip, bool local, Dictionary<string, string> form)
        {
            if (Blocked(res, ip, local)) return;
            if (Pairing.Use(Get(form, "code")))
            {
                LoggedIn(res, ip, local, "Handy per QR-Code angemeldet");
                return;
            }
            Thread.Sleep(1000);
            Failed(res, ip, local, "Der QR-Code ist abgelaufen oder wurde schon benutzt. Bitte den QR-Code in Wake on LAN neu scannen oder das Passwort eingeben.",
                "ungültiger QR-Code");
        }

        private void LoggedIn(Response res, string ip, bool local, string what)
        {
            lock (_failLock) _fails.Remove(ip);
            res.Cookie = CookieValue(_token, 400 * 24 * 3600);
            _host.Log(what + " (" + ip + (local ? "" : ", aus dem Internet") + ")");
            res.Json(200, "{\"ok\":true}");
        }

        /// <summary>Gesperrt? Dann Antwort 429 senden und true liefern.</summary>
        private bool Blocked(Response res, string ip, bool local)
        {
            lock (_failLock)
            {
                if (!local && DateTime.UtcNow < _internetLockedUntil)
                {
                    res.Json(429, "{\"error\":" + Json.Str("Anmeldung aus dem Internet ist wegen vieler falscher Versuche vorübergehend gesperrt.") + "}");
                    return true;
                }
                DateTime until;
                if (_locked.TryGetValue(ip, out until))
                {
                    if (DateTime.UtcNow < until)
                    {
                        res.Json(429, "{\"error\":" + Json.Str("Zu viele falsche Versuche. Bitte in " + Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes)) + " Minuten erneut versuchen.") + "}");
                        return true;
                    }
                    _locked.Remove(ip);
                    _fails.Remove(ip);
                }
            }
            return false;
        }

        /// <summary>Fehlversuch zählen (je Adresse und – von außen – alle zusammen) und passend antworten.</summary>
        private void Failed(Response res, string ip, bool local, string message, string logText)
        {
            int fails;
            lock (_failLock)
            {
                _fails.TryGetValue(ip, out fails);
                fails++;
                _fails[ip] = fails;
                if (fails >= MaxFails) _locked[ip] = DateTime.UtcNow + LockTime;
                if (!local)
                {
                    // Angreifer wechseln die Adresse: alle Fehlversuche von außen zusammen begrenzen
                    _internetFails.Add(DateTime.UtcNow);
                    _internetFails.RemoveAll(t => (DateTime.UtcNow - t).TotalHours >= 1);
                    if (_internetFails.Count >= MaxInternetFails)
                    {
                        _internetLockedUntil = DateTime.UtcNow.AddHours(1);
                        _internetFails.Clear();
                        _host.Log("Anmeldung aus dem Internet für 1 Stunde gesperrt (" + MaxInternetFails + " falsche Passwörter in einer Stunde). Im Heimnetz geht es weiter.");
                    }
                }
            }
            if (fails >= MaxFails)
            {
                _host.Log("Anmeldung gesperrt: " + fails + "× " + logText + " von " + ip + " (10 Minuten)");
                res.Json(429, "{\"error\":" + Json.Str("Zu viele falsche Versuche. Bitte in 10 Minuten erneut versuchen.") + "}");
            }
            else res.Json(401, "{\"error\":" + Json.Str(message) + "}");
        }

        private static byte[] Resource(string name)
        {
            using (var rs = typeof(WebServer).Assembly.GetManifestResourceStream(name))
            {
                if (rs == null) return Encoding.UTF8.GetBytes("Fehlt: " + name);
                using (var ms = new MemoryStream()) { rs.CopyTo(ms); return ms.ToArray(); }
            }
        }

        /// <summary>Programm-Icon als PNG für das Handy (Startbildschirm), aus dem eingebetteten Icon.</summary>
        private static byte[] Icon(int size)
        {
            lock (IconLock)
            {
                byte[] png;
                if (Icons.TryGetValue(size, out png)) return png;
                using (var src = Ui.AppIcon(256))
                using (var bmp = new System.Drawing.Bitmap(size, size))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.Clear(System.Drawing.Color.Transparent);
                        if (src != null) g.DrawImage(src, 0, 0, size, size);
                    }
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        png = ms.ToArray();
                    }
                }
                Icons[size] = png;
                return png;
            }
        }

        private static string Manifest()
        {
            return "{\"name\":\"Wake on LAN\",\"short_name\":\"Wake on LAN\",\"lang\":\"de\",\"start_url\":\"/\",\"scope\":\"/\",\"display\":\"standalone\"," +
                "\"background_color\":\"#202020\",\"theme_color\":\"#202020\"," +
                "\"icons\":[{\"src\":\"/icon-192.png\",\"sizes\":\"192x192\",\"type\":\"image/png\"},{\"src\":\"/icon-512.png\",\"sizes\":\"512x512\",\"type\":\"image/png\"}]}";
        }

        // Nur damit sich die Seite als App installieren lässt – alles kommt immer frisch vom PC
        private const string ServiceWorker =
            "self.addEventListener('install', e => self.skipWaiting());\n" +
            "self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));\n" +
            "self.addEventListener('fetch', e => e.respondWith(fetch(e.request)));\n";
    }

    // ====================================================================== HTTP

    /// <summary>
    /// "Verbindung testen": ruft die laufende Handy-Seite über ihre eingetragene Adresse auf (Name auflösen, verbinden,
    /// bei HTTPS das Zertifikat prüfen, Antwort lesen). Läuft auf dem PC selbst – zeigt eine Adresse auf diesen PC,
    /// geht der Test nicht durch dessen Firewall (Own = true).
    /// </summary>
    internal static class ConnectionTest
    {
        internal sealed class Result
        {
            public bool Ok, Own;
            /// <summary>Fehlertext, wenn Ok = false.</summary>
            public string Error;
            /// <summary>Bei HTTPS: was am Zertifikat nicht stimmt (null = in Ordnung).</summary>
            public string CertProblem;
        }

        public static Result Run(string host, int port, bool https)
        {
            var r = new Result();
            IPAddress[] ips;
            try
            {
                IPAddress one;
                ips = IPAddress.TryParse(host, out one) ? new[] { one } : Dns.GetHostAddresses(host);
            }
            catch { ips = new IPAddress[0]; }
            ips = ips.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
            if (ips.Length == 0) { r.Error = "Der Name „" + host + "“ lässt sich nicht auflösen."; return r; }
            r.Own = ips.Any(WebServer.IsOwnAddress);
            var ip = ips[0];
            try
            {
                using (var client = new TcpClient(ip.AddressFamily))
                {
                    var ar = client.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) { r.Error = "Keine Antwort von " + ip + " auf Port " + port + " (Zeitüberschreitung)."; return r; }
                    client.EndConnect(ar);
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    Stream stream = client.GetStream();
                    if (https)
                    {
                        var policy = SslPolicyErrors.None;
                        var ssl = new SslStream(stream, false, (sender, cert, chain, errors) => { policy = errors; return true; });
                        ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                        stream = ssl;
                        if ((policy & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) r.CertProblem = "das Zertifikat gilt nicht für „" + host + "“";
                        else if (policy != SslPolicyErrors.None) r.CertProblem = "das Zertifikat ist nicht vertrauenswürdig (abgelaufen oder Zwischenzertifikat fehlt)";
                    }
                    byte[] request = Encoding.ASCII.GetBytes("GET /manifest.webmanifest HTTP/1.1\r\nHost: " + host + ":" + port + "\r\nConnection: close\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    stream.Flush();
                    var buffer = new byte[4096];
                    int total = 0, read;
                    while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
                    string answer = Encoding.UTF8.GetString(buffer, 0, total);
                    if (!answer.StartsWith("HTTP/1.1 200") || answer.IndexOf("Wake on LAN", StringComparison.Ordinal) < 0)
                    {
                        r.Error = "Unter " + host + ":" + port + " antwortet etwas anderes als Wake on LAN.";
                        return r;
                    }
                    r.Ok = true;
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                r.Error = "Keine Verbindung zu " + ip + " auf Port " + port + ": " + inner.Message.TrimEnd('.') + ".";
            }
            return r;
        }
    }

    internal sealed class HttpRequest
    {
        public string Method = "", Path = "", Query = "", Body = "";
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public string Cookie(string name)
        {
            string all = Header("Cookie");
            if (all == null) return null;
            foreach (string part in all.Split(';'))
            {
                int i = part.IndexOf('=');
                if (i > 0 && part.Substring(0, i).Trim() == name) return part.Substring(i + 1).Trim();
            }
            return null;
        }

        /// <summary>Formularfelder (application/x-www-form-urlencoded) aus dem Body.</summary>
        public Dictionary<string, string> Form()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in Body.Split('&'))
            {
                if (pair.Length == 0) continue;
                int i = pair.IndexOf('=');
                string k = Uri.UnescapeDataString((i < 0 ? pair : pair.Substring(0, i)).Replace('+', ' '));
                string v = i < 0 ? "" : Uri.UnescapeDataString(pair.Substring(i + 1).Replace('+', ' '));
                d[k] = v;
            }
            return d;
        }
    }

    /// <summary>Liest HTTP/1.1-Anfragen (Kopfzeilen bis 16 KB, Body bis 8 KB).</summary>
    internal sealed class HttpReader
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[4096];
        private int _pos, _len;

        public HttpReader(Stream s) { _s = s; }

        private int Next()
        {
            if (_pos >= _len)
            {
                _len = _s.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0) return -1;
            }
            return _buf[_pos++];
        }

        private string Line(ref int budget)
        {
            var bytes = new List<byte>();
            while (true)
            {
                int b = Next();
                if (b < 0) return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
                if (--budget < 0) throw new IOException("Anfrage zu groß");
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>Nächste Anfrage oder null (Verbindung zu, Zeitüberschreitung, ungültig).</summary>
        public HttpRequest ReadRequest()
        {
            try
            {
                int budget = 16384;
                string first = Line(ref budget);
                while (first != null && first.Length == 0) first = Line(ref budget);
                if (first == null) return null;
                var parts = first.Split(' ');
                if (parts.Length < 3) return null;
                var req = new HttpRequest { Method = parts[0].ToUpperInvariant() };
                string target = parts[1];
                int q = target.IndexOf('?');
                req.Path = q < 0 ? target : target.Substring(0, q);
                req.Query = q < 0 ? "" : target.Substring(q + 1);
                while (true)
                {
                    string line = Line(ref budget);
                    if (line == null) return null;
                    if (line.Length == 0) break;
                    int c = line.IndexOf(':');
                    if (c > 0) req.Headers[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
                }
                int length;
                if (int.TryParse(req.Header("Content-Length") ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out length) && length > 0)
                {
                    if (length > 8192) return null;
                    var body = new byte[length];
                    for (int i = 0; i < length; i++)
                    {
                        int b = Next();
                        if (b < 0) return null;
                        body[i] = (byte)b;
                    }
                    req.Body = Encoding.UTF8.GetString(body);
                }
                return req;
            }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
        }
    }

    internal sealed class Response
    {
        private readonly Stream _s;
        private readonly bool _keep;
        public bool Secure, Head;
        public string Cookie;

        public Response(Stream s, bool keepAlive)
        {
            _s = s;
            _keep = keepAlive;
        }

        public void Json(int status, string json)
        {
            Send(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), false);
        }

        public void Redirect(string url)
        {
            Write(301, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bitte https verwenden: " + url), "Location: " + url + "\r\n", false);
        }

        public void Send(int status, string type, byte[] body, bool cacheable)
        {
            Write(status, type, body, "", cacheable);
        }

        private void Write(int status, string type, byte[] body, string extra, bool cacheable)
        {
            var h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            h.Append("Content-Type: ").Append(type).Append("\r\n");
            h.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            h.Append(cacheable ? "Cache-Control: max-age=86400\r\n" : "Cache-Control: no-store\r\n");
            h.Append("X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\n");
            h.Append("Content-Security-Policy: default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n");
            if (Cookie != null) h.Append("Set-Cookie: ").Append(Cookie).Append("\r\n");
            h.Append(extra);
            h.Append(_keep ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
            h.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(h.ToString());
            _s.Write(head, 0, head.Length);
            if (!Head) _s.Write(body, 0, body.Length);
            _s.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 301: return "Moved Permanently";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 429: return "Too Many Requests";
                default: return "Error";
            }
        }
    }

    /// <summary>Stream, der ein bereits gelesenes erstes Byte wieder voranstellt (Erkennung HTTP/HTTPS).</summary>
    internal sealed class PrefixStream : Stream
    {
        private readonly Stream _inner;
        private int _prefix;

        public PrefixStream(Stream inner, byte first)
        {
            _inner = inner;
            _prefix = first;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (_prefix >= 0)
            {
                buffer[offset] = (byte)_prefix;
                _prefix = -1;
                return 1;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); }
        public override void Flush() { _inner.Flush(); }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override int ReadTimeout { get { return _inner.ReadTimeout; } set { _inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return _inner.WriteTimeout; } set { _inner.WriteTimeout = value; } }
        public override bool CanTimeout { get { return _inner.CanTimeout; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>JSON-Hilfen (ohne zusätzliche Bibliothek).</summary>
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break;
                    case '>': sb.Append("\\u003e"); break;
                    case '&': sb.Append("\\u0026"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Bool(bool b) { return b ? "true" : "false"; }
    }
}
