// Wake on LAN – Zeitpläne über die Windows-Aufgabenplanung (z. B. "Server jeden Montag um 7:00 wecken")
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace WakeOnLan
{
    internal sealed class ScheduleEntry
    {
        /// <summary>Name der Aufgabe (ohne Vorspann), z. B. "Alle wecken Mo–Fr 07.00" – Doppelpunkte sind dort nicht erlaubt.</summary>
        public string Name = "";
        public string Summary = "";
        public bool Enabled;
        public DateTime? NextRun;

        // aus der Aufgabe gelesen (für die Anzeige und zum Bearbeiten); Parsed = false bei von Hand geänderten Aufgaben
        public bool Parsed, Shutdown;
        public string Kind, Value;
        public TimeSpan Time;
        public bool[] Days;

        /// <summary>Überschrift in der Liste: was passiert ("Gruppe „Server“ wecken"); die Uhrzeit steht in der Zeile darunter.</summary>
        public string Title
        {
            get
            {
                if (Parsed) return Schedules.WhatText(Shutdown, Kind, Value);
                // unbekannter Aufbau: Name der Aufgabe, Uhrzeit am Ende mit Doppelpunkt ("07.00" -> "07:00")
                return System.Text.RegularExpressions.Regex.Replace(Name, @"(\d{2})\.(\d{2})$", "$1:$2");
            }
        }

        /// <summary>Zeile darunter: "Mo–Fr · 07:00 Uhr".</summary>
        public string When
        {
            get { return Parsed ? Schedules.DaysText(Days) + " · " + Time.ToString(@"hh\:mm") + " Uhr" : Summary; }
        }
    }

    /// <summary>
    /// Aufgaben in der Windows-Aufgabenplanung (Stammordner, Name beginnt mit "Wake on LAN - ").
    /// Jede Aufgabe ruft "Wake on LAN.exe" mit /alle, /gruppe:… oder einem Gerätenamen auf,
    /// zum Herunterfahren mit /herunterfahren davor.
    /// Läuft unter dem angemeldeten Benutzer, ohne Administratorrechte.
    /// </summary>
    internal static class Schedules
    {
        /// <summary>
        /// Uhrzeit großzügig lesen (wie Sync Pilot): "07:00", "7:00", "0700", "700", "7", "19.45", "19 Uhr" → "HH:MM".
        /// </summary>
        public static bool TryParseTime(string text, out string at)
        {
            at = null;
            string t = (text ?? "").Trim();
            if (t.EndsWith("Uhr", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 3).Trim();
            t = t.Replace('.', ':').Replace(',', ':').Replace(' ', ':').Replace('h', ':').Replace('H', ':').TrimEnd(':');
            // nur Ziffern: "2330" -> 23:30, "930" -> 09:30, "23" -> 23:00, "7" -> 07:00
            if (t.Length >= 3 && t.Length <= 4 && t.All(char.IsDigit)) t = t.Substring(0, t.Length - 2) + ":" + t.Substring(t.Length - 2);
            string[] parts = t.Split(':');
            int h, m = 0;
            if (parts.Length < 1 || parts.Length > 2 || !int.TryParse(parts[0], out h)) return false;
            if (parts.Length == 2 && !int.TryParse(parts[1], out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            at = h.ToString("00") + ":" + m.ToString("00");
            return true;
        }

        public const string Prefix = "Wake on LAN - ";
        public const string ShutdownSwitch = "/herunterfahren";

        /// <summary>Aufruf für die Aufgabe: kind = "alle", "gruppe" oder "geraet".</summary>
        public static string BuildArgs(bool shutdown, string kind, string value)
        {
            string target = kind == "alle" ? "/alle" : kind == "gruppe" ? "\"/gruppe:" + value + "\"" : "\"" + value + "\"";
            return shutdown ? ShutdownSwitch + " " + target : target;
        }

        /// <summary>Aufruf einer Aufgabe zurücklesen; false, wenn er nicht von diesem Programm stammt (z. B. mehrere Geräte).</summary>
        public static bool ParseArgs(string args, out bool shutdown, out string kind, out string value)
        {
            shutdown = false; kind = null; value = "";
            var tokens = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in args ?? "")
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (c == ' ' && !quoted) { if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            if (tokens.Count > 0 && tokens[0].Equals(ShutdownSwitch, StringComparison.OrdinalIgnoreCase)) { shutdown = true; tokens.RemoveAt(0); }
            if (tokens.Count != 1) return false;
            string t = tokens[0];
            if (t.Equals("/alle", StringComparison.OrdinalIgnoreCase)) kind = "alle";
            else if (t.StartsWith("/gruppe:", StringComparison.OrdinalIgnoreCase)) { kind = "gruppe"; value = t.Substring(8).Trim(); }
            else if (t.StartsWith("/") || t.StartsWith("--")) return false;
            else { kind = "geraet"; value = t.Trim(); }
            return kind == "alle" ? !shutdown : value.Length > 0;
        }

        /// <summary>"Alle wecken", "Gruppe „Server“ herunterfahren", "Spiele-PC wecken".</summary>
        public static string WhatText(bool shutdown, string kind, string value)
        {
            if (kind == "alle") return "Alle wecken";
            return TargetText(kind, value) + (shutdown ? " herunterfahren" : " wecken");
        }

        public static string TargetText(string kind, string value)
        {
            return kind == "alle" ? "Alle Geräte von „Alle wecken“" : kind == "gruppe" ? "Gruppe „" + value + "“" : value;
        }

        /// <summary>Aufruf, Uhrzeit und Wochentage aus der Aufgabe lesen (Aufbau wie in Save).</summary>
        private static void Read(ScheduleEntry e, string xml)
        {
            var task = XDocument.Parse(xml).Root;
            var exec = task.Element(Ns + "Actions").Element(Ns + "Exec");
            var trigger = task.Element(Ns + "Triggers").Elements().Single();
            if (trigger.Name != Ns + "CalendarTrigger") return;
            var start = DateTime.Parse(trigger.Element(Ns + "StartBoundary").Value, System.Globalization.CultureInfo.InvariantCulture);
            var days = new bool[7];
            var week = trigger.Element(Ns + "ScheduleByWeek");
            if (trigger.Element(Ns + "ScheduleByDay") != null) days = Enumerable.Repeat(true, 7).ToArray();
            else if (week != null)
            {
                var set = week.Element(Ns + "DaysOfWeek").Elements().Select(x => x.Name.LocalName).ToList();
                for (int i = 0; i < 7; i++) days[i] = set.Contains(DayElements[i]);
            }
            if (!days.Any(x => x)) return;
            var args = exec.Element(Ns + "Arguments");
            if (!ParseArgs(args != null ? args.Value : "", out e.Shutdown, out e.Kind, out e.Value)) return;
            e.Time = new TimeSpan(start.Hour, start.Minute, 0);
            e.Days = days;
            e.Parsed = true;
        }
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        public static readonly string[] DayNames = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };
        private static readonly string[] DayElements = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        private static dynamic Root()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) throw new InvalidOperationException("Die Windows-Aufgabenplanung ist nicht verfügbar.");
            dynamic service = Activator.CreateInstance(t);
            service.Connect();
            return service.GetFolder("\\");
        }

        public static List<ScheduleEntry> List()
        {
            if (Demo.Active) return DemoEntries();
            var result = new List<ScheduleEntry>();
            dynamic folder = Root();
            dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
            foreach (dynamic task in tasks)
            {
                string name = task.Name;
                if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var e = new ScheduleEntry { Name = name.Substring(Prefix.Length) };
                try { e.Enabled = task.Enabled; } catch { }
                try { e.Summary = task.Definition.RegistrationInfo.Description ?? ""; } catch { }
                try { string xml = task.Xml; Read(e, xml); } catch { }
                try
                {
                    DateTime next = task.NextRunTime;
                    if (next.Year > 1900) e.NextRun = next;
                }
                catch { }
                result.Add(e);
            }
            return result.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Aufgabe anlegen oder ersetzen. days: Mo..So; alle sieben = täglich.</summary>
        public static void Save(string name, string arguments, string summary, TimeSpan time, bool[] days)
        {
            if (Demo.Active) return;
            string exe = Application.ExecutablePath;
            var start = DateTime.Today.Add(time);
            XElement schedule;
            if (days.All(x => x))
                schedule = new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1));
            else
            {
                var list = new XElement(Ns + "DaysOfWeek");
                for (int i = 0; i < 7; i++) if (days[i]) list.Add(new XElement(Ns + DayElements[i]));
                schedule = new XElement(Ns + "ScheduleByWeek", list, new XElement(Ns + "WeeksInterval", 1));
            }
            var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", Ui.AppName),
                    new XElement(Ns + "Description", summary)),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "CalendarTrigger",
                        new XElement(Ns + "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
                        new XElement(Ns + "Enabled", true),
                        schedule)),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", false),
                    new XElement(Ns + "StopIfGoingOnBatteries", false),
                    new XElement(Ns + "StartWhenAvailable", false),
                    new XElement(Ns + "ExecutionTimeLimit", "PT10M"),
                    new XElement(Ns + "Enabled", true)),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", exe),
                        new XElement(Ns + "Arguments", arguments),
                        new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(exe)))));
            dynamic folder = Root();
            // 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN (angemeldeter Benutzer)
            folder.RegisterTask(Prefix + CleanName(name), task.ToString(), 6, null, null, 3, null);
        }

        /// <summary>
        /// Programm verschoben: Zeitpläne, deren Programmpfad nicht mehr existiert, auf den aktuellen Pfad umstellen.
        /// Rückgabe: Anzahl der angepassten Zeitpläne.
        /// </summary>
        public static int RepairPaths()
        {
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            int count = 0;
            dynamic folder = Root();
            dynamic tasks = folder.GetTasks(1);
            foreach (dynamic task in tasks)
            {
                string name = task.Name;
                if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                dynamic def = task.Definition;
                bool changed = false;
                foreach (dynamic action in def.Actions)
                {
                    int type = action.Type;
                    if (type != 0) continue; // nur "Programm starten"
                    string path = action.Path;
                    if (string.Equals(path, exe, StringComparison.OrdinalIgnoreCase) || File.Exists(path)) continue;
                    action.Path = exe;
                    action.WorkingDirectory = dir;
                    changed = true;
                }
                if (!changed) continue;
                folder.RegisterTaskDefinition(name, def, 6, null, null, 3, null);
                count++;
            }
            return count;
        }

        public static void Delete(string name)
        {
            if (Demo.Active) return;
            dynamic folder = Root();
            folder.DeleteTask(Prefix + name, 0);
        }

        public static void SetEnabled(string name, bool on)
        {
            if (Demo.Active) return;
            dynamic folder = Root();
            dynamic task = folder.GetTask(Prefix + name);
            task.Enabled = on;
        }

        /// <summary>Zeichen, die in Aufgabennamen nicht erlaubt sind, ersetzen.</summary>
        public static string CleanName(string name)
        {
            foreach (char c in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) name = name.Replace(c, '-');
            return name.Trim();
        }

        /// <summary>Erfundene Zeitpläne für die Screenshots (--screenshots); die echte Aufgabenplanung bleibt unberührt.</summary>
        private static List<ScheduleEntry> DemoEntries()
        {
            Func<int, DayOfWeek[], int, int, DateTime> next = (unused, days, h, m) =>
            {
                for (int i = 0; i < 8; i++)
                {
                    var d = DateTime.Today.AddDays(i).AddHours(h).AddMinutes(m);
                    if (d > DateTime.Now && days.Contains(d.DayOfWeek)) return d;
                }
                return DateTime.Today.AddDays(1).AddHours(h).AddMinutes(m);
            };
            var workdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
            return new List<ScheduleEntry>
            {
                DemoEntry(false, "alle", "", 7, 0, "1111100", true, next(0, workdays, 7, 0)),
                DemoEntry(true, "gruppe", "Büro", 19, 0, "1111100", true, next(0, workdays, 19, 0)),
                DemoEntry(false, "gruppe", "Server", 8, 30, "0000010", true, next(0, new[] { DayOfWeek.Saturday }, 8, 30)),
                DemoEntry(false, "geraet", "Spiele-PC", 18, 0, "0000110", false, null)
            };
        }

        private static ScheduleEntry DemoEntry(bool shutdown, string kind, string value, int h, int m, string days, bool enabled, DateTime? next)
        {
            var e = new ScheduleEntry { Parsed = true, Shutdown = shutdown, Kind = kind, Value = value, Time = new TimeSpan(h, m, 0),
                Days = days.Select(c => c == '1').ToArray(), Enabled = enabled, NextRun = next };
            e.Name = TaskName(shutdown, kind, value, e.Days, e.Time);
            return e;
        }

        /// <summary>Name der Aufgabe: "Gruppe Server wecken Sa 08.30" (ohne Zeichen, die dort nicht erlaubt sind).</summary>
        public static string TaskName(bool shutdown, string kind, string value, bool[] days, TimeSpan time)
        {
            return CleanName(WhatText(shutdown, kind, value).Replace("„", "").Replace("“", "") + " " + DaysText(days) + " " + time.ToString(@"hh\.mm"));
        }

        public static string DaysText(bool[] days)
        {
            if (days.All(x => x)) return "täglich";
            if (days.Take(5).All(x => x) && !days[5] && !days[6]) return "Mo–Fr";
            var list = new List<string>();
            for (int i = 0; i < 7; i++) if (days[i]) list.Add(DayNames[i]);
            return string.Join(", ", list);
        }
    }

    /// <summary>Fenster "Zeitpläne": vorhandene Aufgaben mit Schalter (aktiv), Bearbeiten und Löschen, dazu "Neuer Zeitplan".</summary>
    internal sealed class ScheduleForm : FixedForm, IViewLayout
    {
        private readonly float k;
        private readonly DeviceStore _store;
        private readonly FlowLayoutPanel _list;
        private readonly Label _error, _intro;
        private readonly ModernButton _add, _close;
        private int _width;

        /// <summary>Öffnet "Neuer Zeitplan" (null) bzw. "Zeitplan bearbeiten" im Hauptfenster (gesetzt, wenn die Liste als Ansicht läuft).</summary>
        public Action<ScheduleEntry> OpenEdit;
        private readonly ToolTip _tip = new ModernToolTip();

        public ScheduleForm(DeviceStore store)
        {
            _store = store;
            Theme.Refresh();
            Text = Ui.AppName + " – Zeitpläne";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int W = S(640), m = S(24);

            _intro = new Label
            {
                Text = "Geräte, Gruppen oder „Alle wecken“ zu festen Zeiten wecken oder herunterfahren. Die Zeitpläne liegen in der Windows-Aufgabenplanung " +
                       "und laufen, solange du an diesem PC angemeldet bist – auch wenn dieses Fenster geschlossen ist.",
                ForeColor = Ui.Muted,
                BackColor = Ui.Back,
                Location = new Point(m, m),
                Size = new Size(W - 2 * m, S(44))
            };
            Controls.Add(_intro);
            _list = new FlowLayoutPanel
            {
                Location = new Point(m, m + S(56)),
                Size = new Size(W - 2 * m, S(300)),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Ui.Back
            };
            Controls.Add(_list);
            int y = _list.Bottom + S(16);
            var add = new ModernButton("Neuer Zeitplan …", MenuHelper.Add, true, k) { Location = new Point(m, y) };
            add.Click += (s, e) => Edit(null);
            FormClosed += (s, e) => _tip.Dispose();
            var close = new ModernButton("Schließen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            close.Location = new Point(W - m - close.Width, y);
            _error = new Label { ForeColor = Ui.Red, BackColor = Ui.Back, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(add.Right + S(12), y), Size = new Size(close.Left - add.Right - S(24), add.Height) };
            Controls.Add(add);
            Controls.Add(_error);
            Controls.Add(close);
            CancelButton = close;
            _add = add;
            _close = close;
            ClientSize = new Size(W, y + close.Height + m);
            Fill();
            Arrange(S(760)); // als eigenes Fenster (Screenshots): Text ganz, Liste so hoch wie die Einträge
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        /// <summary>
        /// Als Ansicht im Hauptfenster: ganze Breite nutzen, Liste so hoch wie ihre Einträge (die Ansicht scrollt selbst),
        /// "Neuer Zeitplan" darunter; "Schließen" entfällt (die Navigation führt zurück).
        /// </summary>
        public void Arrange(int width)
        {
            _width = width;
            int m = S(24), w = Math.Max(S(400), width - 2 * m);
            int scrollY = AutoScroll ? AutoScrollPosition.Y : 0;
            SuspendLayout();
            _intro.SetBounds(m, m + scrollY, w, TextRenderer.MeasureText(_intro.Text, _intro.Font, new Size(w, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + S(4));
            int rowW = w;
            int h = 0;
            foreach (Control c in _list.Controls)
            {
                if (c is CardPanel) c.Width = rowW;
                h += c.Height + c.Margin.Vertical;
            }
            _list.AutoScroll = false;
            _list.SetBounds(m, _intro.Bottom + S(12), w, Math.Max(S(40), h));
            int y = _list.Bottom + S(16);
            _add.Location = new Point(m, y);
            _close.Visible = TopLevel; // im Hauptfenster führt die Navigation zurück
            _close.Location = new Point(m + w - _close.Width, y);
            int errRight = TopLevel ? _close.Left - S(12) : m + w;
            _error.SetBounds(_add.Right + S(12), y, Math.Max(S(40), errRight - _add.Right - S(12)), _add.Height);
            if (TopLevel) ClientSize = new Size(w + 2 * m, y + _add.Height + m);
            ResumeLayout(true);
        }

        /// <summary>Neuer Zeitplan (entry = null) oder vorhandenen ändern: im Hauptfenster als eigene Ansicht, sonst als Dialog.</summary>
        private void Edit(ScheduleEntry entry)
        {
            if (OpenEdit != null) { OpenEdit(entry); return; }
            using (var d = new ScheduleEditForm(_store, entry)) if (d.ShowDialog(this) == DialogResult.OK) Fill();
        }

        private void Fill()
        {
            _list.SuspendLayout();
            foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
            List<ScheduleEntry> entries;
            try
            {
                entries = Schedules.List();
                _error.Text = "";
            }
            catch (Exception ex)
            {
                entries = new List<ScheduleEntry>();
                _error.Text = "Aufgabenplanung: " + ex.Message;
            }
            int w = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            if (entries.Count == 0)
                _list.Controls.Add(new Label { Text = "Noch keine Zeitpläne.", ForeColor = Ui.Muted, BackColor = Ui.Back, AutoSize = true, Margin = new Padding(0, S(8), 0, 0) });
            if (_width > 0) w = Math.Max(S(400), _width - 2 * S(24));
            foreach (var e in entries) _list.Controls.Add(Row(e, w));
            _list.ResumeLayout();
            if (_width > 0) Arrange(_width);
        }

        private Control Row(ScheduleEntry e, int width)
        {
            var card = new CardPanel(k) { Width = width, Height = S(64), Margin = new Padding(0, 0, 0, S(8)) };
            // rechts: [Aktiv ◯] [Bearbeiten] [Löschen]
            var del = new ModernButton("", MenuHelper.Delete, false, k) { HeightUnits = 30 };
            del.UpdateSize();
            del.Location = new Point(width - S(16) - del.Width, (card.Height - del.Height) / 2);
            del.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var edit = new ModernButton("", MenuHelper.Edit, false, k) { HeightUnits = 30 };
            edit.UpdateSize();
            edit.Location = new Point(del.Left - S(8) - edit.Width, del.Top);
            edit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            edit.Click += (s, a) =>
            {
                if (e.Parsed) Edit(e);
                else _error.Text = "Diese Aufgabe wurde außerhalb des Programms geändert – hier nur ein-/ausschalten oder löschen.";
            };
            _tip.SetToolTip(edit, "Zeitplan bearbeiten");
            _tip.SetToolTip(del, "Zeitplan löschen");
            var toggle = new ToggleSwitch("Aktiv", k);
            toggle.SetChecked(e.Enabled);
            toggle.Location = new Point(edit.Left - S(16) - toggle.Width, (card.Height - toggle.Height) / 2);
            toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            int textW = Math.Max(S(60), toggle.Left - S(32));
            string sub = e.When;
            if (e.NextRun.HasValue && e.Enabled) sub += (sub.Length > 0 ? " · " : "") + "nächster Lauf " + e.NextRun.Value.ToString("ddd dd.MM. HH:mm");
            if (!e.Enabled) sub += (sub.Length > 0 ? " · " : "") + "ausgeschaltet";
            var title = new Label
            {
                Text = e.Title,
                Font = new Font("Segoe UI Semibold", 10f),
                ForeColor = Ui.Text,
                BackColor = Ui.Card,
                AutoEllipsis = true,
                UseMnemonic = false,
                Location = new Point(S(16), S(10)),
                Size = new Size(textW, S(22)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var when = new Label
            {
                Text = sub,
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                AutoEllipsis = true,
                UseMnemonic = false,
                Location = new Point(S(16), S(33)),
                Size = new Size(textW, S(20)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Controls.Add(title);
            card.Controls.Add(when);
            if (e.Parsed)
            {
                // Doppelklick auf die Zeile: bearbeiten (wie in der Geräteliste)
                foreach (Control c in new Control[] { card, title, when }) c.DoubleClick += (s, a) => Edit(e);
            }
            string name = e.Name;
            toggle.CheckedChanged += (s, a) =>
            {
                try { Schedules.SetEnabled(name, toggle.Checked); History.Add("Zeitplan", (toggle.Checked ? "eingeschaltet: " : "ausgeschaltet: ") + e.Title + " · " + e.When); }
                catch (Exception ex) { _error.Text = ex.Message; }
                BeginInvoke(new Action(Fill));
            };
            del.Click += (s, a) =>
            {
                if (MessageBox.Show(this, "Zeitplan „" + e.Title + " · " + e.When + "“ löschen?", Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                try { Schedules.Delete(name); History.Add("Zeitplan", "gelöscht: " + e.Title + " · " + e.When); }
                catch (Exception ex) { _error.Text = ex.Message; }
                BeginInvoke(new Action(Fill));
            };
            card.Controls.Add(toggle);
            card.Controls.Add(edit);
            card.Controls.Add(del);
            return card;
        }
    }

    /// <summary>Neuer Zeitplan bzw. Zeitplan bearbeiten: Aktion (Wecken, Herunterfahren), Ziel (Alle, Gruppe, Gerät), Uhrzeit und Wochentage.</summary>
    internal sealed class ScheduleEditForm : FixedForm, IViewLayout, IHasChanges
    {

        // ---------- Rückfrage bei ungespeicherten Änderungen ----------
        private string _baseline;
        private string State() { return ChangeTracker.Snapshot(this, _shutdown, _kind, _value); }
        public bool HasChanges { get { return _baseline != null && State() != _baseline; } }
        public void ResetChanges() { _baseline = State(); }
        private readonly float k;
        private readonly ModernButton _target;
        private readonly ModernPill _actWake, _actOff;
        private readonly ModernTextBox _time;
        private readonly ModernPill[] _days = new ModernPill[7];
        private readonly Label _error, _targetLabel, _offHint;
        private readonly DeviceStore _store;
        private readonly ScheduleEntry _old;
        private readonly CardPanel _cWhat, _cWhen;
        private readonly ModernButton _save, _cancel;
        private readonly int _whatHeight, _whenHeight, _daysWidth;
        private bool _shutdown;
        private string _kind = "alle", _value = "";

        public ScheduleEditForm(DeviceStore store, ScheduleEntry entry = null)
        {
            _store = store;
            _old = entry != null && entry.Parsed ? entry : null;
            Theme.Refresh();
            Text = _old != null ? "Zeitplan bearbeiten" : "Neuer Zeitplan";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            if (_old != null) { _shutdown = _old.Shutdown; _kind = _old.Kind; _value = _old.Value; }
            int p = S(20), y = S(44);

            // Karte "Was": Aktion und Ziel
            _cWhat = new CardPanel(k) { Title = "Was" };
            Controls.Add(_cWhat);
            AddLabel(_cWhat, "Aktion", p, ref y);
            _actWake = new ModernPill("Wecken", k) { Location = new Point(p, y), Checked = !_shutdown };
            _actOff = new ModernPill("Herunterfahren", k) { Location = new Point(_actWake.Right + S(6), y), Checked = _shutdown };
            _actWake.Click += (s, e) => SetAction(false);
            _actOff.Click += (s, e) => SetAction(true);
            _cWhat.Controls.Add(_actWake);
            _cWhat.Controls.Add(_actOff);
            y += _actWake.Height + S(16);
            _targetLabel = AddLabel(_cWhat, "", p, ref y);
            _target = new ModernButton("", '\0', false, k) { HeightUnits = 32, Location = new Point(p, y), DropDown = true, Font = new Font("Segoe UI", 9.5f) };
            _target.Click += (s, e) => ChooseTarget();
            _cWhat.Controls.Add(_target);
            _target.UpdateSize();
            y += _target.Height + S(8);
            // Platz bleibt immer frei, damit sich beim Umschalten nichts verschiebt
            _offHint = new Label
            {
                Text = "Fährt ohne Rückfrage herunter – nicht gespeicherte Daten auf dem Gerät können verloren gehen.",
                ForeColor = Ui.Muted, BackColor = Ui.Card, Font = new Font("Segoe UI", 8.75f), UseMnemonic = false,
                Location = new Point(p, y), Size = new Size(S(300), S(36))
            };
            _cWhat.Controls.Add(_offHint);
            _whatHeight = y + _offHint.Height + S(16);

            // Karte "Wann": Uhrzeit und Wochentage
            y = S(44);
            _cWhen = new CardPanel(k) { Title = "Wann" };
            Controls.Add(_cWhen);
            AddLabel(_cWhen, "Uhrzeit", p, ref y);
            _time = new ModernTextBox(k) { Location = new Point(p, y), Width = S(110), Text = _old != null ? _old.Time.ToString(@"hh\:mm") : "07:00" };
            // beim Verlassen in die Schreibweise HH:MM bringen (z. B. "0700" -> "07:00"), wie in Sync Pilot
            _time.Box.Leave += (s, e) =>
            {
                string norm;
                if (Schedules.TryParseTime(_time.Text, out norm) && _time.Text != norm) _time.Text = norm;
            };
            _cWhen.Controls.Add(_time);
            var hint = new Label { Text = "z. B. 07:00 oder 0700", ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = true, Font = new Font("Segoe UI", 8.75f) };
            _cWhen.Controls.Add(hint);
            hint.Location = new Point(_time.Right + S(12), _time.Top + (_time.Height - hint.Height) / 2);
            y += _time.Height + S(16);

            AddLabel(_cWhen, "Wochentage", p, ref y);
            int x = p;
            for (int i = 0; i < 7; i++)
            {
                // Wochentage als Auswahlknöpfe nebeneinander (Klick schaltet ein/aus)
                var day = new ModernPill(Schedules.DayNames[i], k) { Location = new Point(x, y), Checked = _old != null ? _old.Days[i] : i < 5 };
                day.Click += (s, e) => day.Checked = !day.Checked;
                _days[i] = day;
                _cWhen.Controls.Add(day);
                x += day.Width + S(6);
            }
            _whenHeight = y + _days[0].Height + S(20);
            _daysWidth = x - S(6) + p; // so breit muss die Karte mindestens sein

            _cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            _save = new ModernButton("Speichern", '\0', true, k);
            _save.Click += (s, e) => Save();
            _error = new Label { ForeColor = Ui.Red, BackColor = Ui.Back, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(_error);
            Controls.Add(_save);
            Controls.Add(_cancel);
            AcceptButton = _save;
            CancelButton = _cancel;
            Arrange(S(520)); // als eigenes Fenster: beide Karten untereinander
            ShowTarget();
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private Label AddLabel(CardPanel card, string text, int x, ref int y)
        {
            var l = new Label { Text = text, ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = false, UseMnemonic = false, Location = new Point(x, y), Size = new Size(S(300), S(22)) };
            card.Controls.Add(l);
            y += S(24);
            return l;
        }

        /// <summary>
        /// Im Hauptfenster die ganze Breite nutzen: "Was" und "Wann" nebeneinander (ab 760 Breite), sonst untereinander.
        /// Abbrechen/Speichern rechts unter den Karten, die Fehlerzeile links davon.
        /// </summary>
        public void Arrange(int width)
        {
            int m = S(24), g = S(16), p = S(20);
            int w = Math.Max(_daysWidth, width - 2 * m);
            bool two = w >= S(760) && (w - g) / 2 >= _daysWidth;
            int cw = two ? (w - g) / 2 : w;
            int scrollY = AutoScroll ? AutoScrollPosition.Y : 0;
            SuspendLayout();
            int top = m + scrollY;
            if (two)
            {
                int h = Math.Max(_whatHeight, _whenHeight);
                _cWhat.SetBounds(m, top, cw, h);
                _cWhen.SetBounds(m + cw + g, top, w - cw - g, h);
            }
            else
            {
                _cWhat.SetBounds(m, top, cw, _whatHeight);
                _cWhen.SetBounds(m, _cWhat.Bottom + g, cw, _whenHeight);
            }
            _targetLabel.Width = _cWhat.Width - 2 * p;
            _target.Width = _cWhat.Width - 2 * p;
            _offHint.Width = _cWhat.Width - 2 * p;
            int y = Math.Max(_cWhat.Bottom, _cWhen.Bottom) + S(20);
            _save.Location = new Point(m + w - _save.Width, y); // "Abbrechen" links, "Speichern" ganz rechts
            _cancel.Location = new Point(_save.Left - S(10) - _cancel.Width, y);
            _error.SetBounds(m, y, Math.Max(S(40), _cancel.Left - m - S(12)), _save.Height);
            if (TopLevel) ClientSize = new Size(w + 2 * m, y + _save.Height + m);
            ResumeLayout(true);
        }

        private static bool CanShutdown(Device d) { return d.AllowShutdown || d.UseIpmi; }

        /// <summary>Gibt es das gewählte Ziel für die gewählte Aktion (noch)?</summary>
        private bool TargetValid()
        {
            if (_kind == "alle") return !_shutdown;
            if (_kind == "gruppe") return _store.Devices.Any(d => Device.SameGroup(d.Group, _value) && (!_shutdown || CanShutdown(d)));
            if (_kind == "geraet") { var d = _store.Find(_value); return d != null && (!_shutdown || CanShutdown(d)); }
            return false;
        }

        private void SetAction(bool shutdown)
        {
            _shutdown = shutdown;
            _actWake.Checked = !shutdown;
            _actOff.Checked = shutdown;
            // "Alle" gibt es nur beim Wecken; Geräte ohne Herunterfahren fallen weg
            if (_kind != null && !TargetValid()) _kind = null;
            _error.Text = "";
            ShowTarget();
        }

        private void ShowTarget()
        {
            _targetLabel.Text = _shutdown ? "Was soll heruntergefahren werden?" : "Was soll geweckt werden?";
            _target.Text = _kind == null ? "Bitte wählen …" : Schedules.TargetText(_kind, _value);
            _target.UpdateSize();
            if (_cWhat.Width > 0) _target.Width = _cWhat.Width - 2 * S(20);
            _offHint.Visible = _shutdown;
        }

        private void ChooseTarget()
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            Action<string, string> add = (kind, value) =>
            {
                var item = b.Item(Schedules.TargetText(kind, value), '\0', () =>
                {
                    _kind = kind;
                    _value = value;
                    _error.Text = "";
                    ShowTarget();
                });
                item.Checked = kind == _kind && (kind == "alle" || string.Equals(value, _value, StringComparison.CurrentCultureIgnoreCase));
                item.Radio = true;
                menu.Items.Add(item);
            };
            if (!_shutdown) add("alle", "");
            var devices = _store.Devices.Where(d => !_shutdown || CanShutdown(d)).ToList();
            var groups = _store.Groups().Where(g => devices.Any(d => Device.SameGroup(d.Group, g))).ToList();
            if (groups.Count > 0 && menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            foreach (string g in groups) add("gruppe", g);
            if (devices.Count > 0 && menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            foreach (var d in devices) add("geraet", d.Name);
            if (menu.Items.Count == 0)
            {
                menu.Dispose();
                _error.Text = _shutdown ? "Für kein Gerät ist das Herunterfahren eingerichtet." : "Die Geräteliste ist leer.";
                return;
            }
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            MenuHelper.ShowOnScreen(menu, _target, new Point(0, _target.Height));
        }

        private void Save()
        {
            _error.Text = "";
            if (_kind == null) { _error.Text = "Bitte auswählen, was heruntergefahren werden soll."; return; }
            if (!TargetValid()) { _error.Text = "„" + Schedules.TargetText(_kind, _value) + "“ gibt es nicht mehr – bitte neu auswählen."; return; }
            TimeSpan time;
            string t;
            if (!Schedules.TryParseTime(_time.Text, out t) || !TimeSpan.TryParseExact(t, @"hh\:mm", null, out time))
            {
                _error.Text = "Bitte eine gültige Uhrzeit eingeben, z. B. 07:00 oder 0700.";
                _time.Box.Focus();
                return;
            }
            _time.Text = t;
            var days = _days.Select(c => c.Checked).ToArray();
            if (!days.Any(x => x)) { _error.Text = "Bitte mindestens einen Wochentag wählen."; return; }
            string what = Schedules.WhatText(_shutdown, _kind, _value);
            string summary = what + " · " + Schedules.DaysText(days) + " · " + time.ToString(@"hh\:mm") + " Uhr";
            string name = Schedules.TaskName(_shutdown, _kind, _value, days, time);
            try
            {
                bool renamed = _old != null && !string.Equals(_old.Name, name, StringComparison.OrdinalIgnoreCase);
                // gibt es den Zeitplan schon (gleiche Aktion, Tage und Uhrzeit), nicht stillschweigend überschreiben
                if ((_old == null || renamed) && Schedules.List().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    _error.Text = "Diesen Zeitplan gibt es schon.";
                    return;
                }
                Schedules.Save(name, Schedules.BuildArgs(_shutdown, _kind, _value), summary, time, days);
                if (_old != null)
                {
                    // die Aufgabe heißt nach Aktion, Tagen und Uhrzeit: alte entfernen, Schalter "Aktiv" behalten
                    if (renamed) Schedules.Delete(_old.Name);
                    if (!_old.Enabled) Schedules.SetEnabled(name, false);
                    History.Add("Zeitplan", "geändert: " + summary);
                }
                else History.Add("Zeitplan", "angelegt: " + summary);
            }
            catch (Exception ex)
            {
                _error.Text = "Aufgabenplanung: " + ex.Message;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
