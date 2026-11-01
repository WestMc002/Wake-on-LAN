// Wake on LAN – Hauptfenster mit der Geräteliste
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
    internal sealed class MainForm : Form
    {
        private const int CheckIntervalSeconds = 15;
        private const int WakingCheckSeconds = 3;
        private const int WakingTimeoutSeconds = 180;

        private readonly DeviceStore _store;
        private readonly float k;
        private readonly HeaderPanel _header;
        private readonly FlowLayoutPanel _list;
        private readonly Panel _bar;
        private readonly Label _message;
        private readonly ModernButton _wakeAll;
        private readonly ToolTip _tip = new ToolTip();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly List<DeviceRow> _rows = new List<DeviceRow>();
        private readonly List<GroupHeader> _headers = new List<GroupHeader>();

        private readonly bool _demo;
        private readonly Notifier _notifier;
        private readonly ModernTextBox _search;
        private string _filter = "";
        /// <summary>Geänderte LastIp/LastOnline, die noch gespeichert werden müssen (höchstens einmal pro Minute).</summary>
        private bool _dirty;
        private DateTime _lastSave = DateTime.Now;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>demo = true: Beispieldaten für die Screenshots (kein Ping, kein Timer, nichts wird gespeichert).</summary>
        public MainForm(DeviceStore store, bool demo = false)
        {
            _store = store;
            _demo = demo;
            Theme.Refresh();
            Text = Ui.AppName;
            Icon = Ui.WindowIcon();
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            // ---------- Kopf ----------
            _header = new HeaderPanel(Ui.AppName, "Geräte im Netzwerk einschalten – per Weckpaket, Anstoßen oder IPMI", k);
            var headButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Ui.Back, Margin = Padding.Empty, Padding = Padding.Empty };
            var add = new ModernButton("Gerät hinzufügen", MenuHelper.Add, true, k);
            add.Click += (s, e) => AddDevice();
            var more = new ModernButton("", MenuHelper.More, false, k) { Margin = Padding.Empty };
            _tip.SetToolTip(more, "Weitere Funktionen");
            more.Click += (s, e) => ShowMainMenu(more, new Point(0, more.Height));
            headButtons.Controls.Add(add);
            headButtons.Controls.Add(more);
            _header.SetRight(headButtons);

            // ---------- Suche ----------
            var searchBar = new Panel { Dock = DockStyle.Top, Height = S(48), BackColor = Ui.Back };
            _search = new ModernTextBox(k) { Location = new Point(S(28), S(4)), Width = S(340) };
            _search.Box.HandleCreated += (s, e) => SendMessage(_search.Box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "Suchen: Name, IP, MAC oder Gruppe (Strg+F)");
            _search.Box.TextChanged += (s, e) => { _filter = _search.Text.Trim(); BuildRows(); };
            _search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape && _search.Text.Length > 0) { _search.Text = ""; e.Handled = true; e.SuppressKeyPress = true; }
            };
            searchBar.Controls.Add(_search);

            // ---------- Geräteliste ----------
            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Ui.Back,
                Padding = new Padding(S(28), S(2), S(28), S(8))
            };
            _list.ClientSizeChanged += (s, e) => FitRows();
            _list.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowMainMenu(_list, e.Location); };

            // ---------- Knopfleiste ----------
            _bar = new Panel { Dock = DockStyle.Bottom, Height = S(72), BackColor = Ui.Back, Padding = new Padding(S(28), S(16), S(28), S(16)) };
            var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Ui.Back, Margin = Padding.Empty };
            _wakeAll = new ModernButton("Alle wecken", MenuHelper.Power, true, k);
            _wakeAll.Click += (s, e) => WakeAll();
            var refresh = new ModernButton("Status prüfen", MenuHelper.Refresh, false, k);
            refresh.Click += (s, e) => CheckAll();
            _tip.SetToolTip(refresh, "Status aller Geräte jetzt prüfen (F5)");
            left.Controls.Add(_wakeAll);
            left.Controls.Add(refresh);
            var close = new ModernButton("Schließen", '\0', false, k) { Dock = DockStyle.Right, Margin = Padding.Empty };
            close.Click += (s, e) => Close();
            _message = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Ui.Muted,
                BackColor = Ui.Back,
                Padding = new Padding(S(6), 0, S(12), 0)
            };
            _bar.Controls.Add(_message);
            _bar.Controls.Add(left);
            _bar.Controls.Add(close);

            Controls.Add(_list);
            Controls.Add(_bar);
            Controls.Add(new SignatureFooter(k));
            Controls.Add(searchBar);
            Controls.Add(_header);

            // ---------- Fenstergröße ----------
            MinimumSize = new Size(S(760), S(520));
            // ab Layout 2 größer (Schalter "Alle wecken" in jeder Zeile); kleinere gespeicherte Größe aus älteren Versionen verwerfen
            bool keep = _store.LayoutVersion >= DeviceStore.CurrentLayout && _store.WindowWidth > 0 && _store.WindowHeight > 0;
            int w = keep ? _store.WindowWidth : S(1040);
            int h = keep ? _store.WindowHeight : S(820);
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(w, wa.Width), Math.Min(h, wa.Height));
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

            if (!_demo)
            {
                _notifier = new Notifier();
                _notifier.Clicked += (s, e) => { if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); };
            }
            BuildRows();
            _timer.Tick += (s, e) => Tick();
            if (!_demo) Shown += (s, e) => { _timer.Start(); Tick(); };
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { CheckAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.N && e.Control) { AddDevice(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Control) { _search.Box.Focus(); _search.Box.SelectAll(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            if (_dirty) { try { _store.Save(); } catch { } _dirty = false; }
            if (_notifier != null) _notifier.Dispose();
            var size = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            if (size.Width != _store.WindowWidth || size.Height != _store.WindowHeight || _store.LayoutVersion != DeviceStore.CurrentLayout)
            {
                _store.LayoutVersion = DeviceStore.CurrentLayout;
                _store.WindowWidth = size.Width;
                _store.WindowHeight = size.Height;
                try { _store.Save(); } catch { }
            }
            base.OnFormClosing(e);
        }

        // ================================================================== Liste

        private void BuildRows()
        {
            // vorhandene Zeilen (mit ihrem Status) wiederverwenden
            var old = _rows.ToDictionary(r => r.Device);
            _list.SuspendLayout();
            var previous = _list.Controls.Cast<Control>().ToList();
            _list.Controls.Clear();
            foreach (var c in previous) if (!(c is DeviceRow)) c.Dispose(); // z. B. Hinweis "Noch keine Geräte"
            _rows.Clear();
            _headers.Clear();
            // Mit Gruppen: Überschrift je Gruppe (alphabetisch), Geräte ohne Gruppe zuletzt unter "Ohne Gruppe"
            bool grouped = _store.Groups().Count > 0;
            var sections = grouped ? _store.Groups().Concat(new[] { "" }).ToList() : new List<string> { "" };
            foreach (string group in sections)
            {
                var devices = _store.Devices.Where(d => Device.SameGroup(d.Group, group)).ToList();
                if (devices.Count == 0 && group.Length == 0) continue; // "Ohne Gruppe" nur, wenn es solche Geräte gibt
                bool filtering = _filter.Length > 0;
                bool anyMatch = devices.Any(d => Matches(d)) || (filtering && group.Length > 0 && group.IndexOf(_filter, StringComparison.CurrentCultureIgnoreCase) >= 0);
                if (filtering && !anyMatch) { foreach (var d in devices) KeepRow(old, d); continue; }
                GroupHeader header = null;
                if (grouped)
                {
                    header = new GroupHeader(group, _store.Collapsed.Contains(group), k, _tip);
                    // erst nach dem Mausereignis neu aufbauen (der Kopf wird dabei ersetzt)
                    header.ToggleRequested += (s, e) => { var h = (GroupHeader)s; BeginInvoke(new Action(() => ToggleGroup(h))); };
                    header.WakeClicked += (s, e) => WakeGroup((GroupHeader)s);
                    header.MenuRequested += (s, e) => ShowGroupMenu((GroupHeader)s, e.Location);
                    header.DragEnter += (s, e) => HeaderDragOver((GroupHeader)s, e);
                    header.DragOver += (s, e) => HeaderDragOver((GroupHeader)s, e);
                    header.DragLeave += (s, e) => { var h = (GroupHeader)s; SetDropHighlight(h, false); if (h.DropMark != 0) { h.DropMark = 0; h.Invalidate(); } };
                    header.DragDrop += (s, e) => HeaderDrop((GroupHeader)s, e);
                    if (header.WakeButton != null) header.WakeButton.Enabled = devices.Count > 0;
                    _headers.Add(header);
                    _list.Controls.Add(header);
                }
                foreach (var d in devices)
                {
                    DeviceRow row;
                    if (!old.TryGetValue(d, out row))
                    {
                        row = new DeviceRow(d, k, _tip);
                        row.WakeClicked += (s, e) => Wake((DeviceRow)s, null);
                        row.EditRequested += (s, e) => EditDevice(((DeviceRow)s).Device);
                        row.InAllChanged += (s, e) => SetInAll((DeviceRow)s);
                        row.MenuRequested += (s, e) => ShowRowMenu((DeviceRow)s, e.Location);
                        row.DragEnter += (s, e) => RowDragOver((DeviceRow)s, e);
                        row.DragOver += (s, e) => RowDragOver((DeviceRow)s, e);
                        row.DragLeave += (s, e) => SetDropMark((DeviceRow)s, 0);
                        row.DragDrop += (s, e) => RowDrop((DeviceRow)s, e);
                    }
                    else old.Remove(d);
                    _rows.Add(row);
                    if (header != null) header.Rows.Add(row);
                    // bei der Suche: nur Treffer zeigen, zugeklappte Gruppen dabei aufklappen
                    if (filtering ? Matches(d) || (header != null && group.IndexOf(_filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                                  : header == null || !header.Collapsed)
                        _list.Controls.Add(row);
                }
            }
            foreach (var r in old.Values) r.Dispose();
            if (_rows.Count == 0 && _filter.Length == 0) _list.Controls.Add(BuildEmptyCard());
            if (_filter.Length > 0 && _list.Controls.Count == 0)
                _list.Controls.Add(new Label { Text = "Keine Treffer für „" + _filter + "“.", ForeColor = Ui.Muted, BackColor = Ui.Back, AutoSize = true, Margin = new Padding(S(4), S(12), 0, 0) });
            _list.ResumeLayout();
            FitRows();
            _wakeAll.Enabled = _store.Devices.Any(d => d.InAll);
            UpdateWakeAllText();
            if (!_demo) JumpListHelper.Update(_store);
        }

        /// <summary>Suchtreffer: Name, Gruppe, Adresse, zuletzt gefundene IP oder MAC (mit oder ohne Trennzeichen).</summary>
        private bool Matches(Device d)
        {
            if (_filter.Length == 0) return true;
            Func<string, bool> has = t => !string.IsNullOrEmpty(t) && t.IndexOf(_filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
            if (has(d.Name) || has(d.Group) || has(d.Host) || has(d.LastIp) || has(d.IpmiHost)) return true;
            string plain = _filter.Replace(":", "").Replace("-", "").Replace(".", "");
            return plain.Length >= 2 && d.Macs.Any(m => m.Replace(":", "").IndexOf(plain, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Zeile einer ausgeblendeten Gruppe behalten (Status läuft weiter), aber nicht anzeigen.</summary>
        private void KeepRow(Dictionary<Device, DeviceRow> old, Device d)
        {
            DeviceRow row;
            if (old.TryGetValue(d, out row)) { old.Remove(d); _rows.Add(row); }
        }

        private Control BuildEmptyCard()
        {
            var card = new CardPanel(k) { Title = "Noch keine Geräte", Height = S(150), Margin = new Padding(0) };
            card.Controls.Add(new Label
            {
                Text = "Lege ein Gerät an oder übernimm die Geräte aus deinen bisherigen Skripten\r\n„Wake on LAN (<Gerät>).bat“ (Ordner auswählen).",
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                Location = new Point(S(20), S(42)),
                Size = new Size(S(600), S(44))
            });
            var add = new ModernButton("Gerät hinzufügen", MenuHelper.Add, true, k) { Location = new Point(S(20), S(92)) };
            add.Click += (s, e) => AddDevice();
            var import = new ModernButton("Aus alten Skripten übernehmen …", MenuHelper.Folder, false, k) { Location = new Point(add.Right + S(10), S(92)) };
            import.Click += (s, e) => ImportScripts();
            card.Controls.Add(add);
            card.Controls.Add(import);
            return card;
        }

        private void FitRows()
        {
            int w = _list.ClientSize.Width - _list.Padding.Horizontal;
            if (w <= 0) return;
            _list.SuspendLayout();
            foreach (Control c in _list.Controls) c.Width = w - c.Margin.Horizontal;
            _list.ResumeLayout();
        }

        // ================================================================== Status

        private void Tick()
        {
            if (WindowState == FormWindowState.Minimized) return;
            var now = DateTime.Now;
            foreach (var row in _rows)
            {
                if (row.WakeStart.HasValue && (now - row.WakeStart.Value).TotalSeconds > WakingTimeoutSeconds)
                {
                    row.WakeStart = null;
                    if (row.State != DeviceState.Online)
                    {
                        string text = row.Device.Name + " antwortet nach " + (WakingTimeoutSeconds / 60) + " Minuten noch nicht.";
                        SetMessage(text, Ui.Yellow);
                        History.Add(row.Device.Name, "nach " + (WakingTimeoutSeconds / 60) + " Minuten nicht erreicht");
                        Notify(text, ToolTipIcon.Warning);
                    }
                }
                if (row.WakeStart.HasValue) row.Invalidate(); // Sekunden weiterzählen
                if (row.Checking || !row.Device.HasStatusTarget) continue;
                int interval = row.WakeStart.HasValue ? WakingCheckSeconds : CheckIntervalSeconds;
                if ((now - row.LastCheck).TotalSeconds >= interval) StartCheck(row);
            }
            foreach (var h in _headers) h.Invalidate(); // "2 von 3 online"
            if (_dirty && (now - _lastSave).TotalSeconds >= 60)
            {
                try { _store.Save(); } catch { }
                _dirty = false;
                _lastSave = now;
            }
        }

        private void CheckAll()
        {
            foreach (var row in _rows)
                if (!row.Checking && row.Device.HasStatusTarget) StartCheck(row);
        }

        private void StartCheck(DeviceRow row)
        {
            row.Checking = true;
            row.LastCheck = DateTime.Now;
            if (row.State == DeviceState.Unknown) { row.State = DeviceState.Checking; row.Invalidate(); }
            var device = row.Device.Clone();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                StatusResult result;
                try { result = Waker.Check(device); }
                catch (Exception ex) { result = new StatusResult { State = DeviceState.Error, Info = ex.Message }; }
                Post(() =>
                {
                    row.Checking = false;
                    row.LastCheck = DateTime.Now;
                    if (row.IsDisposed) return;
                    var original = row.Device;
                    if (result.FoundIp != null && result.FoundIp != original.LastIp) { original.LastIp = result.FoundIp; _dirty = true; }
                    if (result.State == DeviceState.Online)
                    {
                        // "zuletzt online" höchstens minutengenau merken
                        if (!original.LastOnline.HasValue || (DateTime.Now - original.LastOnline.Value).TotalMinutes >= 1) { original.LastOnline = DateTime.Now; _dirty = true; }
                    }
                    bool cameOnline = row.WakeStart.HasValue && result.State == DeviceState.Online;
                    if (cameOnline)
                    {
                        int sec = (int)(DateTime.Now - row.WakeStart.Value).TotalSeconds;
                        row.WakeStart = null;
                        SetMessage(row.Device.Name + " ist online (nach " + sec + " s).", Ui.Green);
                        History.Add(row.Device.Name, "online nach " + sec + " s");
                        Notify(row.Device.Name + " ist online (nach " + sec + " s).", ToolTipIcon.Info);
                    }
                    row.SetStatus(result.State, result.Info);
                });
            });
        }

        private void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        // ================================================================== Wecken

        /// <summary>Sammelt die Ergebnisse von "Alle wecken".</summary>
        private sealed class WakeAllRun
        {
            public int Remaining, Total, Failed;
            public string Label;
            public Control Button;
        }

        private void Wake(DeviceRow row, WakeAllRun all)
        {
            if (row.Busy) { if (all != null) Finish(all, true); return; }
            row.Busy = true;
            row.Invalidate();
            var device = row.Device.Clone();
            if (all == null) SetMessage("Wecke " + device.Name + " …", Ui.Muted);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                WakeResult result;
                try { result = Waker.Wake(device); }
                catch (Exception ex) { result = new WakeResult(); result.Fail(ex.Message); }
                Post(() =>
                {
                    row.Busy = false;
                    if (result.FoundIp != null && result.FoundIp != row.Device.LastIp) { row.Device.LastIp = result.FoundIp; _dirty = true; }
                    History.Add(device.Name, (result.Ok ? "geweckt: " : "Fehler beim Wecken: ") + result.Message);
                    if (!row.IsDisposed)
                    {
                        if (result.Ok && row.State != DeviceState.Online && device.HasStatusTarget)
                        {
                            row.WakeStart = DateTime.Now;
                            row.LastCheck = DateTime.Now; // erste Prüfung nach wenigen Sekunden
                        }
                        row.LastResult = result.Message;
                        row.LastResultOk = result.Ok;
                        row.Invalidate();
                    }
                    if (all == null)
                        SetMessage(device.Name + ": " + result.Message, result.Ok ? Ui.Green : Ui.Red);
                    else
                        Finish(all, result.Ok);
                });
            });
        }

        private void WakeAll()
        {
            var rows = _rows.Where(r => r.Device.InAll).ToList();
            if (rows.Count == 0) { SetMessage("Kein Gerät ist für „Alle wecken“ vorgesehen.", Ui.Muted); return; }
            WakeMany(rows, "Alle wecken", _wakeAll);
        }

        private void WakeGroup(GroupHeader header)
        {
            var rows = header.Rows.Where(r => !r.IsDisposed).ToList();
            if (rows.Count == 0) return;
            WakeMany(rows, "Gruppe „" + header.Title + "“", header.WakeButton);
        }

        /// <summary>Mehrere Geräte gleichzeitig wecken (Alle wecken, Gruppe wecken).</summary>
        private void WakeMany(List<DeviceRow> rows, string label, Control button)
        {
            var run = new WakeAllRun { Remaining = rows.Count, Total = rows.Count, Label = label, Button = button };
            if (button != null) button.Enabled = false;
            SetMessage(label + ": wecke " + rows.Count + (rows.Count == 1 ? " Gerät …" : " Geräte …"), Ui.Muted);
            foreach (var r in rows) Wake(r, run);
        }

        private void Finish(WakeAllRun run, bool ok)
        {
            if (!ok) run.Failed++;
            if (--run.Remaining > 0) return;
            if (run.Button != null && !run.Button.IsDisposed) run.Button.Enabled = true;
            _wakeAll.Enabled = _store.Devices.Any(d => d.InAll);
            if (run.Failed == 0)
                SetMessage(run.Label + ": " + run.Total + (run.Total == 1 ? " Gerät" : " Geräte") + " angestoßen.", Ui.Green);
            else
                SetMessage(run.Label + ": " + (run.Total - run.Failed) + " von " + run.Total + " Geräten angestoßen, " + run.Failed + " mit Fehler (Maus auf das Gerät zeigt Details).", Ui.Red);
        }

        private void SetMessage(string text, Color color)
        {
            _message.Text = text;
            _message.ForeColor = color;
            _tip.SetToolTip(_message, text);
        }

        // ================================================================== Bearbeiten

        private void AddDevice()
        {
            var d = DeviceForm.Edit(this, null, _store);
            if (d == null) return;
            _store.Devices.Add(d);
            SaveAndRebuild();
            SetMessage(d.Name + " hinzugefügt.", Ui.Muted);
        }

        private void EditDevice(Device device)
        {
            var d = DeviceForm.Edit(this, device, _store);
            if (d == null) return;
            int i = _store.Devices.IndexOf(device);
            if (i < 0) return;
            _store.Devices[i] = d;
            // Zeile behalten, Status neu prüfen
            var row = _rows.FirstOrDefault(r => r.Device == device);
            if (row != null) row.Replace(d);
            SaveAndRebuild();
        }

        private void DuplicateDevice(Device device)
        {
            var copy = device.Clone();
            string baseName = device.Name + " (Kopie)";
            copy.Name = baseName;
            for (int n = 2; _store.Find(copy.Name) != null; n++) copy.Name = device.Name + " (Kopie " + n + ")";
            var d = DeviceForm.Edit(this, copy, _store, true);
            if (d == null) return;
            _store.Devices.Insert(_store.Devices.IndexOf(device) + 1, d);
            SaveAndRebuild();
        }

        private void DeleteDevice(Device device)
        {
            if (MessageBox.Show(this, "„" + device.Name + "“ wirklich löschen?", Ui.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _store.Devices.Remove(device);
            SaveAndRebuild();
            SetMessage(device.Name + " gelöscht.", Ui.Muted);
        }

        /// <summary>Innerhalb der eigenen Gruppe nach oben/unten (tauscht mit dem Nachbarn derselben Gruppe).</summary>
        private void MoveDevice(Device device, int delta)
        {
            var same = _store.Devices.Where(d => Device.SameGroup(d.Group, device.Group)).ToList();
            int i = same.IndexOf(device), j = i + delta;
            if (i < 0 || j < 0 || j >= same.Count) return;
            int a = _store.Devices.IndexOf(device), b = _store.Devices.IndexOf(same[j]);
            _store.Devices[a] = same[j];
            _store.Devices[b] = device;
            SaveAndRebuild();
        }

        private bool CanMove(Device device, int delta)
        {
            var same = _store.Devices.Where(d => Device.SameGroup(d.Group, device.Group)).ToList();
            int j = same.IndexOf(device) + delta;
            return same.IndexOf(device) >= 0 && j >= 0 && j < same.Count;
        }

        // ================================================================== Ziehen und Ablegen

        private void SetDropMark(DeviceRow row, int mark)
        {
            if (row.DropMark != mark) { row.DropMark = mark; row.Invalidate(); }
        }

        private void SetDropHighlight(GroupHeader header, bool on)
        {
            if (header.DropHighlight != on) { header.DropHighlight = on; header.Invalidate(); }
        }

        private void RowDragOver(DeviceRow target, DragEventArgs e)
        {
            var dragged = DeviceRow.Dragged(e);
            if (dragged == null || dragged == target) { e.Effect = DragDropEffects.None; SetDropMark(target, 0); return; }
            var p = target.PointToClient(new Point(e.X, e.Y));
            SetDropMark(target, p.Y < target.Height / 2 ? -1 : 1);
            e.Effect = DragDropEffects.Move;
            ScrollWhileDragging();
        }

        private void RowDrop(DeviceRow target, DragEventArgs e)
        {
            var dragged = DeviceRow.Dragged(e);
            bool after = target.DropMark > 0;
            SetDropMark(target, 0);
            if (dragged == null || dragged == target) return;
            var device = dragged.Device;
            var next = target.Device;
            // erst nach dem Ablegen neu aufbauen
            BeginInvoke(new Action(() => MoveNextTo(device, next, after)));
        }

        private void HeaderDragOver(GroupHeader header, DragEventArgs e)
        {
            // Gruppe auf Gruppe: Reihenfolge ändern ("Ohne Gruppe" bleibt am Ende)
            string draggedGroup = GroupHeader.DraggedGroup(e);
            if (draggedGroup != null)
            {
                bool valid = !Device.SameGroup(draggedGroup, header.Group);
                e.Effect = valid ? DragDropEffects.Move : DragDropEffects.None;
                int mark = 0;
                if (valid)
                {
                    var p = header.PointToClient(new Point(e.X, e.Y));
                    mark = header.Group.Length == 0 ? -1 : (p.Y < header.Height / 2 ? -1 : 1);
                }
                if (header.DropMark != mark) { header.DropMark = mark; header.Invalidate(); }
                if (valid) ScrollWhileDragging();
                return;
            }
            var dragged = DeviceRow.Dragged(e);
            bool ok = dragged != null;
            e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetDropHighlight(header, ok);
            if (ok) ScrollWhileDragging();
        }

        private void HeaderDrop(GroupHeader header, DragEventArgs e)
        {
            string draggedGroup = GroupHeader.DraggedGroup(e);
            if (draggedGroup != null)
            {
                bool after = header.DropMark > 0;
                header.DropMark = 0;
                header.Invalidate();
                if (Device.SameGroup(draggedGroup, header.Group)) return;
                string target = header.Group;
                BeginInvoke(new Action(() => MoveGroupNextTo(draggedGroup, target, after)));
                return;
            }
            var dragged = DeviceRow.Dragged(e);
            SetDropHighlight(header, false);
            if (dragged == null) return;
            var device = dragged.Device;
            string group = header.Group;
            BeginInvoke(new Action(() => MoveToGroupEnd(device, group)));
        }

        /// <summary>Gerät vor bzw. hinter ein anderes Gerät setzen und in dessen Gruppe übernehmen.</summary>
        private void MoveNextTo(Device device, Device target, bool after)
        {
            bool groupChanged = !Device.SameGroup(device.Group, target.Group);
            _store.Devices.Remove(device);
            int i = _store.Devices.IndexOf(target);
            if (i < 0) _store.Devices.Add(device);
            else _store.Devices.Insert(after ? i + 1 : i, device);
            device.Group = target.Group;
            SaveAndRebuild();
            if (groupChanged) SetMessage(device.Name + (device.Group.Length > 0 ? " ist jetzt in der Gruppe „" + device.Group + "“." : " ist jetzt ohne Gruppe."), Ui.Muted);
        }

        /// <summary>Gerät ans Ende einer Gruppe verschieben (Ablegen auf der Gruppen-Überschrift).</summary>
        private void MoveToGroupEnd(Device device, string group)
        {
            if (Device.SameGroup(device.Group, group)) return;
            _store.Devices.Remove(device);
            int last = _store.Devices.FindLastIndex(d => Device.SameGroup(d.Group, group));
            if (last < 0) _store.Devices.Add(device);
            else _store.Devices.Insert(last + 1, device);
            device.Group = group;
            SaveAndRebuild();
            SetMessage(device.Name + (group.Length > 0 ? " ist jetzt in der Gruppe „" + group + "“." : " ist jetzt ohne Gruppe."), Ui.Muted);
        }

        /// <summary>Gruppe vor bzw. hinter eine andere setzen; Ziel "" (Ohne Gruppe) = ans Ende.</summary>
        private void MoveGroupNextTo(string group, string target, bool after)
        {
            var order = _store.Groups();
            string moving = order.FirstOrDefault(g => Device.SameGroup(g, group));
            if (moving == null) return;
            order.Remove(moving);
            int i = target.Length == 0 ? order.Count : order.FindIndex(g => Device.SameGroup(g, target));
            if (i < 0) i = order.Count;
            else if (after && target.Length > 0) i++;
            order.Insert(Math.Min(i, order.Count), moving);
            _store.SetGroupOrder(order);
            SaveAndRebuild();
        }

        /// <summary>Nur die Geräte einer Gruppe neu ordnen; die übrigen behalten ihre Plätze.</summary>
        private void SortGroup(string group, string by, Func<List<Device>, IEnumerable<Device>> order)
        {
            var slots = new List<int>();
            for (int i = 0; i < _store.Devices.Count; i++)
                if (Device.SameGroup(_store.Devices[i].Group, group)) slots.Add(i);
            var sorted = order(slots.Select(slot => _store.Devices[slot]).ToList()).ToList();
            for (int n = 0; n < slots.Count; n++) _store.Devices[slots[n]] = sorted[n];
            SaveAndRebuild();
            SetMessage("Gruppe „" + (group.Length > 0 ? group : "Ohne Gruppe") + "“ sortiert: " + by + ".", Ui.Muted);
        }

        private void MoveGroup(string group, int delta)
        {
            var order = _store.Groups();
            int i = order.FindIndex(g => Device.SameGroup(g, group)), j = i + delta;
            if (i < 0 || j < 0 || j >= order.Count) return;
            string t = order[i]; order[i] = order[j]; order[j] = t;
            _store.SetGroupOrder(order);
            SaveAndRebuild();
        }

        /// <summary>Beim Ziehen am oberen bzw. unteren Rand der Liste weiterscrollen.</summary>
        private void ScrollWhileDragging()
        {
            var p = _list.PointToClient(Cursor.Position);
            int zone = S(40), step = S(24);
            int y = -_list.AutoScrollPosition.Y;
            if (p.Y < zone) y -= step;
            else if (p.Y > _list.ClientSize.Height - zone) y += step;
            else return;
            _list.AutoScrollPosition = new Point(0, Math.Max(0, y));
        }

        // ================================================================== Gruppen

        /// <summary>Schalter "Alle wecken" in der Zeile: sofort speichern, ohne die Liste neu aufzubauen.</summary>
        private void SetInAll(DeviceRow row)
        {
            row.Device.InAll = row.InAllSwitch;
            try { _store.Save(); }
            catch (Exception ex) { SetMessage("Speichern fehlgeschlagen: " + ex.Message, Ui.Red); return; }
            _wakeAll.Enabled = _store.Devices.Any(d => d.InAll);
            UpdateWakeAllText();
            SetMessage(row.Device.Name + (row.Device.InAll ? " ist bei „Alle wecken“ dabei." : " ist nicht mehr bei „Alle wecken“ dabei."), Ui.Muted);
        }

        /// <summary>Anzahl auf dem Knopf: "Alle wecken (4)".</summary>
        private void UpdateWakeAllText()
        {
            int n = _store.Devices.Count(d => d.InAll);
            _wakeAll.Text = n > 0 ? "Alle wecken (" + n + ")" : "Alle wecken";
        }

        private void SetGroup(Device device, string group)
        {
            device.Group = group ?? "";
            SaveAndRebuild();
            SetMessage(device.Name + (device.Group.Length > 0 ? " ist jetzt in der Gruppe „" + device.Group + "“." : " ist jetzt ohne Gruppe."), Ui.Muted);
        }

        /// <summary>Leere Gruppe anlegen; Geräte kommen per Ziehen, "In Gruppe" oder beim Bearbeiten hinein.</summary>
        private void NewGroup()
        {
            string name = TextPrompt.Ask(this, "Neue Gruppe", "Name der neuen Gruppe:", "");
            if (string.IsNullOrEmpty(name)) return;
            if (_store.Groups().Any(g => Device.SameGroup(g, name)))
            {
                SetMessage("Die Gruppe „" + name + "“ gibt es schon.", Ui.Yellow);
                return;
            }
            var order = _store.Groups();
            order.Add(name);
            _store.SetGroupOrder(order);
            SaveAndRebuild();
            SetMessage("Gruppe „" + name + "“ angelegt – Geräte einfach auf die Überschrift ziehen.", Ui.Muted);
        }

        private void ToggleGroup(GroupHeader header)
        {
            if (header.Collapsed) _store.Collapsed.Remove(header.Group);
            else _store.Collapsed.Add(header.Group);
            SaveAndRebuild();
        }

        private void RenameGroup(string group)
        {
            string name = TextPrompt.Ask(this, "Gruppe umbenennen", "Neuer Name der Gruppe „" + group + "“:", group);
            if (string.IsNullOrEmpty(name) || name == group) return;
            // gibt es den Namen schon, landen die Geräte in dieser Gruppe (zusammenführen)
            string target = _store.Groups().FirstOrDefault(g => Device.SameGroup(g, name)) ?? name;
            _store.SetGroupOrder(_store.Groups().Select(g => Device.SameGroup(g, group) ? target : g));
            foreach (var dev in _store.Devices.Where(x => Device.SameGroup(x.Group, group))) dev.Group = target;
            if (_store.Collapsed.Remove(group)) _store.Collapsed.Add(target);
            SaveAndRebuild();
            SetMessage("Gruppe umbenannt: „" + group + "“ → „" + target + "“.", Ui.Muted);
        }

        private void DissolveGroup(string group)
        {
            if (MessageBox.Show(this, "Gruppe „" + group + "“ auflösen? Die Geräte bleiben erhalten und stehen danach unter „Ohne Gruppe“.", Ui.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            foreach (var dev in _store.Devices.Where(x => Device.SameGroup(x.Group, group))) dev.Group = "";
            _store.RemoveGroup(group);
            SaveAndRebuild();
            SetMessage("Gruppe „" + group + "“ aufgelöst.", Ui.Muted);
        }

        private void ShowGroupMenu(GroupHeader header, Point location)
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            string g = header.Group;
            menu.Items.Add(b.Item("Gruppe wecken", MenuHelper.Power, () => WakeGroup(header)));
            menu.Items.Add(b.Item(header.Collapsed ? "Aufklappen" : "Zuklappen", header.Collapsed ? MenuHelper.ChevronDown : MenuHelper.ChevronRight, () => ToggleGroup(header)));
            menu.Items.Add(new ToolStripSeparator());
            var groups = _store.Groups();
            int gi = groups.FindIndex(x => Device.SameGroup(x, g));
            var gUp = b.Item("Gruppe nach oben", MenuHelper.Up, () => MoveGroup(g, -1));
            gUp.Enabled = gi > 0;
            var gDown = b.Item("Gruppe nach unten", MenuHelper.Down, () => MoveGroup(g, 1));
            gDown.Enabled = gi >= 0 && gi < groups.Count - 1;
            menu.Items.Add(gUp);
            menu.Items.Add(gDown);
            // Geräte nur dieser Gruppe sortieren
            var sortGroup = b.Item("Geräte sortieren", MenuHelper.Sort, null);
            sortGroup.DropDownItems.Add(b.Item("Nach Name (A–Z)", MenuHelper.Sort, () => SortGroup(g, "Name",
                list => list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))));
            sortGroup.DropDownItems.Add(b.Item("Nach IP-Adresse", MenuHelper.Sort, () => SortGroup(g, "IP-Adresse",
                list => list.OrderBy(d => IpKey(d)).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))));
            b.StyleDropDown(sortGroup);
            sortGroup.Enabled = header.Rows.Count > 1;
            menu.Items.Add(sortGroup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(b.Item("Umbenennen …", MenuHelper.Edit, () => RenameGroup(g)));
            menu.Items.Add(b.Item("Verknüpfung auf dem Desktop", MenuHelper.Link, () => CreateShortcut("Gruppe " + g, "\"/gruppe:" + g + "\"")));
            var off = b.Item("Gruppe herunterfahren …", MenuHelper.Power, () => ShutdownGroup(header));
            off.Enabled = header.Rows.Any(r => r.Device.AllowShutdown || r.Device.UseIpmi);
            menu.Items.Add(off);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(b.Item("Gruppe auflösen …", MenuHelper.Delete, () => DissolveGroup(g)));
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(header, location);
        }

        private void ImportScripts()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Ordner mit den Skripten „Wake on LAN (<Gerät>).bat“ auswählen", ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string summary;
                List<Device> found;
                try { found = ScriptImport.Read(dlg.SelectedPath, out summary); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Die Skripte konnten nicht gelesen werden:\r\n\r\n" + ex.Message, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                int added = 0, skipped = 0;
                foreach (var d in found)
                {
                    if (_store.Find(d.Name) != null) { skipped++; continue; }
                    _store.Devices.Add(d);
                    added++;
                }
                if (found.Count == 0)
                {
                    MessageBox.Show(this, "In diesem Ordner wurden keine Skripte „Wake on LAN (<Gerät>).bat“ gefunden.", Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                SaveAndRebuild();
                SetMessage(summary + ": " + added + " übernommen" + (skipped > 0 ? ", " + skipped + " schon vorhanden" : "") + ".", Ui.Green);
            }
        }

        private void SaveAndRebuild()
        {
            try { _store.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Die Geräteliste konnte nicht gespeichert werden:\r\n" + _store.FilePath + "\r\n\r\n" + ex.Message,
                    Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            BuildRows();
        }

        // ================================================================== Menüs

        private void ShowRowMenu(DeviceRow row, Point location)
        {
            var menu = BuildRowMenu(row);
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(row, location);
        }

        private ContextMenuStrip BuildRowMenu(DeviceRow row)
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            var d = row.Device;
            menu.Items.Add(b.Item("Wecken", MenuHelper.Power, () => Wake(row, null)));
            menu.Items.Add(b.Item("Bearbeiten …", MenuHelper.Edit, () => EditDevice(d)));
            menu.Items.Add(b.Item("Duplizieren …", MenuHelper.Copy, () => DuplicateDevice(d)));
            menu.Items.Add(new ToolStripSeparator());
            var up = b.Item("Nach oben", MenuHelper.Up, () => MoveDevice(d, -1));
            up.Enabled = CanMove(d, -1);
            var down = b.Item("Nach unten", MenuHelper.Down, () => MoveDevice(d, 1));
            down.Enabled = CanMove(d, 1);
            menu.Items.Add(up);
            menu.Items.Add(down);
            // In Gruppe verschieben: vorhandene Gruppen, neue Gruppe, ohne Gruppe
            var move = b.Item("In Gruppe", MenuHelper.Group, null);
            foreach (string g in _store.Groups())
            {
                string group = g;
                var item = b.Item(group, MenuHelper.Group, () => SetGroup(d, group));
                item.Enabled = !Device.SameGroup(d.Group, group);
                move.DropDownItems.Add(item);
            }
            if (move.DropDownItems.Count > 0) move.DropDownItems.Add(new ToolStripSeparator());
            move.DropDownItems.Add(b.Item("Neue Gruppe …", MenuHelper.Add, () =>
            {
                string name = TextPrompt.Ask(this, "Neue Gruppe", "Name der neuen Gruppe für „" + d.Name + "“:", "");
                if (string.IsNullOrEmpty(name)) return;
                SetGroup(d, _store.Groups().FirstOrDefault(x => Device.SameGroup(x, name)) ?? name);
            }));
            var none = b.Item("Ohne Gruppe", MenuHelper.Delete, () => SetGroup(d, ""));
            none.Enabled = d.Group.Trim().Length > 0;
            move.DropDownItems.Add(none);
            b.StyleDropDown(move);
            menu.Items.Add(move);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(b.Item("Verknüpfung auf dem Desktop", MenuHelper.Link, () => CreateShortcut(d.Name, "\"" + d.Name + "\"")));
            if (d.AllowShutdown || d.UseIpmi)
            {
                menu.Items.Add(new ToolStripSeparator());
                if (d.AllowShutdown)
                {
                    menu.Items.Add(b.Item("Herunterfahren …", MenuHelper.Power, () => Shutdown(row, false, false)));
                    menu.Items.Add(b.Item("Neu starten …", MenuHelper.Refresh, () => Shutdown(row, true, false)));
                }
                if (d.UseIpmi)
                    menu.Items.Add(b.Item("Über IPMI herunterfahren …", MenuHelper.Power, () => Shutdown(row, false, true)));
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(b.Item("Löschen …", MenuHelper.Delete, () => DeleteDevice(d)));
            return menu;
        }

        // ================================================================== Beispieldaten (Screenshots)

        /// <summary>Feste Zustände für die Screenshots: online, wird geweckt, ausgeschaltet, keine Antwort, kein Status.</summary>
        internal void ApplyDemoStatus()
        {
            foreach (var r in _rows)
            {
                switch (r.Device.Name)
                {
                    case "Spiele-PC": r.SetStatus(DeviceState.Online, ""); break;
                    case "Büro-Laptop": r.SetStatus(DeviceState.Offline, ""); r.WakeStart = DateTime.Now.AddSeconds(-12); break;
                    case "Medienserver": r.SetStatus(DeviceState.Online, ""); break;
                    case "Hyper-V-Host": r.SetStatus(DeviceState.PowerOff, ""); break;
                    case "NAS": r.SetStatus(DeviceState.Offline, ""); break;
                }
            }
            foreach (var h in _headers) h.Invalidate();
            SetMessage("Büro-Laptop: 2 Weckpakete gesendet (2 Netzwerkkarten) · Port 445 angestoßen", Ui.Green);
        }

        /// <summary>Kontextmenü eines Geräts für die Screenshots (nicht angezeigt).</summary>
        internal ContextMenuStrip DemoRowMenu(string name)
        {
            var row = _rows.FirstOrDefault(r => r.Device.Name == name);
            return row != null ? BuildRowMenu(row) : null;
        }

        internal Rectangle DemoRowScreenBounds(string name)
        {
            var row = _rows.FirstOrDefault(r => r.Device.Name == name);
            return row != null ? row.RectangleToScreen(row.ClientRectangle) : Rectangle.Empty;
        }

        private void ShowMainMenu(Control owner, Point location)
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            menu.Items.Add(b.Item("Gerät hinzufügen …", MenuHelper.Add, AddDevice));
            menu.Items.Add(b.Item("Neue Gruppe …", MenuHelper.Group, NewGroup));
            var sort = b.Item("Sortieren", MenuHelper.Sort, null);
            sort.DropDownItems.Add(b.Item("Nach Name (A–Z)", MenuHelper.Sort, () => SortDevices("Name",
                _store.Devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))));
            sort.DropDownItems.Add(b.Item("Nach IP-Adresse", MenuHelper.Sort, () => SortDevices("IP-Adresse",
                _store.Devices.OrderBy(d => IpKey(d)).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))));
            sort.DropDownItems.Add(b.Item("„Alle wecken“ zuerst, dann Name", MenuHelper.Sort, () => SortDevices("„Alle wecken“ zuerst",
                _store.Devices.OrderBy(d => d.InAll ? 0 : 1).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))));
            if (_store.Groups().Count > 1)
            {
                sort.DropDownItems.Add(new ToolStripSeparator());
                sort.DropDownItems.Add(b.Item("Gruppen nach Name (A–Z)", MenuHelper.Group, () =>
                {
                    _store.SetGroupOrder(_store.Groups().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
                    SaveAndRebuild();
                    SetMessage("Gruppen sortiert: nach Name.", Ui.Muted);
                }));
            }
            b.StyleDropDown(sort);
            sort.Enabled = _store.Devices.Count > 1;
            menu.Items.Add(sort);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(b.Item("Zeitpläne …", MenuHelper.Clock, () =>
            {
                using (var f = new ScheduleForm(_store)) f.ShowDialog(this);
            }));
            menu.Items.Add(b.Item("Verlauf …", MenuHelper.History, () =>
            {
                using (var f = new HistoryForm()) f.ShowDialog(this);
            }));
            var notify = b.Item("Benachrichtigungen", MenuHelper.Bell, () =>
            {
                _store.Notify = !_store.Notify;
                try { _store.Save(); } catch { }
                SetMessage("Benachrichtigungen " + (_store.Notify ? "eingeschaltet." : "ausgeschaltet."), Ui.Muted);
            });
            notify.Checked = _store.Notify;
            menu.Items.Add(notify);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(b.Item("Geräteliste exportieren …", MenuHelper.Save, ExportDevices));
            menu.Items.Add(b.Item("Geräteliste importieren …", MenuHelper.Open, ImportDevices));
            menu.Items.Add(b.Item("Aus alten Skripten übernehmen …", MenuHelper.Folder, ImportScripts));
            menu.Items.Add(new ToolStripSeparator());
            var all = b.Item("Verknüpfung „Alle wecken“ auf dem Desktop", MenuHelper.Link, () => CreateShortcut("Alle", "/alle"));
            all.Enabled = _store.Devices.Any(d => d.InAll);
            menu.Items.Add(all);
            menu.Items.Add(b.Item("Ordner der Geräteliste öffnen", MenuHelper.Document, () =>
            {
                try { System.IO.Directory.CreateDirectory(_store.Folder); } catch { }
                Ui.Open(_store.Folder);
            }));
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(owner, location);
        }

        // ================================================================== Benachrichtigung, Herunterfahren, Export/Import

        /// <summary>Windows-Benachrichtigung, nur wenn eingeschaltet und das Fenster gerade nicht im Vordergrund ist.</summary>
        private void Notify(string text, ToolTipIcon icon)
        {
            if (_notifier == null || !_store.Notify) return;
            if (Form.ActiveForm == this && WindowState != FormWindowState.Minimized) return;
            _notifier.Show(Ui.AppName, text, icon);
        }

        /// <summary>Herunterfahren bzw. neu starten (Windows) oder über IPMI sanft ausschalten – mit Sicherheitsabfrage.</summary>
        private void Shutdown(DeviceRow row, bool restart, bool ipmi)
        {
            var d = row.Device;
            string what = ipmi ? "über IPMI herunterfahren" : restart ? "neu starten" : "herunterfahren";
            if (MessageBox.Show(this, "„" + d.Name + "“ wirklich " + what + "?" + (ipmi ? "" : "\r\n\r\nNicht gespeicherte Daten auf dem Gerät können verloren gehen."),
                Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var copy = d.Clone();
            SetMessage(d.Name + ": " + what + " …", Ui.Muted);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                WakeResult r;
                try { r = ipmi ? Waker.ShutdownIpmi(copy) : Waker.ShutdownWindows(copy, restart); }
                catch (Exception ex) { r = new WakeResult(); r.Fail(ex.Message); }
                Post(() =>
                {
                    if (r.FoundIp != null && r.FoundIp != d.LastIp) { d.LastIp = r.FoundIp; _dirty = true; }
                    SetMessage(d.Name + ": " + r.Message, r.Ok ? Ui.Muted : Ui.Red);
                    History.Add(d.Name, (r.Ok ? "" : "Fehler: ") + r.Message);
                });
            });
        }

        /// <summary>Alle Geräte einer Gruppe herunterfahren: IPMI-Geräte über IPMI, die anderen über Windows.</summary>
        private void ShutdownGroup(GroupHeader header)
        {
            var targets = header.Rows.Where(r => !r.IsDisposed && (r.Device.UseIpmi || r.Device.AllowShutdown)).Select(r => r.Device).ToList();
            if (targets.Count == 0) return;
            if (MessageBox.Show(this, "Alle " + targets.Count + " Geräte der Gruppe „" + header.Title + "“ herunterfahren?\r\n\r\n" +
                string.Join(", ", targets.Select(x => x.Name)), Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            int remaining = targets.Count, failed = 0;
            string label = "Gruppe „" + header.Title + "“";
            SetMessage(label + ": fahre " + targets.Count + " Geräte herunter …", Ui.Muted);
            foreach (var t in targets)
            {
                var device = t;
                var copy = t.Clone();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    WakeResult res;
                    try { res = copy.UseIpmi ? Waker.ShutdownIpmi(copy) : Waker.ShutdownWindows(copy, false); }
                    catch (Exception ex) { res = new WakeResult(); res.Fail(ex.Message); }
                    Post(() =>
                    {
                        History.Add(device.Name, (res.Ok ? "" : "Fehler: ") + res.Message + " (" + label + ")");
                        if (!res.Ok) failed++;
                        if (--remaining > 0) return;
                        SetMessage(label + ": " + (targets.Count - failed) + " von " + targets.Count + " Geräten fahren herunter" + (failed > 0 ? ", " + failed + " mit Fehler (siehe Verlauf)." : "."), failed > 0 ? Ui.Red : Ui.Muted);
                    });
                });
            }
        }

        private void ExportDevices()
        {
            using (var dlg = new SaveFileDialog { Title = "Geräteliste exportieren", Filter = "Geräteliste (*.xml)|*.xml", FileName = "Wake on LAN Geräte.xml" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _store.Export(dlg.FileName);
                    SetMessage("Geräteliste exportiert (" + _store.Devices.Count + " Geräte, ohne IPMI-Kennwörter).", Ui.Green);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Export fehlgeschlagen:\r\n\r\n" + ex.Message, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ImportDevices()
        {
            using (var dlg = new OpenFileDialog { Title = "Geräteliste importieren", Filter = "Geräteliste (*.xml)|*.xml", CheckFileExists = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var other = new DeviceStore(dlg.FileName);
                try { other.Load(); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Die Datei konnte nicht gelesen werden:\r\n\r\n" + ex.Message, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (other.Devices.Count == 0) { SetMessage("Die Datei enthält keine Geräte.", Ui.Yellow); return; }
                int existing = other.Devices.Count(x => _store.Find(x.Name) != null);
                bool replace = false;
                if (existing > 0)
                {
                    var answer = MessageBox.Show(this, existing + (existing == 1 ? " Gerät gibt" : " Geräte gibt") + " es schon (gleicher Name).\r\n\r\n" +
                        "Ja: durch die importierten ersetzen\r\nNein: überspringen", Ui.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return;
                    replace = answer == DialogResult.Yes;
                }
                int added = 0, replaced = 0;
                foreach (var d in other.Devices)
                {
                    var mine = _store.Find(d.Name);
                    if (mine == null) { _store.Devices.Add(d); added++; continue; }
                    if (!replace) continue;
                    // IPMI-Kennwort ist im Export nicht enthalten: vorhandenes behalten
                    if (d.IpmiPasswordProtected.Length == 0) d.IpmiPasswordProtected = mine.IpmiPasswordProtected;
                    _store.Devices[_store.Devices.IndexOf(mine)] = d;
                    replaced++;
                }
                var order = _store.Groups();
                foreach (string g in other.Groups()) if (!order.Any(x => Device.SameGroup(x, g))) order.Add(g);
                _store.SetGroupOrder(order);
                SaveAndRebuild();
                History.Add("Geräteliste", "importiert aus " + System.IO.Path.GetFileName(dlg.FileName) + ": " + added + " neu, " + replaced + " ersetzt");
                SetMessage("Import: " + added + " neu, " + replaced + " ersetzt" + (existing - replaced > 0 ? ", " + (existing - replaced) + " übersprungen" : "") + ". IPMI-Kennwörter bei Bedarf neu eingeben.", Ui.Green);
            }
        }

        /// <summary>Neue Reihenfolge übernehmen und speichern (danach weiter mit "Nach oben/unten" anpassbar).</summary>
        private void SortDevices(string by, IEnumerable<Device> ordered)
        {
            var list = ordered.ToList();
            _store.Devices.Clear();
            _store.Devices.AddRange(list);
            SaveAndRebuild();
            SetMessage("Liste sortiert: " + by + ".", Ui.Muted);
        }

        /// <summary>Sortierschlüssel für IPv4-Adressen (Zahl statt Text, damit .5 vor .40 kommt); Rechnernamen und leere Adressen ans Ende.</summary>
        private static string IpKey(Device d)
        {
            System.Net.IPAddress ip;
            string h = d.Host.Trim();
            if (h.Length > 0 && System.Net.IPAddress.TryParse(h, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return "0" + ((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3])).ToString("D10");
            }
            return (h.Length > 0 ? "1" : "2") + h.ToLowerInvariant();
        }

        private void CreateShortcut(string label, string arguments)
        {
            try
            {
                string path = Shortcut.CreateOnDesktop(Ui.AppName + " (" + label + ")", arguments);
                SetMessage("Verknüpfung erstellt: " + System.IO.Path.GetFileName(path), Ui.Green);
            }
            catch (Exception ex)
            {
                SetMessage("Verknüpfung konnte nicht erstellt werden: " + ex.Message, Ui.Red);
            }
        }
    }

    /// <summary>Eine Zeile der Geräteliste: Karte mit Status-Punkt, Name, Details, Status und Knöpfen.</summary>
    internal sealed class DeviceRow : Panel
    {
        private readonly float k;
        private readonly ToolTip _tip;
        private readonly ModernButton _wake, _more;
        private readonly ToggleSwitch _all;
        private bool _hover;
        private string _tipText = "";
        /// <summary>Breite der Statusspalte (gleich in allen Zeilen, damit die Schalter untereinander stehen).</summary>
        private int StatusWidth { get { return (int)(170 * k); } }

        public Device Device { get; private set; }
        public DeviceState State = DeviceState.Unknown;
        public string StateInfo = "";
        /// <summary>Zeitpunkt des Weckens (Eigenschaft statt Feld: Steuerelemente sind MarshalByRefObject, CS1690).</summary>
        public DateTime? WakeStart { get; set; }
        public DateTime LastCheck = DateTime.MinValue;
        public bool Checking, Busy;
        public string LastResult = "";
        public bool LastResultOk = true;

        public event EventHandler WakeClicked, EditRequested, InAllChanged;

        /// <summary>Stellung des Schalters "Alle wecken".</summary>
        public bool InAllSwitch { get { return _all.Checked; } }
        public event MouseEventHandler MenuRequested;

        public DeviceRow(Device device, float scale, ToolTip tip)
        {
            Device = device;
            k = scale;
            _tip = tip;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
            Height = (int)(44 * k); // eine Zeile, damit viele Geräte ohne Scrollen passen
            Margin = new Padding(0, 0, 0, (int)(4 * k));

            _wake = new ModernButton("Wecken", MenuHelper.Power, false, k) { HeightUnits = 28, Margin = Padding.Empty };
            _wake.UpdateSize();
            _wake.Click += (s, e) => { if (WakeClicked != null) WakeClicked(this, EventArgs.Empty); };
            _more = new ModernButton("", MenuHelper.More, false, k) { HeightUnits = 28, Margin = Padding.Empty };
            _more.UpdateSize();
            _more.Click += (s, e) => { if (MenuRequested != null) MenuRequested(this, new MouseEventArgs(MouseButtons.Right, 1, _more.Left, _more.Bottom, 0)); };
            tip.SetToolTip(_more, "Bearbeiten, Verknüpfung, Löschen …");
            // Schalter "Alle wecken" direkt in der Zeile
            _all = new ToggleSwitch("Alle wecken", k);
            _all.SetChecked(device.InAll);
            _all.CheckedChanged += (s, e) => { if (InAllChanged != null) InAllChanged(this, EventArgs.Empty); };
            tip.SetToolTip(_all, "Gehört zu „Alle wecken“ (ein/aus)");
            Controls.Add(_all);
            Controls.Add(_wake);
            Controls.Add(_more);
            foreach (Control c in Controls)
            {
                c.MouseEnter += (s, e) => SetHover(true);
                c.MouseLeave += (s, e) => SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
                // Ablegen auch über den Knöpfen: an die Zeile weiterreichen
                c.AllowDrop = true;
                c.DragEnter += (s, e) => OnDragEnter(e);
                c.DragOver += (s, e) => OnDragOver(e);
                c.DragLeave += (s, e) => OnDragLeave(e);
                c.DragDrop += (s, e) => OnDragDrop(e);
            }
            AllowDrop = true;
            if (!device.HasStatusTarget) State = DeviceState.Unknown;
        }

        /// <summary>Nach dem Bearbeiten: neues Gerät übernehmen, Status neu prüfen.</summary>
        public void Replace(Device d)
        {
            Device = d;
            if (_all != null) _all.SetChecked(d.InAll);
            State = DeviceState.Unknown;
            StateInfo = "";
            WakeStart = null;
            LastCheck = DateTime.MinValue;
            Invalidate();
        }

        public void SetStatus(DeviceState state, string info)
        {
            State = state;
            StateInfo = info ?? "";
            Invalidate();
        }

        private void SetHover(bool h)
        {
            if (h == _hover) return;
            _hover = h;
            // BackColor = Kartenfarbe: die Knöpfe zeichnen ihre Ecken in der Farbe ihres Elternelements
            BackColor = h ? Ui.CardHover : Ui.Card;
            Invalidate(true);
        }

        protected override void OnMouseEnter(EventArgs e) { SetHover(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position))); base.OnMouseLeave(e); }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && EditRequested != null) EditRequested(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            if (e.Button == MouseButtons.Right && MenuRequested != null) MenuRequested(this, e);
            base.OnMouseUp(e);
        }

        // ---------- Ziehen und Ablegen ----------
        public const string DragFormat = "WakeOnLan.DeviceRow";
        private bool _pressed;
        private Point _down;
        /// <summary>Einfügemarke beim Ziehen: -1 = davor (oben), 1 = dahinter (unten), 0 = keine.</summary>
        public int DropMark;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = true; _down = e.Location; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_pressed && e.Button == MouseButtons.Left)
            {
                var ds = SystemInformation.DragSize;
                if (Math.Abs(e.X - _down.X) > ds.Width || Math.Abs(e.Y - _down.Y) > ds.Height)
                {
                    _pressed = false;
                    DoDragDrop(new DataObject(DragFormat, this), DragDropEffects.Move);
                }
            }
            base.OnMouseMove(e);
        }

        public static DeviceRow Dragged(DragEventArgs e)
        {
            return e.Data != null && e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as DeviceRow : null;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // Windows ordnet schon beim Setzen der Höhe im Konstruktor an - da gibt es die Knöpfe noch nicht
            if (_wake == null || _more == null || _all == null) return;
            int pad = (int)(8 * k);
            _more.Location = new Point(Width - pad - _more.Width, (Height - _more.Height) / 2);
            _wake.Location = new Point(_more.Left - (int)(8 * k) - _wake.Width, (Height - _wake.Height) / 2);
            // [Text …] [Alle wecken ◯] [Status] [Wecken] […]
            _all.Location = new Point(_wake.Left - (int)(16 * k) - StatusWidth - (int)(12 * k) - _all.Width, (Height - _all.Height) / 2);
        }

        private string StatusText(out Color color, out Color dot, out bool filled)
        {
            filled = true;
            if (Busy) { color = dot = Ui.AccentText; return "Sende …"; }
            if (WakeStart.HasValue && State != DeviceState.Online)
            {
                color = dot = Ui.AccentText;
                return "Wird geweckt … " + (int)(DateTime.Now - WakeStart.Value).TotalSeconds + " s";
            }
            switch (State)
            {
                case DeviceState.Online:
                    color = dot = Ui.Green;
                    return "Online";
                case DeviceState.Offline:
                    color = dot = Ui.Red;
                    if (StateInfo.Length > 0) return StateInfo;
                    return Device.LastOnline.HasValue ? "Offline · " + Ago(Device.LastOnline.Value) : "Offline";
                case DeviceState.PowerOff:
                    color = dot = Ui.Red;
                    return "Ausgeschaltet";
                case DeviceState.Error:
                    color = dot = Ui.Yellow;
                    return "Fehler";
                case DeviceState.Checking:
                    color = Ui.Muted; dot = Ui.Disabled; filled = false;
                    return "Prüfe …";
                default:
                    color = Ui.Disabled; dot = Ui.Disabled; filled = false;
                    return Device.HasStatusTarget ? "" : "Kein Status";
            }
        }

        /// <summary>"vor 5 Min.", "vor 2 Std.", "vor 3 Tagen", ältere als Datum.</summary>
        internal static string Ago(DateTime t)
        {
            var span = DateTime.Now - t;
            if (span.TotalMinutes < 1) return "gerade eben";
            if (span.TotalMinutes < 60) return "vor " + (int)span.TotalMinutes + " Min.";
            if (span.TotalHours < 24) return "vor " + (int)span.TotalHours + " Std.";
            if (span.TotalDays < 14) return "vor " + (int)span.TotalDays + ((int)span.TotalDays == 1 ? " Tag" : " Tagen");
            return "seit " + t.ToString("dd.MM.yyyy");
        }

        private string Details()
        {
            var parts = new List<string>();
            var d = Device;
            if (d.UseMagic && d.Macs.Count > 0) parts.Add(string.Join(", ", d.Macs));
            if (d.Host.Length > 0) parts.Add(d.Host);
            else if (d.AutoIp) parts.Add(d.LastIp.Length > 0 ? d.LastIp + " (automatisch)" : "IP automatisch");
            if (d.UsePort) parts.Add("Port " + d.Port);
            if (d.UseIpmi) parts.Add("IPMI " + d.IpmiHost);
            return string.Join("  ·  ", parts);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = _hover ? Ui.CardHover : Ui.Card;
            using (var p = Ui.Rounded(r, 6 * k))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, p);
            }

            Color statusColor, dotColor;
            bool filled;
            string status = StatusText(out statusColor, out dotColor, out filled);

            // Status-Symbol: grün mit Haken = online, rot mit Kreuz = offline/aus, sonst Punkt bzw. Ring
            float d = 16 * k, cx = 22 * k, cy = Height / 2f;
            bool settled = !Busy && !WakeStart.HasValue;
            bool online = settled && State == DeviceState.Online;
            bool offline = settled && (State == DeviceState.Offline || State == DeviceState.PowerOff);
            if (online || offline)
            {
                using (var b = new SolidBrush(dotColor)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                Color mark = Theme.Dark ? Color.FromArgb(20, 20, 20) : Color.White;
                using (var pen = new Pen(mark, 1.6f * k))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    float m = 3.6f * k;
                    if (online)
                        g.DrawLines(pen, new[] { new PointF(cx - m, cy + 0.2f * k), new PointF(cx - m / 3, cy + m * 0.75f), new PointF(cx + m, cy - m * 0.7f) });
                    else
                    {
                        float c = 3f * k;
                        g.DrawLine(pen, cx - c, cy - c, cx + c, cy + c);
                        g.DrawLine(pen, cx + c, cy - c, cx - c, cy + c);
                    }
                }
            }
            else
            {
                float dd = 10 * k;
                if (filled)
                    using (var b = new SolidBrush(dotColor)) g.FillEllipse(b, cx - dd / 2, cy - dd / 2, dd, dd);
                else
                    using (var pen = new Pen(dotColor, 1.5f * k)) g.DrawEllipse(pen, cx - dd / 2, cy - dd / 2, dd, dd);
            }

            int x = (int)(42 * k);
            int statusX = (_wake != null ? _wake.Left : Width) - (int)(16 * k) - StatusWidth;
            int textRight = (_all != null ? _all.Left : statusX) - (int)(16 * k);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            var measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            using (var fName = new Font("Segoe UI Semibold", 10f))
            using (var fSmall = new Font("Segoe UI", 9f))
            {
                // Alles in einer Zeile: Name und Details – rechts Schalter, Status, Knöpfe
                var nameSize = TextRenderer.MeasureText(g, "Ag", fName, Size.Empty, measure);
                var smallSize = TextRenderer.MeasureText(g, "Ag", fSmall, Size.Empty, measure);
                int nameTop = (Height - nameSize.Height) / 2;
                int smallTop = (Height - smallSize.Height) / 2 + (int)(1 * k); // gleiche Grundlinie wie der Name
                int nameW = TextRenderer.MeasureText(g, Device.Name, fName, Size.Empty, measure).Width;
                int nameMax = Math.Max(0, textRight - x);
                TextRenderer.DrawText(g, Device.Name, fName, new Rectangle(x, nameTop, nameMax, nameSize.Height), Ui.Text, flags);

                // Details (MAC, IP, Port, IPMI) grau dahinter, bei Platzmangel mit "…"
                int dx = x + Math.Min(nameW, nameMax) + (int)(16 * k);
                if (dx < textRight)
                    TextRenderer.DrawText(g, Details(), fSmall, new Rectangle(dx, smallTop, textRight - dx, smallSize.Height), Ui.Muted, flags);
                if (status.Length > 0)
                    TextRenderer.DrawText(g, status, fSmall, new Rectangle(statusX, smallTop, StatusWidth, smallSize.Height), statusColor, flags);
            }

            // Einfügemarke beim Ziehen (Akzentfarbe, oben oder unten in der Karte)
            if (DropMark != 0)
            {
                float lh = 3 * k;
                var mark = new RectangleF(8 * k, DropMark < 0 ? 2 * k : Height - lh - 2 * k, Width - 16 * k, lh);
                using (var mp = Ui.Rounded(mark, lh / 2))
                using (var mb = new SolidBrush(Ui.Primary))
                    g.FillPath(mb, mp);
            }

            // Tooltip mit Einzelheiten
            string tip = Device.Name;
            if (StateInfo.Length > 0) tip += "\r\nStatus: " + StateInfo;
            if (Device.LastOnline.HasValue) tip += "\r\nZuletzt online: " + Device.LastOnline.Value.ToString("dd.MM.yyyy HH:mm") + " (" + Ago(Device.LastOnline.Value) + ")";
            if (Device.HasStatusTarget)
                tip += "\r\nStatus über: " + Device.StatusModeText(Device.Status) + (Device.Status == StatusMode.Port || Device.Status == StatusMode.PingPort ? " " + Device.StatusPort : "");
            else if (Device.Status != StatusMode.Off)
                tip += "\r\nFür den Status eine IP-Adresse eintragen (Bearbeiten).";
            if (LastResult.Length > 0) tip += "\r\nZuletzt: " + LastResult;
            tip += "\r\nDoppelklick: bearbeiten · Rechtsklick: weitere Funktionen";
            if (tip != _tipText) { _tipText = tip; _tip.SetToolTip(this, tip); }
        }
    }

    /// <summary>
    /// Überschrift einer Gruppe: Pfeil zum Auf-/Zuklappen, Name, "3 Geräte · 2 online",
    /// rechts "Gruppe wecken" und "…". "Ohne Gruppe" hat keine Knöpfe.
    /// </summary>
    internal sealed class GroupHeader : Panel
    {
        private readonly float k;
        private readonly ModernButton _wake, _more;
        private bool _hover;
        public readonly string Group;
        public readonly bool Collapsed;
        public readonly List<DeviceRow> Rows = new List<DeviceRow>();

        public event EventHandler ToggleRequested, WakeClicked;
        public event MouseEventHandler MenuRequested;

        public string Title { get { return Group.Length > 0 ? Group : "Ohne Gruppe"; } }
        /// <summary>Hervorhebung, während ein Gerät über die Überschrift gezogen wird.</summary>
        public bool DropHighlight;
        public Control WakeButton { get { return _wake; } }

        public GroupHeader(string group, bool collapsed, float scale, ToolTip tip)
        {
            Group = group ?? "";
            Collapsed = collapsed;
            k = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Back;
            Cursor = Cursors.Hand;
            if (Group.Length > 0)
            {
                // Knöpfe vor dem Setzen der Höhe anlegen (OnLayout greift darauf zu)
                _wake = new ModernButton("Gruppe wecken", MenuHelper.Power, false, k) { HeightUnits = 28, Margin = Padding.Empty };
                _wake.UpdateSize();
                _wake.Click += (s, e) => { if (WakeClicked != null) WakeClicked(this, EventArgs.Empty); };
                _more = new ModernButton("", MenuHelper.More, false, k) { HeightUnits = 28, Margin = Padding.Empty };
                _more.UpdateSize();
                _more.Click += (s, e) => { if (MenuRequested != null) MenuRequested(this, new MouseEventArgs(MouseButtons.Right, 1, _more.Left, _more.Bottom, 0)); };
                tip.SetToolTip(_more, "Umbenennen, Verknüpfung, Gruppe auflösen …");
                Controls.Add(_wake);
                Controls.Add(_more);
                foreach (Control c in Controls)
                {
                    c.Cursor = Cursors.Hand;
                    c.AllowDrop = true;
                    c.DragEnter += (s, e) => OnDragEnter(e);
                    c.DragOver += (s, e) => OnDragOver(e);
                    c.DragLeave += (s, e) => OnDragLeave(e);
                    c.DragDrop += (s, e) => OnDragDrop(e);
                }
            }
            AllowDrop = true;
            tip.SetToolTip(this, "Klick: " + (collapsed ? "aufklappen" : "zuklappen") + " · Rechtsklick: weitere Funktionen");
            Height = (int)(38 * k);
            Margin = new Padding(0, (int)(8 * k), 0, (int)(2 * k));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_wake == null || _more == null) return;
            int pad = (int)(8 * k);
            _more.Location = new Point(Width - pad - _more.Width, (Height - _more.Height) / 2);
            _wake.Location = new Point(_more.Left - (int)(8 * k) - _wake.Width, (Height - _wake.Height) / 2);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        // ---------- Gruppe ziehen (Reihenfolge) ----------
        public const string DragFormat = "WakeOnLan.Group";
        private bool _pressed;
        private Point _down;
        /// <summary>Einfügemarke beim Ziehen einer Gruppe: -1 = davor, 1 = dahinter, 0 = keine.</summary>
        public int DropMark;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = true; _down = e.Location; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_pressed && e.Button == MouseButtons.Left && Group.Length > 0)
            {
                var ds = SystemInformation.DragSize;
                if (Math.Abs(e.X - _down.X) > ds.Width || Math.Abs(e.Y - _down.Y) > ds.Height)
                {
                    _pressed = false; // kein Auf-/Zuklappen nach dem Ziehen
                    DoDragDrop(new DataObject(DragFormat, Group), DragDropEffects.Move);
                }
            }
            base.OnMouseMove(e);
        }

        public static string DraggedGroup(DragEventArgs e)
        {
            return e.Data != null && e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as string : null;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool click = _pressed;
            _pressed = false;
            if (e.Button == MouseButtons.Left && click && ToggleRequested != null) ToggleRequested(this, EventArgs.Empty);
            else if (e.Button == MouseButtons.Right && MenuRequested != null && Group.Length > 0) MenuRequested(this, e);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (DropHighlight)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var hr = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (var hp = Ui.Rounded(hr, 6 * k))
                {
                    using (var hb = new SolidBrush(Ui.InfoBack)) g.FillPath(hb, hp);
                    using (var pen = new Pen(Ui.Primary, 1.5f * k)) g.DrawPath(pen, hp);
                }
            }
            if (DropMark != 0)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float lh = 3 * k;
                var mark = new RectangleF(8 * k, DropMark < 0 ? 0 : Height - lh, Width - 16 * k, lh);
                using (var mp = Ui.Rounded(mark, lh / 2))
                using (var mb = new SolidBrush(Ui.Primary))
                    g.FillPath(mb, mp);
            }
            int gs = (int)(14 * k);
            var chevron = MenuHelper.Glyph(Collapsed ? MenuHelper.ChevronRight : MenuHelper.ChevronDown, _hover ? Ui.Text : Ui.Muted, gs);
            if (chevron != null)
            {
                g.DrawImage(chevron, (int)(13 * k), (Height - gs) / 2, gs, gs);
                chevron.Dispose();
            }
            var measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int x = (int)(42 * k);
            int right = (_wake != null ? _wake.Left : Width) - (int)(16 * k);
            using (var fTitle = new Font("Segoe UI Semibold", 11f))
            using (var fInfo = new Font("Segoe UI", 9f))
            {
                var ts = TextRenderer.MeasureText(g, Title, fTitle, Size.Empty, measure);
                int ty = (Height - ts.Height) / 2;
                TextRenderer.DrawText(g, Title, fTitle, new Rectangle(x, ty, Math.Max(0, right - x), ts.Height), Ui.Text, measure | TextFormatFlags.EndEllipsis);
                int total = Rows.Count, online = Rows.Count(r => r.State == DeviceState.Online);
                string info = total == 0 ? "leer – Geräte hierher ziehen"
                    : total + (total == 1 ? " Gerät" : " Geräte") + (online > 0 ? " · " + online + " online" : "");
                var isz = TextRenderer.MeasureText(g, info, fInfo, Size.Empty, measure);
                int ix = x + ts.Width + (int)(12 * k);
                if (ix < right)
                    TextRenderer.DrawText(g, info, fInfo, new Rectangle(ix, ty + ts.Height - isz.Height - (int)(1 * k), right - ix, isz.Height),
                        online > 0 ? Ui.Green : Ui.Muted, measure | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
