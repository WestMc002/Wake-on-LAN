// Wake on LAN – Programmstart, Kommandozeile und Verknüpfungen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Wake on LAN")]
[assembly: AssemblyProduct("Wake on LAN")]
// Version (Haupt.Neben.Jahr.MonatTag) erzeugt Erstellen.bat in VersionInfo.cs (AppInfo.Version)
[assembly: AssemblyDescription("Wake on LAN")]
[assembly: AssemblyCopyright("© 2026 Günther Schoder")]

namespace WakeOnLan
{
    internal static class Program
    {
        /// <summary>Signal "Fenster zeigen" an das laufende Programm (zweiter Start, z. B. aus dem Startmenü).</summary>
        public static EventWaitHandle ShowSignal;
        private static Mutex _instance;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Screenshots mit Beispieldaten (hell und dunkel) für GitHub, z. B. aus der Erstellen.bat
            if (args.Length > 0 && string.Equals(args[0], "--screenshots", StringComparison.OrdinalIgnoreCase))
            {
                string outDir = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots");
                return Demo.RunScreenshots(outDir) ? 0 : 1;
            }

            var store = new DeviceStore();

            // Autostart: nur im Infobereich starten (Steuerung per Handy)
            bool background = args.Length == 1 && (args[0].Equals("/hintergrund", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--hintergrund", StringComparison.OrdinalIgnoreCase));

            // Kommandozeile: "Wake on LAN.exe" <Gerät> [<Gerät> ...] bzw. /alle – ohne Fenster
            if (args.Length > 0 && !background) return CommandLine.Run(store, args);

            // Nur ein Fenster: läuft das Programm schon (auch versteckt im Infobereich), wird es nach vorne geholt
            bool first;
            _instance = new Mutex(true, "Local\\WakeOnLan.Fenster", out first);
            try { ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WakeOnLan.Zeigen"); } catch { }
            if (!first)
            {
                if (!background && ShowSignal != null)
                {
                    AllowSetForegroundWindow(-1); // das laufende Programm darf sein Fenster nach vorne holen
                    ShowSignal.Set();
                }
                return 0;
            }
            if (!background && ActivateRunning()) return 0; // ältere Version ohne Signal
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception, store);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception, store);
            try
            {
                store.Load();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Die Geräteliste konnte nicht gelesen werden:\r\n" + store.FilePath + "\r\n\r\n" + ex.Message +
                    "\r\n\r\nDas Programm startet mit einer leeren Liste. Die Datei wird erst beim nächsten Speichern überschrieben.",
                    Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Application.Run(new MainForm(store, false, background));
            GC.KeepAlive(_instance);
            return 0;
        }

        /// <summary>Unerwarteter Fehler: Details nach Fehler.log im Ordner der Geräteliste schreiben und kurz melden.</summary>
        private static void ShowCrash(Exception ex, DeviceStore store)
        {
            string log = Path.Combine(store.Folder, "Fehler.log");
            try
            {
                Directory.CreateDirectory(store.Folder);
                File.AppendAllText(log, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + "  Version " + AppInfo.Version + "\r\n" +
                    (ex == null ? "Unbekannter Fehler" : ex.ToString()) + "\r\n\r\n", Encoding.UTF8);
            }
            catch { }
            try
            {
                MessageBox.Show("Unerwarteter Fehler: " + (ex == null ? "unbekannt" : ex.Message) + "\r\n\r\nEinzelheiten stehen in\r\n" + log,
                    Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        /// <summary>Läuft das Fenster schon, wird es nach vorne geholt statt ein zweites zu öffnen.</summary>
        private static bool ActivateRunning()
        {
            try
            {
                var me = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    return true;
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>Wecken über die Kommandozeile, z. B. für Verknüpfungen und Skripte.</summary>
    internal static class CommandLine
    {
        public const string Help =
            "Aufruf:\r\n" +
            "  \"Wake on LAN.exe\"                  Fenster öffnen\r\n" +
            "  \"Wake on LAN.exe\" <Gerät> [...]    Geräte wecken (Name wie in der Liste)\r\n" +
            "  \"Wake on LAN.exe\" /alle            alle Geräte von „Alle wecken“\r\n" +
            "  \"Wake on LAN.exe\" \"/gruppe:<Name>\"  alle Geräte einer Gruppe\r\n" +
            "  \"Wake on LAN.exe\" /hintergrund     nur im Infobereich starten (Steuerung per Handy)\r\n\r\n" +
            "Beim Wecken über die Kommandozeile öffnet sich kein Fenster. Eine Windows-\r\n" +
            "Benachrichtigung meldet, wenn die Geräte online sind (abschaltbar im Fenster\r\n" +
            "unter „…“). Exit-Code: 0 = in Ordnung, 1 = Fehler.";

        public static int Run(DeviceStore store, string[] args)
        {
            if (args.Any(a => a == "/?" || a == "-?" || a.Equals("/hilfe", StringComparison.OrdinalIgnoreCase) || a.Equals("--help", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(Help, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            try { store.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show("Die Geräteliste konnte nicht gelesen werden:\r\n" + store.FilePath + "\r\n\r\n" + ex.Message, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            var devices = new List<Device>();
            var errors = new List<string>();
            foreach (string a in args)
            {
                if (a.Equals("/alle", StringComparison.OrdinalIgnoreCase) || a.Equals("--alle", StringComparison.OrdinalIgnoreCase) || a.Equals("-alle", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var d in store.Devices.Where(x => x.InAll)) if (!devices.Contains(d)) devices.Add(d);
                    continue;
                }
                if (a.StartsWith("/gruppe:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--gruppe:", StringComparison.OrdinalIgnoreCase))
                {
                    string group = a.Substring(a.IndexOf(':') + 1).Trim();
                    var members = store.Devices.Where(x => group.Length > 0 && Device.SameGroup(x.Group, group)).ToList();
                    if (members.Count == 0) errors.Add("Gruppe „" + group + "“: keine Geräte");
                    foreach (var m in members) if (!devices.Contains(m)) devices.Add(m);
                    continue;
                }
                var dev = store.Find(a.Trim());
                if (dev == null) errors.Add("„" + a + "“: Gerät nicht in der Liste");
                else if (!devices.Contains(dev)) devices.Add(dev);
            }

            // alle gleichzeitig wecken (Anstoßen und IPMI warten auf Antworten)
            var results = new WakeResult[devices.Count];
            var threads = new List<Thread>();
            for (int i = 0; i < devices.Count; i++)
            {
                int n = i;
                var t = new Thread(() =>
                {
                    try { results[n] = Waker.Wake(devices[n].Clone()); }
                    catch (Exception ex) { results[n] = new WakeResult(); results[n].Fail(ex.Message); }
                }) { IsBackground = true };
                threads.Add(t);
                t.Start();
            }
            foreach (var t in threads) t.Join();
            bool changed = false;
            for (int i = 0; i < devices.Count; i++)
            {
                if (results[i].FoundIp != null && results[i].FoundIp != devices[i].LastIp) { devices[i].LastIp = results[i].FoundIp; changed = true; }
                History.Add(devices[i].Name, (results[i].Ok ? "geweckt: " : "Fehler beim Wecken: ") + results[i].Message + " (Kommandozeile)");
                if (!results[i].Ok) errors.Add(devices[i].Name + ": " + results[i].Message);
            }
            if (changed) { try { store.Save(); } catch { } }

            if (errors.Count > 0 && devices.Count == 0 && store.Devices.Count == 0)
                errors.Add("Die Geräteliste ist leer. Bitte zuerst im Fenster Geräte anlegen.");

            // Benachrichtigungen: Fehler sofort, danach warten, bis die Geräte online sind (höchstens 3 Minuten)
            var waitFor = new List<Device>();
            for (int i = 0; i < devices.Count; i++)
                if (results[i].Ok && devices[i].HasStatusTarget) waitFor.Add(devices[i]);
            if (!store.Notify && errors.Count == 0) return 0;
            if (!store.Notify) waitFor.Clear();
            using (var waiter = new CliWaiter(store, waitFor, errors, devices.Count))
                Application.Run(waiter);
            return errors.Count == 0 ? 0 : 1;
        }
    }

    /// <summary>
    /// Nach dem Wecken über die Kommandozeile: kurzes Symbol im Infobereich mit Windows-Benachrichtigungen
    /// ("Wecke 3 Geräte …", "Büro-PC ist online (nach 42 s)"), endet von selbst.
    /// </summary>
    internal sealed class CliWaiter : ApplicationContext
    {
        private const int TimeoutSeconds = 180;
        private readonly DeviceStore _store;
        private readonly List<Device> _pending;
        private readonly Notifier _notifier = new Notifier();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly DateTime _start = DateTime.Now;
        private readonly HashSet<Device> _checking = new HashSet<Device>();
        private bool _changed;
        private int _ticks;

        public CliWaiter(DeviceStore store, List<Device> waitFor, List<string> errors, int total)
        {
            _store = store;
            _pending = new List<Device>(waitFor);
            if (errors.Count > 0)
                _notifier.Show(Ui.AppName + " – Fehler", string.Join("\n", errors.Take(4)), ToolTipIcon.Warning);
            else if (_pending.Count > 0)
                _notifier.Show(Ui.AppName, _pending.Count == 1 ? "Wecke " + _pending[0].Name + " …" : "Wecke " + total + " Geräte …", ToolTipIcon.Info);
            else
                _notifier.Show(Ui.AppName, total == 1 ? "Gerät geweckt." : total + " Geräte geweckt.", ToolTipIcon.Info);
            _notifier.Clicked += (s, e) => OpenWindow();
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }

        private void Tick()
        {
            _ticks++;
            int elapsed = (int)(DateTime.Now - _start).TotalSeconds;
            if (_pending.Count == 0)
            {
                // Fehlermeldung bzw. letzte Meldung noch kurz stehen lassen
                if (_ticks >= 4) Finish();
                return;
            }
            if (elapsed > TimeoutSeconds)
            {
                string names = string.Join(", ", _pending.Select(d => d.Name));
                _notifier.Show(Ui.AppName, names + (_pending.Count == 1 ? " antwortet" : " antworten") + " nach " + (TimeoutSeconds / 60) + " Minuten nicht.", ToolTipIcon.Warning);
                foreach (var d in _pending) History.Add(d.Name, "nach " + (TimeoutSeconds / 60) + " Minuten nicht erreicht");
                _pending.Clear();
                _ticks = 0;
                return;
            }
            foreach (var d in _pending.ToList())
            {
                if (_checking.Contains(d)) continue;
                _checking.Add(d);
                var device = d;
                var copy = d.Clone();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    StatusResult r;
                    try { r = Waker.Check(copy); } catch { r = new StatusResult { State = DeviceState.Error }; }
                    try { _timerOwner.BeginInvoke(new Action(() => Done(device, r))); } catch { }
                });
            }
        }

        // Fenster-Handle zum Zurückkehren in den UI-Thread
        private readonly Control _timerOwner = CreateOwner();

        private static Control CreateOwner()
        {
            var c = new Control();
            c.CreateControl();
            var h = c.Handle; // Handle erzwingen
            return c;
        }

        private void Done(Device d, StatusResult r)
        {
            _checking.Remove(d);
            if (r.FoundIp != null && r.FoundIp != d.LastIp) { d.LastIp = r.FoundIp; _changed = true; }
            if (r.State != DeviceState.Online || !_pending.Contains(d)) return;
            _pending.Remove(d);
            d.LastOnline = DateTime.Now;
            _changed = true;
            int sec = (int)(DateTime.Now - _start).TotalSeconds;
            _notifier.Show(Ui.AppName, d.Name + " ist online (nach " + sec + " s).", ToolTipIcon.Info);
            History.Add(d.Name, "online nach " + sec + " s");
            _ticks = 0;
        }

        private void OpenWindow()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true }); } catch { }
        }

        private void Finish()
        {
            _timer.Stop();
            if (_changed) { try { _store.Save(); } catch { } }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _notifier.Dispose();
                _timerOwner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Verknüpfungen (.lnk) über WScript.Shell, ohne zusätzliche Verweise.</summary>
    internal static class Shortcut
    {
        public static string CreateOnDesktop(string title, string arguments)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (char c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
            string path = Path.Combine(desktop, title + ".lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell ist nicht verfügbar.");
            object shell = Activator.CreateInstance(shellType);
            object link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type t = link.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Application.StartupPath });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { title });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null) Marshal.ReleaseComObject(link);
                Marshal.ReleaseComObject(shell);
            }
            return path;
        }
    }
}
