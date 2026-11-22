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

        /// <summary>Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts.</summary>
        private const string ExeName = "Wake on LAN.exe", ManualName = "Liesmich.txt";
        private const string Running = "Ein Update läuft bereits.";
        /// <summary>Höchstgröße des Update-ZIP (zusätzlich zur Größe laut GitHub).</summary>
        private const long MaxZip = 64L * 1024 * 1024;
        private static int _busy;

        /// <summary>Läuft gerade ein Update (in diesem Programm oder als Skript nach einem früheren Start)?</summary>
        public static bool Busy { get { return _busy != 0 || ScriptRunning(); } }

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

        /// <summary>Kennung des Installationsziels (24 Zeichen der SHA-256 des Programmordners) für Sperre und Arbeitsordner.</summary>
        private static string TargetId
        {
            get
            {
                string dir = Path.GetFullPath(Application.StartupPath).TrimEnd('\\').ToUpperInvariant();
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(dir))).Replace("-", "").Substring(0, 24);
            }
        }

        /// <summary>Sperre, die das Update-Skript bis nach dem Dateiaustausch hält: sitzungsübergreifend und je Installationsziel.</summary>
        private static string LockName { get { return @"Global\WakeOnLan-Update-" + TargetId; } }

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

        /// <summary>
        /// Warum gerade nicht aktualisiert wird (null = nichts dagegen): Projektordner, Programm läuft mit Administratorrechten
        /// oder ein früheres Update wurde nicht abgeschlossen (dann <see cref="PendingText"/>).
        /// </summary>
        public static string InstallBlocker()
        {
            if (InProjectFolder)
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            // Läuft Wake on LAN mit Administratorrechten (von Hand so gestartet), liefen Update-Skript und Neustart erhöht,
            // auch wenn der Programmordner beschreibbar ist (Arbeitsordner im Temp-Ordner). Deshalb nie aus einer erhöhten Instanz.
            if (IsElevated)
                return "Wake on LAN läuft gerade mit Administratorrechten. Bitte Wake on LAN normal starten und dann aktualisieren.";
            if (PendingRestore() != null) return PendingText;
            return null;
        }

        /// <summary>Projektordner (Erstellen.bat neben der EXE): dort wird weder aktualisiert noch zurückgesetzt.</summary>
        private static bool InProjectFolder
        {
            get { return File.Exists(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Erstellen.bat")); }
        }

        /// <summary>Ein früheres Update wurde nicht abgeschlossen – „Jetzt aktualisieren“ bietet dann die Wiederherstellung an.</summary>
        public static bool RepairPending { get { return InstallBlocker() == PendingText; } }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "~update-test.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Selbst-Update nach globaler Vorgabe (Referenz WindowGrid 1.65): ZIP begrenzt in einen eigenen Arbeitsordner laden,
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

        private static void InstallLocked(UpdateInfo release, Action<int> progress, bool tray)
        {
            if (release.Problem != null) throw Fail(release.Problem, release.ProblemDetail);
            if (string.IsNullOrEmpty(release.ZipUrl) || string.IsNullOrEmpty(release.Sha256) || release.Manifest == null)
                throw new Exception("Im Release ist keine passende ZIP-Datei.");
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            if (ScriptRunning()) throw new Exception(Running);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            CleanOldAttempts();
            // eigener Arbeitsordner je Versuch; der Name beginnt mit der Zielkennung, damit die Sperrprüfung nur Ordner
            // dieses Installationsziels betrachtet
            string attempt = Path.Combine(Folder, TargetId + "-" + Guid.NewGuid().ToString("N"));
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
                try { Directory.Delete(attempt, true); } catch { }
                throw new Exception("Download unvollständig.");
            }
            string hash = Sha256Of(zip);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(attempt, true); } catch { }
                throw Fail("Download beschädigt.", "Die Prüfsumme (SHA-256) der heruntergeladenen Datei stimmt nicht mit dem signierten Manifest überein.\r\n" +
                    "Erwartet: " + release.Sha256 + "\r\nErhalten: " + hash);
            }
            try { File.Delete(LogFile); } catch { }
            string exe = Application.ExecutablePath; // tatsächlicher Pfad, auch wenn die EXE umbenannt wurde
            bool elevated = !CanWrite(Path.GetDirectoryName(exe));
            // Erhöht: eigener, nicht vorhersagbarer Arbeitsordner direkt unter ProgramData, den das Skript nur für
            // Administratoren beschreibbar anlegt. Sonst reicht der Arbeitsordner im Temp-Ordner.
            string work = elevated
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WakeOnLan-Update-" + TargetId + "-" + Guid.NewGuid().ToString("N"))
                : attempt;
            int pid = Process.GetCurrentProcess().Id;
            // Manifest neben das ZIP legen: Das Skript prüft die Signatur selbst und nimmt die Prüfsumme nur von dort
            string manifestFile = Path.Combine(attempt, ManifestName(release.Version));
            File.WriteAllBytes(manifestFile, release.Manifest);
            var vars = new StringBuilder();
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
            Var(vars, "Log", LogFile);
            Var(vars, "Work", work);
            Var(vars, "ExeName", ExeName);
            Var(vars, "ManualName", ManualName);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            // Skript als Datei im Arbeitsordner, auf der Befehlszeile nur Lader, Werte und SHA-256 des Skripts: Der Lader
            // liest die Datei einmal in den Speicher, prüft die Prüfsumme und führt genau diese Bytes aus.
            // So bleibt die Befehlszeile weit unter 32767 Zeichen, auch bei langen Pfaden.
            byte[] body = new UTF8Encoding(false).GetBytes(Compact(InstallScript));
            string scriptFile = Path.Combine(attempt, "Update-Skript.txt");
            File.WriteAllBytes(scriptFile, body);
            string scriptHash;
            using (var sha = SHA256.Create()) scriptHash = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");
            Var(vars, "ScriptFile", scriptFile);
            Var(vars, "ScriptHash", scriptHash);
            var start = PowerShell(vars + InstallLoader);
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Skript mit Administratorrechten starten
            if (elevated) start.Verb = "runas";
            Process installer;
            try { installer = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                try { Directory.Delete(attempt, true); } catch { }
                if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
            if (!elevated) return; // nicht erhöht startet das Skript das Programm selbst neu (mit normalen Rechten)
            StartRestartHelper(pid, installer, attempt, exe, work, tray);
        }

        /// <summary>
        /// Neustart mit normalen Rechten nach einem erhöhten Update bzw. einer erhöhten Wiederherstellung: Ein nicht
        /// erhöhter Prozess wartet auf deren Ende, übernimmt das Ergebnis in Update.log und startet das Programm.
        /// </summary>
        private static void StartRestartHelper(int pid, Process worker, string attempt, string exe, string work, bool tray)
        {
            int workerId = 0;
            try { if (worker != null) workerId = worker.Id; } catch { }
            var r = new StringBuilder();
            r.Append("$ProcessId = ").Append(pid).Append("\n");
            r.Append("$Installer = ").Append(workerId).Append("\n");
            Var(r, "Attempt", attempt);
            Var(r, "Exe", exe);
            Var(r, "Log", LogFile);
            Var(r, "Work", work);
            r.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            var rs = PowerShell(r + RestartScript);
            rs.UseShellExecute = false;
            rs.CreateNoWindow = true;
            Process.Start(rs);
        }

        /// <summary>Merker im Arbeitsordner: Austausch begonnen und nicht abgeschlossen; Inhalt = Ordner mit den alten Dateien.</summary>
        private const string PendingMarker = "Wiederherstellung-offen";

        /// <summary>Kurzmeldung für Anwender: Ein Update wurde nicht abgeschlossen (Technisches steht im Verlauf).</summary>
        public const string PendingText = "Das letzte Update wurde nicht abgeschlossen.";

        /// <summary>
        /// Arbeitsordner eines nicht abgeschlossenen Updates für dieses Installationsziel (null = keiner). Ordner im
        /// Temp-Ordner, bei denen laut Journal nichts zurückzusetzen ist, werden dabei still aufgeräumt.
        /// </summary>
        private static string PendingRestore()
        {
            try
            {
                var dirs = new List<string>();
                if (Directory.Exists(Folder)) dirs.AddRange(Directory.GetDirectories(Folder, TargetId + "-*"));
                string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                dirs.AddRange(Directory.GetDirectories(data, "WakeOnLan-Update-" + TargetId + "-*"));
                foreach (string dir in dirs)
                {
                    if (!File.Exists(Path.Combine(dir, PendingMarker))) continue;
                    bool temp = dir.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (temp && NothingToRestore(dir))
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                            History.Add("Wake on LAN", "Nicht abgeschlossenes Update aufgeräumt, es war nichts zurückzusetzen: " + dir);
                            continue;
                        }
                        catch { }
                    }
                    return dir;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Prüft das Wiederherstellungsjournal: true, wenn jede eingetragene Datei noch der Sicherung entspricht bzw. bei
        /// „Neu“ nicht vorhanden ist (das Update hat dann nichts verändert). Übrig gebliebene Hilfsdateien werden entfernt.
        /// </summary>
        private static bool NothingToRestore(string dir)
        {
            try
            {
                string exe = Application.ExecutablePath;
                string target = Path.GetDirectoryName(exe);
                string journal = Path.Combine(dir, "Wiederherstellung.txt");
                // Journal fehlt: Der Merker wird vor dem Journal geschrieben, verändert wurde also noch nichts
                if (!File.Exists(journal)) return true;
                foreach (string line in File.ReadAllLines(journal, Encoding.UTF8))
                {
                    if (!line.StartsWith("Vorher ", StringComparison.Ordinal)) continue;
                    var m = Regex.Match(line, @"^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$");
                    if (!m.Success) return false;
                    string dst = m.Groups[3].Value, tmp = m.Groups[4].Value;
                    if (!string.Equals(dst, exe, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(dst, Path.Combine(target, ManualName), StringComparison.OrdinalIgnoreCase)) return false;
                    if (m.Groups[2].Value == "Sicherung")
                    {
                        string bak = Path.Combine(Path.Combine(dir, "Alt"), m.Groups[1].Value);
                        if (!File.Exists(bak) || !File.Exists(dst) || Sha256Of(bak) != Sha256Of(dst)) return false;
                    }
                    else if (File.Exists(dst)) return false;
                    // Hilfsdatei nur im Programmordner und mit dem erwarteten Namen entfernen
                    if (string.Equals(Path.GetDirectoryName(tmp), target, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(tmp).StartsWith("." + Path.GetFileName(dst) + ".update-", StringComparison.OrdinalIgnoreCase))
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Nicht abgeschlossenes Update: nachfragen und die vorherige Version wiederherstellen (das Programm wird dazu
        /// beendet und danach neu gestartet). true = Wiederherstellung läuft, der Aufrufer soll das Programm beenden.
        /// </summary>
        public static bool OfferRepair(IWin32Window owner, bool tray)
        {
            if (InProjectFolder) return false; // im Projektordner wird nichts zurückgesetzt (dort entsteht die EXE beim Bauen)
            string dir = PendingRestore();
            if (dir == null) return false;
            History.Add("Wake on LAN", "Nicht abgeschlossenes Update gefunden: " + dir);
            if (MessageBox.Show(owner,
                PendingText + "\r\n\r\nWake on LAN stellt jetzt die vorherige Version wieder her und startet danach neu.\r\n\r\nJetzt wiederherstellen?",
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
            if (InProjectFolder) throw new Exception("Im Projektordner (mit Erstellen.bat) wird nichts zurückgesetzt.");
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            bool protectedDir = string.Equals(Path.GetDirectoryName(dir), data, StringComparison.OrdinalIgnoreCase);
            string exe = Application.ExecutablePath;
            bool writable = CanWrite(Path.GetDirectoryName(exe));
            // Sicherungen aus dem Temp-Ordner nie mit Administratorrechten zurückkopieren (jedes Programm des Benutzers
            // könnte sie verändert haben); dort hat das Update ohne Adminrechte gearbeitet, also reichen sie auch jetzt.
            if (!protectedDir && !writable)
                throw new Exception("Die vorherige Version lässt sich nicht automatisch wiederherstellen. Bitte Wake on LAN neu herunterladen.");
            bool elevated = protectedDir;
            try { File.Delete(LogFile); } catch { }
            int pid = Process.GetCurrentProcess().Id;
            var vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            Var(vars, "Work", dir);
            Var(vars, "Exe", exe);
            Var(vars, "ManualName", ManualName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "Log", LogFile);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            var start = PowerShell(vars + RepairScript);
            if (elevated) start.Verb = "runas";
            Process repairer;
            try { repairer = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Wiederherstellung abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
            if (!elevated) return;
            // Neustart mit normalen Rechten über den Neustarthelfer (liest das Ergebnis aus dem geschützten Ordner)
            StartRestartHelper(pid, repairer, "", exe, dir, tray);
        }

        /// <summary>Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen – nie bei offener Wiederherstellung.</summary>
        private static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.Exists(Path.Combine(dir, PendingMarker))) continue; // Wiederherstellung offen: nie automatisch löschen
                    if (di.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { di.Delete(true); } catch { }
                }
            }
            catch { }
        }

        private static string Sha256Of(string file)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        }

        /// <summary>PowerShell mit dem Skript als Kommando (-EncodedCommand), nur über den festen Systempfad.</summary>
        private static ProcessStartInfo PowerShell(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) throw new Exception("Windows PowerShell wurde nicht gefunden.");
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(Compact(script)));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            return new ProcessStartInfo(ps, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = system };
        }

        /// <summary>Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein.</summary>
        internal static string Compact(string script)
        {
            var compact = new StringBuilder();
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
                if (c == '\n' || c == '\r') throw new Exception("Ungültiger Pfad für das Update.");
                sb.Append(c);
                // PowerShell nimmt auch typografische einfache Anführungszeichen als Begrenzer: verdoppeln
                if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛') sb.Append(c);
            }
            sb.Append("'\n");
        }

        /// <summary>Ergebnis des letzten Updates („OK x“, „FEHLER …“, „OFFEN …“, „ZURUECK …“) nach dem Neustart mit --nach-update.</summary>
        public static string LastResult()
        {
            try { return File.Exists(LogFile) ? File.ReadAllText(LogFile, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        // Lader für das Update-Skript (steht mit den Werten auf der Befehlszeile). Liest $ScriptFile einmal in den
        // Speicher, prüft die SHA-256 gegen $ScriptHash und führt genau diese Bytes aus. Weicht sie ab oder fehlt die
        // Datei, wird nichts verändert und Wake on LAN startet wieder (erhöht über den Neustarthelfer, der dazu das
        // Ergebnis „FEHLER …“ im geschützten Arbeitsordner findet). New-WorkFolder nutzt auch das Update-Skript.
        private const string InstallLoader = @"
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
    try { New-WorkFolder; $msg | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
  } else {
    try { $msg | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    # nach der Frist erneut pruefen: laeuft die alte Instanz noch, nicht starten (der Start wuerde ohnehin abgefangen)
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
      try { ($msg + ' Wake on LAN wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    }
    elseif ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
    else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
  }
  exit 1
}
. ([scriptblock]::Create($code))
";

        // Update-Skript (Referenz WindowGrid 1.65). Davor setzt das Programm: $ProcessId, $Zip, $ZipSize, $Manifest, $Exe,
        // $Version, $Sha256, $AssetName, $MainKey, $ReserveKey, $LockName, $TargetId, $TempRoot, $Log, $Work, $ExeName,
        // $ManualName, $Elevated, $Tray (über den Lader zusätzlich $ScriptFile, $ScriptHash). Nur ASCII-Anführungszeichen.
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
  # erst nach bestaetigtem Ende der alten Instanz Dateien aendern
  $deadline = (Get-Date).AddSeconds(60)
  while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { throw 'Wake on LAN wurde nicht beendet.' }
    Start-Sleep -Milliseconds 250
  }
  Start-Sleep -Milliseconds 500
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
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  # Status vor dem ersten Ueberschreiben dauerhaft anlegen: bricht der Austausch ab (auch hart beendet), bleibt der
  # Arbeitsordner geschuetzt und neue Updates gesperrt. Laesst er sich nicht schreiben, wird nichts ausgetauscht.
  $pending = Join-Path $Work 'Wiederherstellung-offen'
  $alt | Out-File -LiteralPath $pending -Encoding utf8 -ErrorAction Stop
  if (-not (Test-Path -LiteralPath $pending)) { throw 'Statusdatei konnte nicht angelegt werden.' }
  # Wiederherstellungsjournal: vor jedem Austausch Zielpfad, Sicherungsdatei und ob das Ziel vorher existierte
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  @(('Wake-on-LAN-Update ' + $Version + ' - Wiederherstellungsjournal'),
    'Jede Zeile ""Vorher"" gilt als moeglicherweise ueberschrieben, auch ohne folgende Zeile ""Ersetzt"" (Abbruch waehrend des Kopierens).',
    'Wiederherstellen fuer jede Zeile ""Vorher"": bei ""Sicherung"" die Sicherungsdatei auf das Ziel zurueckkopieren (sie ist vollstaendig), bei ""Neu"" die Zieldatei loeschen, falls vorhanden.',
    '""Ersetzt n"" bestaetigt nur, dass der Austausch dieser Datei abgeschlossen wurde. Eine uebrig gebliebene ""Hilfsdatei"" kann geloescht werden.',
    '') | Set-Content -LiteralPath $journal -Encoding utf8 -ErrorAction Stop
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
        Copy-Item -LiteralPath $p[1] -Destination ($bak + '.teil') -Force
        Move-Item -LiteralPath ($bak + '.teil') -Destination $bak -Force
        $line = 'Vorher ' + $i + ': Sicherung | Ziel: ' + $p[1] + ' | Sicherungsdatei: ' + $bak + ' | Hilfsdatei: ' + $tmp
      } else {
        $line = 'Vorher ' + $i + ': Neu | Ziel: ' + $p[1] + ' | Hilfsdatei: ' + $tmp
      }
      Add-Content -LiteralPath $journal -Value $line -Encoding utf8 -ErrorAction Stop
      $done += ,@($p[1], $bak, $had)
      Write-Entry $p[0] $p[1] $tmp $p[2]
      Add-Content -LiteralPath $journal -Value ('Ersetzt ' + $i) -Encoding utf8 -ErrorAction SilentlyContinue
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
  if ($workReady) { try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {} }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if (-not $noRestart) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated -and -not $noRestart) {
  # nur starten, wenn die alte Instanz beendet ist (z. B. nicht nach Ablauf der Wartefrist)
  if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
    try { ($result + ' Wake on LAN wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  }
  elseif ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
  else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
}
";

        // Wiederherstellung nach einem nicht abgeschlossenen Update. Davor setzt das Programm: $ProcessId, $Work, $Exe,
        // $ManualName, $TargetId, $Log, $Elevated, $Tray. Liest das Journal, nimmt aber Ziele und Sicherungen nie
        // ungeprüft daraus: Ziel nur EXE oder Liesmich, Sicherung immer <Work>\Alt\<Nummer>. Mit Adminrechten nur aus
        // dem eigenen, geschützten Arbeitsordner unter ProgramData. Ergebnis: ZURUECK, FEHLER (nichts verändert) oder
        // OFFEN (Wiederherstellung unvollständig, kein Neustart).
        private const string RepairScript = @"
$ErrorActionPreference = 'Stop'
$result = $null
$noRestart = $false
$target = Split-Path $Exe -Parent
try {
  # im Projektordner (Erstellen.bat neben der EXE) nichts zuruecksetzen
  if (Test-Path -LiteralPath (Join-Path $target 'Erstellen.bat')) { throw 'Im Projektordner wird nichts zurueckgesetzt.' }
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
  if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { throw 'Wake on LAN wurde nicht beendet.' }
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
  $lines = @()
  if (Test-Path -LiteralPath $journal) { $lines = [System.IO.File]::ReadAllLines($journal) }
  $failed = @()
  foreach ($line in $lines) {
    if (-not $line.StartsWith('Vorher ')) { continue }
    if ($line -notmatch '^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$') { throw 'Das Journal ist nicht lesbar.' }
    $n = $Matches[1]; $kind = $Matches[2]; $dst = $Matches[3]; $tmp = $Matches[4]
    if ($allowed -notcontains $dst) { throw 'Unerwarteter Eintrag im Journal.' }
    try {
      if ($kind -eq 'Sicherung') {
        $bak = Join-Path (Join-Path $Work 'Alt') $n
        if (-not (Test-Path -LiteralPath $bak -PathType Leaf)) { throw ('Sicherung fehlt: ' + $bak) }
        $same = (Test-Path -LiteralPath $dst) -and ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash)
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
  $result = 'ZURUECK Die vorherige Version wurde wiederhergestellt.'
} catch {
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($Elevated) {
  # Ergebnis in den geschuetzten Arbeitsordner; den Neustart macht der nicht erhoehte Neustarthelfer
  try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if ($result.StartsWith('ZURUECK')) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
  if (-not $noRestart -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
    if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
    else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
  }
}
";

        // Neustart nach einem Update bzw. einer Wiederherstellung mit Administratorrechten, selbst mit normalen Rechten.
        // Davor setzt das Programm: $ProcessId, $Installer, $Attempt, $Exe, $Log, $Work, $Tray.
        private const string RestartScript = @"
$ErrorActionPreference = 'SilentlyContinue'
# Neustart erst nach bestaetigtem Ende des Installers; bei Fristablauf Fehler melden und nicht starten
$end = (Get-Date).AddMinutes(15)
$workLog = Join-Path $Work 'Update.log'
$finished = $false
while ((Get-Date) -lt $end) {
  if ($Installer -gt 0) { if (-not (Get-Process -Id $Installer -ErrorAction SilentlyContinue)) { $finished = $true; break } }
  elseif (Test-Path -LiteralPath $workLog) { $finished = $true; break }
  Start-Sleep -Milliseconds 500
}
if (-not $finished) {
  'FEHLER Das Update ist nach 15 Minuten nicht fertig geworden. Wake on LAN wurde nicht neu gestartet.' | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
Start-Sleep -Milliseconds 300
$text = $null
if (Test-Path -LiteralPath $workLog) { $text = Get-Content -LiteralPath $workLog -Raw -Encoding UTF8 }
if ($text) { $text = $text.Trim() }
if ($Attempt) { Remove-Item -LiteralPath $Attempt -Recurse -Force }
# Neustart nur bei eindeutigem Abschluss: installiert (OK) oder nichts veraendert bzw. vollstaendig zurueckgesetzt (FEHLER)
$clear = $text -and ($text -match '^(OK|FEHLER|ZURUECK) ') -and -not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen'))
if (-not $clear) {
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.' }
  ($text + ' Wake on LAN wurde nicht neu gestartet.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
$text | Out-File -LiteralPath $Log -Encoding utf8
# alte Instanz muss beendet sein (Ladefehler kommen sehr frueh)
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
  ($text + ' Wake on LAN wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
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
