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
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = r.ReadToEnd();
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
            if (assets != null)
                foreach (object o in assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null || !a.ContainsKey("name") || !a.ContainsKey("browser_download_url")) continue;
                    string name = a["name"] as string, dl = a["browser_download_url"] as string;
                    if (name == null || dl == null || !dl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, wanted, StringComparison.Ordinal)) zipUrl = dl;
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

        /// <summary>Im Projektordner (mit Erstellen.bat) wird nicht automatisch ersetzt: dort entsteht die EXE beim Bauen.</summary>
        public static string InstallBlocker()
        {
            if (File.Exists(Path.Combine(Application.StartupPath, "Erstellen.bat")))
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            return null;
        }

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

        /// <summary>Kann dieses Release direkt installiert werden (gültig signiert, ZIP vorhanden, nicht im Projektordner)?</summary>
        public static bool CanInstall(UpdateInfo u)
        {
            return u != null && u.Problem == null && !string.IsNullOrEmpty(u.ZipUrl) && !string.IsNullOrEmpty(u.Sha256) && u.Manifest != null && InstallBlocker() == null;
        }

        // ------------------------------------------------------------------ Installieren

        /// <summary>Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts.</summary>
        private const string ExeName = "Wake on LAN.exe", ManualName = "Liesmich.txt";
        private const string Running = "Ein Update läuft bereits.";
        private static int _busy;

        /// <summary>Läuft gerade ein Update (in diesem Programm oder als Skript nach einem früheren Start)?</summary>
        public static bool Busy { get { return _busy != 0 || ScriptRunning(); } }

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

        /// <summary>
        /// Selbst-Update nach globaler Vorgabe (Referenz Dock Pilot): ZIP in einen eigenen Arbeitsordner laden, SHA-256 aus dem
        /// signierten Manifest prüfen, dann das Update-Skript per -EncodedCommand starten. Das Skript prüft die Signatur des
        /// Manifests selbst, ersetzt nur EXE und Liesmich (Rückfallkopien im Arbeitsordner) und arbeitet erhöht nur in einem
        /// geschützten Ordner unter %ProgramData%. Nach einem erhöhten Update startet ein nicht erhöhter Prozess das Programm neu.
        /// Danach muss der Aufrufer das Programm beenden.
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
                await InstallLocked(release, progress, tray);
                started = true;
            }
            finally
            {
                if (!started) Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static async Task InstallLocked(UpdateInfo release, Action<int> progress, bool tray)
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
            // eigener Arbeitsordner je Versuch: Download, Entpacken und Rückfallkopien kommen sich nie in die Quere
            string attempt = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attempt);
            string zip = Path.Combine(attempt, AssetName(release.Version));
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                if (progress != null) client.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                await client.DownloadFileTaskAsync(new Uri(release.ZipUrl), zip);
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
            Var(vars, "Manifest", manifestFile);
            Var(vars, "Exe", exe);
            Var(vars, "Version", release.Version);
            Var(vars, "Sha256", release.Sha256.ToUpperInvariant());
            Var(vars, "AssetName", AssetName(release.Version));
            Var(vars, "MainKey", MainKey);
            Var(vars, "ReserveKey", ReserveKey);
            Var(vars, "LockName", LockName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "Log", LogFile);
            Var(vars, "Work", work);
            Var(vars, "ExeName", ExeName);
            Var(vars, "ManualName", ManualName);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            var start = PowerShell(vars + InstallScript);
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
            // Neustart mit normalen Rechten: Ein nicht erhöhter Prozess wartet auf das Ende des Updates, übernimmt
            // das Ergebnis in Update.log und startet das Programm – sonst liefe es danach mit Administratorrechten.
            int installerId = 0;
            try { if (installer != null) installerId = installer.Id; } catch { }
            var r = new StringBuilder();
            r.Append("$ProcessId = ").Append(pid).Append("\n");
            r.Append("$Installer = ").Append(installerId).Append("\n");
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

        /// <summary>Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen.</summary>
        private static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
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
            // Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein
            var compact = new StringBuilder();
            foreach (string raw in script.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                compact.Append(line).Append('\n');
            }
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(compact.ToString()));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            return new ProcessStartInfo(ps, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = system };
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
                if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B') sb.Append(c);
            }
            sb.Append("'\n");
        }

        /// <summary>Ergebnis des letzten Updates ("OK x" bzw. "FEHLER …") nach dem Neustart mit --nach-update.</summary>
        public static string LastResult()
        {
            try { return File.Exists(LogFile) ? File.ReadAllText(LogFile, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        // Update-Skript (Referenz Dock Pilot). Davor setzt das Programm: $ProcessId, $Zip, $Manifest, $Exe, $Version, $Sha256,
        // $AssetName, $MainKey, $ReserveKey, $LockName, $TargetId, $Log, $Work, $ExeName, $ManualName, $Elevated, $Tray.
        // Nur ASCII-Anführungszeichen verwenden.
        private const string InstallScript = @"
$ErrorActionPreference = 'Stop'
$target = Split-Path $Exe -Parent
$workReady = -not $Elevated
$result = $null
$lock = $null
$haveLock = $false
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  Start-Sleep -Milliseconds 500
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
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
            if ($inactive) { [System.IO.Directory]::Delete($_.FullName, $true) }
          }
        }
      } catch {}
    }
    # Arbeitsordner gleich mit Rechten anlegen: schreiben nur SYSTEM und Administratoren, lesen alle angemeldeten Benutzer
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
    $workReady = $true
    $zipWork = Join-Path $Work 'Update.zip'
    $manifestWork = Join-Path $Work 'Update.manifest'
    Copy-Item -LiteralPath $Zip -Destination $zipWork -Force
    Copy-Item -LiteralPath $Manifest -Destination $manifestWork -Force
  } else {
    $zipWork = $Zip
    $manifestWork = $Manifest
  }
  # Signatur des Manifests selbst pruefen (eigene Herausgeberpruefung, an der Kopie, die verwendet wird)
  $bytes = [System.IO.File]::ReadAllBytes($manifestWork)
  if ($bytes.Length -gt 8192) { throw 'Das Manifest ist zu gross.' }
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
  # Pruefsumme aus dem gepruften Manifest an der Datei pruefen, die tatsaechlich entpackt wird
  $hash = (Get-FileHash -LiteralPath $zipWork -Algorithm SHA256).Hash
  if ($hash -ne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $dir = Join-Path $Work 'Neu'
  if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
  Expand-Archive -LiteralPath $zipWork -DestinationPath $dir -Force
  # nur bekannte Dateien uebernehmen
  $new = Join-Path $dir $ExeName
  if (-not (Test-Path -LiteralPath $new -PathType Leaf)) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($new, $Exe))
  $manual = Join-Path $dir $ManualName
  if (Test-Path -LiteralPath $manual -PathType Leaf) { $pairs += ,@($manual, (Join-Path $target $ManualName)) }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $had = Test-Path -LiteralPath $p[1]
      if ($had) { Copy-Item -LiteralPath $p[1] -Destination $bak -Force }
      $done += ,@($p[1], $bak, $had)
      Copy-Item -LiteralPath $p[0] -Destination $p[1] -Force
    }
  } catch {
    foreach ($d in $done) {
      if ($d[2]) { Copy-Item -LiteralPath $d[1] -Destination $d[0] -Force }
      else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction SilentlyContinue }
    }
    throw
  }
  Remove-Item -LiteralPath $alt -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $zipWork -Force -ErrorAction SilentlyContinue
  if ($Elevated) { Remove-Item -LiteralPath $manifestWork -Force -ErrorAction SilentlyContinue }
  $result = 'OK ' + $Version
} catch {
  $result = 'FEHLER ' + $_.Exception.Message
}
if ($Elevated) {
  # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner schreiben; Neustart macht der nicht erhoehte Prozess
  if ($workReady) { try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {} }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated) {
  if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
  else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
}
";

        // Neustart nach einem Update mit Administratorrechten, selbst mit normalen Rechten.
        // Davor setzt das Programm: $ProcessId, $Installer, $Attempt, $Exe, $Log, $Work, $Tray.
        private const string RestartScript = @"
$ErrorActionPreference = 'SilentlyContinue'
try { Wait-Process -Id $ProcessId -Timeout 60 } catch {}
$end = (Get-Date).AddMinutes(15)
$workLog = Join-Path $Work 'Update.log'
while ((Get-Date) -lt $end) {
  if ($Installer -gt 0) { if (-not (Get-Process -Id $Installer -ErrorAction SilentlyContinue)) { break } }
  elseif (Test-Path -LiteralPath $workLog) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Milliseconds 300
$text = $null
if (Test-Path -LiteralPath $workLog) { $text = Get-Content -LiteralPath $workLog -Raw -Encoding UTF8 }
if ($text) { $text = $text.Trim() }
if (-not $text) { $text = 'FEHLER Das Update wurde nicht ausgefuehrt.' }
$text | Out-File -LiteralPath $Log -Encoding utf8
Remove-Item -LiteralPath $Attempt -Recurse -Force
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
