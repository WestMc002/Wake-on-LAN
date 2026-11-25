// Wake on LAN – Update-Prüfung über die GitHub-Releases und Selbst-Update (wie Fensterpositionen wiederherstellen)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>Neueste Veröffentlichung auf GitHub.</summary>
    internal sealed class UpdateInfo
    {
        public string Version;   // z. B. 1.27.2026.1106 (ohne "v")
        public string Url;       // Release-Seite auf GitHub
        public string ZipUrl;    // angehängtes ZIP (EXE und Liesmich), nur „Wake-on-LAN-<Version>.zip“ und nur mit gültiger Signatur
        public long ZipSize;     // Größe des ZIP laut GitHub („size“), 0 = unbekannt
        public string Sha256;    // SHA-256 des ZIP aus dem signierten Manifest (Großbuchstaben)
        public byte[] Manifest;  // das geprüfte Manifest (Bytes wie auf GitHub); das Update-Skript prüft es selbst noch einmal
        public string Problem;   // Kurzmeldung, warum nicht installiert wird (z. B. „Nicht signiert.“), sonst null
        public string ProblemDetail; // ausführlicher Text dazu (für „Details“)
        public string Notes;     // Abschnitt "Änderungen" als Klartext (Tooltip der Hinweisleiste)
    }

    /// <summary>
    /// Fragt nur die öffentliche GitHub-Schnittstelle ab (keine Anmeldung, es werden keine Daten übertragen).
    /// Selbst-Update: ZIP laden, Hilfsskript starten, Programm beenden; das Skript ersetzt EXE und Liesmich und startet neu.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Repo = "WestMc002/Wake-on-LAN";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        private const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";

        /// <summary>Arbeitsordner für Download und Hilfsskript.</summary>
        public static string Folder { get { return Path.Combine(Path.GetTempPath(), "Wake-on-LAN-Update"); } }
        public static string LogFile { get { return Path.Combine(Folder, "Update.log"); } }

        private static string UserAgent { get { return "Wake-on-LAN/" + Ui.Version; } } // GitHub verlangt einen User-Agent

        private static void PrepareTls()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>Neueste Veröffentlichung holen. Wirft bei Netz- oder Formatfehlern (mit verständlicher Meldung).</summary>
        public static UpdateInfo Latest()
        {
            PrepareTls();
            var req = (HttpWebRequest)WebRequest.Create(Api);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.Accept = "application/vnd.github+json";
            req.UserAgent = UserAgent;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            string text;
            try
            {
                // Antwort auf 1 MB begrenzen (übliche Antworten haben wenige KB)
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                    text = Encoding.UTF8.GetString(ReadLimited(stream, 1024 * 1024));
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.NotFound)
                {
                    // /releases/latest kennt nur fertige Versionen – kein Fehler
                    var pre = new Exception("Nur Vorabversionen");
                    pre.Data["Prerelease"] = true;
                    throw pre;
                }
                string detail = ex.Message;
                if (ex.InnerException != null && ex.InnerException.Message != ex.Message) detail += "\r\n" + ex.InnerException.Message;
                if (http != null && (int)http.StatusCode == 403) throw Fail("Zu viele Anfragen.", detail);
                if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ProxyNameResolutionFailure
                    || ex.Status == WebExceptionStatus.ConnectFailure) throw Fail("Keine Verbindung.", detail);
                if (ex.Status == WebExceptionStatus.Timeout) throw Fail("Keine Antwort.", detail);
                throw Fail("Nicht erreichbar.", detail);
            }
            catch (IOException ex)
            {
                throw new Exception(ex.Message); // unbekannter Fehler: nur „Details“
            }

            Dictionary<string, object> obj;
            try { obj = MiniJson.Parse(text) as Dictionary<string, object>; }
            catch { obj = null; }
            string tag = obj != null && obj.ContainsKey("tag_name") ? obj["tag_name"] as string : null;
            string version = (tag ?? "").TrimStart('v', 'V').Trim();
            if (Parse(version) == null) throw Fail("Antwort unlesbar.", null); // ohne „Details“

            var info = new UpdateInfo { Version = version, Url = ReleasesPage };
            string url = obj.ContainsKey("html_url") ? obj["html_url"] as string : null;
            // nur Seiten und Downloads des eigenen Repositorys verwenden
            if (url != null && url.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase)) info.Url = url;
            // ZIP und signiertes Manifest nur mit den erwarteten Namen und von der Download-Adresse dieses Repositorys.
            // Die Prüfsumme kommt nur aus dem signierten Manifest, nicht von GitHub: Wer das Release austauscht,
            // kann ohne den privaten Schlüssel keine passende Signatur erzeugen.
            var assets = obj.ContainsKey("assets") ? obj["assets"] as List<object> : null;
            string wanted = AssetName(version), wantedManifest = ManifestName(version);
            string prefix = "https://github.com/" + Repo + "/releases/download/";
            string zipUrl = null, manifestUrl = null;
            long zipSize = 0;
            if (assets != null)
                foreach (object o in assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null || !a.ContainsKey("name") || !a.ContainsKey("browser_download_url")) continue;
                    string name = a["name"] as string, dl = a["browser_download_url"] as string;
                    if (name == null || dl == null || !dl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, wanted, StringComparison.Ordinal))
                    {
                        zipUrl = dl;
                        object size;
                        if (a.TryGetValue("size", out size) && size is double) zipSize = (long)(double)size;
                    }
                    else if (string.Equals(name, wantedManifest, StringComparison.Ordinal)) manifestUrl = dl;
                }
            info.Notes = ExtractNotes(obj.ContainsKey("body") ? obj["body"] as string : null);
            if (zipUrl == null) return info; // kein passendes ZIP: nur die Release-Seite
            if (manifestUrl == null)
            {
                info.Problem = "Nicht signiert.";
                info.ProblemDetail = "Im Release fehlt die Signatur (" + wantedManifest + "). Wake on LAN installiert nur signierte Updates.";
                return info;
            }
            byte[] manifest;
            try { manifest = DownloadSmall(manifestUrl, 8192); }
            catch (Exception ex)
            {
                info.Problem = "Signatur nicht lesbar.";
                info.ProblemDetail = "Die Signatur konnte nicht geladen werden: " + ex.Message;
                return info;
            }
            string sha, why;
            string problem = CheckManifest(manifest, version, out sha, out why);
            if (problem != null) { info.Problem = problem; info.ProblemDetail = why; return info; }
            info.ZipUrl = zipUrl;
            info.ZipSize = zipSize;
            info.Sha256 = sha;
            info.Manifest = manifest;
            return info;
        }

        /// <summary>Feste Kurzmeldung mit technischem Originaltext für „Details“ (null = ohne „Details“).</summary>
        private static Exception Fail(string message, string detail)
        {
            var ex = new Exception(message);
            ex.Data["Detail"] = detail ?? "";
            return ex;
        }

        /// <summary>true, wenn die Version neuer als die laufende ist.</summary>
        public static bool IsNewer(string version)
        {
            Version a = Parse(version), b = Parse(Ui.Version);
            return a != null && b != null && a > b;
        }

        private static Version Parse(string v)
        {
            Version r;
            return System.Version.TryParse(v ?? "", out r) ? r : null;
        }

        /// <summary>Name des ZIP bzw. des signierten Manifests im Release.</summary>
        public static string AssetName(string version) { return "Wake-on-LAN-" + version + ".zip"; }
        public static string ManifestName(string version) { return "Wake-on-LAN-" + version + ".manifest"; }

        // ------------------------------------------------------------------ Signatur

        // Öffentliche Schlüssel (RSA 3072) des Herausgebers: Haupt- und Reserveschlüssel (für alle eigenen Programme gleich).
        // Signiert wird mit dem Hauptschlüssel; der Reserveschlüssel ersetzt ihn, falls er verloren geht.
        private const string MainKey = "<RSAKeyValue><Modulus>tLtylaHt5SgGCrTrEaM0H3Z/UqIQWSL2c3rCpuhZEYnCyJVDIYFCNuLbZ2OeapsJgCttOnwv5djvmpHTcGV6DeFM059O699xepTE0QWKYYM69sCFG4gunH2xh39bM20q9qWbui1Fyy+nN4cqzy26eR7tzXyClAJSS1zCKBuoF1uAtt2c1f1fcCHXdHsjWXZA6Z8OZeiIOizYSfiVHNIJFr8kSFQfnGRaygwp2ta27Hv0z0oKM6Zrvld/vXkgL2um7X1+GV24IzIVT7J8nxmar//yPoc6ugYhgcLjUMBgjwe/5m4REJQFWwntMF1uj5Jf+ehBFZS87kGtSvTykh7mea71JMdLpvAuhicMaP4T8Fr1uTYCPKR4cuiRI+6uogKVOZN23BuZS5iBj1zZkcmopNOXbQupsOkYp2QwejMHoEV7cEc71ZlRqqJed0vBqO8B5H21mrBGJm3kKHxa2QFzY5BZY7ZcC4EG2TZIkC8eZRNUXXmZOdAWGR+aPKeJg7DZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        private const string ReserveKey = "<RSAKeyValue><Modulus>zY/NkGbkd4IZHMMuGty2WLmQ0hm6lEZBql2v84BPf3NT+rpenfLK0kk7aKfRXgFS1G8tz+tHHd58GhioFFhnXN2syFTUus7u5csblitORGoNjTf9Rlicxw6Ut3hX7tkn2u+2PRKE5xOOL+epZV1YvBgr/W67WTt5RZEyfA0Vgn8llhrXaSD32iVNEzkeY5yy1bvQAmoxAk3KjNcD8Uuia9XP9qs/orDO0giIj/xsfY2D7qp/rkhSaIOnt/+gRWHbjhC/PGPga3dmcIMC1C3X0Z/meg6XgI+zWeXOOxFe8jCVRlaeGDoRJlJJGvg6uzIJlLCQeNxnSEufZCD10e+ex2xx75Gp3bqCvBvr49VXEC+pLxQteQMj2V5tuhq23MtIeSV0huaKZ1SPm1kxFjG5vj/11bo2VCkcx9dhfEBDdsucJqdUecbLJp58Y6smM2Ah4Qh7nILSNHHcpWey100qMN3WdwXsfzPVuRtD1c16KgC3TQIov2/2fGQGUxn1Awox</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        private static readonly string[] ManifestKeys = { "Programm", "Version", "Datei", "SHA256", "Schluessel", "Erstellt" };

        /// <summary>
        /// Manifest prüfen (UTF-8, LF, feste Reihenfolge, letzte Zeile „Signatur=“ mit RSA-SHA256/PKCS#1 v1.5 über alle
        /// Bytes davor). Rückgabe: null = gültig (dann sha gesetzt), sonst Kurzmeldung; detail erklärt sie.
        /// </summary>
        public static string CheckManifest(byte[] data, string version, out string sha, out string detail)
        {
            sha = null;
            detail = null;
            const string Invalid = "Signatur ungültig.";
            byte[] marker = Encoding.ASCII.GetBytes("\nSignatur=");
            int at = IndexOf(data, marker);
            if (at < 0) { detail = "Im Manifest fehlt die Zeile „Signatur=“."; return Invalid; }
            byte[] signed = new byte[at + 1];
            Array.Copy(data, signed, signed.Length);
            byte[] signature;
            try { signature = Convert.FromBase64String(Encoding.ASCII.GetString(data, at + marker.Length, data.Length - at - marker.Length).Trim()); }
            catch { detail = "Die Signatur im Manifest ist nicht lesbar."; return Invalid; }
            string[] lines;
            try { lines = new UTF8Encoding(false, true).GetString(signed).Split('\n'); }
            catch { detail = "Das Manifest ist kein gültiger UTF-8-Text."; return Invalid; }
            if (lines.Length != ManifestKeys.Length + 1 || lines[ManifestKeys.Length].Length != 0) { detail = "Das Manifest hat nicht den erwarteten Aufbau."; return Invalid; }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < ManifestKeys.Length; i++)
            {
                if (!lines[i].StartsWith(ManifestKeys[i] + "=", StringComparison.Ordinal)) { detail = "Im Manifest fehlt oder steht falsch: " + ManifestKeys[i] + "."; return Invalid; }
                values[ManifestKeys[i]] = lines[i].Substring(ManifestKeys[i].Length + 1);
            }
            string keyName = values["Schluessel"];
            string keyXml = keyName == "Haupt" ? MainKey : keyName == "Reserve" ? ReserveKey : null;
            if (keyXml == null) { detail = "Unbekannter Schlüssel im Manifest: " + keyName; return Invalid; }
            bool ok;
            try
            {
                // PROV_RSA_AES (24): RSA mit SHA-256 auch unter .NET 4
                using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(keyXml);
                    ok = rsa.VerifyData(signed, "SHA256", signature);
                }
            }
            catch (Exception ex) { detail = "Die Signatur konnte nicht geprüft werden: " + ex.Message; return Invalid; }
            if (!ok)
            {
                detail = "Die Signatur passt nicht zum " + (keyName == "Haupt" ? "Haupt" : "Reserve") +
                         "schlüssel. Das Release wurde nicht vom Herausgeber signiert oder nachträglich verändert.";
                return Invalid;
            }
            if (values["Programm"] != "Wake on LAN") { detail = "Das Manifest gehört zu „" + values["Programm"] + "“, nicht zu Wake on LAN."; return Invalid; }
            if (values["Version"] != version) { detail = "Das Manifest nennt Version " + values["Version"] + ", das Release " + version + "."; return Invalid; }
            if (values["Datei"] != AssetName(version)) { detail = "Das Manifest nennt die Datei „" + values["Datei"] + "“."; return Invalid; }
            if (!Regex.IsMatch(values["SHA256"], "^[0-9a-f]{64}$")) { detail = "Die Prüfsumme im Manifest ist ungültig."; return Invalid; }
            sha = values["SHA256"].ToUpperInvariant();
            return null;
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ Installieren

        // ------------------------------------------------------------------ Installieren

        /// <summary>Höchstgröße des Update-ZIP (zusätzlich zur Größe laut GitHub).</summary>
        private const long MaxZip = 64L * 1024 * 1024;
        /// <summary>Läuft Wake on LAN gerade mit Administratorrechten?</summary>
        private static bool IsElevated
        {
            get
            {
                try
                {
                    using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        /// <summary>Stream lesen, höchstens max Bytes – sonst Abbruch.</summary>
        private static byte[] ReadLimited(Stream stream, int max)
        {
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[8192];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > max) throw new Exception("Antwort zu groß.");
                }
                return ms.ToArray();
            }
        }

        /// <summary>Datei laden und beim Lesen auf max Bytes begrenzen (danach Abbruch, Datei weg). progress: Prozent.</summary>
        private static void DownloadLimited(string url, string file, long max, Action<int> progress)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            bool ok = false;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.ContentLength > max) throw new Exception("Das Update ist größer als erwartet.");
                    long expected = response.ContentLength > 0 ? response.ContentLength : max;
                    using (var stream = response.GetResponseStream())
                    using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int n, last = -1;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += n;
                            if (total > max) throw new Exception("Das Update ist größer als erwartet.");
                            fs.Write(buffer, 0, n);
                            int percent = (int)Math.Min(100, total * 100 / Math.Max(1, expected));
                            if (progress != null && percent != last) { last = percent; progress(percent); }
                        }
                    }
                }
                ok = true;
            }
            finally
            {
                if (!ok) try { File.Delete(file); } catch { }
            }
        }

        /// <summary>Kleine Datei laden (höchstens max Bytes).</summary>
        private static byte[] DownloadSmall(string url, int max)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[4096];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > max) throw new Exception("Die Datei ist zu groß.");
                }
                return ms.ToArray();
            }
        }

        /// <summary>Kann dieses Release direkt installiert werden (gültig signiert, ZIP vorhanden, nichts blockiert)?</summary>
        public static bool CanInstall(UpdateInfo u)
        {
            return u != null && u.Problem == null && !string.IsNullOrEmpty(u.ZipUrl) && !string.IsNullOrEmpty(u.Sha256) && u.Manifest != null && InstallBlocker() == null;
        }

        /// <summary>Ein früheres Update wurde nicht abgeschlossen – „Jetzt aktualisieren“ bietet dann die Wiederherstellung an.</summary>
        public static bool RepairPending { get { return InstallBlocker() == PendingText; } }

        private const string NoZip = "Im Release ist keine passende ZIP-Datei.";

        /// <summary>
        /// Warum nicht installiert wird (null = kann installiert werden):
        /// - im Projektordner (mit Erstellen.bat): dort entsteht die EXE beim Bauen
        /// - Wake on LAN läuft selbst mit Administratorrechten: Skript und Neustart liefen dann erhöht mit Arbeitsordner
        ///   im Temp-Ordner, sobald die Schreibprobe gelingt (normal gestartet nimmt das Update den geschützten Weg)
        /// - ein früheres Update wurde nicht abgeschlossen (PendingText): erst wiederherstellen (OfferRepair)
        /// Liest nur und löscht nichts.
        /// </summary>
        public static string InstallBlocker()
        {
            if (InProjectFolder)
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            if (IsElevated)
                return "Wake on LAN läuft gerade mit Administratorrechten. Bitte Wake on LAN normal starten und dann aktualisieren.";
            if (PendingRestore() != null) return PendingText;
            return null;
        }

        /// <summary>Läuft die EXE im Projektordner (Erstellen.bat daneben)? Dort entsteht sie beim Bauen.</summary>
        private static bool InProjectFolder
        {
            get { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Erstellen.bat")); }
        }

        /// <summary>Schreibprobe mit Zufallsnamen, CreateNew und DeleteOnClose: berührt nie eine vorhandene Datei.</summary>
        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".schreibprobe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts.</summary>
        private const string ExeName = "Wake on LAN.exe", ManualName = "Liesmich.txt";

        // Ergebnis bis 1.72: eine gemeinsame Datei (nur noch, wenn ein altes Skript die neue EXE startet oder eine
        // Wiederherstellung eine ältere EXE zurückbringt). Ab 1.73 je Installationsziel und Versuch eine eigene Datei;
        // die Versuchskennung (32 Hex-Zeichen) gibt der Neustart mit --nach-update weiter.
        private static string LogFor(string attemptId) { return Path.Combine(Folder, "Update-" + TargetId + "-" + attemptId + ".log"); }
        // Startbestätigung für den Neustarthelfer (Prozess-ID des erhöhten Skripts oder „ABBRUCH“), Bereitschaft des
        // Helfers, Empfangsbestätigung und Freigabe für das erhöhte Skript – je Versuch
        private static string StartFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".start"); }
        private static string ReadyFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".bereit"); }
        private static string ReleaseFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".freigabe"); }
        private static string AckFileFor(string attemptId) { return Path.Combine(Folder, TargetId + "-" + attemptId + ".empfangen"); }

        /// <summary>Startzeit dieser Instanz (UTC-Ticks): Die Skripte erkennen die alte Instanz an Prozess-ID und Startzeit.</summary>
        private static long ProcessStartTicks
        {
            get { try { return Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; } catch { return 0; } }
        }

        /// <summary>Gültige Versuchskennung (32 Hex-Zeichen, klein)?</summary>
        public static bool IsAttemptId(string id)
        {
            return id != null && Regex.IsMatch(id, "^[0-9a-f]{32}$");
        }

        /// <summary>
        /// Selbst-Update nach globaler Vorgabe (Referenz RDP Pilot 1.59): ZIP begrenzt in einen eigenen Arbeitsordner laden,
        /// SHA-256 aus dem signierten Manifest prüfen, Update-Skript als Datei ablegen und über einen kurzen Lader starten, der
        /// die Prüfsumme des Skripts kontrolliert. Das Skript prüft die Signatur des Manifests selbst, tauscht nur EXE und
        /// Liesmich atomar aus (Journal, Rücksicherung) und arbeitet erhöht nur in einem geschützten Ordner unter %ProgramData%.
        /// Nach einem erhöhten Update startet ein nicht erhöhter Prozess das Programm neu. Danach muss der Aufrufer beenden.
        /// </summary>
        /// <param name="tray">nach dem Update wieder unsichtbar im Infobereich starten (automatisches Update im Hintergrund)</param>
        public static async Task Install(UpdateInfo release, Action<int> progress, bool tray)
        {
            // nur ein Update zur Zeit: von Hand und automatisch teilen sich diese Sperre; sie bleibt nach dem Start
            // des Skripts gesetzt, weil sich das Programm danach beendet
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new Exception(Running);
            bool started = false;
            try
            {
                await Task.Run(() => InstallLocked(release, progress, tray));
                started = true;
            }
            finally
            {
                if (!started) Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>Läuft gerade ein Update (in diesem Programm oder als Skript nach einem früheren Start)?</summary>
        public static bool Busy
        {
            get { return _busy != 0 || ScriptRunning(); }
        }

        private static int _busy;
        private const string Running = "Ein Update läuft bereits.";

        /// <summary>
        /// Name der Sperre, die Update- und Reparatur-Skript bis zum Ende des Dateiaustauschs bzw. der Rücksicherung
        /// halten: sitzungsübergreifend (Global\) und je Installationsziel. Wake on LAN hält sie selbst nur kurz zum Aufräumen.
        /// </summary>
        private static string LockName
        {
            get { return @"Global\WakeOnLan-Update-" + TargetId; }
        }

        /// <summary>Kennung des Installationsziels (24 Zeichen der SHA-256 des Programmordners) für Sperre und Arbeitsordner.</summary>
        private static string TargetId
        {
            get
            {
                string dir = Path.GetFullPath(Application.StartupPath).TrimEnd('\\').ToUpperInvariant();
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(dir))).Replace("-", "").Substring(0, 24);
            }
        }

        private static bool ScriptRunning()
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(LockName, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; } // vom Skript mit Administratorrechten oder aus einem anderen Konto angelegt
            catch { return false; }
        }

        private static void InstallLocked(UpdateInfo release, Action<int> progress, bool tray)
        {
            if (release.Problem != null)
            {
                Exception unsigned = new Exception(release.Problem);
                unsigned.Data["Detail"] = release.ProblemDetail;
                throw unsigned;
            }
            if (string.IsNullOrEmpty(release.ZipUrl) || string.IsNullOrEmpty(release.Sha256) || release.Manifest == null) throw new Exception(NoZip);
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            if (ScriptRunning()) throw new Exception(Running);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            // Aufräumen nur unter der Sperre des Installationsziels; ist sie belegt, läuft gerade ein Update
            if (!WithTargetLock(CleanupLocked)) throw new Exception(Running);
            if (PendingRestore() != null) throw new Exception(PendingText);
            // eigener Arbeitsordner je Versuch; der Name beginnt mit der Zielkennung, damit die Sperrprüfung nur Ordner
            // dieses Installationsziels betrachtet
            string attemptId = Guid.NewGuid().ToString("N");
            string attempt = Path.Combine(Folder, TargetId + "-" + attemptId);
            Directory.CreateDirectory(attempt);
            string zip = Path.Combine(attempt, AssetName(release.Version));
            // Download begrenzen: höchstens die von GitHub genannte Größe, in jedem Fall höchstens 64 MB
            long limit = MaxZip;
            if (release.ZipSize > 0)
            {
                if (release.ZipSize > MaxZip) throw new Exception("Das Update ist ungewöhnlich groß und wird nicht geladen.");
                limit = release.ZipSize;
            }
            DownloadLimited(release.ZipUrl, zip, limit, progress);
            if (release.ZipSize > 0 && new FileInfo(zip).Length != release.ZipSize)
            {
                try { File.Delete(zip); } catch { }
                throw new Exception("Download unvollständig.");
            }
            string hash = Sha256Of(zip);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zip); } catch { }
                Exception bad = new Exception("Download beschädigt.");
                bad.Data["Detail"] = "Die Prüfsumme (SHA-256) der heruntergeladenen Datei stimmt nicht mit dem signierten Manifest überein.\n" +
                                     "Erwartet: " + release.Sha256 + "\nErhalten: " + hash;
                throw bad;
            }
            string logFile = LogFor(attemptId);
            string exe = Application.ExecutablePath;
            bool elevated = !CanWrite(Path.GetDirectoryName(exe));
            // Erhöht: eigener, nicht vorhersagbarer Arbeitsordner direkt unter ProgramData, den das Skript nur für
            // Administratoren beschreibbar anlegt. Sonst reicht der Arbeitsordner im Temp-Ordner des Benutzers.
            string work = elevated
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WakeOnLan-Update-" + TargetId + "-" + Guid.NewGuid().ToString("N"))
                : attempt;
            int pid = Process.GetCurrentProcess().Id;
            // Manifest neben das ZIP legen: Das Skript prüft die Signatur selbst und nimmt die Prüfsumme nur daraus
            string manifestFile = Path.Combine(attempt, ManifestName(release.Version));
            File.WriteAllBytes(manifestFile, release.Manifest);
            StringBuilder vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            Var(vars, "Zip", zip);
            vars.Append("$ZipSize = ").Append(new FileInfo(zip).Length).Append("\n");
            Var(vars, "Manifest", manifestFile);
            Var(vars, "Exe", exe);
            Var(vars, "Version", release.Version);
            Var(vars, "Sha256", release.Sha256.ToUpperInvariant());
            Var(vars, "AssetName", AssetName(release.Version));
            Var(vars, "MainKey", MainKey);
            Var(vars, "ReserveKey", ReserveKey);
            Var(vars, "LockName", LockName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "TempRoot", Folder);
            Var(vars, "Log", logFile);
            vars.Append("$LegacyFallback = $false\n");
            Var(vars, "AttemptId", attemptId);
            Var(vars, "ReleaseId", attemptId);
            Var(vars, "ReleaseFile", elevated ? ReleaseFileFor(attemptId) : "");
            vars.Append("$ProcessStart = ").Append(ProcessStartTicks).Append("\n");
            Var(vars, "Work", work);
            Var(vars, "ExeName", ExeName);
            Var(vars, "ManualName", ManualName);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            // Skript als Datei im Arbeitsordner, auf der Befehlszeile nur Lader, Werte und SHA-256 des Skripts:
            // Der Lader liest die Datei einmal in den Speicher, prüft die Prüfsumme und führt genau diese Bytes aus.
            byte[] body = new UTF8Encoding(false).GetBytes(Compact(InstallScript));
            string scriptFile = Path.Combine(attempt, "Update-Skript.txt");
            File.WriteAllBytes(scriptFile, body);
            string scriptHash;
            using (SHA256 sha = SHA256.Create()) scriptHash = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");
            Var(vars, "ScriptFile", scriptFile);
            Var(vars, "ScriptHash", scriptHash);
            ProcessStartInfo start = PowerShell(vars + InstallLoader);
            if (!elevated)
            {
                Process.Start(start);
                return;
            }
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Skript mit Administratorrechten starten.
            // Zuerst den Neustarthelfer (normale Rechte) – scheitert sein Start, wird nichts Erhöhtes gestartet.
            StartElevated(start, pid, attempt, exe, work, attemptId, "Update", logFile, tray);
        }

        /// <summary>
        /// Ablauf mit Administratorrechten: 1. Neustarthelfer (normale Rechte) starten und auf seine Bereitschaft warten,
        /// 2. erhöhtes Skript starten, 3. dessen Prozess-ID als Startbestätigung für den Helfer schreiben, 4. auf die
        /// Empfangsbestätigung des Helfers warten (bis dahin bleibt der eigene Handle auf den Prozess offen), 5. Freigabe
        /// für genau diesen Versuch schreiben. Das erhöhte Skript ändert erst nach dem Programmende und nur mit Freigabe
        /// etwas; scheitert ein Schritt, fehlt die Freigabe und es bricht ohne Änderung ab.
        /// </summary>
        private static void StartElevated(ProcessStartInfo start, int pid, string attempt, string exe, string work, string attemptId,
                                          string what, string helperLog, bool tray)
        {
            string startFile = StartFileFor(attemptId), readyFile = ReadyFileFor(attemptId);
            string releaseFile = ReleaseFileFor(attemptId), ackFile = AckFileFor(attemptId);
            foreach (string f in new string[] { startFile, readyFile, releaseFile, ackFile }) try { File.Delete(f); } catch { }
            string failed = what + " nicht gestartet: Der Neustart konnte nicht vorbereitet werden. Es wurde nichts verändert.";
            Process helper = StartRestartHelper(pid, attempt, exe, work, attemptId, startFile, readyFile, ackFile, helperLog, tray);
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(readyFile))
            {
                bool exited = true;
                try { exited = helper == null || helper.HasExited; } catch { }
                if (exited || DateTime.UtcNow > until)
                {
                    try { if (!exited) helper.Kill(); } catch { }
                    throw new Exception(failed);
                }
                Thread.Sleep(100);
            }
            start.Verb = "runas";
            Process elevatedProcess;
            try { elevatedProcess = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                WriteStartFile(startFile, "ABBRUCH");
                if (ex.NativeErrorCode == 1223) throw new Exception(what + " abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
            catch { WriteStartFile(startFile, "ABBRUCH"); throw; }
            int id = 0;
            try { if (elevatedProcess != null) id = elevatedProcess.Id; } catch { }
            if (id <= 0 || !WriteStartFile(startFile, id.ToString(CultureInfo.InvariantCulture)))
            {
                WriteStartFile(startFile, "ABBRUCH");
                throw new Exception(failed);
            }
            // Zweite Bestätigung: Der Helfer hat die Prozess-ID empfangen und hält einen Handle darauf. Bis dahin bleibt
            // der Handle in elevatedProcess offen, die Prozess-ID kann also nicht neu vergeben werden.
            until = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                string ack = ReadSmall(ackFile);
                if (ack == id.ToString(CultureInfo.InvariantCulture)) break;
                bool exited = true;
                try { exited = helper.HasExited; } catch { }
                if (exited || DateTime.UtcNow > until) throw new Exception(failed); // ohne Freigabe: das Skript bricht ohne Änderung ab
                Thread.Sleep(100);
            }
            bool helperGone = true;
            try { helperGone = helper.HasExited; } catch { }
            if (helperGone) throw new Exception(failed);
            // Freigabe zuletzt: fehlt sie, bricht das erhöhte Skript nach dem Programmende ohne Änderung ab
            if (!WriteStartFile(releaseFile, attemptId)) throw new Exception(failed);
            GC.KeepAlive(elevatedProcess);
        }

        /// <summary>
        /// Steuerdatei aus dem Temp-Ordner einmal öffnen und aus demselben Stream höchstens 257 Bytes lesen; mehr als
        /// 256 Bytes oder nicht lesbar = ungültig (null). Nicht erst die Größe prüfen und dann erneut öffnen.
        /// </summary>
        private static string ReadSmall(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buf = new byte[257];
                    int n = 0, k;
                    while (n < 257 && (k = fs.Read(buf, n, 257 - n)) > 0) n += k;
                    if (n > 256) return null;
                    return Encoding.ASCII.GetString(buf, 0, n).Trim();
                }
            }
            catch { return null; }
        }

        private static bool WriteStartFile(string file, string text)
        {
            try
            {
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
                return true;
            }
            catch { return false; }
        }

        private static Process StartRestartHelper(int pid, string attempt, string exe, string work, string attemptId,
                                                  string startFile, string readyFile, string ackFile, string log, bool tray)
        {
            StringBuilder r = new StringBuilder();
            r.Append("$ProcessId = ").Append(pid).Append("\n");
            r.Append("$ProcessStart = ").Append(ProcessStartTicks).Append("\n");
            Var(r, "Attempt", attempt);
            Var(r, "Exe", exe);
            Var(r, "Log", log);
            Var(r, "Work", work);
            // Wiederherstellung (attempt leer): die wiederhergestellte EXE kann älter sein; Start-Program prüft ihre Version
            Var(r, "AttemptId", attemptId);
            r.Append("$LegacyFallback = $").Append(string.IsNullOrEmpty(attempt) ? "true" : "false").Append("\n");
            Var(r, "StartFile", startFile);
            Var(r, "ReadyFile", readyFile);
            Var(r, "AckFile", ackFile);
            r.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            return Process.Start(PowerShell(r + RestartScript));
        }

        // --- Nicht abgeschlossenes Update (Globale Richtlinien, Selbst-Update › Offene Wiederherstellung) ----------

        /// <summary>Merker im Arbeitsordner: Rücksicherung unvollständig (Format 2: „Journalformat 2“ und der Alt-Ordner).</summary>
        private const string PendingMarker = "Wiederherstellung-offen";

        /// <summary>Kurzmeldung für Anwender: Ein Update wurde nicht abgeschlossen (Technisches steht im Fehlerprotokoll).</summary>
        public const string PendingText = "Das letzte Update wurde nicht abgeschlossen.";

        /// <summary>
        /// Arbeitsordner eines nicht abgeschlossenen Updates für dieses Installationsziel (null = keiner). Nur lesend:
        /// Aufgeräumt wird ausschließlich in CleanupLocked unter der Sperre des Installationsziels.
        /// </summary>
        private static string PendingRestore()
        {
            try { foreach (string dir in PendingDirs()) return dir; }
            catch { }
            return null;
        }

        /// <summary>Arbeitsordner mit Merker „Wiederherstellung-offen“ für dieses Ziel (Temp-Ordner und ProgramData).</summary>
        private static List<string> PendingDirs()
        {
            List<string> dirs = new List<string>();
            if (Directory.Exists(Folder)) dirs.AddRange(Directory.GetDirectories(Folder, TargetId + "-*"));
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            dirs.AddRange(Directory.GetDirectories(data, "WakeOnLan-Update-" + TargetId + "-*"));
            return dirs.FindAll(d => File.Exists(Path.Combine(d, PendingMarker)));
        }

        /// <summary>
        /// Sperre des Installationsziels kurz selbst halten (dieselbe Mutex wie Update- und Reparatur-Skript), damit
        /// Aufräumen und Prüfen nie gleichzeitig mit einem laufenden Update oder einer Wiederherstellung geschehen.
        /// false = gerade gesperrt (ein Skript läuft) – dann nichts tun.
        /// </summary>
        private static bool WithTargetLock(Action action)
        {
            Mutex m;
            try { m = new Mutex(false, LockName); }
            catch (UnauthorizedAccessException) { return false; } // von einem Skript mit Administratorrechten gehalten
            using (m)
            {
                bool have;
                try { have = m.WaitOne(0); }
                catch (AbandonedMutexException) { have = true; }
                if (!have) return false;
                try { action(); }
                finally { m.ReleaseMutex(); }
                return true;
            }
        }

        /// <summary>
        /// Unter der Sperre: Temp-Arbeitsordner, bei denen laut Journal nichts zurückzusetzen ist, still entfernen, und
        /// abgebrochene Versuche (älter als ein Tag) aufräumen. Unter ProgramData wird nie still aufgeräumt: Wake on LAN
        /// aktualisiert nie mit Adminrechten (InstallBlocker greift vorher), offene Ordner dort werden per Rückfrage
        /// wiederhergestellt.
        /// </summary>
        public static void QuietCleanup()
        {
            try { WithTargetLock(CleanupLocked); } catch { }
        }

        private static void CleanupLocked()
        {
            if (!Directory.Exists(Folder)) return;
            foreach (string dir in PendingDirs())
            {
                if (!dir.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!NothingToRestore(dir)) continue;
                try
                {
                    Directory.Delete(dir, true);
                    History.Add("Wake on LAN", "Nicht abgeschlossenes Update aufgeräumt, es war nichts zurückzusetzen: " + dir);
                }
                catch { }
            }
            CleanOldAttempts();
        }

        /// <summary>„Vorher“-Eintrag im Wiederherstellungsjournal.</summary>
        private sealed class JournalEntry { public string N, Kind, Dst, Hash, Tmp; }

        // Formate des Wiederherstellungsjournals – alle werden ausdrücklich unterschieden, alles andere gilt als
        // unbekannt (Ordner und Sicherungen bleiben erhalten):
        // - Format 2 (ab 1.73): Merker „Journalformat 2“ + Alt-Ordner; Journal mit Formatzeile, „Geplant 1..n“, „Bereit“,
        //   Sicherungen mit SHA-256, „Ersetzt k“ direkt nach „Vorher k“ (Pflicht). Journal entsteht vor dem Merker.
        // - Vorstufe (Stand WindowGrid 1.67, in Wake on LAN nie veröffentlicht): Merker nur Alt-Ordner; Journal mit „Bereit“
        //   (ohne Formatzeile und Plan), Sicherungen mit SHA-256, „Ersetzt“ kann fehlen.
        // - Format 1.72: Merker nur Alt-Ordner; Journal ohne „Bereit“, Sicherungen ohne Prüfsumme, „Ersetzt“ kann
        //   fehlen. Der Merker entstand vor dem Journal.
        // Fehlt bei einem Merker im alten Aufbau das Journal, ist der Stand ungeklärt.
        private const string JournalFormat = "Journalformat 2";
        private enum MarkerKind { Unknown, Legacy, Format2 }

        /// <summary>Merker lesen: Format 2 = „Journalformat 2“ + Alt-Ordner; alter Aufbau = nur der Alt-Ordner; sonst Unknown.</summary>
        private static MarkerKind ReadMarker(string dir)
        {
            try
            {
                string marker = Path.Combine(dir, PendingMarker);
                if (new FileInfo(marker).Length > 4096) return MarkerKind.Unknown;
                List<string> lines = new List<string>();
                foreach (string l in File.ReadAllLines(marker, Encoding.UTF8))
                {
                    string t = l.Trim().TrimStart('\uFEFF');
                    if (t.Length > 0) lines.Add(t);
                }
                string alt = Path.Combine(dir, "Alt");
                if (lines.Count == 2 && lines[0] == JournalFormat && string.Equals(lines[1], alt, StringComparison.OrdinalIgnoreCase)) return MarkerKind.Format2;
                if (lines.Count == 1 && string.Equals(lines[0], alt, StringComparison.OrdinalIgnoreCase)) return MarkerKind.Legacy;
            }
            catch { }
            return MarkerKind.Unknown;
        }

        /// <summary>
        /// Journal streng prüfen – dieselben Regeln wie im Reparatur-Skript. false = Stand ungeklärt (nichts aufräumen,
        /// Wiederherstellung endet mit OFFEN). Bei true enthält entries alle „Vorher“-Einträge in Reihenfolge.
        /// </summary>
        private static bool ReadJournal(string dir, out List<JournalEntry> entries)
        {
            entries = null;
            string exe = Application.ExecutablePath;
            string manual = Path.Combine(Path.GetDirectoryName(exe), ManualName);
            Func<string, bool> allowed = d => string.Equals(d, exe, StringComparison.OrdinalIgnoreCase) || string.Equals(d, manual, StringComparison.OrdinalIgnoreCase);
            MarkerKind kind = ReadMarker(dir);
            if (kind == MarkerKind.Unknown) return false;
            string journal = Path.Combine(dir, "Wiederherstellung.txt");
            if (!File.Exists(journal) || new FileInfo(journal).Length > 64 * 1024) return false;
            string[] lines = File.ReadAllLines(journal, Encoding.UTF8);
            if (lines.Length < 2 || !lines[0].StartsWith("Wake-on-LAN-Update ", StringComparison.Ordinal) ||
                !lines[0].EndsWith(" - Wiederherstellungsjournal", StringComparison.Ordinal)) return false;
            bool format2 = kind == MarkerKind.Format2;
            bool formatReady = !format2 && Array.IndexOf(lines, "Bereit") >= 0;
            bool withHash = format2 || formatReady;
            List<string> planned = new List<string>();
            List<JournalEntry> list = new List<JournalEntry>();
            int i = 1;
            if (format2)
            {
                if (lines[1] != JournalFormat) return false;
                i = 2;
            }
            // Erklärtext bis zur ersten Zeile mit Schlüsselwort
            string first = format2 ? "Geplant " : formatReady ? "Bereit" : "Vorher ";
            while (i < lines.Length && !lines[i].StartsWith(first, StringComparison.Ordinal))
            {
                if (IsJournalKeyword(lines[i])) return false;
                i++;
            }
            if (format2)
            {
                for (; i < lines.Length && lines[i].StartsWith("Geplant ", StringComparison.Ordinal); i++)
                {
                    Match g = Regex.Match(lines[i], @"^Geplant (\d+): Ziel: (.+)$");
                    if (!g.Success || g.Groups[1].Value != (planned.Count + 1).ToString(CultureInfo.InvariantCulture) || !allowed(g.Groups[2].Value)) return false;
                    string p = g.Groups[2].Value;
                    if (planned.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) return false;
                    planned.Add(p);
                }
                if (planned.Count == 0) return false;
            }
            if (withHash)
            {
                if (i >= lines.Length || lines[i] != "Bereit") return false;
                i++;
            }
            bool replacedLast = true;
            for (; i < lines.Length; i++)
            {
                string line = lines[i];
                Match e = Regex.Match(line, @"^Ersetzt (\d+)$");
                if (e.Success)
                {
                    if (list.Count == 0 || replacedLast || e.Groups[1].Value != list[list.Count - 1].N) return false;
                    replacedLast = true;
                    continue;
                }
                Match m = withHash
                    ? Regex.Match(line, @"^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+? \| SHA256: ([0-9A-F]{64}))? \| Hilfsdatei: (.+)$")
                    : Regex.Match(line, @"^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)()(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$");
                if (!m.Success) return false;
                JournalEntry entry = new JournalEntry();
                entry.N = m.Groups[1].Value; entry.Kind = m.Groups[2].Value; entry.Dst = m.Groups[3].Value;
                entry.Hash = m.Groups[4].Value; entry.Tmp = m.Groups[5].Value;
                // fortlaufend ab 1; nur im Format 2 muss die vorige Datei bestätigt sein („Ersetzt“ wird dort mit Abbruch
                // bei Fehler geschrieben, eine fehlende Zeile bedeutet also ein beschädigtes Journal)
                if (entry.N != (list.Count + 1).ToString(CultureInfo.InvariantCulture) || (format2 && list.Count > 0 && !replacedLast) || !allowed(entry.Dst)) return false;
                if (withHash)
                {
                    if (entry.Kind == "Sicherung" && entry.Hash.Length == 0) return false;
                    if (entry.Kind == "Neu" && line.Contains(" | Sicherungsdatei: ")) return false;
                }
                if (format2 && (list.Count >= planned.Count || !string.Equals(planned[list.Count], entry.Dst, StringComparison.OrdinalIgnoreCase))) return false;
                // je Ziel höchstens eine „Vorher“-Zeile; ein doppeltes Ziel macht das Journal widersprüchlich
                if (list.Exists(x => string.Equals(x.Dst, entry.Dst, StringComparison.OrdinalIgnoreCase))) return false;
                list.Add(entry);
                replacedLast = false;
            }
            entries = list;
            return true;
        }

        private static bool IsJournalKeyword(string line)
        {
            return line == "Bereit" || line == JournalFormat || line.StartsWith("Geplant ", StringComparison.Ordinal) ||
                   line.StartsWith("Vorher ", StringComparison.Ordinal) || line.StartsWith("Ersetzt ", StringComparison.Ordinal);
        }

        /// <summary>
        /// true, wenn das Journal die strenge Prüfung besteht und jede eingetragene Datei noch der Sicherung entspricht
        /// bzw. bei „Neu“ nicht vorhanden ist (das Update hat dann nichts verändert). Übrig gebliebene Hilfsdateien werden
        /// erst danach entfernt. Im Zweifel bleibt der Arbeitsordner erhalten.
        /// </summary>
        private static bool NothingToRestore(string dir)
        {
            try
            {
                string target = Path.GetDirectoryName(Application.ExecutablePath);
                List<JournalEntry> entries;
                if (!ReadJournal(dir, out entries)) return false;
                foreach (JournalEntry e in entries)
                {
                    if (e.Kind == "Sicherung")
                    {
                        string bak = Path.Combine(Path.Combine(dir, "Alt"), e.N);
                        // Stromausfall zwischen Kopie und Umbenennen: .teil nur mit Prüfsumme aus dem Journal (unten verglichen)
                        if (!File.Exists(bak) && e.Hash.Length > 0 && File.Exists(bak + ".teil")) bak += ".teil";
                        if (!File.Exists(bak) || !File.Exists(e.Dst)) return false;
                        string hash = Sha256Of(bak);
                        if ((e.Hash.Length > 0 && hash != e.Hash) || hash != Sha256Of(e.Dst)) return false;
                    }
                    else if (File.Exists(e.Dst)) return false;
                }
                foreach (JournalEntry e in entries)
                    // Hilfsdatei nur im Programmordner und mit dem erwarteten Namen entfernen
                    if (string.Equals(Path.GetDirectoryName(e.Tmp), target, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(e.Tmp).StartsWith("." + Path.GetFileName(e.Dst) + ".update-", StringComparison.OrdinalIgnoreCase))
                        try { if (File.Exists(e.Tmp)) File.Delete(e.Tmp); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Nicht abgeschlossenes Update: nachfragen und die vorherige Version wiederherstellen (Wake on LAN wird dazu
        /// beendet und danach neu gestartet). true = Wiederherstellung läuft, Wake on LAN soll sich beenden.
        /// </summary>
        /// <summary>Arbeitsordner direkt unter ProgramData (geschützter Ordner eines Updates mit Administratorrechten)?</summary>
        private static bool IsProtectedDir(string dir)
        {
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.Equals(Path.GetDirectoryName(dir), data, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Altformat 1.72 (Merker nur Alt-Ordner, Journal ohne „Bereit“, Sicherungen ohne Prüfsumme): Die Sicherungen lassen
        /// sich nicht gegen eine Prüfsumme prüfen. Liegt es in einem geschützten Arbeitsordner unter ProgramData, wird mit
        /// Administratorrechten nichts zurückgesetzt, sondern auf die Neuinstallation hingewiesen. Im Zweifel (Journal nicht
        /// lesbar) false: dann entscheidet die normale Prüfung (OFFEN). Referenz: RDP Pilot 1.59.
        /// </summary>
        private static bool LegacyWithoutHash(string dir)
        {
            try
            {
                if (ReadMarker(dir) != MarkerKind.Legacy) return false;
                string journal = Path.Combine(dir, "Wiederherstellung.txt");
                if (!File.Exists(journal) || new FileInfo(journal).Length > 64 * 1024) return false;
                return Array.IndexOf(File.ReadAllLines(journal, Encoding.UTF8), "Bereit") < 0;
            }
            catch { return false; }
        }

        public const string ReinstallText = PendingText + " Es stammt von einer älteren Version, deren Sicherungen keine Prüfsumme haben. " +
            "Mit Administratorrechten setzt Wake on LAN sie deshalb nicht automatisch zurück.\n\n" +
            "Bitte die aktuelle Version von der Download-Seite laden und neu installieren.";

        public static bool OfferRepair(IWin32Window owner, bool tray)
        {
            // im Projektordner (Erstellen.bat neben der EXE) wird nie etwas zurückgesetzt, wie beim Update
            if (InProjectFolder) return false;
            // nie aus einer Instanz mit Administratorrechten (sonst liefe die Reparatur mit geerbten Rechten)
            if (IsElevated)
            {
                MessageBox.Show(owner, "Wake on LAN läuft gerade mit Administratorrechten. Bitte Wake on LAN normal starten und dann aktualisieren.",
                    "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            // erst unter der Sperre aufräumen, was nichts verändert hat; ist sie belegt, läuft gerade ein Update
            if (!WithTargetLock(CleanupLocked))
            {
                MessageBox.Show(owner, Running, "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            string dir = PendingRestore();
            if (dir == null) return false;
            History.Add("Wake on LAN", "Nicht abgeschlossenes Update gefunden: " + dir);
            if (IsProtectedDir(dir) && LegacyWithoutHash(dir))
            {
                History.Add("Wake on LAN", "Altes Journal ohne Prüfsummen im geschützten Arbeitsordner, keine Wiederherstellung: " + dir);
                if (MessageBox.Show(owner, ReinstallText + "\n\nDownload-Seite jetzt öffnen?", "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    Ui.Open(ReleasesPage);
                return false;
            }
            if (MessageBox.Show(owner,
                PendingText + "\n\nWake on LAN stellt jetzt die vorherige Version wieder her und startet danach neu.\n\nJetzt wiederherstellen?",
                "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return false;
            try { Repair(dir, tray); return true; }
            catch (Exception ex)
            {
                History.Add("Wake on LAN", "Wiederherstellung nicht gestartet: " + ex.Message);
                MessageBox.Show(owner, ex.Message, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private static void Repair(string dir, bool tray)
        {
            if (InProjectFolder) throw new Exception("Im Projektordner wird nichts zurückgesetzt.");
            if (IsElevated) throw new Exception("Wake on LAN läuft gerade mit Administratorrechten.");
            bool protectedDir = IsProtectedDir(dir);
            if (protectedDir && LegacyWithoutHash(dir)) throw new Exception(ReinstallText);
            string exe = Application.ExecutablePath;
            bool writable = CanWrite(Path.GetDirectoryName(exe));
            // Sicherungen aus dem Temp-Ordner nie mit Administratorrechten zurückkopieren (jedes Programm des Benutzers
            // könnte sie verändert haben); dort hat das Update ohne Adminrechte gearbeitet, also reichen sie auch jetzt.
            if (!protectedDir && !writable)
                throw new Exception("Die vorherige Version lässt sich nicht automatisch wiederherstellen. Bitte Wake on LAN neu herunterladen.");
            bool elevated = protectedDir;
            string attemptId = Guid.NewGuid().ToString("N");
            int pid = Process.GetCurrentProcess().Id;
            StringBuilder vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            // Die wiederhergestellte EXE kann älter als 1.73 sein: Start-Program prüft ihre Version. Ab 1.73 mit Kennung
            // und eigener Ergebnisdatei, sonst ohne Kennung und mit Kopie in die gemeinsame Update.log
            Var(vars, "AttemptId", attemptId);
            vars.Append("$LegacyFallback = $true\n");
            Var(vars, "ReleaseId", attemptId);
            Var(vars, "ReleaseFile", elevated ? ReleaseFileFor(attemptId) : "");
            vars.Append("$ProcessStart = ").Append(ProcessStartTicks).Append("\n");
            Var(vars, "Work", dir);
            Var(vars, "Exe", exe);
            Var(vars, "ManualName", ManualName);
            Var(vars, "LockName", LockName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "Log", LogFor(attemptId));
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            // Reparatur-Skript als Datei im Temp-Ordner, auf der Befehlszeile nur Lader, Werte und SHA-256 (wie beim
            // Update): Ein Austausch der Datei bleibt wirkungslos, und die Befehlszeile bleibt auch bei langen Pfaden kurz.
            Directory.CreateDirectory(Folder);
            byte[] body = new UTF8Encoding(false).GetBytes(Compact(RepairScript));
            string scriptFile = Path.Combine(Folder, "Reparatur-Skript-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllBytes(scriptFile, body);
            string scriptHash;
            using (SHA256 sha = SHA256.Create()) scriptHash = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");
            Var(vars, "ScriptFile", scriptFile);
            Var(vars, "ScriptHash", scriptHash);
            ProcessStartInfo start = PowerShell(vars + RepairLoader);
            if (!elevated)
            {
                Process.Start(start);
                return;
            }
            // Neustart mit normalen Rechten über den Neustarthelfer (liest das Ergebnis aus dem geschützten Ordner)
            StartElevated(start, pid, "", exe, dir, attemptId, "Wiederherstellung", LogFor(attemptId), tray);
        }

        /// <summary>
        /// Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen; Ordner mit offener
        /// Wiederherstellung nie. Dazu Reparatur-Skripte, Start- und Freigabedateien und nicht abgeholte Ergebnisse
        /// dieses Ziels nach einem Tag.
        /// </summary>
        private static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    DirectoryInfo di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.Exists(Path.Combine(dir, PendingMarker))) continue; // Wiederherstellung offen: nie automatisch löschen
                    if (di.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { di.Delete(true); } catch { }
                }
                foreach (string pattern in new string[] { "Reparatur-Skript-*.txt", TargetId + "-*.start", TargetId + "-*.bereit",
                    TargetId + "-*.freigabe", TargetId + "-*.empfangen", TargetId + "-*.tmp", "Update-" + TargetId + "-*.log" })
                    foreach (string f in Directory.GetFiles(Folder, pattern))
                        try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch { }
            }
            catch { }
        }

        private static string Sha256Of(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        /// <summary>PowerShell mit dem Skript als Kommando (-EncodedCommand): Es liegt nirgends als Datei, die man austauschen könnte.</summary>
        private static ProcessStartInfo PowerShell(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            // nur der feste Systempfad, kein Suchen über PATH oder den aktuellen Ordner
            if (!File.Exists(ps)) throw new Exception("Windows PowerShell wurde nicht gefunden.");
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(Compact(script)));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            ProcessStartInfo start = new ProcessStartInfo(ps, arguments);
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.WorkingDirectory = system;
            return start;
        }

        /// <summary>Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein.</summary>
        internal static string Compact(string script)
        {
            StringBuilder compact = new StringBuilder();
            foreach (string raw in script.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                compact.Append(line).Append('\n');
            }
            return compact.ToString();
        }

        /// <summary>Variable für das Skript als PowerShell-Text in einfachen Anführungszeichen.</summary>
        private static void Var(StringBuilder sb, string name, string value)
        {
            sb.Append('$').Append(name).Append(" = '");
            foreach (char c in value ?? "")
            {
                sb.Append(c);
                // PowerShell nimmt auch typografische einfache Anführungszeichen als Begrenzer: verdoppeln
                if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B') sb.Append(c);
                if (c == '\n' || c == '\r') throw new Exception("Ungültiger Pfad für das Update.");
            }
            sb.Append("'\n");
        }

        /// <summary>
        /// Ergebnis des letzten Updates („OK x“, „FEHLER …“, „ZURUECK …“, „OFFEN …“) nach dem Neustart mit --nach-update.
        /// attemptId: Versuchskennung aus --nach-update (ab 1.73); ohne Kennung die gemeinsame Datei bis 1.72. Nur das
        /// Ergebnis genau dieses Versuchs und dieses Installationsziels wird gelesen (höchstens 16 KB) und danach entfernt.
        /// </summary>
        public static string LastResult(string attemptId)
        {
            string file = IsAttemptId(attemptId) ? LogFor(attemptId) : LogFile;
            try
            {
                if (!File.Exists(file)) return null;
                string text;
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length > 16 * 1024) text = "FEHLER Ergebnis des Updates unlesbar (Update.log zu groß).";
                    else text = Encoding.UTF8.GetString(ReadLimited(fs, 16 * 1024)).Trim().TrimStart('\uFEFF');
                }
                try { File.Delete(file); } catch { }
                return text;
            }
            catch { return null; }
        }

        // Skripte nach RDP Pilot 1.59 (Journalformat 2, Ergebnis je Versuch, Ablauf mit Rechteerhöhung), nur Namen
        // angepasst. Nur ASCII-Anführungszeichen. InstallLoader/RepairLoader stehen mit den Werten auf der Befehlszeile,
        // lesen $ScriptFile einmal in den Speicher, prüfen die SHA-256 gegen $ScriptHash und führen genau diese Bytes aus.
        // RestartScript: Neustarthelfer mit normalen Rechten für Update und Wiederherstellung mit Administratorrechten.
        private const string InstallLoader = @"
# alte Instanz an Prozess-ID und Startzeit erkennen (eine wiederverwendete Prozess-ID gilt nicht als alte Instanz)
function Test-OldInstance {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $p) { return $false }
  if ($ProcessStart -gt 0) { try { return ($p.StartTime.ToUniversalTime().Ticks -eq $ProcessStart) } catch { return $true } }
  return $true
}
# Steuerdatei aus dem Temp-Ordner (Startdatei, Bestaetigung, Freigabe): einmal oeffnen und aus demselben Stream
# hoechstens 257 Bytes lesen; mehr als 256 Bytes oder nicht lesbar = ungueltig ($null)
function Read-Small([string]$p) {
  try {
    $fs = [System.IO.File]::Open($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $b = New-Object byte[] 257
      $n = 0
      while ($n -lt 257) { $k = $fs.Read($b, $n, 257 - $n); if ($k -le 0) { break }; $n += $k }
    } finally { $fs.Dispose() }
    if ($n -gt 256) { return $null }
    return [System.Text.Encoding]::ASCII.GetString($b, 0, $n).Trim()
  } catch { return $null }
}
# Ergebnis im Arbeitsordner je Versuch als Letztes atomar veroeffentlichen: erst .teil, dann umbenennen
function Publish-WorkResult([string]$text) {
  $final = Join-Path $Work ('Update-' + $AttemptId + '.log')
  $tmp = $final + '.teil'
  Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  $text | Out-File -LiteralPath $tmp -Encoding utf8 -ErrorAction Stop
  Move-Item -LiteralPath $tmp -Destination $final -Force -ErrorAction Stop
}
# Ergebnis dieses Versuchs selbst anzeigen (fuer wiederhergestellte Fassungen ohne Versuchskennung): hoechstens 16 KB
# aus der eigenen Ergebnisdatei, ohne die gemeinsame Update.log anzufassen
function Show-Result {
  $text = $null
  try {
    $fs = [System.IO.File]::Open($Log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $b = New-Object byte[] 16385
      $n = 0
      while ($n -lt 16385) { $k = $fs.Read($b, $n, 16385 - $n); if ($k -le 0) { break }; $n += $k }
    } finally { $fs.Dispose() }
    if ($n -le 16384) { $text = (New-Object System.Text.UTF8Encoding($false)).GetString($b, 0, $n).TrimStart([char]0xFEFF).Trim() }
  } catch {}
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.' }
  $icon = 'Warning'
  if ($text -match '^OK ') { $icon = 'Information' }
  $text = $text -replace '^(OK|FEHLER|ZURUECK|OFFEN) ', ''
  try {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($text, 'Wake on LAN', 'OK', $icon)
  } catch {}
}
# Neustart mit --nach-update und Versuchskennung. Nach einer Wiederherstellung ($LegacyFallback) kann die EXE aelter
# als 1.73 sein und kennt keine Kennung: Dann normal starten und das Ergebnis dieses Versuchs hier anzeigen. Die
# gemeinsame Update.log der aelteren Fassungen wird nicht mehr beschrieben (parallele Versuche ueberschreiben sich nicht).
function Start-Program([bool]$tray) {
  $modern = $true
  if ($LegacyFallback) { try { $modern = ([Version][System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion) -ge [Version]'1.73' } catch { $modern = $false } }
  if ($modern -and $AttemptId) {
    $a = @('--nach-update', $AttemptId)
    if ($tray) { $a += '/tray' }
    Start-Process -FilePath $Exe -ArgumentList $a
    return
  }
  if ($tray) { Start-Process -FilePath $Exe -ArgumentList @('/tray') } else { Start-Process -FilePath $Exe }
  Show-Result
}
$ErrorActionPreference = 'Stop'
# geschuetzten Arbeitsordner anlegen: Besitzer Administratoren, schreiben nur SYSTEM und Administratoren, lesen angemeldete Benutzer
function New-WorkFolder {
  $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
  $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
  $root = [Environment]::GetFolderPath('CommonApplicationData')
  if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('WakeOnLan-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
  $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
  $none = [System.Security.AccessControl.PropagationFlags]::None
  $sec = New-Object System.Security.AccessControl.DirectorySecurity
  $sec.SetAccessRuleProtection($true, $false)
  $sec.SetOwner($admins)
  foreach ($sid in @($admins, $system)) {
    $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, $none, 'Allow')))
  }
  $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $none, 'Allow')))
  if (Test-Path -LiteralPath $Work) { throw 'Der Arbeitsordner fuer das Update existiert bereits.' }
  [void][System.IO.Directory]::CreateDirectory($Work, $sec)
  $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
  if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner fuer das Update ist nicht geschuetzt.' }
}
$code = $null
try {
  # hoechstens 256 KB lesen; waehrend des Lesens darf niemand schreiben (FileShare.Read)
  $fs = [System.IO.File]::Open($ScriptFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt 262144) { throw 'Das Update-Skript ist zu gross.' }
    $b = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $b.Length) { $k = $fs.Read($b, $n, $b.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $b.Length) { throw 'Das Update-Skript ist unvollstaendig.' }
  } finally { $fs.Dispose() }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $h = [System.BitConverter]::ToString($sha.ComputeHash($b)).Replace('-', '')
  $sha.Dispose()
  if ($h -cne $ScriptHash) { throw 'Das Update-Skript wurde veraendert.' }
  $code = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($b)
} catch {
  # eindeutiger Status: Skript nicht geladen, nichts veraendert (FEHLER = bisherige Version darf wieder starten)
  $msg = 'FEHLER Update-Skript nicht geladen, nichts veraendert: ' + $_.Exception.Message
  if ($Elevated) {
    # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner; den Neustart macht der nicht erhoehte Helfer
    try { New-WorkFolder; Publish-WorkResult $msg } catch {}
  } else {
    try { $msg | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    # nach der Frist erneut pruefen: laeuft die alte Instanz noch, nicht starten (der Start wuerde ohnehin abgefangen)
    if (Test-OldInstance) {
      try { ($msg + ' Wake on LAN wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    }
    elseif ($Tray) { Start-Program $true }
    else { Start-Program $false }
  }
  exit 1
}
. ([scriptblock]::Create($code))
";

        private const string InstallScript = @"
$ErrorActionPreference = 'Stop'
# Dateien begrenzt lesen bzw. kopieren; waehrenddessen darf niemand schreiben (FileShare.Read). Bei Ueberschreitung Abbruch, Teilkopie weg.
function Read-Limited([string]$path, [long]$max) {
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $path) }
    $buf = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $buf.Length) { $k = $fs.Read($buf, $n, $buf.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $buf.Length) { throw ('Die Datei ist unvollstaendig: ' + $path) }
    return ,$buf
  } finally { $fs.Dispose() }
}
function Copy-Limited([string]$src, [string]$dst, [long]$max) {
  $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  $out = $null
  try {
    if ($in.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
    $out = [System.IO.File]::Open($dst, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $buf = New-Object byte[] 65536
    $total = 0
    while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
      $total += $k
      if ($total -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
      $out.Write($buf, 0, $k)
    }
    $out.Dispose()
    $out = $null
  } catch {
    if ($out) { $out.Dispose(); $out = $null; Remove-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue }
    throw
  } finally {
    if ($out) { $out.Dispose() }
    $in.Dispose()
  }
}
# ZIP hoechstens so gross wie heruntergeladen und nie ueber 64 MB
$zipMax = [Math]::Min([long]$ZipSize, [long]67108864)
$target = Split-Path $Exe -Parent
$workReady = -not $Elevated
$result = $null
$lock = $null
$haveLock = $false
$noRestart = $false
$archive = $null
$zipStream = $null
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  # tatsaechliches Token pruefen: erhoeht nur, wenn das Programm es so gestartet hat (geschuetzter Arbeitsordner)
  $isAdmin = (New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin -and -not $Elevated) { throw 'Das Update laeuft unerwartet mit Administratorrechten.' }
  # erst nach bestaetigtem Ende der alten Instanz Dateien aendern
  $deadline = (Get-Date).AddSeconds(60)
  while (Test-OldInstance) {
    if ((Get-Date) -gt $deadline) { throw 'Wake on LAN wurde nicht beendet.' }
    Start-Sleep -Milliseconds 250
  }
  Start-Sleep -Milliseconds 500
  # erhoeht: nur mit ausdruecklicher Freigabe fuer genau diesen Versuch. Wake on LAN erteilt sie erst, wenn der
  # Neustarthelfer bereit ist und die Startbestaetigung geschrieben wurde; ohne Freigabe nichts veraendern.
  if ($Elevated) {
    $released = $false
    $released = ((Read-Small $ReleaseFile) -ceq $ReleaseId)
    if (-not $released) { throw 'Das Update wurde nicht freigegeben.' }
  }
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('WakeOnLan-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    # alte Arbeitsordner frueherer Updates entfernen: nur eigene (Besitzer Administratoren), keine Verknuepfungen,
    # und nur nachweislich inaktive: gleiches Ziel (diese Sperre halten wir) oder anderes Ziel ohne gehaltene Sperre
    # und mindestens eine Stunde unveraendert; Ordner ohne Zielkennung (aeltere Versionen) nur nach einer Stunde
    Get-ChildItem -LiteralPath $root -Directory -Filter 'WakeOnLan-Update-*' -Force -ErrorAction SilentlyContinue | ForEach-Object {
      try {
        if (-not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
          $o = (Get-Acl -LiteralPath $_.FullName).GetOwner([System.Security.Principal.SecurityIdentifier])
          if ($o -eq $admins -or $o -eq $system) {
            $inactive = $false
            if ($_.Name -match '^WakeOnLan-Update-([0-9A-F]{24})-[0-9a-f]{32}$') {
              if ($Matches[1] -ceq $TargetId) { $inactive = $true }
              else {
                $held = $true
                try { $other = $null; $held = [System.Threading.Mutex]::TryOpenExisting('Global\WakeOnLan-Update-' + $Matches[1], [ref]$other); if ($other) { $other.Dispose() } } catch { $held = $true }
                $inactive = (-not $held) -and ($_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1))
              }
            } else {
              $inactive = $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1)
            }
            if (Test-Path -LiteralPath (Join-Path $_.FullName 'Wiederherstellung-offen')) { $inactive = $false }
            if ($inactive) { [System.IO.Directory]::Delete($_.FullName, $true) }
          }
        }
      } catch {}
    }
    # Arbeitsordner gleich mit Rechten anlegen (New-WorkFolder steht im Lader)
    New-WorkFolder
    $workReady = $true
    $zipWork = Join-Path $Work 'Update.zip'
    $manifestWork = Join-Path $Work 'Update.manifest'
    Copy-Limited $Zip $zipWork $zipMax
    Copy-Limited $Manifest $manifestWork 8192
  } else {
    $zipWork = $Zip
    $manifestWork = $Manifest
  }
  # Signatur des Manifests selbst pruefen (eigene Herausgeberpruefung, an der Kopie, die verwendet wird)
  $bytes = Read-Limited $manifestWork 8192
  $marker = [System.Text.Encoding]::ASCII.GetBytes([string][char]10 + 'Signatur=')
  $at = -1
  for ($i = 0; $i -le $bytes.Length - $marker.Length -and $at -lt 0; $i++) {
    $j = 0
    while ($j -lt $marker.Length -and $bytes[$i + $j] -eq $marker[$j]) { $j++ }
    if ($j -eq $marker.Length) { $at = $i }
  }
  if ($at -lt 0) { throw 'Im Manifest fehlt die Signatur.' }
  $signed = New-Object byte[] ($at + 1)
  [System.Array]::Copy($bytes, $signed, $at + 1)
  $sig = [System.Convert]::FromBase64String([System.Text.Encoding]::ASCII.GetString($bytes, $at + $marker.Length, $bytes.Length - $at - $marker.Length).Trim())
  $lines = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($signed).Split([char]10)
  $names = @('Programm', 'Version', 'Datei', 'SHA256', 'Schluessel', 'Erstellt')
  if ($lines.Count -ne 7 -or $lines[6] -ne '') { throw 'Das Manifest hat nicht den erwarteten Aufbau.' }
  $mv = @{}
  for ($i = 0; $i -lt 6; $i++) {
    $p = $names[$i] + '='
    if (-not $lines[$i].StartsWith($p, [System.StringComparison]::Ordinal)) { throw ('Im Manifest fehlt ' + $names[$i] + '.') }
    $mv[$names[$i]] = $lines[$i].Substring($p.Length)
  }
  if ($mv['Schluessel'] -ceq 'Haupt') { $keyXml = $MainKey }
  elseif ($mv['Schluessel'] -ceq 'Reserve') { $keyXml = $ReserveKey }
  else { throw 'Unbekannter Schluessel im Manifest.' }
  $csp = New-Object System.Security.Cryptography.CspParameters -ArgumentList 24
  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList $csp
  $rsa.PersistKeyInCsp = $false
  $rsa.FromXmlString($keyXml)
  $valid = $rsa.VerifyData($signed, 'SHA256', $sig)
  $rsa.Clear()
  if (-not $valid) { throw 'Die Signatur des Updates ist ungueltig.' }
  if ($mv['Programm'] -cne 'Wake on LAN' -or $mv['Version'] -cne $Version -or $mv['Datei'] -cne $AssetName) { throw 'Das Manifest passt nicht zu diesem Update.' }
  if ($mv['SHA256'] -cnotmatch '^[0-9a-f]{64}$') { throw 'Die Pruefsumme im Manifest ist ungueltig.' }
  $expected = $mv['SHA256'].ToUpperInvariant()
  if ($expected -ne $Sha256) { throw 'Die Pruefsumme im Manifest weicht von der Pruefung im Programm ab.' }
  # ZIP einmal oeffnen (waehrenddessen darf niemand schreiben oder loeschen), Pruefsumme ueber genau diesen Stream
  # berechnen und denselben Stream entpacken; er bleibt bis nach dem Austausch offen
  $zipStream = [System.IO.File]::Open($zipWork, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  if ($zipStream.Length -gt $zipMax) { throw 'Das Update ist zu gross.' }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $hash = [System.BitConverter]::ToString($sha.ComputeHash($zipStream)).Replace('-', '')
  $sha.Dispose()
  if ($hash -ne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $zipStream.Position = 0
  Add-Type -AssemblyName System.IO.Compression
  $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
  # nur bekannte Eintraege (feste Namen im Stammordner), je Datei begrenzt; geschrieben wird direkt aus dem Archiv ins Ziel
  function Find-Entry([string]$name, [long]$max) {
    $hits = @($archive.Entries | Where-Object { $_.FullName -eq $name })
    if ($hits.Count -gt 1) { throw ('Im ZIP steht ' + $name + ' mehrfach.') }
    if ($hits.Count -eq 0) { return $null }
    if ($hits[0].Length -gt $max) { throw ('Im ZIP ist ' + $name + ' zu gross.') }
    return $hits[0]
  }
  # erst vollstaendig in eine Hilfsdatei im Zielordner schreiben, auf den Datentraeger bringen und pruefen,
  # dann das Ziel in einem Schritt ersetzen (File.Replace bzw. File.Move): Das Ziel ist nie halb geschrieben
  function Write-Entry($entry, [string]$dst, [string]$tmp, [long]$max) {
    try {
      $in = $entry.Open()
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
          $buf = New-Object byte[] 65536
          $total = 0
          while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
            $total += $k
            if ($total -gt $max) { throw ('Im ZIP ist ' + $entry.FullName + ' zu gross.') }
            $out.Write($buf, 0, $k)
          }
          $out.Flush($true)
        } finally { $out.Dispose() }
      } finally { $in.Dispose() }
      if ($total -ne $entry.Length) { throw ('Im ZIP ist ' + $entry.FullName + ' unvollstaendig.') }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  # Ruecksicherung ebenso: Sicherung erst vollstaendig in eine Hilfsdatei im Zielordner, dann das Ziel in einem Schritt ersetzen
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      # [NullString]::Value statt $null: PowerShell macht aus $null sonst einen leeren Text (ungueltiger Pfad)
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $exeEntry = Find-Entry $ExeName 33554432
  if (-not $exeEntry) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($exeEntry, $Exe, 33554432))
  $manualEntry = Find-Entry $ManualName 1048576
  if ($manualEntry) { $pairs += ,@($manualEntry, (Join-Path $target $ManualName), 1048576) }
  # unter der Sperre erneut nach offenen Wiederherstellungen fuer dieses Ziel suchen (ein anderer Versuch kann seit
  # der Vorpruefung im Programm abgebrochen sein); Treffer oder fehlgeschlagene Pruefung = nichts austauschen
  $open = $null
  try {
    $roots = @(@($TempRoot, ($TargetId + '-*')), @([Environment]::GetFolderPath('CommonApplicationData'), ('WakeOnLan-Update-' + $TargetId + '-*')))
    foreach ($r in $roots) {
      if (Test-Path -LiteralPath $r[0]) {
        foreach ($d in @(Get-ChildItem -LiteralPath $r[0] -Directory -Filter $r[1] -Force -ErrorAction Stop)) {
          if ($d.FullName -ne $Work -and (Test-Path -LiteralPath (Join-Path $d.FullName 'Wiederherstellung-offen'))) { $open = $d.FullName }
        }
      }
    }
  } catch { throw ('Offene Wiederherstellungen konnten nicht geprueft werden: ' + $_.Exception.Message) }
  if ($open) { throw ('Eine fruehere Wiederherstellung ist noch offen (' + $open + ').') }
  # Wiederherstellungsdaten dauerhaft schreiben (WriteThrough + Flush auf den Datentraeger), bevor die zugehoerige
  # Programmdatei ausgetauscht wird: Nach einem Stromausfall darf das Journal nie hinter dem Dateistand zurueckliegen.
  function Write-Durable([string]$path, [string[]]$lines, [bool]$append) {
    if ($append) { $mode = [System.IO.FileMode]::Append } else { $mode = [System.IO.FileMode]::CreateNew }
    $fs = New-Object System.IO.FileStream($path, $mode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read, 4096, [System.IO.FileOptions]::WriteThrough)
    try {
      $nl = [string][char]13 + [char]10
      $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes(($lines -join $nl) + $nl)
      $fs.Write($bytes, 0, $bytes.Length)
      $fs.Flush($true)
    } finally { $fs.Dispose() }
  }
  function Copy-Durable([string]$src, [string]$dst) {
    $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $out = New-Object System.IO.FileStream($dst, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 81920, [System.IO.FileOptions]::WriteThrough)
      try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
      if ((Get-Item -LiteralPath $dst).Length -ne $in.Length) { throw ('Sicherung von ' + $src + ' unvollstaendig.') }
    } finally { $in.Dispose() }
  }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  # Journal zuerst (Format 2: geplante Dateien, dann ""Bereit""), dann der Status: Ein Status ohne vollstaendiges Journal gilt als beschaedigt.
  # Wiederherstellungsjournal: vor jedem Austausch Zielpfad, Sicherungsdatei und ob das Ziel vorher existierte
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  $plan = @()
  $k = 0
  foreach ($p in $pairs) { $k++; $plan += ('Geplant ' + $k + ': Ziel: ' + $p[1]) }
  $journalHead = @((('Wake-on-LAN-Update ' + $Version + ' - Wiederherstellungsjournal'), 'Journalformat 2',
    'Jede Zeile ""Vorher"" gilt als moeglicherweise ueberschrieben, auch ohne folgende Zeile ""Ersetzt"" (Abbruch waehrend des Kopierens).',
    'Wiederherstellen fuer jede Zeile ""Vorher"": bei ""Sicherung"" die Sicherungsdatei auf das Ziel zurueckkopieren (sie ist vollstaendig), bei ""Neu"" die Zieldatei loeschen, falls vorhanden.',
    '""Ersetzt n"" bestaetigt nur, dass der Austausch dieser Datei abgeschlossen wurde. Eine uebrig gebliebene ""Hilfsdatei"" kann geloescht werden.',
    '') + $plan + @('Bereit'))
  Write-Durable $journal $journalHead $false
  # Status vor dem ersten Ueberschreiben dauerhaft anlegen: bricht der Austausch ab (auch hart beendet), bleibt der
  # Arbeitsordner geschuetzt und neue Updates gesperrt. Laesst er sich nicht schreiben, wird nichts ausgetauscht.
  $pending = Join-Path $Work 'Wiederherstellung-offen'
  Write-Durable $pending @('Journalformat 2', $alt) $false
  if (-not (Test-Path -LiteralPath $pending)) { throw 'Statusdatei konnte nicht angelegt werden.' }
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $tmp = Join-Path (Split-Path $p[1] -Parent) ('.' + (Split-Path $p[1] -Leaf) + '.update-' + [Guid]::NewGuid().ToString('N'))
      $had = Test-Path -LiteralPath $p[1]
      if ($had) {
        # erst vollstaendig unter Hilfsnamen kopieren, dann umbenennen: vorhandene Sicherungsdatei = vollstaendig
        Remove-Item -LiteralPath ($bak + '.teil') -Force -ErrorAction SilentlyContinue
        Copy-Durable $p[1] ($bak + '.teil')
        Move-Item -LiteralPath ($bak + '.teil') -Destination $bak -Force
        $bakHash = (Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash
        $line = 'Vorher ' + $i + ': Sicherung | Ziel: ' + $p[1] + ' | Sicherungsdatei: ' + $bak + ' | SHA256: ' + $bakHash + ' | Hilfsdatei: ' + $tmp
      } else {
        $line = 'Vorher ' + $i + ': Neu | Ziel: ' + $p[1] + ' | Hilfsdatei: ' + $tmp
      }
      Write-Durable $journal @($line) $true
      $done += ,@($p[1], $bak, $had)
      Write-Entry $p[0] $p[1] $tmp $p[2]
      Write-Durable $journal @('Ersetzt ' + $i) $true
    }
  } catch {
    # jede Datei einzeln zuruecksetzen, Fehler sammeln; bei unvollstaendiger Ruecksicherung Sicherung behalten und nicht neu starten
    $err = $_.Exception.Message
    $failed = @()
    foreach ($d in $done) {
      try {
        if ($d[2]) { Restore-File $d[1] $d[0] }
        else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction Stop }
      } catch { $failed += $d[0] }
    }
    if ($failed.Count -gt 0) {
      # Status bleibt stehen: Arbeitsordner wird nie automatisch bereinigt, neue Updates sind gesperrt
      $noRestart = $true
      throw ('Ruecksicherung unvollstaendig (' + ($failed -join ', ') + '). Die alten Dateien liegen in ' + $alt + ', die Zuordnung steht in ' + $journal + '. Fehler: ' + $err)
    }
    # Ruecksicherung vollstaendig bestaetigt: Status entfernen; klappt das nicht, sicherheitshalber nicht neu starten
    try { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop } } catch { $noRestart = $true }
    throw
  }
  # Installation vollstaendig: erst Status entfernen, dann die Rueckfallkopien
  try { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop }
  catch { $noRestart = $true; throw ('Update installiert, aber der Status in ' + $Work + ' konnte nicht entfernt werden.') }
  Remove-Item -LiteralPath $alt -Recurse -Force -ErrorAction SilentlyContinue
  $archive.Dispose()
  $zipStream.Dispose()
  Remove-Item -LiteralPath $zipWork -Force -ErrorAction SilentlyContinue
  if ($Elevated) { Remove-Item -LiteralPath $manifestWork -Force -ErrorAction SilentlyContinue }
  $result = 'OK ' + $Version
} catch {
  if ($archive) { try { $archive.Dispose() } catch {} }
  if ($zipStream) { try { $zipStream.Dispose() } catch {} }
  # Abschlussstatus: FEHLER = nichts veraendert bzw. vollstaendig zurueckgesetzt (Neustart erlaubt),
  # OFFEN = Ruecksicherung unvollstaendig (kein Neustart, Sicherung bleibt)
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($Elevated) {
  # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner schreiben; Neustart macht der nicht erhoehte Prozess
  if ($workReady) { try { Publish-WorkResult $result } catch {} }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if (-not $noRestart) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated -and -not $noRestart) {
  # nur starten, wenn die alte Instanz beendet ist (z. B. nicht nach Ablauf der Wartefrist)
  if (Test-OldInstance) {
    try { ($result + ' Wake on LAN wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  }
  elseif ($Tray) { Start-Program $true }
  else { Start-Program $false }
}
";

        private const string RepairLoader = @"
# alte Instanz an Prozess-ID und Startzeit erkennen (eine wiederverwendete Prozess-ID gilt nicht als alte Instanz)
function Test-OldInstance {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $p) { return $false }
  if ($ProcessStart -gt 0) { try { return ($p.StartTime.ToUniversalTime().Ticks -eq $ProcessStart) } catch { return $true } }
  return $true
}
# Steuerdatei aus dem Temp-Ordner (Startdatei, Bestaetigung, Freigabe): einmal oeffnen und aus demselben Stream
# hoechstens 257 Bytes lesen; mehr als 256 Bytes oder nicht lesbar = ungueltig ($null)
function Read-Small([string]$p) {
  try {
    $fs = [System.IO.File]::Open($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $b = New-Object byte[] 257
      $n = 0
      while ($n -lt 257) { $k = $fs.Read($b, $n, 257 - $n); if ($k -le 0) { break }; $n += $k }
    } finally { $fs.Dispose() }
    if ($n -gt 256) { return $null }
    return [System.Text.Encoding]::ASCII.GetString($b, 0, $n).Trim()
  } catch { return $null }
}
# Ergebnis im Arbeitsordner je Versuch als Letztes atomar veroeffentlichen: erst .teil, dann umbenennen
function Publish-WorkResult([string]$text) {
  $final = Join-Path $Work ('Update-' + $AttemptId + '.log')
  $tmp = $final + '.teil'
  Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  $text | Out-File -LiteralPath $tmp -Encoding utf8 -ErrorAction Stop
  Move-Item -LiteralPath $tmp -Destination $final -Force -ErrorAction Stop
}
# Ergebnis dieses Versuchs selbst anzeigen (fuer wiederhergestellte Fassungen ohne Versuchskennung): hoechstens 16 KB
# aus der eigenen Ergebnisdatei, ohne die gemeinsame Update.log anzufassen
function Show-Result {
  $text = $null
  try {
    $fs = [System.IO.File]::Open($Log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $b = New-Object byte[] 16385
      $n = 0
      while ($n -lt 16385) { $k = $fs.Read($b, $n, 16385 - $n); if ($k -le 0) { break }; $n += $k }
    } finally { $fs.Dispose() }
    if ($n -le 16384) { $text = (New-Object System.Text.UTF8Encoding($false)).GetString($b, 0, $n).TrimStart([char]0xFEFF).Trim() }
  } catch {}
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.' }
  $icon = 'Warning'
  if ($text -match '^OK ') { $icon = 'Information' }
  $text = $text -replace '^(OK|FEHLER|ZURUECK|OFFEN) ', ''
  try {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($text, 'Wake on LAN', 'OK', $icon)
  } catch {}
}
# Neustart mit --nach-update und Versuchskennung. Nach einer Wiederherstellung ($LegacyFallback) kann die EXE aelter
# als 1.73 sein und kennt keine Kennung: Dann normal starten und das Ergebnis dieses Versuchs hier anzeigen. Die
# gemeinsame Update.log der aelteren Fassungen wird nicht mehr beschrieben (parallele Versuche ueberschreiben sich nicht).
function Start-Program([bool]$tray) {
  $modern = $true
  if ($LegacyFallback) { try { $modern = ([Version][System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion) -ge [Version]'1.73' } catch { $modern = $false } }
  if ($modern -and $AttemptId) {
    $a = @('--nach-update', $AttemptId)
    if ($tray) { $a += '/tray' }
    Start-Process -FilePath $Exe -ArgumentList $a
    return
  }
  if ($tray) { Start-Process -FilePath $Exe -ArgumentList @('/tray') } else { Start-Process -FilePath $Exe }
  Show-Result
}
$code = $null
try {
  # hoechstens 256 KB lesen; waehrend des Lesens darf niemand schreiben (FileShare.Read)
  $fs = [System.IO.File]::Open($ScriptFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt 262144) { throw 'Das Reparatur-Skript ist zu gross.' }
    $b = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $b.Length) { $k = $fs.Read($b, $n, $b.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $b.Length) { throw 'Das Reparatur-Skript ist unvollstaendig.' }
  } finally { $fs.Dispose() }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $h = [System.BitConverter]::ToString($sha.ComputeHash($b)).Replace('-', '')
  $sha.Dispose()
  if ($h -cne $ScriptHash) { throw 'Das Reparatur-Skript wurde veraendert.' }
  $code = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($b)
} catch {
  # nichts veraendert. Erhoeht: ohne Ergebnisdatei beenden (Neustarthelfer: Ergebnis unbekannt, kein Neustart).
  # Ohne Adminrechte: FEHLER ins Protokoll und die bisherige Version wieder starten.
  if (-not $Elevated) {
    $msg = 'FEHLER Reparatur-Skript nicht geladen, nichts veraendert: ' + $_.Exception.Message
    try { $msg | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-OldInstance)) { Start-Program ([bool]$Tray) }
  }
  exit 1
}
. ([scriptblock]::Create($code))
";

        private const string RepairScript = @"
$ErrorActionPreference = 'Stop'
$result = $null
$noRestart = $false
$haveLock = $false
$lock = $null
$workTrusted = $false
$target = Split-Path $Exe -Parent
try {
  $deadline = (Get-Date).AddSeconds(60)
  while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
  if (Test-OldInstance) { throw 'Wake on LAN wurde nicht beendet.' }
  # im Projektordner (Erstellen.bat neben der EXE) nie etwas zuruecksetzen
  if (Test-Path -LiteralPath (Join-Path $target 'Erstellen.bat')) { throw 'Im Projektordner wird nichts zurueckgesetzt.' }
  # tatsaechliches Token pruefen: erhoeht nur, wenn das Programm es so gestartet hat
  $isAdmin = (New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin -and -not $Elevated) { throw 'Die Wiederherstellung laeuft unerwartet mit Administratorrechten.' }
  # erhoeht: nur mit ausdruecklicher Freigabe fuer genau diesen Versuch. Wake on LAN erteilt sie erst, wenn der
  # Neustarthelfer bereit ist und die Startbestaetigung geschrieben wurde; ohne Freigabe nichts veraendern.
  if ($Elevated) {
    $released = $false
    $released = ((Read-Small $ReleaseFile) -ceq $ReleaseId)
    if (-not $released) { throw 'Die Wiederherstellung wurde nicht freigegeben.' }
  }
  # dieselbe Sperre wie das Update: nie zwei Wiederherstellungen oder Update und Wiederherstellung gleichzeitig
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  # unter der Sperre erneut pruefen: Eine andere Wiederherstellung kann inzwischen fertig sein
  if (-not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen'))) { throw 'Es ist keine Wiederherstellung mehr offen.' }
  $item = Get-Item -LiteralPath $Work -Force
  if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Unerwarteter Arbeitsordner.' }
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('WakeOnLan-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner ist nicht geschuetzt.' }
  }
  # Erhoeht: Arbeitsordner, Journal, Ordner Alt und jede Sicherung muessen den Administratoren oder SYSTEM gehoeren,
  # duerfen keine Umleitung sein und duerfen fuer niemand anderen beschreibbar sein (wirksame ACL, ohne reine
  # Vererbungseintraege). Sonst koennte ein Prozess ohne Adminrechte untergeschobene Dateien zuruecksetzen lassen.
  $trusted = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
  $writeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
  function Assert-Protected([string]$p) {
    if (-not $Elevated) { return }
    $it = Get-Item -LiteralPath $p -Force
    if ($it.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw ('Unerwartete Umleitung: ' + $p) }
    $acl = Get-Acl -LiteralPath $p
    if ($trusted -notcontains $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value) { throw ('Nicht geschuetzt (Besitzer): ' + $p) }
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
      if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
      if ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
      if ($trusted -contains $rule.IdentityReference.Value) { continue }
      if (([int64]$rule.FileSystemRights -band $writeMask) -ne 0) { throw ('Nicht geschuetzt (Schreibrecht fuer ' + $rule.IdentityReference.Value + '): ' + $p) }
    }
  }
  Assert-Protected $Work
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $allowed = @($Exe, (Join-Path $target $ManualName))
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  # Journal vollstaendig und streng pruefen, bevor irgendetwas zurueckgesetzt wird (dieselben Regeln wie im Programm,
  # ReadMarker/ReadJournal). Passt etwas nicht: Stand ungeklaert, Merker bleibt, kein Neustart (OFFEN).
  # Drei Formate: Format 2 (Merker ""Journalformat 2"" + Alt-Ordner; Formatzeile, ""Geplant 1..n"", ""Bereit"", Hash,
  # ""Ersetzt"" Pflicht), Format Vorstufe (in Wake on LAN nie veroeffentlicht; Merker nur Alt-Ordner; ""Bereit"", Hash, ""Ersetzt"" optional), Format 1.72
  # (Merker nur Alt-Ordner; ohne ""Bereit"" und Hash). Unbekannter Merker oder fehlendes Journal: ungeklaert.
  $noRestart = $true
  $markerFile = Join-Path $Work 'Wiederherstellung-offen'
  Assert-Protected $markerFile
  if ((Get-Item -LiteralPath $markerFile).Length -gt 4096) { throw 'Der Status ist zu gross.' }
  $altDir = Join-Path $Work 'Alt'
  $mlines = @([System.IO.File]::ReadAllLines($markerFile) | ForEach-Object { $_.Trim().TrimStart([char]0xFEFF) } | Where-Object { $_.Length -gt 0 })
  if ($mlines.Count -eq 2 -and $mlines[0] -ceq 'Journalformat 2' -and $mlines[1] -eq $altDir) { $format2 = $true }
  elseif ($mlines.Count -eq 1 -and $mlines[0] -eq $altDir) { $format2 = $false }
  else { throw 'Der Status ist nicht lesbar.' }
  $entries = @()
  $keywords = '^(Bereit$|Journalformat 2$|Geplant |Vorher |Ersetzt )'
  if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) { throw 'Das Wiederherstellungsjournal fehlt.' }
  Assert-Protected $journal
  if ((Get-Item -LiteralPath $journal).Length -gt 65536) { throw 'Das Wiederherstellungsjournal ist zu gross.' }
  if (Test-Path -LiteralPath $altDir) { Assert-Protected $altDir }
  $workTrusted = $true
  $lines = [System.IO.File]::ReadAllLines($journal)
  if ($lines.Count -lt 2 -or -not $lines[0].StartsWith('Wake-on-LAN-Update ') -or -not $lines[0].EndsWith(' - Wiederherstellungsjournal')) { throw 'Das Journal ist nicht lesbar.' }
  $format155 = (-not $format2) -and ($lines -ccontains 'Bereit')
  $withHash = $format2 -or $format155
  # Altformat 1.72 ohne Pruefsummen: mit Administratorrechten nichts zurueckspielen (Sicherungen nicht pruefbar),
  # sondern abbrechen; Wake on LAN verweist auf die Neuinstallation aus dem signierten Paket
  if ($Elevated -and -not $withHash) { throw 'Altes Journal ohne Pruefsummen: mit Administratorrechten wird nicht zurueckgesetzt, bitte neu installieren.' }
  $planned = @()
  $i = 1
  if ($format2) {
    if ($lines[1] -cne 'Journalformat 2') { throw 'Das Journal ist nicht lesbar.' }
    $i = 2
  }
  if ($format2) { $first = 'Geplant ' } elseif ($format155) { $first = 'Bereit' } else { $first = 'Vorher ' }
  while ($i -lt $lines.Count -and -not $lines[$i].StartsWith($first)) { if ($lines[$i] -cmatch $keywords) { throw 'Das Journal ist nicht lesbar.' }; $i++ }
  if ($format2) {
    while ($i -lt $lines.Count -and $lines[$i].StartsWith('Geplant ')) {
      if ($lines[$i] -cnotmatch '^Geplant (\d+): Ziel: (.+)$') { throw 'Das Journal ist nicht lesbar.' }
      if ($Matches[1] -ne [string]($planned.Count + 1) -or $allowed -notcontains $Matches[2] -or $planned -contains $Matches[2]) { throw 'Unerwarteter Eintrag im Journal.' }
      $planned += $Matches[2]
      $i++
    }
    if ($planned.Count -eq 0) { throw 'Das Journal ist unvollstaendig.' }
  }
  if ($withHash) {
    if ($i -ge $lines.Count -or $lines[$i] -cne 'Bereit') { throw 'Das Journal ist unvollstaendig.' }
    $i++
  }
  $replacedLast = $true
    for (; $i -lt $lines.Count; $i++) {
      $line = $lines[$i]
      if ($line -cmatch '^Ersetzt (\d+)$') {
        if ($entries.Count -eq 0 -or $replacedLast -or $Matches[1] -ne $entries[$entries.Count - 1].N) { throw 'Das Journal ist nicht lesbar.' }
        $replacedLast = $true
        continue
      }
      if ($withHash) { $ok = $line -cmatch '^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+? \| SHA256: ([0-9A-F]{64}))? \| Hilfsdatei: (.+)$' }
      else { $ok = $line -cmatch '^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)()(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$' }
      if (-not $ok) { throw 'Das Journal ist nicht lesbar.' }
      $e = @{ N = $Matches[1]; Kind = $Matches[2]; Dst = $Matches[3]; Hash = [string]$Matches[4]; Tmp = $Matches[5] }
      if ($e.N -ne [string]($entries.Count + 1) -or ($format2 -and $entries.Count -gt 0 -and -not $replacedLast) -or $allowed -notcontains $e.Dst) { throw 'Unerwarteter Eintrag im Journal.' }
      if ($format2 -and ($entries.Count -ge $planned.Count -or $planned[$entries.Count] -ne $e.Dst)) { throw 'Unerwarteter Eintrag im Journal.' }
      if ($withHash) {
        if ($e.Kind -eq 'Sicherung' -and $e.Hash.Length -eq 0) { throw 'Das Journal ist nicht lesbar.' }
        if ($e.Kind -eq 'Neu' -and $line.Contains(' | Sicherungsdatei: ')) { throw 'Das Journal ist nicht lesbar.' }
      }
      if ($e.Kind -eq 'Sicherung') {
        $alt = Join-Path $Work 'Alt'
        $e.Bak = Join-Path $alt $e.N
        # Stromausfall zwischen Kopie und Umbenennen: die vollstaendige .teil-Datei nur mit passender Pruefsumme nehmen
        if (-not (Test-Path -LiteralPath $e.Bak -PathType Leaf) -and $e.Hash.Length -gt 0 -and (Test-Path -LiteralPath ($e.Bak + '.teil') -PathType Leaf)) { $e.Bak = $e.Bak + '.teil' }
        if (-not (Test-Path -LiteralPath $e.Bak -PathType Leaf)) { throw ('Sicherung fehlt: ' + $e.Bak) }
        Assert-Protected $alt
        Assert-Protected $e.Bak
        $bakHash = (Get-FileHash -LiteralPath $e.Bak -Algorithm SHA256).Hash
        if ($e.Hash.Length -gt 0 -and $bakHash -ne $e.Hash) { throw ('Sicherung veraendert: ' + $e.Bak) }
        $e.Hash = $bakHash
      }
      foreach ($x in $entries) { if ($x.Dst -eq $e.Dst) { throw 'Das Journal nennt ein Ziel doppelt.' } }
      $entries += ,$e
      $replacedLast = $false
  }
  # ab hier kann schon eine Datei zurueckgesetzt sein: jeder Abbruch ist OFFEN (Merker bleibt, kein Neustart)
  $failed = @()
  foreach ($e in $entries) {
    $kind = $e.Kind; $dst = $e.Dst; $tmp = $e.Tmp
    try {
      if ($kind -eq 'Sicherung') {
        $bak = $e.Bak
        $same = (Test-Path -LiteralPath $dst) -and ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -eq $e.Hash)
        if (-not $same) { Restore-File $bak $dst }
      } elseif (Test-Path -LiteralPath $dst) {
        Remove-Item -LiteralPath $dst -Force
      }
      if ((Split-Path $tmp -Parent) -eq $target -and (Split-Path $tmp -Leaf) -like ('.' + (Split-Path $dst -Leaf) + '.update-*')) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      }
    } catch { $failed += ($dst + ': ' + $_.Exception.Message) }
  }
  if ($failed.Count -gt 0) { $noRestart = $true; throw ('Wiederherstellung unvollstaendig (' + ($failed -join '; ') + ').') }
  Remove-Item -LiteralPath (Join-Path $Work 'Wiederherstellung-offen') -Force
  # vollstaendig zurueckgesetzt und Merker entfernt: Neustart erlaubt
  $noRestart = $false
  $result = 'ZURUECK Die vorherige Version wurde wiederhergestellt.'
} catch {
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if ($Elevated) {
  # Ergebnis nur in einen gepruefen Arbeitsordner; sonst ohne Ergebnisdatei beenden (Neustarthelfer: Ergebnis unbekannt,
  # kein Neustart)
  if (-not $workTrusted) { if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }; exit 1 }
  try { Publish-WorkResult $result } catch {}
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if ($result.StartsWith('ZURUECK')) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
  if (-not $noRestart -and -not (Test-OldInstance)) {
    if ($Tray) { Start-Program $true }
    else { Start-Program $false }
  }
}
";

        private const string RestartScript = @"
# alte Instanz an Prozess-ID und Startzeit erkennen (eine wiederverwendete Prozess-ID gilt nicht als alte Instanz)
function Test-OldInstance {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $p) { return $false }
  if ($ProcessStart -gt 0) { try { return ($p.StartTime.ToUniversalTime().Ticks -eq $ProcessStart) } catch { return $true } }
  return $true
}
# Steuerdatei aus dem Temp-Ordner (Startdatei, Bestaetigung, Freigabe): einmal oeffnen und aus demselben Stream
# hoechstens 257 Bytes lesen; mehr als 256 Bytes oder nicht lesbar = ungueltig ($null)
function Read-Small([string]$p) {
  try {
    $fs = [System.IO.File]::Open($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $b = New-Object byte[] 257
      $n = 0
      while ($n -lt 257) { $k = $fs.Read($b, $n, 257 - $n); if ($k -le 0) { break }; $n += $k }
    } finally { $fs.Dispose() }
    if ($n -gt 256) { return $null }
    return [System.Text.Encoding]::ASCII.GetString($b, 0, $n).Trim()
  } catch { return $null }
}
# Ergebnis im Arbeitsordner je Versuch als Letztes atomar veroeffentlichen: erst .teil, dann umbenennen
function Publish-WorkResult([string]$text) {
  $final = Join-Path $Work ('Update-' + $AttemptId + '.log')
  $tmp = $final + '.teil'
  Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  $text | Out-File -LiteralPath $tmp -Encoding utf8 -ErrorAction Stop
  Move-Item -LiteralPath $tmp -Destination $final -Force -ErrorAction Stop
}
# Ergebnis dieses Versuchs selbst anzeigen (fuer wiederhergestellte Fassungen ohne Versuchskennung): hoechstens 16 KB
# aus der eigenen Ergebnisdatei, ohne die gemeinsame Update.log anzufassen
function Show-Result {
  $text = $null
  try {
    $fs = [System.IO.File]::Open($Log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $b = New-Object byte[] 16385
      $n = 0
      while ($n -lt 16385) { $k = $fs.Read($b, $n, 16385 - $n); if ($k -le 0) { break }; $n += $k }
    } finally { $fs.Dispose() }
    if ($n -le 16384) { $text = (New-Object System.Text.UTF8Encoding($false)).GetString($b, 0, $n).TrimStart([char]0xFEFF).Trim() }
  } catch {}
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.' }
  $icon = 'Warning'
  if ($text -match '^OK ') { $icon = 'Information' }
  $text = $text -replace '^(OK|FEHLER|ZURUECK|OFFEN) ', ''
  try {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($text, 'Wake on LAN', 'OK', $icon)
  } catch {}
}
# Neustart mit --nach-update und Versuchskennung. Nach einer Wiederherstellung ($LegacyFallback) kann die EXE aelter
# als 1.73 sein und kennt keine Kennung: Dann normal starten und das Ergebnis dieses Versuchs hier anzeigen. Die
# gemeinsame Update.log der aelteren Fassungen wird nicht mehr beschrieben (parallele Versuche ueberschreiben sich nicht).
function Start-Program([bool]$tray) {
  $modern = $true
  if ($LegacyFallback) { try { $modern = ([Version][System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion) -ge [Version]'1.73' } catch { $modern = $false } }
  if ($modern -and $AttemptId) {
    $a = @('--nach-update', $AttemptId)
    if ($tray) { $a += '/tray' }
    Start-Process -FilePath $Exe -ArgumentList $a
    return
  }
  if ($tray) { Start-Process -FilePath $Exe -ArgumentList @('/tray') } else { Start-Process -FilePath $Exe }
  Show-Result
}
$ErrorActionPreference = 'SilentlyContinue'
# Startbestaetigung abwarten: Wake on LAN schreibt nach dem Start des erhoehten Skripts dessen Prozess-ID in $StartFile
# (oder ABBRUCH). Endet Wake on LAN ohne Bestaetigung bzw. kommt ABBRUCH, wurde nichts Erhoehtes gestartet: beenden.
$Installer = 0
# Bereitschaft melden: erst danach startet Wake on LAN das erhoehte Skript
try { 'bereit' | Out-File -LiteralPath $ReadyFile -Encoding ascii } catch {}
$end = (Get-Date).AddMinutes(15)
while ((Get-Date) -lt $end) {
  if (Test-Path -LiteralPath $StartFile) {
    $s = [string](Read-Small $StartFile)
    Remove-Item -LiteralPath $StartFile -Force
    if ($s.Trim() -match '^[0-9]{1,10}$') { $Installer = [int]$s.Trim() }
    break
  }
  if (-not (Test-OldInstance)) {
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path -LiteralPath $StartFile)) { break }
  } else { Start-Sleep -Milliseconds 250 }
}
if ($Installer -le 0) { exit }
# Handle auf genau diesen Prozess oeffnen (SYNCHRONIZE | QUERY_LIMITED_INFORMATION, auch fuer erhoehte Prozesse
# erlaubt). Wake on LAN haelt seinen eigenen Handle offen, bis diese Bestaetigung da ist: Die Prozess-ID kann bis
# dahin nicht neu vergeben werden, danach haelt dieser Handle den Prozess fest.
$hInst = [IntPtr]::Zero
$k32 = $null
$waitNote = $null
try {
  $sig = '[DllImport(""kernel32.dll"", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid); [DllImport(""kernel32.dll"", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr handle, int ms); [DllImport(""kernel32.dll"")] public static extern bool CloseHandle(IntPtr handle); [DllImport(""kernel32.dll"")] public static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);'
  $k32 = Add-Type -MemberDefinition $sig -Name 'Warten' -Namespace 'WakeOnLanNeustart' -PassThru
  $hInst = $k32::OpenProcess(0x00101000, $false, $Installer)
  if ($hInst -eq [IntPtr]::Zero) { $waitNote = 'Kein Zugriff auf das Update (Windows-Fehler ' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error() + '), Rueckfall auf die Prozess-ID.' }
} catch { $waitNote = 'Warten ueber Handle nicht moeglich, Rueckfall auf die Prozess-ID: ' + $_.Exception.Message }
# Startzeit des erhoehten Skripts merken (ueber den Handle bzw. OpenProcess 0x1000), damit im Rueckfall auf die
# Prozess-ID ein Prozess mit anderer Startzeit nicht als das Update gilt
function Get-StartTicks([IntPtr]$h) {
  if ($h -eq [IntPtr]::Zero -or -not $k32) { return $null }
  $c = 0L; $e = 0L; $kt = 0L; $u = 0L
  if ($k32::GetProcessTimes($h, [ref]$c, [ref]$e, [ref]$kt, [ref]$u)) { return $c }
  return $null
}
function Get-InstallerTicks {
  if ($hInst -ne [IntPtr]::Zero) { return (Get-StartTicks $hInst) }
  if (-not $k32) { return $null }
  $hq = $k32::OpenProcess(0x1000, $false, $Installer)
  if ($hq -eq [IntPtr]::Zero) { return $null }
  try { return (Get-StartTicks $hq) } finally { [void]$k32::CloseHandle($hq) }
}
$instTicks = Get-InstallerTicks
# Kein Handle (z. B. UAC mit einem anderen Administratorkonto): Rueckfall auf die Prozess-ID. Bis zur Bestaetigung
# haelt Wake on LAN seinen eigenen Handle offen; die Prozess-ID kann bis dahin nicht neu vergeben werden.
# Empfang bestaetigen: erst danach gibt Wake on LAN das erhoehte Skript frei
try { [string]$Installer | Out-File -LiteralPath $AckFile -Encoding ascii } catch { exit }
# $true = laeuft noch. Liegt das Ergebnis dieses Versuchs vor, ist das Skript fertig (es schreibt es als Letztes).
# WAIT_OBJECT_0 (0) = beendet, WAIT_TIMEOUT (258) = laeuft; WAIT_FAILED: Fehlercode merken und ab dann ueber die
# Prozess-ID pruefen. Im Rueckfall gilt ein Prozess mit anderer Startzeit nicht als das Update.
$workLog = Join-Path $Work ('Update-' + $AttemptId + '.log')
$unclearEnd = $null
# $endConfirmed: Ende des Updates belegt (Ergebnisdatei, Handle signalisiert, kein Prozess mit der ID, andere
# Startzeit). Nur dann darf der Versuchsordner geloescht werden; sonst bleibt er fuer die spaetere Bereinigung.
$endConfirmed = $false
function Test-Installer {
  if (Test-Path -LiteralPath $workLog) { $script:endConfirmed = $true; return $false }
  if ($hInst -ne [IntPtr]::Zero) {
    $r = $k32::WaitForSingleObject($hInst, 0)
    if ($r -eq 0) { $script:endConfirmed = $true; return $false }
    if ($r -eq 258) { return $true }
    $script:waitNote = 'Warten auf das Update fehlgeschlagen (Windows-Fehler ' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error() + '), Rueckfall auf die Prozess-ID.'
    try { [void]$k32::CloseHandle($hInst) } catch {}
    $script:hInst = [IntPtr]::Zero
  }
  $p = Get-Process -Id $Installer -ErrorAction SilentlyContinue
  if (-not $p) { $script:endConfirmed = $true; return $false }
  if ($instTicks -ne $null) { $now = Get-InstallerTicks; if ($now -ne $null -and $now -ne $instTicks) { $script:endConfirmed = $true; return $false }; return $true }
  # Identitaet ungeklaert (kein Handle, keine Startzeit): Ein Prozess mit dieser ID beweist nichts. Dann zaehlt nur
  # noch die Ergebnisdatei dieses Versuchs (oben); kommt sie nicht innerhalb einer kurzen Frist, endet das Warten,
  # und ohne Ergebnis wird ein unklarer Zustand gemeldet (kein Neustart). Nur wenn gar kein Prozess mit dieser ID
  # mehr laeuft, ist das Update sicher beendet (siehe oben).
  if (-not $waitNote) { $script:waitNote = 'Das Update liess sich nur ueber die Prozess-ID verfolgen.' }
  if ($script:unclearEnd -eq $null) { $script:unclearEnd = (Get-Date).AddMinutes(2) }
  return ((Get-Date) -lt $script:unclearEnd)
}
# Neustart erst nach bestaetigtem Ende des Installers; bei Fristablauf Fehler melden und nicht starten
$end = (Get-Date).AddMinutes(15)
$finished = $false
while ((Get-Date) -lt $end) {
  if (-not (Test-Installer)) { $finished = $true; break }
  Start-Sleep -Milliseconds 500
}
if ($hInst -ne [IntPtr]::Zero) { try { [void]$k32::CloseHandle($hInst) } catch {} }
if (-not $finished) {
  $msg = 'FEHLER Das Update ist nach 15 Minuten nicht fertig geworden. Wake on LAN wurde nicht neu gestartet.'
  if ($waitNote) { $msg += ' ' + $waitNote }
  $msg | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
Start-Sleep -Milliseconds 300
$text = $null
try {
  $fs = [System.IO.File]::Open($workLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    $b = New-Object byte[] 16385
    $n = 0
    while ($n -lt 16385) { $k = $fs.Read($b, $n, 16385 - $n); if ($k -le 0) { break }; $n += $k }
  } finally { $fs.Dispose() }
  if ($n -le 16384) { $text = (New-Object System.Text.UTF8Encoding($false)).GetString($b, 0, $n).TrimStart([char]0xFEFF) }
} catch {}
if ($text) { $text = $text.Trim() }
# Versuchsordner (Eingabedateien des Updates) nur bei belegtem Ende loeschen: Laeuft das Update moeglicherweise noch,
# bleibt er erhalten und wird spaeter von der Bereinigung entfernt
if ($Attempt -and $endConfirmed) { Remove-Item -LiteralPath $Attempt -Recurse -Force }
# Neustart nur bei eindeutigem Abschluss: installiert (OK) oder nichts veraendert bzw. vollstaendig zurueckgesetzt (FEHLER)
$clear = $text -and ($text -match '^(OK|FEHLER|ZURUECK) ') -and -not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen'))
if (-not $clear) {
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.'; if ($waitNote) { $text += ' ' + $waitNote } }
  ($text + ' Wake on LAN wurde nicht neu gestartet.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
$text | Out-File -LiteralPath $Log -Encoding utf8
# alte Instanz muss beendet sein (Ladefehler kommen sehr frueh)
$deadline = (Get-Date).AddSeconds(60)
while ((Test-OldInstance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (Test-OldInstance) {
  ($text + ' Wake on LAN wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
if ($Tray) { Start-Program $true }
else { Start-Program $false }
";

        /// <summary>Aus dem Release-Text nur den Abschnitt "## Änderungen", Punkte als "•", ohne Markdown.</summary>
        internal static string ExtractNotes(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var lines = body.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            bool inside = false, any = false;
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.StartsWith("#"))
                {
                    if (inside) break;
                    inside = l.TrimStart('#').Trim().StartsWith("Änderungen", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inside || l.Length == 0) continue;
                if (l.StartsWith("- ") || l.StartsWith("* ")) l = "•  " + l.Substring(2).Trim();
                l = l.Replace("**", "").Replace("`", "");
                if (any) sb.Append("\r\n");
                sb.Append(l);
                any = true;
            }
            return sb.ToString();
        }
    }

    internal enum UpdateKind { Unknown, Current, Available, Failed, Checking }

    /// <summary>Stand der Update-Prüfung für das Info-Fenster.</summary>
    internal sealed class UpdateStatus
    {
        public readonly UpdateKind Kind;
        public readonly string Title, Detail;
        /// <summary>Technischer Originaltext für „Details“ (leer = kein „Details“).</summary>
        public string More = "";
        public UpdateStatus(UpdateKind kind, string title, string detail) { Kind = kind; Title = title; Detail = detail ?? ""; }

        /// <summary>
        /// Fehler der Prüfung als Stand: feste Kurzmeldung (Message) mit Originaltext in Data["Detail"];
        /// unbekannte Fehler nur mit „Details“. „Nur Vorabversionen“ ist kein Fehler.
        /// </summary>
        public static UpdateStatus FromError(Exception ex)
        {
            if (ex.Data.Contains("Prerelease"))
                return new UpdateStatus(UpdateKind.Unknown, "Nur Vorabversionen", "Noch keine fertige Version.");
            if (ex.Data.Contains("Detail"))
                return new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", ex.Message) { More = ex.Data["Detail"] as string ?? "" };
            return new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", "") { More = ex.Message };
        }
    }

    /// <summary>Verbindung des Info-Fensters zur Update-Prüfung im Hauptfenster (wie Fensterpositionen).</summary>
    internal sealed class UpdateHooks
    {
        public Func<UpdateStatus> Status;
        public Func<bool> Auto;
        public Action<bool> SetAuto;
        public Func<Task<UpdateStatus>> CheckNow;
        public Func<bool> CanInstall;
        public Action Install;
        public Action OpenPage;
        /// <summary>„Automatisch aktualisieren“ (ohne diese Hooks fehlt die Zeile im Info-Fenster).</summary>
        public Func<bool> AutoInstall;
        public Action<bool> SetAutoInstall;
    }

    /// <summary>Symbol des Update-Stands (Größe wie die Navigation: 11 pt).</summary>
    internal sealed class StatusGlyph : Control
    {
        private readonly float k;
        private char _glyph;
        private Color _color;

        public StatusGlyph(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        public void Set(char glyph, Color color)
        {
            _glyph = glyph;
            _color = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            if (_glyph == '\0') return;
            int gs = (int)Math.Round(11f * 96f / 72f * k);
            var img = MenuHelper.Glyph(_glyph, _color, gs);
            if (img == null) return;
            e.Graphics.DrawImage(img, (Width - gs) / 2, (Height - gs) / 2, gs, gs);
            img.Dispose();
        }
    }

    /// <summary>Kleiner JSON-Leser (Objekte, Listen, Texte, Zahlen, true/false/null) für die GitHub-Antwort.</summary>
    internal static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Skip(s, ref i);
            if (i != s.Length) throw new InvalidDataException("JSON: unerwartete Zeichen am Ende.");
            return v;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new InvalidDataException("JSON: unerwartetes Ende.");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    Expect(s, ref i, ':');
                    d[key] = Value(s, ref i);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return l;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new InvalidDataException("JSON: unerwartetes Zeichen '" + c + "'.");
            double num;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out num);
            return num;
        }

        private static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new InvalidDataException("JSON: '" + c + "' erwartet.");
            i++;
        }

        private static string Str(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new InvalidDataException("JSON: Text ohne Ende.");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new InvalidDataException("JSON: Text ohne Ende.");
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new InvalidDataException("JSON: \\u unvollständig.");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
        }
    }

    /// <summary>
    /// Hinweisleiste oben im Inhalt: neue Version mit "Jetzt aktualisieren", "Release-Seite" und "Später"
    /// (wie Fensterpositionen wiederherstellen).
    /// </summary>
    internal sealed class UpdateBar : Control
    {
        private readonly float k;
        public readonly ModernButton Install, Page, Later;

        public UpdateBar(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Back;
            Font = new Font("Segoe UI Semibold", 9.5f);
            Install = new ModernButton("Jetzt aktualisieren", MenuHelper.Download, true, k) { Margin = Padding.Empty };
            Page = new ModernButton("Release-Seite", MenuHelper.Link, false, k) { Margin = Padding.Empty };
            Later = new ModernButton("Später", '\0', false, k) { Margin = Padding.Empty };
            foreach (var b in new[] { Install, Page, Later })
            {
                b.HeightUnits = 32;
                b.UpdateSize();
                Controls.Add(b);
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Install == null || Page == null || Later == null) return; // schon beim Setzen der Schrift im Konstruktor
            int x = Width - (int)(12 * k);
            foreach (var b in new[] { Later, Page, Install })
            {
                x -= b.Width;
                b.Location = new Point(x, (Height - b.Height) / 2);
                x -= (int)(8 * k);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color back = Parent != null ? Parent.BackColor : Ui.Back;
            Color fill = Accent.Mix(Ui.Primary, back, 0.14);
            // Die Knöpfe füllen ihre Ecken mit Parent.BackColor: BackColor = Farbe der Leiste, sonst stehen sie als
            // helle Rechtecke in der getönten Leiste (auch nach einem Wechsel hell/dunkel)
            if (BackColor != fill) { BackColor = fill; foreach (Control c in Controls) c.Invalidate(); }
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * k))
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(Accent.Mix(Ui.Primary, back, 0.45)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            int x = (int)(16 * k), gs = (int)Math.Round(11f * 96f / 72f * k);
            var img = MenuHelper.Glyph(MenuHelper.Download, Ui.AccentText, gs);
            if (img != null)
            {
                g.DrawImage(img, x, (Height - gs) / 2, gs, gs);
                img.Dispose();
            }
            x += (int)(34 * k);
            int right = (Install != null ? Install.Left : Width) - (int)(12 * k);
            int room = Math.Max(10, right - x);
            const TextFormatFlags one = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            string text = Text ?? "";
            int dash = text.IndexOf(" – ", StringComparison.Ordinal);
            if (dash > 0 && TextRenderer.MeasureText(text, Font, Size.Empty, one).Width > room)
            {
                // wenig Platz: zweizeilig – oben fett „Neue Version x“, darunter grau „Installiert ist y“ (globale Vorgabe)
                string top = text.Substring(0, dash).Replace(" verfügbar", "");
                string rest = text.Substring(dash + 3).TrimEnd('.');
                if (rest.Length > 0) rest = char.ToUpper(rest[0]) + rest.Substring(1);
                using (var small = new Font("Segoe UI", 9f))
                {
                    int h1 = TextRenderer.MeasureText(top, Font, Size.Empty, one | TextFormatFlags.NoPadding).Height;
                    int h2 = TextRenderer.MeasureText(rest, small, Size.Empty, one | TextFormatFlags.NoPadding).Height;
                    int gap = (int)(2 * k), y0 = (Height - h1 - gap - h2) / 2;
                    TextRenderer.DrawText(g, top, Font, new Rectangle(x, y0, room, h1), Ui.Text, (one & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, rest, small, new Rectangle(x, y0 + h1 + gap, room, h2), Ui.Muted, (one & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.NoPadding);
                }
                return;
            }
            TextRenderer.DrawText(g, text, Font, new Rectangle(x, 0, room, Height), Ui.Text, one);
        }
    }
}
