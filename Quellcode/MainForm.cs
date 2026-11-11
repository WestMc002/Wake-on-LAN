// Wake on LAN – Hauptfenster mit der Geräteliste
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WakeOnLan
{
    internal sealed class MainForm : Form, IRemoteHost
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
        private readonly ToolTip _tip = new ModernToolTip();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly List<DeviceRow> _rows = new List<DeviceRow>();
        private readonly List<GroupHeader> _headers = new List<GroupHeader>();

        // Navigation links (wie bei Sync Pilot): angepinnt = immer offen und schiebt den Inhalt;
        // sonst nur Symbole, ☰ klappt sie über dem Inhalt aus, nach der Auswahl bzw. beim Verlassen klappt sie wieder ein
        private readonly Panel _nav, _navSpacer;
        private readonly SignatureFooter _footer;
        private readonly NavButton _navMenu, _navPin, _navDevices, _navSchedules, _navHistory, _navRemote, _navMore, _navQuit;

        // Update-Prüfung über GitHub: erst 15 s nach dem Start, danach stündlich nachsehen, ob 24 h seit der letzten Prüfung vorbei sind
        private readonly System.Windows.Forms.Timer _updateTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        private UpdateInfo _update;          // neuere Version (null = keine)
        private bool _updateRunning, _installing, _updateBalloon, _updateBalloonShown;
        private string _updateError;     // Fehler der letzten Prüfung von Hand (Info-Fenster)
        private readonly Panel _updateHost;  // Hinweisleiste oben im Inhalt, nur sichtbar bei neuer Version
        private readonly UpdateBar _updateBar;
        /// <summary>Neustart durch das Update-Skript (--nach-update): Ergebnis melden.</summary>
        public static bool AfterUpdate;
        private readonly System.Windows.Forms.Timer _navTimer = new System.Windows.Forms.Timer { Interval = 400 };
        private bool _navOpen;

        // Ansichten im selben Fenster (wie Sync Pilot): Zeitpläne, Verlauf und Steuerung per Handy erscheinen im
        // Inhaltsbereich statt als eigenes Fenster. Technisch das bisherige Fenster ohne Rahmen, eingebettet.
        private readonly Panel _viewHost, _viewBody;
        private readonly Label _viewTitle;
        private Panel _searchBar;
        private Form _view;
        private Action<DialogResult> _viewDone;
        private EventHandler _viewResize;

        private readonly bool _demo;
        private readonly Notifier _notifier;
        private readonly ModernTextBox _search;
        private string _filter = "";
        /// <summary>Geänderte LastIp/LastOnline, die noch gespeichert werden müssen (höchstens einmal pro Minute).</summary>
        private bool _dirty;
        private DateTime _lastSave = DateTime.Now;

        // Steuerung per Handy und Weiterlaufen im Infobereich
        private WebServer _web, _pendingWeb;
        private int _webGeneration;
        private string _webError;
        private bool _startHidden, _started, _exit, _trayHintShown;
        private ContextMenuStrip _trayMenu;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>
        /// demo = true: Beispieldaten für die Screenshots (kein Ping, kein Timer, nichts wird gespeichert).
        /// startHidden = true: nur im Infobereich starten (Autostart mit /hintergrund).
        /// </summary>
        public MainForm(DeviceStore store, bool demo = false, bool startHidden = false)
        {
            _store = store;
            _demo = demo;
            _startHidden = startHidden && !demo;
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
            var add = new ModernButton("Gerät hinzufügen", MenuHelper.Add, true, k) { Margin = Padding.Empty };
            add.Click += (s, e) => ShowAddMenu(add);
            headButtons.Controls.Add(add);
            _header.SetRight(headButtons, RightInset);

            // ---------- Suche ----------
            var searchBar = _searchBar = new Panel { Dock = DockStyle.Top, Height = S(48), BackColor = Ui.Back };
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
                Padding = new Padding(S(28), S(2), RightInset, S(8))
            };
            _list.ClientSizeChanged += (s, e) => FitRows();
            _list.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowMainMenu(_list, e.Location); };

            // ---------- Knopfleiste ----------
            _bar = new Panel { Dock = DockStyle.Bottom, Height = S(72), BackColor = Ui.Back, Padding = new Padding(S(28), S(16), RightInset, S(16)) };
            var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Ui.Back, Margin = Padding.Empty };
            _wakeAll = new ModernButton("Alle wecken", MenuHelper.Power, true, k);
            _wakeAll.Click += (s, e) => WakeAll();
            var refresh = new ModernButton("Status prüfen", MenuHelper.Refresh, false, k);
            refresh.Click += (s, e) => CheckAll();
            _tip.SetToolTip(refresh, "Status aller Geräte jetzt prüfen (F5)");
            left.Controls.Add(_wakeAll);
            left.Controls.Add(refresh);
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
            // kein Knopf "Schließen" (wie Sync Pilot): Fenster schließen über das X, ganz beenden über "Beenden" in der Navigation

            // ---------- Navigation ----------
            _navSpacer = new Panel { Dock = DockStyle.Left, BackColor = Ui.Back };
            _nav = new Panel { BackColor = Ui.Back };
            _nav.Paint += (s, e) =>
            {
                // ausgeklappt über dem Inhalt: feine Trennlinie rechts
                if (_navOpen && !_store.NavPinned)
                    using (var p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, _nav.Width - 1, 0, _nav.Width - 1, _nav.Height);
            };
            _navMenu = AddNav("", MenuHelper.Menu, () =>
            {
                if (_store.NavPinned) SetNavPinned(false);
                else { _navOpen = !_navOpen; ApplyNav(); }
            });
            _navMenu.AccessibleName = "Navigation aus- bzw. einklappen";
            _tip.SetToolTip(_navMenu, "Navigation aus- bzw. einklappen");
            _navPin = AddNav("", MenuHelper.Pin, () => SetNavPinned(!_store.NavPinned));
            _navPin.IconOnly = true;
            _navDevices = AddNav("Geräte", MenuHelper.Devices, () => { CloseNav(); if (ConfirmLeaveView()) CloseView(); });
            _navDevices.Selected = true;
            _navSchedules = AddNav("Zeitpläne", MenuHelper.Clock, ShowSchedules);
            _navHistory = AddNav("Verlauf", MenuHelper.History, () => ShowView(_navHistory, new HistoryForm(), null));
            _navRemote = AddNav("Steuerung per Handy", MenuHelper.Phone, OpenRemoteSettings);
            // Einstellungen und übrige Funktionen (früher Knopf "…" oben rechts; auch per Rechtsklick in die freie
            // Fläche der Liste). Unten wie bei Sync Pilot: Einstellungen (Zahnrad), Beenden (Ein/Aus)
            _navMore = AddNav("Einstellungen", MenuHelper.Settings, () =>
            {
                CloseNav();
                var current = _viewTitle.Tag as NavButton ?? _navDevices; // aktuelle Ansicht
                current.Selected = false;
                _navMore.Selected = true; // ausgewählt, solange das Menü offen ist
                ShowMainMenu(_navMore, new Point(_navMore.Width + S(4), _navMore.Height), ToolStripDropDownDirection.AboveRight, () =>
                {
                    if (!_navMore.IsDisposed) _navMore.Selected = false;
                    if (!current.IsDisposed) current.Selected = _viewTitle.Tag == current || (current == _navDevices && _view == null);
                    if (_view == null && !_navDevices.IsDisposed) _navDevices.Selected = true;
                });
            });
            // komplett beenden (sonst schließt das X nur das Fenster und das Programm läuft im Infobereich weiter)
            _navQuit = AddNav("Beenden", MenuHelper.Power, () => { CloseNav(); ExitProgram(); });
            _navQuit.AccessibleName = "Beenden (auch im Infobereich)";
            _nav.Resize += (s, e) => LayoutNav();
            _navTimer.Tick += (s, e) =>
            {
                // ausgeklappt, nicht angepinnt: wieder einklappen, sobald die Maus die Navigation verlässt
                if (!_navOpen || _store.NavPinned || _nav.Capture || !Visible) return;
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) { _navOpen = false; ApplyNav(); }
            };
            _navTimer.Start();
            _footer = new SignatureFooter(k);

            // ---------- Update-Hinweis (wie Fensterpositionen) ----------
            _updateBar = new UpdateBar(k) { Dock = DockStyle.Fill };
            _updateBar.Install.Click += async (s, e) => await InstallUpdate();
            _updateBar.Page.Click += (s, e) => OpenReleasePage();
            _updateBar.Later.Click += (s, e) => ShowUpdateBar(false);
            _updateHost = new Panel { Dock = DockStyle.Top, Height = S(64), BackColor = Ui.Back, Padding = new Padding(S(28), S(10), RightInset, S(2)), Visible = false };
            _updateHost.Controls.Add(_updateBar);

            // Andockreihenfolge: Kopf und Fußzeile über die volle Breite, dann Navigation, dann Suche, Knopfleiste und Liste
            // ---------- Ansicht im Inhaltsbereich (statt eigener Fenster) ----------
            _viewTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = S(56),
                Font = new Font("Segoe UI Semibold", 20f),
                ForeColor = Ui.Text,
                BackColor = Ui.Back,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false
            };
            _viewBody = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back, AutoScroll = true };
            _viewHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back, Visible = false, Padding = new Padding(S(4), S(4), RightInset - S(24), S(4)) };
            _viewHost.Controls.Add(_viewBody);
            _viewHost.Controls.Add(_viewTitle);
            // die eingebetteten Fenster haben selbst 24 Rand: Überschrift und Inhalt beginnen so beide bei 28
            _viewTitle.Padding = new Padding(S(24), 0, 0, 0);

            Controls.Add(_viewHost);
            Controls.Add(_list);
            Controls.Add(_bar);
            Controls.Add(searchBar);
            Controls.Add(_updateHost);
            Controls.Add(_navSpacer);
            Controls.Add(_footer);
            Controls.Add(_header);
            Controls.Add(_nav); // nicht angedockt: liegt ausgeklappt über dem Inhalt
            _nav.BringToFront();
            Resize += (s, e) => ApplyNav();
            _footer.LocationChanged += (s, e) => ApplyNav();
            _header.SizeChanged += (s, e) => ApplyNav();
            ApplyNav();

            // ---------- Fenstergröße ----------
            MinimumSize = new Size(S(860), S(520));
            // ab Layout 2 größer (Schalter "Alle wecken" in jeder Zeile); kleinere gespeicherte Größe aus älteren Versionen verwerfen.
            // Layout 3: Navigation links – gespeicherte Breite aus Layout 2 um die Navigation verbreitern.
            bool keep = _store.LayoutVersion >= 2 && _store.WindowWidth > 0 && _store.WindowHeight > 0;
            int w = keep ? _store.WindowWidth + (_store.LayoutVersion < 3 ? S(230) : 0) : S(1260);
            int h = keep ? _store.WindowHeight : S(820);
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(w, wa.Width), Math.Min(h, wa.Height));
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

            if (!_demo)
            {
                _notifier = new Notifier();
                _notifier.Clicked += (s, e) =>
                {
                    ShowFromTray();
                    // Klick auf die Meldung "Update verfügbar" startet das Update (mit Rückfrage)
                    if (_updateBalloon && _update != null) { _updateBalloon = false; Post(() => { var t = InstallUpdate(); }); }
                    else _updateBalloon = false;
                };
                AboutDialog.Updates = MakeUpdateHooks(); // Info-Fenster: Stand, "Jetzt prüfen", automatisch prüfen
            }
            BuildRows();
            _timer.Tick += (s, e) => Tick();
            if (!_demo) Shown += (s, e) => StartUp();
            Theme.Apply(this);
        }

        /// <summary>Start mit /hintergrund: Fenster gar nicht erst anzeigen, nur das Symbol im Infobereich.</summary>
        protected override void SetVisibleCore(bool value)
        {
            if (_startHidden)
            {
                _startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
                BeginInvoke(new Action(StartUp));
            }
            base.SetVisibleCore(value);
        }

        /// <summary>Einmal nach dem Start: Statusprüfung, Zeitpläne, Handy-Steuerung, Infobereich.</summary>
        private void StartUp()
        {
            if (_started) return;
            _started = true;
            _timer.Start();
            Tick();
            RepairSchedules();
            if (Autostart.Repair()) History.Add("Autostart", "auf neuen Programmpfad umgestellt (" + Application.StartupPath + ")");
            StartRemote();
            UpdateTray();
            StartUpdateTimer();
            // Zweiter Programmstart (z. B. Verknüpfung) holt dieses Fenster aus dem Infobereich
            if (Program.ShowSignal != null)
                ThreadPool.RegisterWaitForSingleObject(Program.ShowSignal, (st, timeout) => Post(ShowFromTray), null, -1, false);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        /// <summary>
        /// Einheitlicher rechter Rand des Inhalts (Kopf-Knopf, Hinweisleiste, Zeilen, Knopfleiste):
        /// 28 plus die Breite der senkrechten Bildlaufleiste, die rechts in der Liste ihren festen Platz hat.
        /// </summary>
        private int RightInset { get { return S(28) + SystemInformation.VerticalScrollBarWidth; } }

        // ================================================================== Navigation

        private NavButton AddNav(string text, char glyph, Action click)
        {
            var b = new NavButton(text, glyph, k);
            b.Click += (s, e) => click();
            _nav.Controls.Add(b);
            return b;
        }

        /// <summary>Ausgeklappte (nicht angepinnte) Navigation nach einer Auswahl wieder einklappen.</summary>
        private void CloseNav()
        {
            if (!_store.NavPinned && _navOpen) { _navOpen = false; ApplyNav(); }
        }

        // ================================================================== Ansichten im selben Fenster

        /// <summary>
        /// Zeigt ein bisheriges Dialogfenster als Ansicht im Inhaltsbereich (ohne Rahmen, eingebettet).
        /// "Schließen", "Abbrechen" bzw. "Speichern" des Fensters führen zurück zur Geräteliste; done erhält das Ergebnis.
        /// </summary>
        private void ShowView(NavButton item, Form f, Action<DialogResult> done, string title = null)
        {
            if (title == null) title = item.Text;
            CloseNav();
            if (_view != null && _viewTitle.Tag == item && _viewTitle.Text == title) { f.Dispose(); return; } // schon offen
            if (!ConfirmLeaveView()) { f.Dispose(); return; } // ungespeicherte Änderungen: auf der Seite bleiben
            CloseView();
            if (!Visible) ShowFromTray();

            var size = f.ClientSize;
            // Verlauf und anpassbare Ansichten (Steuerung per Handy) füllen den Bereich; feste Fenster behalten ihre Größe
            bool fill = !(f is FixedForm) || f is IViewLayout;
            f.TopLevel = false;
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.ClientSize = size;
            if (fill) f.Dock = DockStyle.Fill;
            else f.Location = new Point(0, 0);
            // Knöpfe mit DialogResult schließen ein eingebettetes Fenster nicht von selbst
            foreach (var b in AllButtons(f))
            {
                var button = b;
                if (button.DialogResult != DialogResult.None)
                    button.Click += (s, e) => { f.DialogResult = button.DialogResult; BeginInvoke(new Action(f.Close)); };
            }
            f.FormClosed += (s, e) => BeginInvoke(new Action(() => EndView(f)));

            _view = f;
            _viewDone = done;
            _viewTitle.Text = title;
            _viewTitle.Tag = item;
            foreach (var n in new[] { _navDevices, _navSchedules, _navHistory, _navRemote }) n.Selected = n == item;

            _list.Visible = false;
            _searchBar.Visible = false;
            _bar.Visible = false;
            _viewBody.Controls.Add(f);
            _viewHost.Visible = true;
            _viewHost.BringToFront();
            _nav.BringToFront();
            f.Show();
            var adaptive = f as IViewLayout;
            if (adaptive != null)
            {
                // an die Breite anpassen (z. B. Karten untereinander); das Fenster scrollt selbst, nur senkrecht
                f.AutoScroll = true;
                f.AutoScrollMargin = new Size(0, S(24));
                _viewResize = (s, e) =>
                {
                    if (_view != f || f.IsDisposed) return;
                    // Positionen gelten in einem gescrollten Fenster relativ zur Ansicht: erst nach oben, dann zurück
                    int y = -f.AutoScrollPosition.Y;
                    f.AutoScrollPosition = new Point(0, 0);
                    f.SuspendLayout();
                    adaptive.Arrange(f.ClientSize.Width);
                    f.ResumeLayout(true);
                    f.AutoScrollPosition = new Point(0, y);
                };
                f.ClientSizeChanged += _viewResize;
                _viewResize(this, EventArgs.Empty);
            }
            f.Focus();
            // Stand für die Rückfrage merken, wenn die Seite fertig aufgebaut ist
            var tracked = f as IHasChanges;
            if (tracked != null) BeginInvoke(new Action(() => { if (_view == f && !f.IsDisposed) tracked.ResetChanges(); }));
        }

        /// <summary>
        /// Vor dem Verlassen einer Seite mit ungespeicherten Änderungen nachfragen (globale Vorgabe).
        /// true = verlassen (keine Änderungen oder „Ja“), false = auf der Seite bleiben.
        /// </summary>
        private bool ConfirmLeaveView()
        {
            var tracked = _view as IHasChanges;
            if (tracked == null || _view.IsDisposed || !tracked.HasChanges) return true;
            if (!Visible) ShowFromTray();
            return MessageBox.Show(this, "Die Änderungen auf der Seite „" + _viewTitle.Text + "“ sind noch nicht gespeichert.\r\n\r\nÄnderungen verwerfen?",
                Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private static IEnumerable<Button> AllButtons(Control c)
        {
            foreach (Control child in c.Controls)
            {
                var b = child as Button;
                if (b != null) yield return b;
                foreach (var inner in AllButtons(child)) yield return inner;
            }
        }

        /// <summary>Offene Ansicht schließen (wie "Abbrechen") und zur Geräteliste zurück.</summary>
        private void CloseView()
        {
            var f = _view;
            if (f == null) return;
            _closingForNav = true; // eine andere Seite wird geöffnet: Rücksprünge (z. B. zur Zeitplan-Liste) entfallen
            try
            {
                f.DialogResult = DialogResult.Cancel;
                f.Close();
                EndView(f);
            }
            finally { _closingForNav = false; }
        }

        private bool _closingForNav;

        private void EndView(Form f)
        {
            if (_view != f) return;
            var done = _viewDone;
            var result = f.DialogResult;
            _view = null;
            _viewDone = null;
            if (_viewResize != null) { f.ClientSizeChanged -= _viewResize; _viewResize = null; }
            _viewTitle.Tag = null;
            _viewBody.Controls.Remove(f);
            if (!f.IsDisposed) f.Dispose();
            _viewHost.Visible = false;
            _bar.Visible = true;
            _searchBar.Visible = true;
            _list.Visible = true;
            foreach (var n in new[] { _navSchedules, _navHistory, _navRemote }) n.Selected = false;
            _navDevices.Selected = true;
            _nav.BringToFront();
            FitRows();
            if (done != null) done(result);
        }

        private void SetNavPinned(bool pinned)
        {
            _store.NavPinned = pinned;
            try { _store.Save(); } catch { }
            _navOpen = pinned;
            ApplyNav();
        }

        private void ApplyNav()
        {
            if (_nav == null || _footer == null || _header == null) return;
            bool pinned = _store.NavPinned;
            bool open = pinned || _navOpen;
            int full = S(230), compact = S(68);
            if (_navSpacer.Width != (pinned ? full : compact))
            {
                _navSpacer.Width = pinned ? full : compact;
                FitRows();
            }
            int top = _header.Bottom, h = Math.Max(0, _footer.Top - _header.Bottom);
            _nav.SetBounds(0, top, open ? full : compact, h);
            _nav.BringToFront();
            _navPin.Visible = open;
            _navPin.Glyph = pinned ? MenuHelper.Unpin : MenuHelper.Pin;
            string pinText = pinned ? "Navigation lösen (einklappbar)" : "Navigation anpinnen";
            _navPin.AccessibleName = pinText;
            _tip.SetToolTip(_navPin, pinText);
            _nav.Invalidate();
            _navPin.Invalidate();
            LayoutNav();
        }

        private void LayoutNav()
        {
            if (_navQuit == null) return;
            int x = S(10), w = _nav.Width - S(20), h = _navDevices.Height;
            int y = S(4);
            _navMenu.SetBounds(x, y, S(48), h);
            _navPin.SetBounds(_nav.Width - S(10) - S(36), y + (h - S(36)) / 2, S(36), S(36));
            y += h + S(10);
            foreach (var b in new[] { _navDevices, _navSchedules, _navHistory, _navRemote })
            {
                b.SetBounds(x, y, w, h);
                y += h + S(4);
            }
            int bottom = _nav.Height - S(14);
            _navQuit.Visible = KeepRunning;
            if (KeepRunning)
            {
                _navQuit.SetBounds(x, bottom - h, w, h);
                bottom -= h + S(4);
            }
            _navMore.SetBounds(x, bottom - h, w, h);
        }

        // ================================================================== Update-Prüfung (wie Fensterpositionen wiederherstellen)

        private void StartUpdateTimer()
        {
            if (AfterUpdate)
            {
                // Neustart durch das Update-Skript: Ergebnis melden
                string result = UpdateCheck.LastResult();
                if (result != null && result.StartsWith("OK"))
                {
                    string v = result.Substring(2).Trim();
                    SetMessage("Update auf Version " + v + " installiert.", Ui.Green);
                    History.Add("Wake on LAN", "Update auf Version " + v + " installiert");
                }
                else if (result != null)
                {
                    string why = result.Replace("FEHLER", "").Trim();
                    SetMessage("Update fehlgeschlagen: " + why + " – die bisherige Version läuft weiter.", Ui.Red);
                    History.Add("Wake on LAN", "Update fehlgeschlagen: " + why);
                }
            }
            _updateTimer.Tick += async (s, e) =>
            {
                _updateTimer.Interval = 6 * 3600000; // danach alle 6 Stunden nachsehen, ob die Prüfung fällig ist (läuft oft tagelang im Infobereich)
                await CheckForUpdates(false);
            };
            _updateTimer.Start();
        }

        private UpdateHooks MakeUpdateHooks()
        {
            return new UpdateHooks
            {
                Status = UpdateState,
                Auto = () => _store.UpdateCheck,
                SetAuto = on =>
                {
                    _store.UpdateCheck = on;
                    try { _store.Save(); } catch { }
                },
                CheckNow = () => CheckForUpdates(true),
                CanInstall = () => _update != null && !string.IsNullOrEmpty(_update.ZipUrl) && UpdateCheck.InstallBlocker() == null,
                Install = () => Post(() => { var t = InstallUpdate(); }), // erst nach dem Schließen des Info-Fensters
                OpenPage = OpenReleasePage
            };
        }

        /// <summary>Stand für das Info-Fenster: Symbol, Überschrift und zweite Zeile.</summary>
        private UpdateStatus UpdateState()
        {
            string last = _store.UpdateLast == DateTime.MinValue ? "" : "Zuletzt geprüft " + _store.UpdateLast.ToString("dd.MM.yyyy");
            if (_update != null) return new UpdateStatus(UpdateKind.Available, "Version " + _update.Version + " verfügbar", last);
            if (_updateError != null) return new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", _updateError);
            if (_store.UpdateLast == DateTime.MinValue) return new UpdateStatus(UpdateKind.Unknown, "Noch nicht geprüft", "");
            return new UpdateStatus(UpdateKind.Current, "Aktuell", last);
        }

        /// <summary>
        /// Neueste Version auf GitHub abfragen. Automatisch (manual = false) höchstens alle 20 Stunden und still;
        /// Fehler dann ohne Meldung (nächster Versuch nach einer Stunde).
        /// </summary>
        private async Task<UpdateStatus> CheckForUpdates(bool manual)
        {
            if (_demo || _updateRunning) return UpdateState();
            if (!manual && (!_store.UpdateCheck || (DateTime.Now - _store.UpdateLast).TotalHours < 20)) return UpdateState();
            _updateRunning = true;
            try
            {
                var release = await Task.Run(() => UpdateCheck.Latest());
                _updateError = null;
                _store.UpdateLast = DateTime.Now;
                try { _store.Save(); } catch { }
                if (UpdateCheck.IsNewer(release.Version))
                {
                    bool fresh = _update == null || _update.Version != release.Version;
                    _update = release;
                    ShowUpdateBar(true);
                    // im Infobereich: einmal je Version und Programmstart melden; Klick startet das Update
                    if ((fresh || !_updateBalloonShown) && !manual && !Visible && _notifier != null)
                    {
                        _updateBalloon = true;
                        _updateBalloonShown = true;
                        _notifier.Show("Update verfügbar", "Wake on LAN " + release.Version + " ist verfügbar (installiert: " + Ui.Version + "). Klicken zum Aktualisieren.", ToolTipIcon.Info);
                    }
                }
                else
                {
                    _update = null;
                    ShowUpdateBar(false);
                    // keine Meldung im Hauptfenster: der Stand steht im Info-Fenster, dort wurde geprüft
                }
            }
            catch (Exception ex)
            {
                if (manual)
                {
                    _updateError = ex.Message; // erscheint im Info-Fenster
                }
            }
            finally { _updateRunning = false; }
            return UpdateState();
        }

        private void ShowUpdateBar(bool show)
        {
            show = show && _update != null;
            if (show)
            {
                _updateBar.Text = "Neue Version " + _update.Version + " verfügbar – installiert ist " + Ui.Version + ".";
                string blocker = UpdateCheck.InstallBlocker();
                _updateBar.Install.Enabled = blocker == null && !string.IsNullOrEmpty(_update.ZipUrl);
                _tip.SetToolTip(_updateBar.Install, blocker ?? (string.IsNullOrEmpty(_update.ZipUrl) ? "Im Release ist keine ZIP-Datei angehängt." :
                    "ZIP herunterladen, Programm beenden, EXE und Liesmich ersetzen und neu starten.\r\nGeräteliste, Zeitpläne und Einstellungen bleiben erhalten."));
                _tip.SetToolTip(_updateBar, string.IsNullOrEmpty(_update.Notes) ? "Die Änderungen stehen auf der Release-Seite." : "Änderungen:\r\n" + _update.Notes);
            }
            if (show == _updateHost.Visible) return;
            _updateHost.Visible = show;
            // Fenster um die Leiste vergrößern, soweit der Bildschirm reicht (globale Vorgabe)
            if (show && WindowState == FormWindowState.Normal)
            {
                var wa = Screen.FromControl(this).WorkingArea;
                int grow = Math.Min(_updateHost.Height, wa.Bottom - Bottom);
                if (grow > 0) Height += grow;
            }
        }

        private void OpenReleasePage()
        {
            Ui.Open(_update != null && !string.IsNullOrEmpty(_update.Url) ? _update.Url : UpdateCheck.ReleasesPage);
        }

        private async Task InstallUpdate()
        {
            if (_update == null || _installing) return;
            var info = _update;
            string text = "Wake on LAN wird beendet, auf Version " + info.Version + " aktualisiert und neu gestartet.\r\n" +
                "Geräteliste, Zeitpläne und Einstellungen bleiben erhalten.";
            if (_web != null) text += "\r\nDie Steuerung per Handy ist dabei kurz nicht erreichbar.";
            if (MessageBox.Show(this, text + "\r\n\r\nJetzt aktualisieren?", "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _installing = true;
            foreach (var b in new[] { _updateBar.Install, _updateBar.Page, _updateBar.Later }) b.Enabled = false;
            string barText = _updateBar.Text;
            try
            {
                _updateBar.Text = "Lade Version " + info.Version + " …";
                await UpdateCheck.Install(info, percent => Post(() => _updateBar.Text = "Lade Version " + info.Version + " … " + percent + " %"));
                History.Add("Wake on LAN", "Update auf Version " + info.Version + " wird installiert");
                _exit = true; // wirklich beenden, nicht in den Infobereich – das Skript wartet darauf und startet neu
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _updateBar.Text = barText;
                _updateBar.Page.Enabled = true;
                _updateBar.Later.Enabled = true;
                _updateBar.Install.Enabled = UpdateCheck.InstallBlocker() == null;
                _installing = false;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_view != null) { base.OnKeyDown(e); return; } // Tastenkürzel der Geräteliste nur dort
            if (e.KeyCode == Keys.F5) { CheckAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.N && e.Control) { AddDevice(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Control) { _search.Box.Focus(); _search.Box.SelectAll(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        /// <summary>Im Hintergrund weiterlaufen: Handy-Steuerung ist eingeschaltet und "Im Infobereich weiterlaufen" gewählt.</summary>
        private bool KeepRunning { get { return !_demo && _store.Remote.Enabled && _store.Remote.Background; } }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exit && !_demo && e.CloseReason == CloseReason.UserClosing && KeepRunning)
            {
                // Schließen = in den Infobereich; Beenden über das Symbol bzw. das Menü
                e.Cancel = true;
                SaveWindowSize();
                if (_dirty) { try { _store.Save(); } catch { } _dirty = false; }
                Hide();
                UpdateTray();
                if (!_trayHintShown && _notifier != null)
                {
                    _trayHintShown = true;
                    _notifier.Show(Ui.AppName, "Läuft im Infobereich weiter – die Steuerung per Handy bleibt erreichbar. Beenden: Rechtsklick auf das Symbol.", ToolTipIcon.Info);
                }
                base.OnFormClosing(e);
                return;
            }
            if (!_exit && !_demo && e.CloseReason == CloseReason.UserClosing && !ConfirmLeaveView())
            {
                e.Cancel = true;
                base.OnFormClosing(e);
                return;
            }
            _timer.Stop();
            if (_web != null) { _web.Dispose(); _web = null; }
            if (_pendingWeb != null) { _pendingWeb.Dispose(); _pendingWeb = null; }
            if (_dirty) { try { _store.Save(); } catch { } _dirty = false; }
            if (_notifier != null) _notifier.Dispose();
            SaveWindowSize();
            base.OnFormClosing(e);
        }

        private void SaveWindowSize()
        {
            if (_demo) return;
            var size = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            if (size.Width != _store.WindowWidth || size.Height != _store.WindowHeight || _store.LayoutVersion != DeviceStore.CurrentLayout)
            {
                _store.LayoutVersion = DeviceStore.CurrentLayout;
                _store.WindowWidth = size.Width;
                _store.WindowHeight = size.Height;
                try { _store.Save(); } catch { }
            }
        }

        /// <summary>Fenster aus dem Infobereich (bzw. minimiert) wieder anzeigen.</summary>
        private void ShowFromTray()
        {
            if (IsDisposed) return;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            UpdateTray();
        }

        /// <summary>Programm wirklich beenden (auch wenn es sonst im Infobereich weiterläuft).</summary>
        private void ExitProgram()
        {
            if (FocusOpenDialog()) return;
            if (!ConfirmLeaveView()) return;
            _exit = true;
            Close();
        }

        /// <summary>Ist gerade ein Dialog offen (z. B. aus dem Infobereich aufgerufen), diesen nach vorne holen statt weiterzumachen.</summary>
        private bool FocusOpenDialog()
        {
            if (OwnedForms.Length == 0) return false;
            ShowFromTray();
            OwnedForms[0].Activate();
            return true;
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
            var scan = new ModernButton("Im Netzwerk suchen …", MenuHelper.Search, false, k) { Location = new Point(add.Right + S(10), S(92)) };
            scan.Click += (s, e) => AddFromScan();
            var import = new ModernButton("Aus alten Skripten übernehmen …", MenuHelper.Folder, false, k) { Location = new Point(scan.Right + S(10), S(92)) };
            import.Click += (s, e) => ImportScripts();
            card.Controls.Add(add);
            card.Controls.Add(scan);
            card.Controls.Add(import);
            return card;
        }

        private void FitRows()
        {
            // rechter Rand einheitlich (RightInset): ohne Bildlaufleiste hält die Liste deren Platz frei,
            // mit Bildlaufleiste bleiben 28 zwischen Zeilen und Leiste – nichts springt, alles endet auf einer Linie
            int right = _list.VerticalScroll.Visible ? S(28) : RightInset;
            if (_list.Padding.Right != right) _list.Padding = new Padding(_list.Padding.Left, _list.Padding.Top, right, _list.Padding.Bottom);
            int w = _list.ClientSize.Width - _list.Padding.Horizontal;
            if (w <= 0) return;
            int y = -_list.AutoScrollPosition.Y;
            _list.SuspendLayout();
            foreach (Control c in _list.Controls) c.Width = w - c.Margin.Horizontal;
            _list.ResumeLayout(true);
            // Wird die Liste schmaler (z. B. Navigation angepinnt), waren die Zeilen kurz zu breit: WinForms schiebt den
            // Inhalt dann seitlich weg und lässt die waagrechte Bildlaufleiste stehen. Zurück an den linken Rand.
            if (_list.AutoScrollPosition.X != 0 || _list.HorizontalScroll.Visible)
            {
                _list.AutoScrollPosition = new Point(0, y);
                _list.PerformLayout();
            }
        }

        // ================================================================== Status

        private void Tick()
        {
            var now = DateTime.Now;
            if (_dirty && (now - _lastSave).TotalSeconds >= 60)
            {
                try { _store.Save(); } catch { }
                _dirty = false;
                _lastSave = now;
            }
            // Status nur prüfen, wenn jemand hinschaut: Fenster sichtbar, Handy verbunden oder ein Gerät wird gerade geweckt
            bool watched = (Visible && WindowState != FormWindowState.Minimized) || (_web != null && _web.RecentlyUsed);
            if (!watched && !_rows.Any(r => r.WakeStart.HasValue)) return;
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

        /// <summary>Gerät wecken. via: Herkunft für den Verlauf (z. B. "Handy"), sonst null.</summary>
        private void Wake(DeviceRow row, WakeAllRun all, string via = null)
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
                    History.Add(device.Name, (result.Ok ? "geweckt: " : "Fehler beim Wecken: ") + result.Message + (via != null ? " (" + via + ")" : ""));
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
        private void WakeMany(List<DeviceRow> rows, string label, Control button, string via = null)
        {
            var run = new WakeAllRun { Remaining = rows.Count, Total = rows.Count, Label = label, Button = button };
            if (button != null) button.Enabled = false;
            SetMessage(label + ": wecke " + rows.Count + (rows.Count == 1 ? " Gerät …" : " Geräte …") + (via != null ? " (" + via + ")" : ""), Ui.Muted);
            foreach (var r in rows) Wake(r, run, via);
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

        /// <summary>Knopf "Gerät hinzufügen": selbst eingeben oder im Netzwerk suchen.</summary>
        private void ShowAddMenu(Control button)
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            menu.Items.Add(b.Item("Gerät eingeben …", MenuHelper.Edit, AddDevice));
            menu.Items.Add(b.Item("Im Netzwerk suchen …", MenuHelper.Search, AddFromScan));
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(button, new Point(0, button.Height));
        }

        /// <summary>Geräte über die Netzwerksuche übernehmen: eines zum Bearbeiten, mehrere direkt (Name, IP, MAC).</summary>
        private void AddFromScan()
        {
            // Suche als Ansicht im Hauptfenster; nach "Hinzufügen" geht es (bei einem Gerät) mit "Gerät hinzufügen" weiter
            var f = new ScanForm(_store);
            ShowView(_navDevices, f, result =>
            {
                if (result != DialogResult.OK) return;
                var picked = f.Selected.ToList();
                if (picked.Count > 0 && !_closingForNav) BeginInvoke(new Action(() => AddPicked(picked)));
            }, "Gerät im Netzwerk suchen");
        }

        private void AddPicked(List<Arp.Found> picked)
        {
            if (picked.Count == 1)
            {
                EditInView(ScanForm.ToDevice(picked[0], _store), true, d =>
                {
                    _store.Devices.Add(d);
                    SaveAndRebuild();
                    SetMessage(d.Name + " hinzugefügt.", Ui.Muted);
                });
                return;
            }
            var names = new List<string>();
            foreach (var found in picked)
            {
                var d = ScanForm.ToDevice(found, _store);
                _store.Devices.Add(d);
                names.Add(d.Name);
            }
            SaveAndRebuild();
            SetMessage(names.Count + " Geräte hinzugefügt: " + string.Join(", ", names) + " – bei Bedarf per Doppelklick anpassen.", Ui.Green);
        }

        /// <summary>
        /// Gerät bearbeiten bzw. anlegen als Ansicht im Hauptfenster (wie Sync Pilot); "Speichern" ruft saved mit dem
        /// geänderten Gerät auf, "Abbrechen" oder ein Wechsel über die Navigation verwirft.
        /// </summary>
        private void EditInView(Device device, bool isNew, Action<Device> saved)
        {
            var f = new DeviceForm(device, _store, isNew);
            string title = isNew ? "Gerät hinzufügen" : "Gerät bearbeiten – " + device.Name;
            ShowView(_navDevices, f, result => { if (result == DialogResult.OK && f.Result != null) saved(f.Result); }, title);
        }

        private void EditSchedule(ScheduleEntry entry)
        {
            var f = new ScheduleEditForm(_store, entry);
            ShowView(_navSchedules, f, r => { if (!_closingForNav) BeginInvoke(new Action(ShowSchedules)); }, f.Text);
        }

        private void ShowSchedules()
        {
            var f = new ScheduleForm(_store);
            // "Neuer Zeitplan" bzw. "Zeitplan bearbeiten" ebenfalls im selben Fenster; danach zurück zur Liste der Zeitpläne
            f.OpenEdit = entry => BeginInvoke(new Action(() => EditSchedule(entry)));
            ShowView(_navSchedules, f, null);
        }

        private void AddDevice()
        {
            EditInView(null, true, d =>
            {
                _store.Devices.Add(d);
                SaveAndRebuild();
                SetMessage(d.Name + " hinzugefügt.", Ui.Muted);
            });
        }

        private void EditDevice(Device device)
        {
            EditInView(device, false, d =>
            {
                int i = _store.Devices.IndexOf(device);
                if (i < 0) return;
                _store.Devices[i] = d;
                // Zeile behalten, Status neu prüfen
                var row = _rows.FirstOrDefault(r => r.Device == device);
                if (row != null) row.Replace(d);
                SaveAndRebuild();
            });
        }

        private void DuplicateDevice(Device device)
        {
            var copy = device.Clone();
            string baseName = device.Name + " (Kopie)";
            copy.Name = baseName;
            for (int n = 2; _store.Find(copy.Name) != null; n++) copy.Name = device.Name + " (Kopie " + n + ")";
            EditInView(copy, true, d =>
            {
                int at = _store.Devices.IndexOf(device);
                _store.Devices.Insert(at < 0 ? _store.Devices.Count : at + 1, d);
                SaveAndRebuild();
            });
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
            // moderner Ordnerdialog (wie im Explorer) statt der alten Baumansicht
            string folder = FolderPicker.Pick(this, "Ordner mit den Skripten „Wake on LAN (<Gerät>).bat“ auswählen");
            if (folder == null) return;
            {
                string summary;
                List<Device> found;
                try { found = ScriptImport.Read(folder, out summary); }
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

        // ================================================================== Steuerung per Handy

        /// <summary>Webserver nach den Einstellungen (neu) starten; Zertifikat laden kann dauern -> im Hintergrund.</summary>
        /// <summary>Startet den Webserver im Hintergrund neu; done (optional) bekommt danach den Fehler bzw. null.</summary>
        private void StartRemote(Action<string> done = null)
        {
            int generation = ++_webGeneration;
            if (_web != null) { _web.Dispose(); _web = null; }
            if (_pendingWeb != null) { _pendingWeb.Dispose(); _pendingWeb = null; }
            _webError = null;
            if (_demo || !_store.Remote.Enabled) { UpdateTray(); if (done != null) done(null); return; }
            var server = new WebServer(_store.Remote, this);
            _pendingWeb = server;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try { server.Start(); }
                catch (Exception ex) { error = ex.Message; }
                Post(() =>
                {
                    if (_pendingWeb == server) _pendingWeb = null;
                    if (generation != _webGeneration || IsDisposed) { server.Dispose(); return; }
                    if (error != null)
                    {
                        server.Dispose();
                        _webError = error;
                        SetMessage("Steuerung per Handy: " + error, Ui.Red);
                        History.Add("Handy", "Start fehlgeschlagen: " + error);
                        if (!Visible) Notify("Steuerung per Handy: " + error, ToolTipIcon.Warning);
                    }
                    else
                    {
                        _web = server;
                        CheckFirewallLater();
                    }
                    UpdateTray();
                    if (done != null) done(error);
                });
            });
        }

        /// <summary>Fehlt die Firewall-Freigabe (z. B. nach dem Verschieben des Programms), einen Hinweis zeigen.</summary>
        private void CheckFirewallLater()
        {
            int port = _store.Remote.Port;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string problem = Firewall.Problem(port);
                if (problem == null) return;
                Post(() => SetMessage("Steuerung per Handy: " + problem + " (siehe „Steuerung per Handy“ in der Navigation).", Ui.Yellow));
            });
        }

        private void OpenRemoteSettings()
        {
            if (FocusOpenDialog()) return;
            if (!Visible) ShowFromTray();
            var f = new RemoteForm(_store, _demo, _demo || _web != null, _webError);
            // "Speichern" übernimmt und startet den Server neu, die Seite bleibt offen (globale Vorgabe)
            // der Server startet im Hintergrund: Ergebnis erst melden, wenn der Start fertig ist
            f.Saved = report =>
            {
                string saveError = null;
                try { _store.Save(); }
                catch (Exception ex) { saveError = "Speichern fehlgeschlagen: " + ex.Message; }
                StartRemote(startError =>
                {
                    UpdateTray();
                    string text = _store.Remote.Enabled ? "Gespeichert – Steuerung per Handy: " + _store.Remote.Url(_store.Remote.Address) : "Gespeichert – Steuerung per Handy ausgeschaltet.";
                    report(Tuple.Create(_web != null, saveError ?? startError, text));
                });
            };
            ShowView(_navRemote, f, null);
        }

        /// <summary>Symbol im Infobereich: dauerhaft, solange das Programm im Hintergrund weiterläuft oder versteckt ist.</summary>
        private void UpdateTray()
        {
            if (_notifier == null || IsDisposed) return;
            bool persistent = KeepRunning || !Visible;
            if (persistent && _trayMenu == null) _trayMenu = BuildTrayMenu();
            string tip = Ui.AppName;
            if (_store.Remote.Enabled) tip += _web != null ? " – Handy: Port " + _web.Port : " – Handy: nicht gestartet";
            _notifier.SetPersistent(persistent, _trayMenu, tip);
            LayoutNav(); // "Beenden" nur, solange das Programm im Infobereich weiterläuft
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            // oben fett "Update auf x installieren …", nur wenn eine neue Version gefunden wurde
            var update = b.Item("Update installieren …", MenuHelper.Download, () => { ShowFromTray(); var t = InstallUpdate(); });
            update.Font = new Font(menu.Font, FontStyle.Bold);
            var updateSep = new ToolStripSeparator();
            menu.Items.Add(update);
            menu.Items.Add(updateSep);
            menu.Opening += (s, e) =>
            {
                bool on = _update != null && !_installing;
                update.Visible = on;
                updateSep.Visible = on;
                if (on) update.Text = "Update auf " + _update.Version + " installieren …";
            };
            menu.Items.Add(b.Item("Öffnen", MenuHelper.Open, ShowFromTray));
            menu.Items.Add(b.Item("Alle wecken", MenuHelper.Power, () => { WakeAll(); }));
            menu.Items.Add(b.Item("Steuerung per Handy …", MenuHelper.Phone, OpenRemoteSettings));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(b.Item("Beenden", MenuHelper.Close, ExitProgram));
            return menu;
        }

        /// <summary>Aufruf aus dem Webserver (anderer Thread) im Fenster-Thread ausführen.</summary>
        private string OnUi(Func<string> f, string fallback)
        {
            if (IsDisposed || !IsHandleCreated) return fallback;
            if (!InvokeRequired) return f();
            try { return (string)Invoke(f); }
            catch (Exception) { return fallback; }
        }

        string IRemoteHost.Snapshot()
        {
            return OnUi(BuildSnapshot, "{\"error\":\"Programm wird beendet\"}");
        }

        string IRemoteHost.Act(string action, string name, string mode)
        {
            return OnUi(() => RemoteAct(action, name ?? "", mode ?? ""), Answer(false, "Das Programm wird gerade beendet."));
        }

        string IRemoteHost.ThemeCss()
        {
            return OnUi(BuildThemeCss, "");
        }

        void IRemoteHost.Log(string text)
        {
            History.Add("Handy", text);
        }

        private static string Answer(bool ok, string text)
        {
            return "{\"ok\":" + Json.Bool(ok) + ",\"text\":" + Json.Str(text) + "}";
        }

        /// <summary>Geräte nach Gruppen (Reihenfolge wie im Fenster) mit Status als JSON für die Handy-Seite.</summary>
        private string BuildSnapshot()
        {
            var sb = new StringBuilder();
            string msgKind = _message.ForeColor == Ui.Red ? "error" : _message.ForeColor == Ui.Green ? "ok" : _message.ForeColor == Ui.Yellow ? "warn" : "info";
            sb.Append("{\"app\":").Append(Json.Str(Ui.AppName))
              .Append(",\"pc\":").Append(Json.Str(Environment.MachineName))
              .Append(",\"version\":").Append(Json.Str(Ui.Version))
              .Append(",\"year\":").Append(DateTime.Now.Year)
              .Append(",\"all\":").Append(_store.Devices.Count(d => d.InAll))
              .Append(",\"message\":").Append(Json.Str(_message.Text))
              .Append(",\"messageKind\":").Append(Json.Str(msgKind))
              .Append(",\"groups\":[");
            bool grouped = _store.Groups().Count > 0;
            var sections = grouped ? _store.Groups().Concat(new[] { "" }).ToList() : new List<string> { "" };
            bool firstGroup = true;
            foreach (string g in sections)
            {
                var devices = _store.Devices.Where(d => Device.SameGroup(d.Group, g)).ToList();
                if (devices.Count == 0) continue;
                if (!firstGroup) sb.Append(',');
                firstGroup = false;
                sb.Append("{\"name\":").Append(Json.Str(g))
                  .Append(",\"title\":").Append(Json.Str(grouped ? (g.Length > 0 ? g : "Ohne Gruppe") : ""))
                  .Append(",\"devices\":[");
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    var row = _rows.FirstOrDefault(r => r.Device == d);
                    string kind = "unknown", text = d.HasStatusTarget ? "Prüfe …" : "Kein Status";
                    if (row != null) text = row.WebStatus(out kind);
                    string error = row != null && !row.LastResultOk ? row.LastResult : "";
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(Json.Str(d.Name))
                      .Append(",\"kind\":").Append(Json.Str(kind))
                      .Append(",\"text\":").Append(Json.Str(text))
                      .Append(",\"ip\":").Append(Json.Str(d.Host.Length > 0 ? d.Host : d.LastIp))
                      .Append(",\"inAll\":").Append(Json.Bool(d.InAll))
                      .Append(",\"shutdown\":").Append(Json.Bool(d.AllowShutdown))
                      .Append(",\"ipmi\":").Append(Json.Bool(d.UseIpmi))
                      .Append(",\"error\":").Append(Json.Str(error))
                      .Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string RemoteAct(string action, string name, string mode)
        {
            const string via = "Handy";
            switch (action)
            {
                case "wake":
                    {
                        var row = _rows.FirstOrDefault(r => string.Equals(r.Device.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (row == null) return Answer(false, "„" + name + "“ ist nicht in der Liste.");
                        if (row.Busy) return Answer(true, row.Device.Name + " wird gerade geweckt.");
                        Wake(row, null, via);
                        return Answer(true, "Wecke " + row.Device.Name + " …");
                    }
                case "group":
                    {
                        var rows = _rows.Where(r => Device.SameGroup(r.Device.Group, name)).ToList();
                        if (rows.Count == 0) return Answer(false, "In dieser Gruppe sind keine Geräte.");
                        string title = name.Length > 0 ? name : "Ohne Gruppe";
                        var header = _headers.FirstOrDefault(h => Device.SameGroup(h.Group, name));
                        WakeMany(rows, "Gruppe „" + title + "“", header != null ? header.WakeButton : null, via);
                        return Answer(true, "Gruppe „" + title + "“: wecke " + rows.Count + (rows.Count == 1 ? " Gerät …" : " Geräte …"));
                    }
                case "all":
                    {
                        var rows = _rows.Where(r => r.Device.InAll).ToList();
                        if (rows.Count == 0) return Answer(false, "Kein Gerät ist für „Alle wecken“ vorgesehen.");
                        WakeMany(rows, "Alle wecken", _wakeAll, via);
                        return Answer(true, "Wecke " + rows.Count + (rows.Count == 1 ? " Gerät …" : " Geräte …"));
                    }
                case "shutdown":
                    {
                        var d = _store.Find(name);
                        if (d == null) return Answer(false, "„" + name + "“ ist nicht in der Liste.");
                        bool ipmi = mode == "ipmi", restart = mode == "restart";
                        if (ipmi ? !d.UseIpmi : !d.AllowShutdown) return Answer(false, "Für " + d.Name + " ist das nicht eingerichtet.");
                        DoShutdown(d, restart, ipmi, via);
                        return Answer(true, d.Name + ": " + (ipmi ? "über IPMI herunterfahren" : restart ? "neu starten" : "herunterfahren") + " …");
                    }
            }
            return Answer(false, "Unbekannte Aktion.");
        }

        /// <summary>Windows-Akzentfarbe für die Handy-Seite (hell und dunkel), wie im Fenster.</summary>
        private string BuildThemeCss()
        {
            Accent.Refresh();
            var p = Accent.Palette;
            Color light = p != null ? p[4] : Color.FromArgb(37, 99, 235);
            Color lightHover = p != null ? p[5] : Color.FromArgb(29, 78, 216);
            Color lightText = p != null ? p[5] : Color.FromArgb(29, 78, 216);
            Color dark = p != null ? p[1] : Color.FromArgb(96, 165, 250);
            Color darkHover = p != null ? p[2] : Color.FromArgb(147, 180, 255);
            Color darkText = p != null ? p[1] : Color.FromArgb(147, 180, 255);
            Func<Color, string> hex = c => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            Func<Color, string> on = c => Accent.Luminance(c) > 0.5 ? "#141414" : "#FFFFFF";
            return ":root{--akzent:" + hex(light) + ";--akzent-hover:" + hex(lightHover) + ";--akzent-schrift:" + hex(lightText) + ";--auf-akzent:" + on(light) + "}\n" +
                "@media (prefers-color-scheme: dark){:root{--akzent:" + hex(dark) + ";--akzent-hover:" + hex(darkHover) + ";--akzent-schrift:" + hex(darkText) + ";--auf-akzent:" + on(dark) + "}}\n";
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

        /// <summary>
        /// Screenshots: eine Ansicht im Hauptfenster öffnen, so wie man sie im Programm sieht
        /// ("geraet" = Gerät bearbeiten, "zeitplaene", "handy"; alles andere = Geräteliste).
        /// </summary>
        internal void DemoShowView(string what, string deviceName)
        {
            switch (what)
            {
                case "geraet":
                    var d = _store.Find(deviceName) ?? _store.Devices.FirstOrDefault();
                    if (d != null) EditInView(d, false, x => { });
                    break;
                case "zeitplaene": ShowSchedules(); break;
                case "handy": OpenRemoteSettings(); break;
                default: CloseView(); break;
            }
        }

        /// <summary>
        /// Screenshots: Fenster so hoch machen, dass die Ansicht ohne Scrollen passt (höchstens maxHeight).
        /// Rückgabe: true, wenn die Höhe geändert wurde.
        /// </summary>
        internal bool DemoFitView(int maxHeight)
        {
            var f = _view;
            if (f == null || !f.AutoScroll) return false;
            int missing = f.DisplayRectangle.Height - f.ClientSize.Height;
            int height = Math.Min(maxHeight, Height + Math.Max(0, missing));
            if (height == Height) return false;
            Height = height;
            return true;
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

        private void ShowMainMenu(Control owner, Point location, ToolStripDropDownDirection direction = ToolStripDropDownDirection.Default, Action closed = null)
        {
            var b = new ModernMenuBuilder(this);
            var menu = b.CreateMenu();
            menu.Items.Add(b.Item("Gerät hinzufügen …", MenuHelper.Add, AddDevice));
            menu.Items.Add(b.Item("Im Netzwerk suchen …", MenuHelper.Search, AddFromScan));
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
            menu.Closed += (s, e) =>
            {
                if (closed != null) closed();
                BeginInvoke(new Action(menu.Dispose));
            };
            menu.Show(owner, location, direction);
        }

        /// <summary>Nach dem Verschieben des Programms: Zeitpläne mit altem Programmpfad still auf den neuen Pfad umstellen.</summary>
        private void RepairSchedules()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int n;
                try { n = Schedules.RepairPaths(); } catch { return; }
                if (n == 0) return;
                Post(() =>
                {
                    SetMessage(n + (n == 1 ? " Zeitplan" : " Zeitpläne") + " auf den neuen Programmordner umgestellt.", Ui.Muted);
                    History.Add("Zeitplan", n + " auf neuen Programmpfad umgestellt (" + Application.StartupPath + ")");
                });
            });
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
            DoShutdown(d, restart, ipmi, null);
        }

        /// <summary>Herunterfahren ohne Rückfrage (nach der Abfrage im Fenster bzw. auf dem Handy).</summary>
        private void DoShutdown(Device d, bool restart, bool ipmi, string via)
        {
            string what = ipmi ? "über IPMI herunterfahren" : restart ? "neu starten" : "herunterfahren";
            var copy = d.Clone();
            SetMessage(d.Name + ": " + what + " …" + (via != null ? " (" + via + ")" : ""), Ui.Muted);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                WakeResult r;
                try { r = ipmi ? Waker.ShutdownIpmi(copy) : Waker.ShutdownWindows(copy, restart); }
                catch (Exception ex) { r = new WakeResult(); r.Fail(ex.Message); }
                Post(() =>
                {
                    if (r.FoundIp != null && r.FoundIp != d.LastIp) { d.LastIp = r.FoundIp; _dirty = true; }
                    SetMessage(d.Name + ": " + r.Message, r.Ok ? Ui.Muted : Ui.Red);
                    History.Add(d.Name, (r.Ok ? "" : "Fehler: ") + r.Message + (via != null ? " (" + via + ")" : ""));
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
        private readonly ModernButton _wake;
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

            // gleich breit wie "Gruppe wecken", damit die Knöpfe rechts eine ruhige Spalte bilden
            _wake = new ModernButton("Wecken", MenuHelper.Power, false, k) { HeightUnits = 28, Margin = Padding.Empty, FixedWidth = GroupHeader.WakeWidth(k), AlignLeft = true };
            _wake.UpdateSize();
            _wake.Click += (s, e) => { if (WakeClicked != null) WakeClicked(this, EventArgs.Empty); };
            // Schalter "Alle wecken" direkt in der Zeile
            _all = new ToggleSwitch("Alle wecken", k);
            _all.SetChecked(device.InAll);
            _all.CheckedChanged += (s, e) => { if (InAllChanged != null) InAllChanged(this, EventArgs.Empty); };
            tip.SetToolTip(_all, "Gehört zu „Alle wecken“ (ein/aus)");
            Controls.Add(_all);
            Controls.Add(_wake);
            foreach (Control c in Controls)
            {
                // Rechtsklick auch über dem Schalter bzw. "Wecken": Menü des Geräts
                c.MouseUp += (s, e) =>
                {
                    var src = (Control)s;
                    if (e.Button == MouseButtons.Right && MenuRequested != null)
                        MenuRequested(this, new MouseEventArgs(MouseButtons.Right, 1, src.Left + e.X, src.Top + e.Y, 0));
                };
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
            if (_wake == null || _all == null) return;
            int pad = (int)(8 * k);
            _wake.Location = new Point(Width - pad - _wake.Width, (Height - _wake.Height) / 2);
            // [Text …] [Alle wecken ◯] [Status] [Wecken] – weitere Funktionen per Rechtsklick
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

        /// <summary>Status für die Handy-Seite: Text wie in der Zeile und Art (online, offline, off, error, checking, waking, busy, unknown).</summary>
        internal string WebStatus(out string kind)
        {
            Color color, dot;
            bool filled;
            string text = StatusText(out color, out dot, out filled);
            if (Busy) kind = "busy";
            else if (WakeStart.HasValue && State != DeviceState.Online) kind = "waking";
            else
            {
                switch (State)
                {
                    case DeviceState.Online: kind = "online"; break;
                    case DeviceState.Offline: kind = "offline"; break;
                    case DeviceState.PowerOff: kind = "off"; break;
                    case DeviceState.Error: kind = "error"; break;
                    case DeviceState.Checking: kind = "checking"; break;
                    default: kind = Device.HasStatusTarget ? "checking" : "unknown"; break;
                }
            }
            // Lief die Prüfung im Hintergrund nicht (niemand hat hingeschaut), ist der alte Stand nicht verlässlich
            if ((kind == "online" || kind == "offline" || kind == "off" || kind == "error") && Device.HasStatusTarget && (DateTime.Now - LastCheck).TotalSeconds > 60)
            {
                kind = "checking";
                text = "Prüfe …";
            }
            if (text.Length == 0) text = kind == "checking" ? "Prüfe …" : "Kein Status";
            return text;
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

        private const string DetailGap = "  ·  ";

        private List<string> Details()
        {
            var parts = new List<string>();
            var d = Device;
            if (d.UseMagic && d.Macs.Count > 0) parts.Add(string.Join(", ", d.Macs));
            if (d.Host.Length > 0) parts.Add(d.Host);
            else if (d.AutoIp) parts.Add(d.LastIp.Length > 0 ? d.LastIp + " (automatisch)" : "IP automatisch");
            if (d.UsePort) parts.Add("Port " + d.Port);
            if (d.UseIpmi) parts.Add("IPMI " + d.IpmiHost);
            return parts;
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

                // Details (MAC, IP, Port, IPMI) grau dahinter. Bei Platzmangel fallen ganze Angaben von hinten weg
                // (kein abgeschnittenes "Po…"); alle stehen im Tooltip. Nur eine einzelne zu lange Angabe bekommt "…".
                int dx = x + Math.Min(nameW, nameMax) + (int)(16 * k);
                if (dx < textRight)
                {
                    var parts = Details();
                    string shown = string.Join(DetailGap, parts);
                    while (parts.Count > 1 && TextRenderer.MeasureText(g, shown, fSmall, Size.Empty, measure).Width > textRight - dx)
                    {
                        parts.RemoveAt(parts.Count - 1);
                        shown = string.Join(DetailGap, parts);
                    }
                    TextRenderer.DrawText(g, shown, fSmall, new Rectangle(dx, smallTop, textRight - dx, smallSize.Height), Ui.Muted, flags);
                }
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
            foreach (string part in Details()) tip += "\r\n" + part;
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
    /// rechts "Gruppe wecken" (weitere Funktionen per Rechtsklick). "Ohne Gruppe" hat keine Knöpfe.
    /// </summary>
    internal sealed class GroupHeader : Panel
    {
        private static int _wakeWidth;
        private static float _wakeScale;

        /// <summary>Gemeinsame Breite von "Gruppe wecken" und "Wecken" (Breite des längeren Texts).</summary>
        public static int WakeWidth(float k)
        {
            if (_wakeWidth > 0 && _wakeScale == k) return _wakeWidth;
            using (var b = new ModernButton("Gruppe wecken", MenuHelper.Power, false, k) { HeightUnits = 28 })
            {
                b.UpdateSize();
                _wakeWidth = b.Width;
            }
            _wakeScale = k;
            return _wakeWidth;
        }

        private readonly float k;
        private readonly ModernButton _wake;
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
            // Überschrift als Karte in der Hover-Farbe der Zeilen (auch zugeklappt als Block erkennbar);
            // BackColor = Kartenfarbe, weil "Gruppe wecken" seine Ecken in der Farbe des Elternelements zeichnet
            BackColor = Ui.CardHover;
            Cursor = Cursors.Hand;
            if (Group.Length > 0)
            {
                // Knöpfe vor dem Setzen der Höhe anlegen (OnLayout greift darauf zu)
                _wake = new ModernButton("Gruppe wecken", MenuHelper.Power, false, k) { HeightUnits = 28, Margin = Padding.Empty, FixedWidth = WakeWidth(k), AlignLeft = true };
                _wake.UpdateSize();
                _wake.Click += (s, e) => { if (WakeClicked != null) WakeClicked(this, EventArgs.Empty); };
                Controls.Add(_wake);
                foreach (Control c in Controls)
                {
                    c.Cursor = Cursors.Hand;
                    c.MouseEnter += (s, e) => SetHover(true);
                    c.MouseLeave += (s, e) => SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
                    // Rechtsklick auch über "Gruppe wecken": Menü der Gruppe
                    c.MouseUp += (s, e) =>
                    {
                        var src = (Control)s;
                        if (e.Button == MouseButtons.Right && MenuRequested != null)
                            MenuRequested(this, new MouseEventArgs(MouseButtons.Right, 1, src.Left + e.X, src.Top + e.Y, 0));
                    };
                    c.AllowDrop = true;
                    c.DragEnter += (s, e) => OnDragEnter(e);
                    c.DragOver += (s, e) => OnDragOver(e);
                    c.DragLeave += (s, e) => OnDragLeave(e);
                    c.DragDrop += (s, e) => OnDragDrop(e);
                }
            }
            AllowDrop = true;
            tip.SetToolTip(this, "Klick: " + (collapsed ? "aufklappen" : "zuklappen") + " · Rechtsklick: weitere Funktionen");
            Height = (int)(44 * k); // so hoch wie eine Gerätezeile
            Margin = new Padding(0, (int)(10 * k), 0, (int)(4 * k));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_wake == null) return;
            int pad = (int)(8 * k);
            _wake.Location = new Point(Width - pad - _wake.Width, (Height - _wake.Height) / 2);
        }

        private void SetHover(bool h)
        {
            if (h == _hover) return;
            _hover = h;
            BackColor = h ? Ui.Hover : Ui.CardHover;
            Invalidate(true);
        }

        protected override void OnMouseEnter(EventArgs e) { SetHover(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position))); base.OnMouseLeave(e); }

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
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var cp = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * k))
            {
                using (var cb = new SolidBrush(BackColor)) g.FillPath(cb, cp);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, cp);
            }
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
