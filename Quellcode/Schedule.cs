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
        public string Name = "";
        public string Summary = "";
        public bool Enabled;
        public DateTime? NextRun;
    }

    /// <summary>
    /// Aufgaben in der Windows-Aufgabenplanung (Stammordner, Name beginnt mit "Wake on LAN - ").
    /// Jede Aufgabe ruft "Wake on LAN.exe" mit /alle, /gruppe:… oder einem Gerätenamen auf.
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
                try
                {
                    DateTime next = task.NextRunTime;
                    if (next.Year > 1900) e.NextRun = next;
                }
                catch { }
                result.Add(e);
            }
            return result.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Aufgabe anlegen oder ersetzen. days: Mo..So; alle sieben = täglich.</summary>
        public static void Save(string name, string arguments, string summary, TimeSpan time, bool[] days)
        {
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
                new ScheduleEntry { Name = "Alle wecken Mo–Fr 07.00", Summary = "Alle wecken · Mo–Fr · 07:00 Uhr", Enabled = true, NextRun = next(0, workdays, 7, 0) },
                new ScheduleEntry { Name = "Gruppe Server Sa 08.30", Summary = "Gruppe „Server“ · Sa · 08:30 Uhr", Enabled = true, NextRun = next(0, new[] { DayOfWeek.Saturday }, 8, 30) },
                new ScheduleEntry { Name = "Spiele-PC Fr, Sa 18.00", Summary = "Spiele-PC · Fr, Sa · 18:00 Uhr", Enabled = false, NextRun = null }
            };
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

    /// <summary>Fenster "Zeitpläne": vorhandene Aufgaben mit Schalter (aktiv) und Löschen, dazu "Neuer Zeitplan".</summary>
    internal sealed class ScheduleForm : FixedForm, IViewLayout
    {
        private readonly float k;
        private readonly DeviceStore _store;
        private readonly FlowLayoutPanel _list;
        private readonly Label _error, _intro;
        private readonly ModernButton _add, _close;
        private int _width;

        /// <summary>Öffnet "Neuer Zeitplan" im Hauptfenster (gesetzt, wenn die Liste als Ansicht läuft).</summary>
        public Action OpenNew;

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
                Text = "Geräte, Gruppen oder „Alle wecken“ zu festen Zeiten wecken. Die Zeitpläne liegen in der Windows-Aufgabenplanung " +
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
            add.Click += (s, e) =>
            {
                // im Hauptfenster als eigene Ansicht, sonst als Dialog
                if (OpenNew != null) { OpenNew(); return; }
                using (var d = new ScheduleEditForm(_store)) if (d.ShowDialog(this) == DialogResult.OK) Fill();
            };
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
            card.Controls.Add(new Label
            {
                Text = e.Name,
                Font = new Font("Segoe UI Semibold", 10f),
                ForeColor = Ui.Text,
                BackColor = Ui.Card,
                AutoEllipsis = true,
                Location = new Point(S(16), S(10)),
                Size = new Size(width - S(220), S(22)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });
            string sub = e.Summary;
            if (e.NextRun.HasValue && e.Enabled) sub += (sub.Length > 0 ? " · " : "") + "nächster Lauf " + e.NextRun.Value.ToString("ddd dd.MM. HH:mm");
            if (!e.Enabled) sub += (sub.Length > 0 ? " · " : "") + "ausgeschaltet";
            card.Controls.Add(new Label
            {
                Text = sub,
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                AutoEllipsis = true,
                Location = new Point(S(16), S(33)),
                Size = new Size(width - S(220), S(20)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });
            var toggle = new ToggleSwitch("Aktiv", k);
            toggle.SetChecked(e.Enabled);
            toggle.Location = new Point(width - S(70) - toggle.Width - S(16), (card.Height - toggle.Height) / 2);
            toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            string name = e.Name;
            toggle.CheckedChanged += (s, a) =>
            {
                try { Schedules.SetEnabled(name, toggle.Checked); History.Add("Zeitplan", name + (toggle.Checked ? " eingeschaltet" : " ausgeschaltet")); }
                catch (Exception ex) { _error.Text = ex.Message; }
                BeginInvoke(new Action(Fill));
            };
            var del = new ModernButton("", MenuHelper.Delete, false, k) { HeightUnits = 30 };
            del.UpdateSize();
            del.Location = new Point(width - S(16) - del.Width, (card.Height - del.Height) / 2);
            del.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            del.Click += (s, a) =>
            {
                if (MessageBox.Show(this, "Zeitplan „" + name + "“ löschen?", Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                try { Schedules.Delete(name); History.Add("Zeitplan", name + " gelöscht"); }
                catch (Exception ex) { _error.Text = ex.Message; }
                BeginInvoke(new Action(Fill));
            };
            card.Controls.Add(toggle);
            card.Controls.Add(del);
            return card;
        }
    }

    /// <summary>Neuer Zeitplan: Ziel (Alle, Gruppe, Gerät), Uhrzeit und Wochentage.</summary>
    internal sealed class ScheduleEditForm : FixedForm
    {
        private readonly float k;
        private readonly ModernButton _target;
        private readonly ModernTextBox _time;
        private readonly ModernPill[] _days = new ModernPill[7];
        private readonly Label _error;
        private readonly DeviceStore _store;
        private string _args = "/alle", _targetText = "Alle wecken";

        public ScheduleEditForm(DeviceStore store)
        {
            _store = store;
            Theme.Refresh();
            Text = "Neuer Zeitplan";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int W = S(460), m = S(24), y = m, inner = W - 2 * m;

            AddLabel("Was soll geweckt werden?", m, ref y, inner);
            _target = new ModernButton(_targetText, '\0', false, k) { HeightUnits = 32, Location = new Point(m, y), DropDown = true, Font = new Font("Segoe UI", 9.5f) };
            _target.UpdateSize();
            _target.Width = inner;
            _target.Click += (s, e) => ChooseTarget();
            Controls.Add(_target);
            y += _target.Height + S(16);

            AddLabel("Uhrzeit", m, ref y, inner);
            _time = new ModernTextBox(k) { Location = new Point(m, y), Width = S(110), Text = "07:00" };
            // beim Verlassen in die Schreibweise HH:MM bringen (z. B. "0700" -> "07:00"), wie in Sync Pilot
            _time.Box.Leave += (s, e) =>
            {
                string norm;
                if (Schedules.TryParseTime(_time.Text, out norm) && _time.Text != norm) _time.Text = norm;
            };
            Controls.Add(_time);
            var hint = new Label { Text = "z. B. 07:00 oder 0700", ForeColor = Ui.Muted, BackColor = Ui.Back, AutoSize = true, Font = new Font("Segoe UI", 8.75f) };
            Controls.Add(hint);
            hint.Location = new Point(_time.Right + S(12), _time.Top + (_time.Height - hint.Height) / 2);
            y += _time.Height + S(16);

            AddLabel("Wochentage", m, ref y, inner);
            int x = m;
            for (int i = 0; i < 7; i++)
            {
                // Wochentage als Auswahlknöpfe nebeneinander (Klick schaltet ein/aus)
                var day = new ModernPill(Schedules.DayNames[i], k) { Location = new Point(x, y), Checked = i < 5 };
                day.Click += (s, e) => day.Checked = !day.Checked;
                _days[i] = day;
                Controls.Add(day);
                x += day.Width + S(6);
            }
            y += _days[0].Height + S(24);

            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            var save = new ModernButton("Speichern", '\0', true, k);
            save.Click += (s, e) => Save();
            save.Location = new Point(W - m - save.Width, y); // "Abbrechen" links, "Speichern" ganz rechts
            cancel.Location = new Point(save.Left - S(10) - cancel.Width, y);
            _error = new Label { ForeColor = Ui.Red, BackColor = Ui.Back, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(m, y), Size = new Size(cancel.Left - m - S(12), save.Height) };
            Controls.Add(_error);
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
            ClientSize = new Size(W, y + save.Height + m);
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private void AddLabel(string text, int x, ref int y, int width)
        {
            Controls.Add(new Label { Text = text, ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(x, y), Size = new Size(width, S(22)) });
            y += S(24);
        }

        private void ChooseTarget()
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            Action<string, string, char> add = (text, args, glyph) =>
            {
                var item = b.Item(text, glyph, () =>
                {
                    _targetText = text;
                    _args = args;
                    _target.Text = text;
                    _target.Width = ClientSize.Width - S(48);
                });
                item.Checked = args == _args;
                item.Radio = true;
                menu.Items.Add(item);
            };
            add("Alle wecken", "/alle", '\0');
            var groups = _store.Groups();
            if (groups.Count > 0) menu.Items.Add(new ToolStripSeparator());
            foreach (string g in groups) add("Gruppe „" + g + "“", "\"/gruppe:" + g + "\"", '\0');
            if (_store.Devices.Count > 0) menu.Items.Add(new ToolStripSeparator());
            foreach (var d in _store.Devices) add(d.Name, "\"" + d.Name + "\"", '\0');
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(_target, new Point(0, _target.Height));
        }

        private void Save()
        {
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
            string summary = _targetText + " · " + Schedules.DaysText(days) + " · " + time.ToString(@"hh\:mm") + " Uhr";
            string name = _targetText.Replace("„", "").Replace("“", "") + " " + Schedules.DaysText(days) + " " + time.ToString(@"hh\.mm");
            try
            {
                Schedules.Save(name, _args, summary, time, days);
                History.Add("Zeitplan", Schedules.CleanName(name) + " angelegt (" + summary + ")");
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
