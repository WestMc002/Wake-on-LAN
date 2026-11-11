// Wake on LAN – Gerät anlegen und bearbeiten
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>Dialog "Gerät bearbeiten" in drei Spalten: Allgemein und Status | Weckpaket | IPMI und Anstoßen.</summary>
    internal sealed class DeviceForm : FixedForm, IViewLayout, IHasChanges
    {

        // ---------- Rückfrage bei ungespeicherten Änderungen ----------
        private string _baseline;
        private string State() { return ChangeTracker.Snapshot(this, _mode); }
        public bool HasChanges { get { return _baseline != null && State() != _baseline; } }
        public void ResetChanges() { _baseline = State(); }
        // Karten und Knöpfe für die Anordnung nach Breite (1–3 Spalten, als Ansicht im Hauptfenster)
        private CardPanel _cGeneral, _cStatus, _cMagic, _cIpmi, _cPort;
        private readonly Dictionary<CardPanel, int> _natural = new Dictionary<CardPanel, int>();
        private ModernButton _saveButton, _cancelButton;
        private int _margin, _gap, _colW;

        private readonly float k;
        private readonly Device _original;
        private readonly DeviceStore _store;
        private readonly bool _isNew;
        public Device Result;

        private ModernTextBox _name, _group, _host, _macs, _broadcast, _port, _ipmiHost, _ipmiUser, _ipmiPassword, _ipmiTool, _statusPort, _shutdownUser, _shutdownPassword;
        private ModernCheck _inAll, _useMagic, _usePort, _useIpmi, _autoIp, _allowShutdown;
        private ModernButton _browse, _statusMode;
        private StatusMode _mode;
        private readonly List<Control> _statusPortFields = new List<Control>();
        private readonly List<Control> _shutdownFields = new List<Control>();
        private Label _passwordHint, _error;
        private readonly List<Control> _magicFields = new List<Control>();
        private readonly List<Control> _portFields = new List<Control>();
        private readonly List<Control> _ipmiFields = new List<Control>();

        /// <summary>Öffnet den Dialog. device = null legt ein neues Gerät an. Rückgabe: geändertes Gerät oder null.</summary>
        public static Device Edit(IWin32Window owner, Device device, DeviceStore store, bool isNew = false)
        {
            using (var f = new DeviceForm(device, store, isNew || device == null))
                return f.ShowDialog(owner) == DialogResult.OK ? f.Result : null;
        }

        internal DeviceForm(Device device, DeviceStore store, bool isNew)
        {
            _original = device;
            _store = store;
            _isNew = isNew;
            Theme.Refresh();
            Text = isNew ? "Gerät hinzufügen" : "Gerät bearbeiten – " + device.Name;
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            var d = device != null ? device.Clone() : new Device { UseMagic = true };
            int margin = S(24), gap = S(16), colW = S(360);
            int x2 = margin + colW + gap, x3 = x2 + colW + gap;

            // ---------- Spalte 1: Allgemein und Status ----------
            var general = NewCard("Allgemein", margin, margin, colW);
            int y = S(44);
            _name = Field(general, "Name", d.Name, ref y);
            _group = Field(general, "Gruppe (optional)", d.Group, ref y);
            var groups = new AutoCompleteStringCollection();
            groups.AddRange(store.Groups().ToArray());
            _group.Box.AutoCompleteCustomSource = groups;
            _group.Box.AutoCompleteSource = AutoCompleteSource.CustomSource;
            _group.Box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _host = Field(general, "IP-Adresse oder Rechnername", d.Host, ref y);
            _autoIp = Check(general, "Sonst IP automatisch über die MAC ermitteln", d.AutoIp, ref y);
            y = Hint(general, d.LastIp.Length > 0 ? "Zuletzt gefunden: " + d.LastIp + " (nur im eigenen Netz)." : "Über die ARP-Tabelle bzw. eine kurze Suche im eigenen Netz.", y);
            _inAll = Check(general, "Bei „Alle wecken“ dabei", d.InAll, ref y);
            general.Height = y + S(8);

            var status = NewCard("Status und Herunterfahren", margin, general.Bottom + gap, colW);
            y = S(44);
            status.Controls.Add(new Label { Text = "Status prüfen über", ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = false, Location = new Point(S(20), y), Size = new Size(colW - S(40), S(22)) });
            y += S(24);
            _mode = d.Status;
            _statusMode = new ModernButton(Device.StatusModeText(_mode), '\0', false, k) { HeightUnits = 32, Location = new Point(S(20), y), DropDown = true, Font = new Font("Segoe UI", 9.5f) };
            _statusMode.UpdateSize();
            _statusMode.Width = colW - S(40);
            _statusMode.Click += (s, e) => ChooseStatusMode();
            status.Controls.Add(_statusMode);
            y += _statusMode.Height + S(12);
            _statusPort = Field(status, "TCP-Port für den Status", d.StatusPort.ToString(), ref y, S(110), fullLabel: true);
            y = Hint(status, "z. B. 3389 (Remotedesktop), 22 (SSH), 5000 (Synology). 445 ist gesperrt – das würde das Gerät wecken.", y);
            _statusPortFields.Add(_statusPort);
            _allowShutdown = Check(status, "Herunterfahren und Neustart über Windows", d.AllowShutdown, ref y);
            int halfS = (colW - S(40) - S(12)) / 2;
            int yLogin = y;
            _shutdownUser = Field(status, "Benutzer (optional)", d.ShutdownUser, ref y, halfS);
            y = yLogin;
            _shutdownPassword = Field(status, "Kennwort", "", ref y, halfS, 1, S(20) + halfS + S(12));
            _shutdownPassword.Password = true;
            string loginHint = "z. B. Büro-PC\\Admin – leer: dein Windows-Benutzer.";
            if (d.ShutdownPasswordProtected.Length > 0)
                loginHint = d.GetShutdownPassword() != null ? "Kennwort gespeichert (verschlüsselt) – leer lassen, um es zu behalten." : "Gespeichertes Kennwort ist hier nicht lesbar – bitte neu eingeben.";
            y = Hint(status, loginHint, y);
            _shutdownFields.Add(_shutdownUser);
            _shutdownFields.Add(_shutdownPassword);
            status.Height = y + S(8);

            // ---------- Spalte 2: Weckpaket ----------
            var magic = NewCard("Weckpaket (Wake on LAN)", x2, margin, colW);
            y = S(44);
            _useMagic = Check(magic, "Weckpaket senden", d.UseMagic, ref y);
            _macs = Field(magic, "MAC-Adressen (eine pro Zeile)", string.Join("\r\n", d.Macs), ref y, 0, 3);
            _broadcast = Field(magic, "Zusätzliche Zieladresse (optional)", d.Broadcast, ref y);
            y = Hint(magic, "Das Weckpaket geht immer als Broadcast über alle Netzwerkkarten. Hier z. B. die Broadcast-Adresse eines anderen Netzes.", y);
            _magicFields.Add(_macs);
            _magicFields.Add(_broadcast);
            magic.Height = y + S(12);

            // ---------- Spalte 3: IPMI und Anstoßen ----------
            var ipmi = NewCard("IPMI (Server-Fernwartung, z. B. iDRAC)", x3, margin, colW);
            y = S(44);
            _useIpmi = Check(ipmi, "Über IPMI einschalten", d.UseIpmi, ref y);
            int half = (colW - S(40) - S(12)) / 2;
            int yRow = y;
            _ipmiHost = Field(ipmi, "BMC-Adresse", d.IpmiHost, ref y, half);
            int yAfter = y;
            y = yRow;
            _ipmiUser = Field(ipmi, "Benutzer", d.IpmiUser, ref y, half, 1, S(20) + half + S(12));
            y = Math.Max(y, yAfter);
            _ipmiPassword = Field(ipmi, "Kennwort", "", ref y);
            _ipmiPassword.Password = true;
            _passwordHint = new Label { ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = false, Location = new Point(S(20), y - S(6)), Size = new Size(colW - S(40), S(20)), Font = new Font("Segoe UI", 8.5f) };
            ipmi.Controls.Add(_passwordHint);
            if (d.IpmiPasswordProtected.Length > 0)
                _passwordHint.Text = d.GetIpmiPassword() != null
                    ? "Gespeichert (verschlüsselt) – leer lassen, um es zu behalten."
                    : "Gespeichertes Kennwort ist hier nicht lesbar – bitte neu eingeben.";
            y += S(16);
            int browseW = S(40);
            _ipmiTool = Field(ipmi, "ipmitool", d.IpmiTool, ref y, colW - S(40) - browseW - S(8));
            _browse = new ModernButton("", MenuHelper.Folder, false, k) { HeightUnits = 32 };
            _browse.UpdateSize();
            _browse.Size = new Size(browseW, S(32));
            _browse.Location = new Point(colW - S(20) - browseW, _ipmiTool.Top);
            _browse.Click += (s, e) => BrowseIpmiTool();
            ipmi.Controls.Add(_browse);
            _ipmiFields.AddRange(new Control[] { _ipmiHost, _ipmiUser, _ipmiPassword, _ipmiTool, _browse });
            ipmi.Height = y + S(8);

            var port = NewCard("Anstoßen", x3, ipmi.Bottom + gap, colW);
            y = S(44);
            _usePort = Check(port, "Über Netzwerkzugriff anstoßen", d.UsePort, ref y);
            y = Hint(port, "Verbindungsversuch auf einen Port – weckt Netzwerkkarten mit „Wake on Pattern Match“. Nutzt die IP-Adresse.", y);
            _port = Field(port, "Port", d.Port.ToString(), ref y, S(110), fullLabel: true);
            _portFields.Add(_port);
            port.Height = y + S(8);

            _cGeneral = general; _cStatus = status; _cMagic = magic; _cIpmi = ipmi; _cPort = port;
            foreach (var c in new[] { general, status, magic, ipmi, port }) _natural[c] = c.Height;
            _margin = margin; _gap = gap; _colW = colW;
            MakeStretchable();
            int bottom = Math.Max(status.Bottom, Math.Max(magic.Bottom, port.Bottom));

            foreach (var c in new Control[] { general, status, magic, ipmi, port }) Controls.Add(c);

            // ---------- Knöpfe ----------
            int W = x3 + colW + margin;
            y = bottom + S(20);
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            var save = new ModernButton("Speichern", '\0', true, k) { Margin = Padding.Empty };
            save.Click += (s, e) => Save();
            save.Location = new Point(W - margin - save.Width, y); // "Abbrechen" links, "Speichern" ganz rechts
            cancel.Location = new Point(save.Left - S(10) - cancel.Width, y);
            _error = new Label
            {
                ForeColor = Ui.Red,
                BackColor = Ui.Back,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(margin, y),
                Size = new Size(cancel.Left - margin - S(12), save.Height)
            };
            Controls.Add(_error);
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = null; // Enter = Speichern über ProcessDialogKey, im MAC-Feld bleibt Enter ein Zeilenumbruch
            CancelButton = cancel;
            _saveButton = save;
            _cancelButton = cancel;

            Arrange(W); // als eigenes Fenster (Screenshots): drei Spalten

            _useMagic.CheckedChanged += (s, e) => UpdateEnabled();
            _usePort.CheckedChanged += (s, e) => UpdateEnabled();
            _useIpmi.CheckedChanged += (s, e) => UpdateEnabled();
            _allowShutdown.CheckedChanged += (s, e) => UpdateEnabled();
            UpdateEnabled();
            Shown += (s, e) => { _name.Box.Focus(); _name.Box.SelectAll(); };
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        /// <summary>
        /// Karten nach verfügbarer Breite in 3, 2 oder 1 Spalte anordnen (Kartenbreite bleibt), Spalten unten bündig;
        /// darunter rechts "Abbrechen" und "Speichern", links davon die Fehlerzeile.
        /// </summary>
        public void Arrange(int width)
        {
            int m = _margin, g = _gap;
            int cols = width >= 2 * m + 3 * _colW + 2 * g ? 3 : width >= 2 * m + 2 * _colW + g ? 2 : 1;
            // Karten nutzen die ganze Breite (mindestens die ursprüngliche Spaltenbreite)
            int cw = Math.Max(_colW, (width - 2 * m - (cols - 1) * g) / cols);
            List<CardPanel>[] layout;
            if (cols == 3) layout = new[] { new List<CardPanel> { _cGeneral, _cStatus }, new List<CardPanel> { _cMagic }, new List<CardPanel> { _cIpmi, _cPort } };
            else if (cols == 2) layout = new[] { new List<CardPanel> { _cGeneral, _cStatus, _cPort }, new List<CardPanel> { _cMagic, _cIpmi } };
            else layout = new[] { new List<CardPanel> { _cGeneral, _cStatus, _cMagic, _cIpmi, _cPort } };

            int scrollY = AutoScroll ? AutoScrollPosition.Y : 0;
            int bottom = 0;
            for (int i = 0; i < layout.Length; i++)
            {
                int y = m + scrollY;
                foreach (var c in layout[i])
                {
                    c.SetBounds(m + i * (cw + g), y, cw, _natural[c]);
                    y = c.Bottom + g;
                }
                bottom = Math.Max(bottom, y - g);
            }
            if (cols > 1) // Spalten unten bündig: die letzte Karte jeder Spalte wächst
                foreach (var col in layout) { var last = col[col.Count - 1]; last.Height += bottom - last.Bottom; }

            int right = m + cols * cw + (cols - 1) * g;
            int by = bottom + S(20);
            _saveButton.Location = new Point(right - _saveButton.Width, by);
            _cancelButton.Location = new Point(_saveButton.Left - S(10) - _cancelButton.Width, by);
            _error.SetBounds(m, by, Math.Max(S(40), _cancelButton.Left - m - S(12)), _saveButton.Height);
            if (TopLevel) ClientSize = new Size(right + m, by - scrollY + _saveButton.Height + m);
        }

        /// <summary>
        /// Inhalte der Karten wachsen mit der Kartenbreite: Felder, Texte und Schalter über die ganze Breite per Anchor,
        /// Knöpfe am rechten Rand bleiben rechts, Feldpaare (Benutzer/Kennwort, BMC-Adresse/Benutzer) teilen sich die Breite.
        /// </summary>
        private void MakeStretchable()
        {
            foreach (var card in new[] { _cGeneral, _cStatus, _cMagic, _cIpmi, _cPort })
            {
                int inner = _colW - S(20);
                foreach (Control c in card.Controls)
                {
                    bool reachesRight = c.Right >= inner - S(2);
                    bool startsLeft = c.Left <= S(22);
                    if (reachesRight && startsLeft) c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                    else if (reachesRight && c is Button) c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                    else if (startsLeft && c == _ipmiTool) c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                }
            }
            SplitPair(_cStatus, _shutdownUser, _shutdownPassword);
            SplitPair(_cIpmi, _ipmiHost, _ipmiUser);
        }

        /// <summary>Zwei Felder nebeneinander je halb so breit wie die Karte (mit Beschriftung darüber).</summary>
        private void SplitPair(CardPanel card, Control left, Control right)
        {
            Func<Control, Label> labelOf = box => card.Controls.OfType<Label>().FirstOrDefault(l => l.Left == box.Left && l.Top == box.Top - S(24));
            var ll = labelOf(left);
            var rl = labelOf(right);
            foreach (var c in new Control[] { left, right, ll, rl }) if (c != null) c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            card.Resize += (s, e) =>
            {
                int half = (card.Width - S(40) - S(12)) / 2;
                int x2 = S(20) + half + S(12);
                left.Width = half;
                right.SetBounds(x2, right.Top, half, right.Height);
                if (ll != null) ll.Width = half;
                if (rl != null) rl.SetBounds(x2, rl.Top, half, rl.Height);
            };
        }

        // ================================================================== Aufbau

        private CardPanel NewCard(string title, int x, int y, int w)
        {
            return new CardPanel(k) { Title = title, Location = new Point(x, y), Width = w, Height = S(100) };
        }

        /// <summary>Beschriftung und Eingabefeld untereinander. width = 0: volle Breite.</summary>
        private ModernTextBox Field(CardPanel card, string label, string value, ref int y, int width = 0, int lines = 1, int x = 0, bool fullLabel = false)
        {
            if (x == 0) x = S(20);
            if (width == 0) width = card.Width - S(40);
            card.Controls.Add(new Label
            {
                Text = label,
                ForeColor = Ui.Text,
                BackColor = Ui.Card,
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(x, y),
                Size = new Size(fullLabel ? card.Width - x - S(20) : width, S(22))
            });
            y += S(24);
            var box = new ModernTextBox(k, lines > 1, lines) { Location = new Point(x, y), Width = width, Text = value };
            card.Controls.Add(box);
            y += box.Height + S(12);
            return box;
        }

        private ModernCheck Check(CardPanel card, string text, bool value, ref int y)
        {
            var c = new ModernCheck(text, k) { Location = new Point(S(18), y), Checked = value };
            c.Width = card.Width - S(38); // Text links, Schalter rechts (wie in "Steuerung per Handy")
            card.Controls.Add(c);
            y += c.Height + S(8);
            return c;
        }

        /// <summary>Grauer Hinweistext (mehrzeilig, Höhe nach Inhalt).</summary>
        private int Hint(CardPanel card, string text, int y)
        {
            int w = card.Width - S(40);
            var font = new Font("Segoe UI", 8.5f);
            int h = TextRenderer.MeasureText(text, font, new Size(w, 0), TextFormatFlags.WordBreak).Height;
            card.Controls.Add(new Label
            {
                Text = text,
                Font = font,
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                AutoSize = false,
                Location = new Point(S(20), y - S(4)),
                Size = new Size(w, h + S(2))
            });
            return y - S(4) + h + S(12);
        }

        private void UpdateEnabled()
        {
            foreach (var c in _magicFields) c.Enabled = _useMagic.Checked;
            foreach (var c in _portFields) c.Enabled = _usePort.Checked;
            foreach (var c in _ipmiFields) c.Enabled = _useIpmi.Checked;
            foreach (var c in _statusPortFields) c.Enabled = _mode == StatusMode.Port || _mode == StatusMode.PingPort;
            foreach (var c in _shutdownFields) c.Enabled = _allowShutdown.Checked;
        }

        /// <summary>Auswahl "Status prüfen über" als Menü im Windows-11-Stil.</summary>
        private void ChooseStatusMode()
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            foreach (StatusMode m in new[] { StatusMode.Ping, StatusMode.Port, StatusMode.PingPort, StatusMode.Ipmi, StatusMode.Off })
            {
                var mode = m;
                var item = b.Item(Device.StatusModeText(mode), '\0', () =>
                {
                    _mode = mode;
                    _statusMode.Text = Device.StatusModeText(mode);
                    _statusMode.Width = _statusMode.Parent.Width - S(40);
                    UpdateEnabled();
                });
                item.Checked = mode == _mode;
                item.Radio = true;
                menu.Items.Add(item);
            }
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(_statusMode, new Point(0, _statusMode.Height));
        }

        private void BrowseIpmiTool()
        {
            using (var dlg = new OpenFileDialog { Title = "ipmitool auswählen", Filter = "ipmitool.exe|ipmitool.exe|Programme (*.exe)|*.exe", CheckFileExists = true })
            {
                try
                {
                    string dir = Path.GetDirectoryName(_ipmiTool.Text);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                }
                catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) _ipmiTool.Text = dlg.FileName;
            }
        }

        // ================================================================== Speichern

        /// <summary>Enter = Speichern (globale Vorgabe), außer im mehrzeiligen MAC-Feld.</summary>
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Enter && !_macs.ContainsFocus)
            {
                // auf einem Knopf (z. B. Abbrechen oder Auswahlfeld) bleibt Enter beim Knopf
                Control c = ActiveControl;
                while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
                if (!(c is IButtonControl)) { Save(); return true; }
            }
            return base.ProcessDialogKey(keyData);
        }

        private void Save()
        {
            var d = _original != null ? _original.Clone() : new Device();
            d.Name = _name.Text.Trim();
            d.Host = _host.Text.Trim();
            d.Group = _group.Text.Trim();
            d.AutoIp = _autoIp.Checked;
            d.AllowShutdown = _allowShutdown.Checked;
            d.ShutdownUser = _shutdownUser.Text.Trim();
            if (d.ShutdownUser.Length == 0) d.ShutdownPasswordProtected = "";
            else if (_shutdownPassword.Text.Length > 0) d.SetShutdownPassword(_shutdownPassword.Text);
            if (d.AllowShutdown && d.ShutdownUser.Length > 0 && d.GetShutdownPassword() == null) { Fail("Bitte das Kennwort für " + d.ShutdownUser + " eingeben.", _shutdownPassword); return; }
            d.Status = _mode;
            // Schreibweise einer vorhandenen Gruppe übernehmen ("büro" -> "Büro")
            var existing = _store.Groups().FirstOrDefault(g => Device.SameGroup(g, d.Group));
            if (existing != null) d.Group = existing;
            d.InAll = _inAll.Checked;
            d.UseMagic = _useMagic.Checked;
            d.UsePort = _usePort.Checked;
            d.UseIpmi = _useIpmi.Checked;

            if (d.Name.Length == 0) { Fail("Bitte einen Namen eingeben.", _name); return; }
            var other = _store.Find(d.Name);
            if (other != null && (_isNew || other != _original)) { Fail("Ein Gerät mit diesem Namen gibt es schon.", _name); return; }
            if (!d.UseMagic && !d.UsePort && !d.UseIpmi) { Fail("Bitte mindestens eine Art zu wecken auswählen.", null); return; }

            // MAC-Adressen (auch wenn das Weckpaket aus ist, damit nichts verloren geht)
            var macs = new List<string>();
            foreach (string line in _macs.Text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Trim().Length == 0) continue;
                string mac = Device.NormalizeMac(line);
                if (mac == null) { Fail("Ungültige MAC-Adresse: " + line.Trim(), _macs); return; }
                if (!macs.Contains(mac)) macs.Add(mac);
            }
            d.Macs = macs;
            d.Broadcast = _broadcast.Text.Trim();
            if (d.UseMagic)
            {
                if (macs.Count == 0) { Fail("Bitte mindestens eine MAC-Adresse eingeben.", _macs); return; }
                System.Net.IPAddress ip;
                if (d.Broadcast.Length > 0 && !System.Net.IPAddress.TryParse(d.Broadcast, out ip)) { Fail("Die Zieladresse ist keine gültige IP-Adresse.", _broadcast); return; }
            }

            int portNo;
            if (!int.TryParse(_port.Text.Trim(), out portNo) || portNo < 1 || portNo > 65535)
            {
                if (d.UsePort) { Fail("Der Port muss zwischen 1 und 65535 liegen.", _port); return; }
                portNo = Device.DefaultPort;
            }
            d.Port = portNo;
            int statusPort;
            bool needPort = _mode == StatusMode.Port || _mode == StatusMode.PingPort;
            if (!int.TryParse(_statusPort.Text.Trim(), out statusPort) || statusPort < 1 || statusPort > 65535)
            {
                if (needPort) { Fail("Der Status-Port muss zwischen 1 und 65535 liegen.", _statusPort); return; }
                statusPort = Device.DefaultStatusPort;
            }
            if (needPort && Device.IsWakingPort(statusPort)) { Fail("Port " + statusPort + " würde das Gerät wecken – bitte einen anderen nehmen (z. B. 3389).", _statusPort); return; }
            d.StatusPort = statusPort;
            if (_mode == StatusMode.Ipmi && !d.UseIpmi) { Fail("Status über IPMI braucht „Über IPMI einschalten“.", null); return; }
            if (d.AutoIp && d.Macs.Count == 0) { Fail("Für „IP automatisch“ wird eine MAC-Adresse gebraucht.", _macs); return; }
            if (d.UsePort && d.Host.Length == 0 && !d.AutoIp) { Fail("Zum Anstoßen wird die IP-Adresse gebraucht (oder „IP automatisch“).", _host); return; }

            d.IpmiHost = _ipmiHost.Text.Trim();
            d.IpmiUser = _ipmiUser.Text.Trim();
            d.IpmiTool = _ipmiTool.Text.Trim().Length > 0 ? _ipmiTool.Text.Trim() : Device.DefaultIpmiTool;
            if (_ipmiPassword.Text.Length > 0) d.SetIpmiPassword(_ipmiPassword.Text);
            if (d.UseIpmi)
            {
                if (d.IpmiHost.Length == 0) { Fail("Bitte die BMC-Adresse eingeben.", _ipmiHost); return; }
                if (d.IpmiUser.Length == 0) { Fail("Bitte den IPMI-Benutzer eingeben.", _ipmiUser); return; }
                if (d.GetIpmiPassword() == null) { Fail("Bitte das IPMI-Kennwort eingeben.", _ipmiPassword); return; }
                if (!File.Exists(d.IpmiTool)) { Fail("ipmitool wurde nicht gefunden.", _ipmiTool); return; }
            }

            Result = d;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Fail(string text, ModernTextBox field)
        {
            _error.Text = text;
            if (field != null) { field.Box.Focus(); field.Box.SelectAll(); }
        }
    }

    /// <summary>Kleiner Eingabedialog (z. B. Gruppe umbenennen, neue Gruppe).</summary>
    internal sealed class TextPrompt : FixedForm
    {
        private readonly ModernTextBox _box;

        public static string Ask(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new TextPrompt(title, label, value))
                return f.ShowDialog(owner) == DialogResult.OK ? f._box.Text.Trim() : null;
        }

        private TextPrompt(string title, string label, string value)
        {
            Theme.Refresh();
            Text = title;
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            float k;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int m = (int)(24 * k), w = (int)(380 * k);
            var lbl = new Label { Text = label, ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(m, m), Size = new Size(w, (int)(22 * k)) };
            _box = new ModernTextBox(k) { Location = new Point(m, lbl.Bottom + (int)(4 * k)), Width = w, Text = value ?? "" };
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            var ok = new ModernButton("OK", '\0', true, k) { Margin = Padding.Empty };
            ok.Click += (s, e) => { if (_box.Text.Trim().Length > 0) { DialogResult = DialogResult.OK; Close(); } };
            int by = _box.Bottom + (int)(20 * k);
            ok.Location = new Point(m + w - ok.Width, by); // "Abbrechen" links, "OK" ganz rechts
            cancel.Location = new Point(ok.Left - (int)(10 * k) - cancel.Width, by);
            Controls.Add(lbl);
            Controls.Add(_box);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(m + w + m, by + ok.Height + m);
            Shown += (s, e) => { _box.Box.Focus(); _box.Box.SelectAll(); };
            Theme.Apply(this);
        }
    }
}
