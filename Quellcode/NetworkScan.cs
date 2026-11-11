// Wake on LAN – Geräte im Netzwerk suchen und übernehmen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>
    /// Dialog "Im Netzwerk suchen": fragt alle Adressen des eigenen Netzes per ARP ab und listet die gefundenen Geräte
    /// mit IP, MAC und Rechnername. Ausgewählte Geräte werden übernommen (eines: zum Bearbeiten, mehrere: direkt).
    /// </summary>
    internal sealed class ScanForm : FixedForm
    {
        private readonly float k;
        private readonly DeviceStore _store;
        private readonly FlowLayoutPanel _list;
        private readonly Label _status;
        private readonly ScanProgress _progress;
        private readonly ModernButton _add, _search, _rangeButton;
        private readonly ModernTextBox _from, _to;
        private readonly List<Arp.Range> _ranges;
        private bool _settingRange;
        private readonly List<ScanRow> _rows = new List<ScanRow>();
        private volatile bool _cancel;
        private bool _running;

        /// <summary>Ausgewählte Geräte (nach "Hinzufügen").</summary>
        public readonly List<Arp.Found> Selected = new List<Arp.Found>();

        public ScanForm(DeviceStore store)
        {
            _store = store;
            Theme.Refresh();
            Text = "Geräte im Netzwerk suchen";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int m = S(24), w = S(720);

            var intro = new Label
            {
                Text = "Sucht eingeschaltete Geräte per ARP – das weckt nichts und findet auch Geräte, deren Firewall Ping blockiert. " +
                    "Ausgeschaltete Geräte erscheinen nicht; Antworten gibt es nur aus dem eigenen Netz (nicht über Router oder VPN).",
                ForeColor = Ui.Muted, BackColor = Ui.Back, AutoSize = false, Font = new Font("Segoe UI", 9f),
                Location = new Point(m, m), Size = new Size(w, S(40))
            };
            Controls.Add(intro);
            int y = intro.Bottom + S(8);

            // Bereich: Auswahl der eigenen Netze oder eigene Eingabe von–bis
            _ranges = Arp.LocalRanges();
            int fieldW = S(130), gap = S(10);
            Controls.Add(new Label { Text = "Netz", ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(m, y), Size = new Size(S(300), S(22)) });
            int xFrom = m + S(300) + gap, xTo = xFrom + fieldW + gap;
            Controls.Add(new Label { Text = "Von", ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(xFrom, y), Size = new Size(fieldW, S(22)) });
            Controls.Add(new Label { Text = "Bis", ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(xTo, y), Size = new Size(fieldW, S(22)) });
            y += S(24);
            _rangeButton = new ModernButton("Eigener Bereich", '\0', false, k) { HeightUnits = 32, DropDown = true, Font = new Font("Segoe UI", 9.5f), Margin = Padding.Empty, FixedWidth = S(300) };
            _rangeButton.UpdateSize();
            _rangeButton.Location = new Point(m, y);
            _rangeButton.Click += (s, e) => ChooseRange();
            Controls.Add(_rangeButton);
            _from = new ModernTextBox(k) { Location = new Point(xFrom, y), Width = fieldW };
            _to = new ModernTextBox(k) { Location = new Point(xTo, y), Width = fieldW };
            _from.Box.TextChanged += (s, e) => RangeEdited();
            _to.Box.TextChanged += (s, e) => RangeEdited();
            _from.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartScan(); } };
            _to.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartScan(); } };
            Controls.Add(_from);
            Controls.Add(_to);
            // feste Breite für "Suchen" und "Anhalten", rechts bündig mit der Liste
            _search = new ModernButton("Anhalten", MenuHelper.Search, false, k) { HeightUnits = 32, Margin = Padding.Empty };
            _search.UpdateSize();
            _search.FixedWidth = _search.Width;
            _search.Text = "Suchen";
            _search.Location = new Point(m + w - _search.Width, y);
            _search.Click += (s, e) => { if (_running) _cancel = true; else StartScan(); };
            Controls.Add(_search);
            y += _from.Height + S(14);
            _progress = new ScanProgress(k) { Location = new Point(m, y), Size = new Size(w, S(4)) };
            Controls.Add(_progress);
            y += S(12);
            _status = new Label { ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(m, y), Size = new Size(w, S(22)) };
            Controls.Add(_status);
            y += S(30);

            _list = new FlowLayoutPanel
            {
                Location = new Point(m, y),
                Size = new Size(w, S(380)),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Ui.Back,
                Padding = new Padding(0, 0, S(4), 0)
            };
            _list.HorizontalScroll.Maximum = 0;
            _list.HorizontalScroll.Visible = false;
            _list.ClientSizeChanged += (s, e) => FitRows();
            Controls.Add(_list);
            y = _list.Bottom + S(20);

            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            _add = new ModernButton("Hinzufügen", MenuHelper.Add, true, k) { Margin = Padding.Empty, Enabled = false };
            _add.Click += (s, e) => Accept();
            _add.Location = new Point(m + w - _add.Width, y); // "Abbrechen" links, "Hinzufügen" ganz rechts
            cancel.Location = new Point(_add.Left - S(10) - cancel.Width, y);
            Controls.Add(_add);
            Controls.Add(cancel);
            CancelButton = cancel;
            ClientSize = new Size(w + 2 * m, y + _add.Height + m);

            if (_ranges.Count > 0) SetRange(_ranges[0]);
            // nicht sofort suchen: erst Netz bzw. Bereich wählen, dann "Suchen" (oder Enter)
            _status.Text = _ranges.Count > 0 ? "Netz auswählen oder Von/Bis eintragen und auf „Suchen“ klicken."
                : "Kein eigenes Netz erkannt – bitte Von/Bis eintragen und auf „Suchen“ klicken.";
            AcceptButton = null;
            Shown += (s, e) => _search.Focus();
            FormClosing += (s, e) => _cancel = true;
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        // ------------------------------------------------------------------ Bereich

        private void SetRange(Arp.Range r)
        {
            _settingRange = true;
            _from.Text = r.FirstText;
            _to.Text = r.LastText;
            _settingRange = false;
            _rangeButton.Text = r.Name;
        }

        /// <summary>Von/Bis von Hand geändert: passt es zu keinem eigenen Netz mehr, heißt die Auswahl "Eigener Bereich".</summary>
        private void RangeEdited()
        {
            if (_settingRange) return;
            var r = ReadRange();
            var match = r == null ? null : _ranges.FirstOrDefault(x => x.First == r.First && x.Last == r.Last);
            _rangeButton.Text = match != null ? match.Name : "Eigener Bereich";
        }

        private void ChooseRange()
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            var current = ReadRange();
            foreach (var r in _ranges)
            {
                var range = r;
                var item = b.Item(r.Name + "  (" + r.Count + " Adressen)", '\0', () => SetRange(range));
                item.Radio = true;
                item.Checked = current != null && current.First == r.First && current.Last == r.Last;
                menu.Items.Add(item);
            }
            var own = b.Item("Eigener Bereich (Von/Bis eintragen)", '\0', () => { _rangeButton.Text = "Eigener Bereich"; _from.Box.Focus(); _from.Box.SelectAll(); });
            own.Radio = true;
            own.Checked = current == null || !_ranges.Any(x => x.First == current.First && x.Last == current.Last);
            menu.Items.Add(own);
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            MenuHelper.ShowOnScreen(menu, _rangeButton, new Point(0, _rangeButton.Height));
        }

        /// <summary>Bereich aus Von/Bis (null, wenn eine Adresse ungültig ist).</summary>
        private Arp.Range ReadRange()
        {
            uint? a = Arp.ParseIp(_from.Text), z = Arp.ParseIp(_to.Text);
            if (a == null || z == null) return null;
            return new Arp.Range { First = a.Value, Last = z.Value };
        }

        // ------------------------------------------------------------------ Suche

        private void StartScan()
        {
            if (_running) return;
            var range = ReadRange();
            if (range == null) { _status.ForeColor = Ui.Red; _status.Text = "Bitte bei Von und Bis gültige IP-Adressen eintragen, z. B. 192.168.178.1 bis 192.168.178.254."; return; }
            if (range.Count <= 0) { _status.ForeColor = Ui.Red; _status.Text = "Die Von-Adresse muss vor der Bis-Adresse liegen."; return; }
            if (range.Count > Arp.MaxScan) { _status.ForeColor = Ui.Red; _status.Text = "Der Bereich ist zu groß (" + range.Count + " Adressen) – höchstens " + Arp.MaxScan + " auf einmal."; return; }
            _running = true;
            _cancel = false;
            _search.Text = "Anhalten";
            foreach (var r in _rows) r.Dispose();
            _rows.Clear();
            _list.Controls.Clear();
            UpdateAddButton();
            _progress.Value = 0;
            _status.ForeColor = Ui.Text;
            _status.Text = "Suche läuft …";
            int lastShown = -1;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<Arp.Found> found;
                string error = null;
                try
                {
                    found = Arp.Scan(range, (done, total) =>
                    {
                        int percent = total == 0 ? 100 : done * 100 / total;
                        if (percent == lastShown) return;
                        lastShown = percent;
                        Post(() =>
                        {
                            _progress.Value = percent;
                            _status.Text = done < total ? "Prüfe " + done + " von " + total + " Adressen …" : "Ermittle Rechnernamen …";
                        });
                    }, () => _cancel);
                }
                catch (Exception ex) { found = new List<Arp.Found>(); error = ex.Message; }
                Post(() => ShowResult(found, error));
            });
        }

        private void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        private void ShowResult(List<Arp.Found> found, string error)
        {
            _running = false;
            _search.Text = "Suchen";
            _progress.Value = 100;
            if (error != null) { _status.ForeColor = Ui.Red; _status.Text = "Suche fehlgeschlagen: " + error; return; }
            if (found.Count == 0)
            {
                _status.ForeColor = Ui.Yellow;
                _status.Text = _cancel ? "Suche angehalten – keine Geräte gefunden."
                    : "Keine Geräte gefunden. Liegt der Bereich im eigenen Netz dieses PCs (nicht hinter einem Router oder VPN)?";
                return;
            }
            int known = 0;
            _list.SuspendLayout();
            foreach (var f in found)
            {
                var existing = _store.Devices.FirstOrDefault(d => d.Macs.Any(x => string.Equals(x, f.Mac, StringComparison.OrdinalIgnoreCase)))
                    ?? _store.Devices.FirstOrDefault(d => d.Host.Length > 0 && d.Host == f.Ip);
                string note = f.Self ? "dieser PC" : existing != null ? "schon in der Liste: " + existing.Name : "";
                if (existing != null) known++;
                var row = new ScanRow(f, note, k) { Width = RowWidth() };
                row.Click += (s, e) => { var r = (ScanRow)s; r.Selected = !r.Selected; r.Invalidate(); UpdateAddButton(); };
                row.DoubleClick += (s, e) => { var r = (ScanRow)s; foreach (var o in _rows) o.Selected = o == r; Accept(); };
                _rows.Add(row);
                _list.Controls.Add(row);
            }
            _list.ResumeLayout();
            FitRows();
            _status.ForeColor = Ui.Text;
            _status.Text = (_cancel ? "Angehalten – " : "") + found.Count + (found.Count == 1 ? " Gerät" : " Geräte") + " gefunden" + (known > 0 ? ", davon " + known + " schon in der Liste" : "") +
                ". Zum Übernehmen anklicken (Doppelklick: sofort bearbeiten).";
        }

        /// <summary>Zeilenbreite: Platz für die senkrechte Bildlaufleiste immer freihalten (keine waagrechte Leiste).</summary>
        private int RowWidth()
        {
            return _list.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(2);
        }

        private void FitRows()
        {
            int w = RowWidth();
            foreach (var r in _rows) if (r.Width != w) r.Width = w;
        }

        private void UpdateAddButton()
        {
            int n = _rows.Count(r => r.Selected);
            // rechte Kante bleibt, "Abbrechen" rückt mit
            int right = _add.Right;
            _add.Text = n > 1 ? "Hinzufügen (" + n + ")" : "Hinzufügen";
            _add.UpdateSize();
            _add.Left = right - _add.Width;
            if (CancelButton is Control) ((Control)CancelButton).Left = _add.Left - S(10) - ((Control)CancelButton).Width;
            _add.Enabled = n > 0;
        }

        private void Accept()
        {
            Selected.Clear();
            Selected.AddRange(_rows.Where(r => r.Selected).Select(r => r.Found));
            if (Selected.Count == 0) return;
            _cancel = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Gerät aus einem Suchergebnis: Name aus dem Rechnernamen, IP (sonst automatisch über die MAC), Weckpaket an die MAC.</summary>
        public static Device ToDevice(Arp.Found f, DeviceStore store)
        {
            string name = f.HostName.Length > 0 ? f.HostName : "Gerät " + f.Ip;
            string unique = name;
            for (int n = 2; store.Find(unique) != null; n++) unique = name + " (" + n + ")";
            var d = new Device { Name = unique, Host = f.Ip, AutoIp = true, UseMagic = true };
            d.Macs.Add(f.Mac);
            return d;
        }
    }

    /// <summary>Eine Zeile im Suchergebnis: Auswahlkreis, Name, IP · MAC, Hinweis rechts.</summary>
    internal sealed class ScanRow : Control
    {
        private readonly float k;
        private bool _hover;
        public readonly Arp.Found Found;
        public readonly string Note;
        public bool Selected;

        public ScanRow(Arp.Found found, string note, float scale)
        {
            Found = found;
            Note = note ?? "";
            k = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = (int)(40 * k);
            Margin = new Padding(0, 0, 0, (int)(4 * k));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Ui.Rounded(r, 6 * k))
            {
                using (var b = new SolidBrush(Selected ? Ui.InfoBack : _hover ? Ui.CardHover : Ui.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Selected ? Ui.Primary : Ui.Border, Selected ? 1.5f * k : 1f)) g.DrawPath(pen, p);
            }
            // Auswahlkreis (ausgefüllt mit Haken, wenn ausgewählt)
            float d = 18 * k, cx = 22 * k, cy = Height / 2f;
            if (Selected)
            {
                using (var b = new SolidBrush(Ui.Primary)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                using (var pen = new Pen(Ui.PrimaryText, 1.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float m = 4f * k;
                    g.DrawLines(pen, new[] { new PointF(cx - m, cy + 0.2f * k), new PointF(cx - m / 3, cy + m * 0.75f), new PointF(cx + m, cy - m * 0.7f) });
                }
            }
            else
                using (var pen = new Pen(Ui.Muted, 1.2f * k)) g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);

            // Spalten: Name | IP | MAC | Hinweis rechts (gleiche Spalten in allen Zeilen)
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            var measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int x = (int)(44 * k), nameW = (int)(230 * k), ipW = (int)(120 * k), macW = (int)(150 * k), pad = (int)(12 * k);
            using (var fName = new Font("Segoe UI Semibold", 10f))
            using (var fSmall = new Font("Segoe UI", 9f))
            {
                string name = Found.HostName.Length > 0 ? Found.HostName : "(ohne Namen)";
                TextRenderer.DrawText(g, name, fName, new Rectangle(x, 0, nameW - pad, Height), Found.HostName.Length > 0 ? Ui.Text : Ui.Muted, flags);
                int xIp = x + nameW, xMac = xIp + ipW, xNote = xMac + macW;
                TextRenderer.DrawText(g, Found.Ip, fSmall, new Rectangle(xIp, 0, ipW - pad, Height), Ui.Muted, flags);
                TextRenderer.DrawText(g, Found.Mac, fSmall, new Rectangle(xMac, 0, macW - pad, Height), Ui.Muted, flags);
                if (Note.Length > 0)
                {
                    int right = Width - pad;
                    int noteW = Math.Min(TextRenderer.MeasureText(g, Note, fSmall, Size.Empty, measure).Width + 2, Math.Max(0, right - xNote));
                    TextRenderer.DrawText(g, Note, fSmall, new Rectangle(right - noteW, 0, noteW, Height), Found.Self ? Ui.Muted : Ui.Yellow, flags | TextFormatFlags.Right);
                }
            }
        }
    }

    /// <summary>Schmaler Fortschrittsbalken in der Akzentfarbe.</summary>
    internal sealed class ScanProgress : Control
    {
        private readonly float k;
        private int _value;

        public ScanProgress(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, Width, Height);
            using (var p = Ui.Rounded(r, Height / 2f))
            using (var b = new SolidBrush(Ui.Border)) g.FillPath(b, p);
            if (_value <= 0) return;
            var f = new RectangleF(0, 0, Math.Max(Height, Width * _value / 100f), Height);
            using (var p = Ui.Rounded(f, Height / 2f))
            using (var b = new SolidBrush(Ui.Primary)) g.FillPath(b, p);
        }
    }
}
