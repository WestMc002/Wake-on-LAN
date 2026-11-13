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
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>Neueste Veröffentlichung auf GitHub.</summary>
    internal sealed class UpdateInfo
    {
        public string Version;   // z. B. 1.27.2026.1106 (ohne "v")
        public string Url;       // Release-Seite auf GitHub
        public string ZipUrl;    // angehängtes ZIP (EXE und Liesmich)
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
            var assets = obj.ContainsKey("assets") ? obj["assets"] as List<object> : null;
            if (assets != null)
                foreach (object o in assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null || !a.ContainsKey("name") || !a.ContainsKey("browser_download_url")) continue;
                    string name = a["name"] as string, dl = a["browser_download_url"] as string;
                    if (name != null && dl != null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && dl.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ZipUrl = dl;
                        break;
                    }
                }
            info.Notes = ExtractNotes(obj.ContainsKey("body") ? obj["body"] as string : null);
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

        /// <summary>
        /// ZIP herunterladen, Hilfsskript schreiben und starten. Das Skript wartet, bis dieses Programm beendet ist,
        /// ersetzt EXE und Liesmich und startet das Programm neu. Danach muss der Aufrufer das Programm beenden.
        /// </summary>
        public static async Task Install(UpdateInfo release, Action<int> progress)
        {
            if (string.IsNullOrEmpty(release.ZipUrl)) throw new Exception("Im Release ist keine ZIP-Datei angehängt.");
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            string zip = Path.Combine(Folder, "Wake-on-LAN-" + release.Version + ".zip");
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                if (progress != null) client.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                await client.DownloadFileTaskAsync(new Uri(release.ZipUrl), zip);
            }
            if (new FileInfo(zip).Length < 1024) throw new Exception("Die heruntergeladene Datei ist unvollständig.");
            string script = Path.Combine(Folder, "Update.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            try { File.Delete(LogFile); } catch { }
            string exe = Application.ExecutablePath;
            string target = Path.GetDirectoryName(exe);
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -ProcessId " + Process.GetCurrentProcess().Id +
                " -Zip \"" + zip + "\" -Exe \"" + exe + "\" -Version \"" + release.Version + "\"")
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Hilfsskript mit Administratorrechten starten
            if (!CanWrite(target)) start.Verb = "runas";
            try { Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
        }

        /// <summary>Ergebnis des letzten Updates ("OK x" bzw. "FEHLER …") nach dem Neustart mit --nach-update.</summary>
        public static string LastResult()
        {
            try { return File.Exists(LogFile) ? File.ReadAllText(LogFile, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        private const string Script = @"# Wake on LAN – Update (wird vom Programm erzeugt)
param([int]$ProcessId,[string]$Zip,[string]$Exe,[string]$Version)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'Update.log'
$name = Split-Path $Exe -Leaf
$target = Split-Path $Exe -Parent
try {
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  Start-Sleep -Milliseconds 500
  $dir = Join-Path $PSScriptRoot 'Neu'
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  Expand-Archive -Path $Zip -DestinationPath $dir -Force
  $new = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1
  if (-not $new) { throw ('Im ZIP fehlt ' + $name) }
  $old = $Exe + '.alt'
  Copy-Item $Exe $old -Force
  try {
    Copy-Item $new.FullName $Exe -Force
    Get-ChildItem $new.DirectoryName -File | Where-Object { $_.Name -ne $name } | Copy-Item -Destination $target -Force
  } catch {
    Copy-Item $old $Exe -Force
    throw
  }
  Remove-Item $old -Force
  Remove-Item $dir -Recurse -Force
  Remove-Item $Zip -Force
  ('OK ' + $Version) | Out-File $log -Encoding utf8
} catch {
  ('FEHLER ' + $_.Exception.Message) | Out-File $log -Encoding utf8
}
Start-Process -FilePath $Exe -ArgumentList '--nach-update'
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
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * k))
            using (var b = new SolidBrush(Accent.Mix(Ui.Primary, back, 0.14)))
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
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(10, right - x), Height), Ui.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
