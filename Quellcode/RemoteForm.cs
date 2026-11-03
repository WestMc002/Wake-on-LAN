// Wake on LAN – Einstellungen "Steuerung per Handy" (Port, Passwort, HTTPS, QR-Code, Firewall, Autostart)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>
    /// Dialog in zwei Spalten: links Grundeinstellungen und HTTPS, rechts QR-Code mit Adresse, Firewall und Kurzanleitung.
    /// </summary>
    internal sealed class RemoteForm : FixedForm
    {
        private readonly float k;
        private readonly DeviceStore _store;
        private readonly RemoteSettings _r;
        private readonly bool _demo;

        private ModernCheck _enabled, _background, _autostart, _https, _internet;
        private ModernTextBox _port, _address, _cert, _key, _certPassword, _certName;
        private ModernButton _setPin, _logoutAll, _checkCert, _copy, _firewallButton;
        private ModernPill _srcFile, _srcStore;
        private readonly List<Control> _fileFields = new List<Control>();
        private readonly List<Control> _storeFields = new List<Control>();
        /// <summary>Neues Passwort aus dem Dialog (wird erst beim Speichern übernommen).</summary>
        private string _newPin;
        private readonly bool _running;
        private string _pairCode;
        private readonly System.Windows.Forms.Timer _qrTimer;
        private Label _pinHint, _certStatus, _url, _firewall, _serverStatus, _error;
        private QrBox _qr;
        private readonly List<Control> _remoteFields = new List<Control>();
        private readonly List<Control> _httpsFields = new List<Control>();
        private bool _logout;
        private int _certCheck;

        public RemoteForm(DeviceStore store, bool demo, bool running, string runError)
        {
            _store = store;
            _demo = demo;
            _running = running;
            _r = store.Remote.Clone();
            if (demo)
            {
                _r.Enabled = true;
                _r.Port = RemoteSettings.DefaultPort;
                _r.Address = "wol.example.com";
                _r.Https = true;
                _r.CertFile = @"C:\Zertifikate\wol.example.com.crt";
                _r.KeyFile = @"C:\Zertifikate\wol.example.com.key";
                _r.PinHash = "demo";
            }
            Theme.Refresh();
            Text = "Steuerung per Handy";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            int margin = S(24), gap = S(16), colW = S(400), col2W = S(340);
            // drei Spalten nebeneinander (Grundeinstellungen | HTTPS | Handy), damit der Dialog auch auf 1080p-Bildschirmen passt
            int x2 = margin + colW + gap, x3 = x2 + colW + gap;

            // ---------- Links: Grundeinstellungen ----------
            var basic = NewCard("Steuerung per Handy", margin, margin, colW);
            int y = S(44);
            _enabled = Check(basic, "Steuerung per Handy einschalten", _r.Enabled, ref y);
            y = Hint(basic, "Wake on LAN zeigt die Geräteliste als Webseite an – im Handy-Browser öffnen, Passwort eingeben, wecken.", y);
            int portW = S(96), addrX = S(20) + portW + S(12);
            int yRow = y;
            _port = Field(basic, "Port", _r.Port.ToString(), ref y, portW);
            y = yRow;
            _address = Field(basic, "Adresse für das Handy", _r.Address.Length > 0 ? _r.Address : DefaultAddress(), ref y, colW - S(20) - addrX, addrX);
            y = Hint(basic, "Rechnername, IP-Adresse oder Domain/DynDNS-Name (wie im Zertifikat). Daraus entstehen Link und QR-Code.", y);
            basic.Controls.Add(new Label { Text = "Passwort", ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = false, Location = new Point(S(20), y), Size = new Size(colW - S(40), S(22)) });
            y += S(24);
            _setPin = new ModernButton(_r.HasPin ? "Ändern …" : "Festlegen …", MenuHelper.Lock, false, k) { HeightUnits = 32 };
            _setPin.UpdateSize();
            _setPin.Location = new Point(S(20), y);
            _setPin.Click += (s, e) => ChoosePin();
            basic.Controls.Add(_setPin);
            _logoutAll = new ModernButton("Alle Handys abmelden", '\0', false, k) { HeightUnits = 32 };
            _logoutAll.UpdateSize();
            _logoutAll.Location = new Point(_setPin.Right + S(8), y);
            y += _setPin.Height + S(12);
            _logoutAll.Enabled = _r.HasPin;
            _logoutAll.Click += (s, e) =>
            {
                _logout = true;
                _logoutAll.Enabled = false;
                _pinHint.Text = "Alle Handys werden beim Speichern abgemeldet und müssen sich neu anmelden.";
                UpdateUrl();
            };
            basic.Controls.Add(_logoutAll);
            _pinHint = new Label { ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = false, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(20), y - S(4)), Size = new Size(colW - S(40), S(34)) };
            basic.Controls.Add(_pinHint);
            y += S(34);
            _background = Check(basic, "Beim Schließen im Infobereich weiterlaufen", _r.Background, ref y);
            _autostart = Check(basic, "Mit Windows starten (nur im Infobereich)", demo || Autostart.Enabled, ref y);
            basic.Height = y + S(8);
            _remoteFields.AddRange(new Control[] { _port, _address, _setPin, _logoutAll, _background, _autostart });

            // ---------- Links: HTTPS ----------
            var tls = NewCard("Verschlüsselung (HTTPS)", x2, margin, colW);
            y = S(44);
            _https = Check(tls, "Verschlüsselt über HTTPS", _r.Https, ref y);
            _srcFile = new ModernPill("Zertifikatsdatei", k) { Location = new Point(S(20), y) };
            _srcStore = new ModernPill("Windows-Zertifikatspeicher", k) { Location = new Point(S(20) + _srcFile.Width + S(8), y) };
            _srcFile.Checked = !_r.CertFromStore;
            _srcStore.Checked = _r.CertFromStore;
            _srcFile.Click += (s, e) => SetSource(false);
            _srcStore.Click += (s, e) => SetSource(true);
            tls.Controls.Add(_srcFile);
            tls.Controls.Add(_srcStore);
            y += _srcFile.Height + S(14);
            int sourceTop = y;
            int browseW = S(40);
            _cert = Field(tls, "Zertifikat (.crt, .pem, .cer, .pfx, .p12)", _r.CertFile, ref y, colW - S(40) - browseW - S(8), 0, true);
            var lCert = tls.Controls[tls.Controls.Count - 2];
            var browseCert = Browse(tls, _cert, browseW, colW, () => ChooseFile(_cert, "Zertifikat auswählen", "Zertifikate (*.crt;*.pem;*.cer;*.pfx;*.p12)|*.crt;*.pem;*.cer;*.pfx;*.p12|Alle Dateien (*.*)|*.*", true));
            _key = Field(tls, "Privater Schlüssel (.key, .pem) – nicht bei .pfx/.p12", _r.KeyFile, ref y, colW - S(40) - browseW - S(8), 0, true);
            var lKey = tls.Controls[tls.Controls.Count - 2];
            var browseKey = Browse(tls, _key, browseW, colW, () => ChooseFile(_key, "Privaten Schlüssel auswählen", "Schlüssel (*.key;*.pem)|*.key;*.pem|Alle Dateien (*.*)|*.*", false));
            _certPassword = Field(tls, "Kennwort (nur .pfx/.p12)", "", ref y, S(180));
            var lCertPassword = tls.Controls[tls.Controls.Count - 2];
            _certPassword.Password = true;
            // Windows-Zertifikatspeicher: Domain an derselben Stelle wie die Dateifelder
            int yStore = sourceTop;
            _certName = Field(tls, "Domain im Zertifikat (z. B. wol.meinedomain.de)", _r.CertName, ref yStore);
            var lCertName = tls.Controls[tls.Controls.Count - 2];
            Hint(tls, "Z. B. von win-acme abgelegt und automatisch verlängert – Wake on LAN nimmt immer das neueste gültige Zertifikat.", yStore);
            var storeHint = tls.Controls[tls.Controls.Count - 1];
            _fileFields.AddRange(new Control[] { lCert, _cert, browseCert, lKey, _key, browseKey, lCertPassword, _certPassword });
            _storeFields.AddRange(new Control[] { lCertName, _certName, storeHint });
            _checkCert = new ModernButton("Zertifikat prüfen", MenuHelper.Shield, false, k) { HeightUnits = 32 };
            _checkCert.UpdateSize();
            _checkCert.Location = new Point(S(20) + S(180) + S(12), _certPassword.Top);
            _checkCert.Click += (s, e) => CheckCertificate();
            tls.Controls.Add(_checkCert);
            _certStatus = new Label { ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = false, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(20), y - S(4)), Size = new Size(colW - S(40), S(34)) };
            tls.Controls.Add(_certStatus);
            y += S(34);
            _internet = Check(tls, "Auch aus dem Internet erreichbar", _r.Internet, ref y);
            y = Hint(tls, "Im Router den TCP-Port auf diesen PC weiterleiten. Nur mit HTTPS; Passwort mindestens 12 Zeichen. Nach 5 falschen Passwörtern ist eine Adresse 10 Minuten gesperrt, nach 15 in einer Stunde die Anmeldung von außen 1 Stunde.", y);
            tls.Height = y + S(8);
            _httpsFields.AddRange(new Control[] { _srcFile, _srcStore, _cert, browseCert, _key, browseKey, _certPassword, _certName, _checkCert, _certStatus });

            // ---------- Rechts: QR-Code und Hinweise ----------
            var phone = NewCard("Auf dem Handy öffnen", x3, margin, col2W);
            y = S(44);
            int qrSize = S(200);
            _qr = new QrBox { Location = new Point((col2W - qrSize) / 2, y), Size = new Size(qrSize, qrSize) };
            phone.Controls.Add(_qr);
            y += qrSize + S(10);
            _url = new Label { ForeColor = Ui.AccentText, BackColor = Ui.Card, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 9.5f), Location = new Point(S(20), y), Size = new Size(col2W - S(40), S(22)) };
            phone.Controls.Add(_url);
            y += S(26);
            _copy = new ModernButton("Link kopieren", MenuHelper.Copy, false, k) { HeightUnits = 32 };
            _copy.UpdateSize();
            _copy.Location = new Point((col2W - _copy.Width) / 2, y);
            _copy.Click += (s, e) => { try { Clipboard.SetText(CurrentUrl()); SetCopyText("Kopiert"); } catch { } };
            phone.Controls.Add(_copy);
            y += _copy.Height + S(14);
            y = Hint(phone, "1. QR-Code mit der Handy-Kamera scannen (Handy im WLAN, per VPN oder – wenn eingeschaltet – aus dem Internet).\r\n" +
                "2. Das Handy ist sofort angemeldet, ohne Passwort, und bleibt es. Der QR-Code gilt 10 Minuten und nur einmal und wird von selbst erneuert. " +
                "Ohne Kamera: Link öffnen und Passwort eingeben.\r\n" +
                "3. Im Browsermenü „Zum Startbildschirm hinzufügen“ bzw. „App installieren“: Wake on LAN öffnet sich dann wie eine App" +
                " (ohne Browserleiste nur mit HTTPS).", y);
            phone.Controls.Add(new Label { Text = "Windows-Firewall", ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = false, Font = new Font("Segoe UI Semibold", 9.5f), Location = new Point(S(20), y), Size = new Size(col2W - S(40), S(22)) });
            y += S(24);
            _firewall = new Label { ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = false, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(20), y), Size = new Size(col2W - S(40), S(66)) };
            phone.Controls.Add(_firewall);
            y += S(68);
            _firewallButton = new ModernButton("Freigeben …", MenuHelper.Shield, false, k) { HeightUnits = 32, Location = new Point(S(20), y) };
            _firewallButton.UpdateSize();
            _firewallButton.Click += (s, e) => AllowFirewall();
            phone.Controls.Add(_firewallButton);
            y += _firewallButton.Height + S(12);
            _serverStatus = new Label { ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = false, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(20), y), Size = new Size(col2W - S(40), S(34)) };
            if (demo || running) { _serverStatus.Text = "Läuft – Handys können sich verbinden."; _serverStatus.ForeColor = Ui.Green; }
            else if (!string.IsNullOrEmpty(runError)) { _serverStatus.Text = "Nicht gestartet: " + runError; _serverStatus.ForeColor = Ui.Red; }
            phone.Controls.Add(_serverStatus);
            y += S(36);
            phone.Height = y + S(4);

            // Spalten unten bündig
            int bottom = Math.Max(basic.Bottom, Math.Max(tls.Bottom, phone.Bottom));
            basic.Height += bottom - basic.Bottom;
            tls.Height += bottom - tls.Bottom;
            phone.Height += bottom - phone.Bottom;
            foreach (var c in new Control[] { basic, tls, phone }) Controls.Add(c);

            // ---------- Knöpfe ----------
            int W = x3 + col2W + margin;
            y = bottom + S(20);
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            var save = new ModernButton("Speichern", '\0', true, k) { Margin = Padding.Empty };
            save.Click += (s, e) => Save();
            cancel.Location = new Point(W - margin - cancel.Width, y);
            save.Location = new Point(cancel.Left - S(10) - save.Width, y);
            _error = new Label { ForeColor = Ui.Red, BackColor = Ui.Back, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(margin, y), Size = new Size(save.Left - margin - S(12), save.Height) };
            Controls.Add(_error);
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
            ClientSize = new Size(W, y + save.Height + margin);

            _enabled.CheckedChanged += (s, e) => UpdateState();
            _https.CheckedChanged += (s, e) =>
            {
                if (!_https.Checked) _internet.Checked = false; // aus dem Internet nur mit HTTPS
                UpdateState();
                if (_https.Checked && HasCertInput()) CheckCertificate();
            };
            _internet.CheckedChanged += (s, e) => UpdateState();
            _port.Box.TextChanged += (s, e) => { UpdateUrl(); CheckFirewall(); };
            _address.Box.TextChanged += (s, e) => UpdateUrl();
            _cert.Box.TextChanged += (s, e) => _certStatus.Text = "";
            _certName.Box.TextChanged += (s, e) => _certStatus.Text = "";
            _qrTimer = new System.Windows.Forms.Timer { Interval = 9 * 60 * 1000 }; // vor Ablauf des 10-Minuten-Codes erneuern
            _qrTimer.Tick += (s, e) => { _pairCode = null; UpdateUrl(); };
            _qrTimer.Start();
            FormClosed += (s, e) => { _qrTimer.Dispose(); if (_pairCode != null) Pairing.Clear(); };
            ShowSource();
            UpdateState();
            UpdatePinHint();
            if (demo)
            {
                _certStatus.Text = "wol.example.com · gültig bis 31.01.2027";
                _certStatus.ForeColor = Ui.Green;
                _firewall.Text = "Port " + _r.Port + " ist freigegeben.";
                _firewall.ForeColor = Ui.Green;
                _firewallButton.Enabled = false;
            }
            else
            {
                CheckFirewall();
                if (_r.Https && (_r.CertFromStore ? _r.CertName.Length > 0 : _r.CertFile.Length > 0)) Shown += (s, e) => CheckCertificate();
            }
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private static string DefaultAddress()
        {
            var ips = RemoteSettings.LocalAddresses();
            return ips.Count > 0 ? ips[0] : Environment.MachineName;
        }

        // ================================================================== Aufbau (wie Gerät bearbeiten)

        private CardPanel NewCard(string title, int x, int y, int w)
        {
            return new CardPanel(k) { Title = title, Location = new Point(x, y), Width = w, Height = S(100) };
        }

        private ModernTextBox Field(CardPanel card, string label, string value, ref int y, int width = 0, int x = 0, bool fullLabel = false)
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
            var box = new ModernTextBox(k) { Location = new Point(x, y), Width = width, Text = value };
            card.Controls.Add(box);
            y += box.Height + S(12);
            return box;
        }

        private ModernCheck Check(CardPanel card, string text, bool value, ref int y)
        {
            var c = new ModernCheck(text, k) { Location = new Point(S(18), y), Checked = value };
            c.Width = card.Width - S(38); // Text links, Schalter rechts
            card.Controls.Add(c);
            y += c.Height + S(8);
            return c;
        }

        private int Hint(CardPanel card, string text, int y)
        {
            int w = card.Width - S(40);
            var font = new Font("Segoe UI", 8.5f);
            int h = TextRenderer.MeasureText(text, font, new Size(w, 0), TextFormatFlags.WordBreak).Height;
            card.Controls.Add(new Label { Text = text, Font = font, ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = false, Location = new Point(S(20), y - S(4)), Size = new Size(w, h + S(2)) });
            return y - S(4) + h + S(12);
        }

        private ModernButton Browse(CardPanel card, ModernTextBox box, int w, int colW, Action click)
        {
            var b = new ModernButton("", MenuHelper.Folder, false, k) { HeightUnits = 32 };
            b.UpdateSize();
            b.Size = new Size(w, S(32));
            b.Location = new Point(colW - S(20) - w, box.Top);
            b.Click += (s, e) => click();
            card.Controls.Add(b);
            return b;
        }

        private void ChooseFile(ModernTextBox box, string title, string filter, bool isCert)
        {
            using (var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true })
            {
                try
                {
                    string dir = Path.GetDirectoryName(box.Text.Trim().Length > 0 ? box.Text.Trim() : _cert.Text.Trim());
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                }
                catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                box.Text = dlg.FileName;
                if (isCert)
                {
                    // passenden Schlüssel daneben vorschlagen, bei .pfx/.p12 keinen
                    if (CertLoader.IsPfx(dlg.FileName)) _key.Text = "";
                    else if (_key.Text.Trim().Length == 0 || !File.Exists(_key.Text.Trim())) _key.Text = CertLoader.GuessKeyFile(dlg.FileName);
                }
                CheckCertificate();
            }
        }

        private void UpdateState()
        {
            bool on = _enabled.Checked;
            foreach (var c in _remoteFields) c.Enabled = on;
            if (on && (!_r.HasPin || _logout || _newPin != null)) _logoutAll.Enabled = false;
            _https.Enabled = on;
            foreach (var c in _httpsFields) c.Enabled = on && _https.Checked;
            ShowSource();
            _internet.Enabled = on && _https.Checked;
            _qr.Enabled = on;
            _copy.Enabled = on;
            UpdatePinHint();
            UpdateUrl();
        }

        private void UpdatePinHint()
        {
            if (_logout && _newPin == null) return;
            int min = _internet.Checked ? 12 : 8;
            _pinHint.ForeColor = Ui.Muted;
            if (_newPin != null)
            {
                _pinHint.Text = "Das neue Passwort wird beim Speichern übernommen – angemeldete Handys müssen sich neu anmelden.";
                if (_newPin.Length < min) { _pinHint.Text = "Das neue Passwort ist zu kurz (mindestens " + min + " Zeichen)."; _pinHint.ForeColor = Ui.Red; }
            }
            else _pinHint.Text = _r.HasPin
                ? "Passwort ist festgelegt. Mindestens " + min + " Zeichen. Nur nötig, wenn das Handy keine Kamera hat – sonst meldet der QR-Code an."
                : "Mindestens " + min + " Zeichen, am besten Buchstaben, Ziffern und Sonderzeichen gemischt. Nur nötig ohne Kamera – sonst meldet der QR-Code an.";
        }

        /// <summary>Passwort zweimal eingeben (wie in Sync Pilot).</summary>
        private void ChoosePin()
        {
            using (var d = new PinForm(_internet.Checked ? 12 : 8, _r.HasPin || _newPin != null))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _newPin = d.Pin;
            }
            _setPin.Text = "Ändern …";
            _setPin.UpdateSize();
            _logoutAll.Left = _setPin.Right + S(8);
            _logoutAll.Enabled = false;
            UpdatePinHint();
            UpdateUrl();
        }

        // ================================================================== Herkunft des Zertifikats

        private void SetSource(bool store)
        {
            if (!_srcFile.Enabled) return;
            _srcFile.Checked = !store;
            _srcStore.Checked = store;
            _certStatus.Text = "";
            ShowSource();
            if (HasCertInput()) CheckCertificate();
        }

        private void ShowSource()
        {
            bool store = _srcStore.Checked;
            foreach (var c in _fileFields) c.Visible = !store;
            foreach (var c in _storeFields) c.Visible = store;
            _checkCert.Left = store ? S(20) : S(20) + S(180) + S(12);
        }

        private bool HasCertInput()
        {
            return _srcStore.Checked ? _certName.Text.Trim().Length > 0 : _cert.Text.Trim().Length > 0;
        }

        /// <summary>Zertifikat so laden, wie es im Dialog gerade eingestellt ist (zum Prüfen mit eigenem Schlüsselnamen).</summary>
        private System.Security.Cryptography.X509Certificates.X509Certificate2 LoadFromForm(string certFile, string keyFile, string password, bool store, string name)
        {
            var t = new RemoteSettings { CertFile = certFile, KeyFile = keyFile, CertMode = store ? "Speicher" : "Datei", CertName = name };
            t.CertPassword = password;
            return t.LoadCertificate(CertLoader.TestKeyName);
        }

        private int PortValue()
        {
            int p;
            return int.TryParse(_port.Text.Trim(), out p) && p >= 1 && p <= 65535 ? p : 0;
        }

        private string CurrentUrl()
        {
            var t = new RemoteSettings { Https = _https.Checked, Port = PortValue() > 0 ? PortValue() : RemoteSettings.DefaultPort };
            return t.Url(_address.Text.Trim().Length > 0 ? _address.Text.Trim() : DefaultAddress());
        }

        private void UpdateUrl()
        {
            string url = CurrentUrl();
            _url.Text = url;
            _qr.Text = _enabled.Checked ? QrText(url) : "";
            SetCopyText("Link kopieren");
        }

        /// <summary>
        /// Inhalt des QR-Codes: Link mit Einmal-Code (meldet ohne Passwort an), solange der Server mit diesen Einstellungen läuft;
        /// sonst nur der Link (dann Anmeldung mit Passwort).
        /// </summary>
        private string QrText(string url)
        {
            // nicht nach "Alle Handys abmelden" bzw. neuem Passwort: der Anmeldeschlüssel wird beim Speichern ersetzt
            bool same = _running && !_demo && _r.Enabled && PortValue() == _r.Port && _https.Checked == _r.Https && !_logout && _newPin == null;
            if (!same) return url;
            if (_pairCode == null) _pairCode = Pairing.NewCode();
            return url + "#pair=" + _pairCode;
        }

        private void SetCopyText(string text)
        {
            if (_copy.Text == text) return;
            _copy.Text = text;
            _copy.UpdateSize();
            if (_copy.Parent != null) _copy.Left = (_copy.Parent.Width - _copy.Width) / 2;
        }

        // ================================================================== Zertifikat und Firewall

        /// <summary>Zertifikat im Hintergrund laden (Zwischenzertifikate nachladen kann einige Sekunden dauern).</summary>
        private void CheckCertificate()
        {
            if (_demo) return;
            string certFile = _cert.Text.Trim(), keyFile = _key.Text.Trim();
            string password = _certPassword.Text.Length > 0 ? _certPassword.Text : _r.CertPassword;
            bool store = _srcStore.Checked;
            string name = _certName.Text.Trim();
            if (!HasCertInput()) { _certStatus.Text = ""; return; }
            int run = ++_certCheck;
            _certStatus.ForeColor = Ui.Muted;
            _certStatus.Text = "Prüfe Zertifikat …";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string text;
                bool ok = false, warn = false;
                List<string> names = null;
                try
                {
                    using (var cert = LoadFromForm(certFile, keyFile, password, store, name))
                    {
                        names = CertLoader.DnsNames(cert);
                        text = CertLoader.Describe(cert);
                        ok = true;
                        if (cert.NotAfter < DateTime.Now) { text = "Abgelaufen: " + text; ok = false; }
                        else if (cert.NotAfter < DateTime.Now.AddDays(14)) { text += " – läuft bald ab"; warn = true; }
                    }
                }
                catch (Exception ex) { text = ex.Message; }
                finally { CertLoader.DeleteTestKey(); }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (run != _certCheck) return;
                        _certStatus.Text = text;
                        _certStatus.ForeColor = !ok ? Ui.Red : warn ? Ui.Yellow : Ui.Green;
                        // Adresse aus dem Zertifikat übernehmen, wenn bisher nur eine IP/der Rechnername drinsteht
                        if (ok && names != null && names.Count > 0 && !MatchesCertificate(_address.Text.Trim(), names) && !names[0].StartsWith("*"))
                        {
                            string a = _address.Text.Trim();
                            System.Net.IPAddress ip;
                            if (a.Length == 0 || System.Net.IPAddress.TryParse(a, out ip) || a.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                                _address.Text = names[0];
                        }
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        /// <summary>Passt die Adresse zu einem Namen im Zertifikat (auch *.domain)?</summary>
        internal static bool MatchesCertificate(string address, List<string> names)
        {
            if (string.IsNullOrEmpty(address)) return false;
            foreach (string n in names)
            {
                if (string.Equals(n, address, StringComparison.OrdinalIgnoreCase)) return true;
                if (n.StartsWith("*.") && address.EndsWith(n.Substring(1), StringComparison.OrdinalIgnoreCase)
                    && address.Substring(0, address.Length - n.Length + 1).IndexOf('.') < 0) return true;
            }
            return false;
        }

        private void CheckFirewall()
        {
            if (_demo) return;
            int port = PortValue();
            if (port == 0) { _firewall.Text = ""; return; }
            _firewall.ForeColor = Ui.Muted;
            _firewall.Text = "Prüfe …";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string problem = Firewall.Problem(port);
                bool windowsOn = Firewall.WindowsFirewallOn();
                var others = Firewall.OtherFirewalls();
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (PortValue() != port) return;
                        string text = problem != null ? problem + "."
                            : windowsOn ? "Port " + port + " ist in der Windows-Firewall freigegeben."
                            : "Die Windows-Firewall ist aus – dort ist nichts nötig.";
                        if (others.Count > 0)
                            text += " Zusätzlich aktiv: " + string.Join(", ", others) + " – dort eingehend TCP-Port " + port + " ebenfalls erlauben.";
                        _firewall.Text = text;
                        _firewall.ForeColor = problem != null || others.Count > 0 ? Ui.Yellow : Ui.Green;
                        _firewallButton.Enabled = problem != null;
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        private void AllowFirewall()
        {
            int port = PortValue();
            if (port == 0) { Fail("Bitte zuerst einen gültigen Port eingeben.", _port); return; }
            string error = Firewall.Allow(port);
            if (error != null) _error.Text = error;
            CheckFirewall();
        }

        // ================================================================== Speichern

        private void Save()
        {
            _error.Text = "";
            bool on = _enabled.Checked;
            int port = PortValue();
            string pin = _newPin ?? "";
            string address = _address.Text.Trim();
            if (on)
            {
                if (port == 0) { Fail("Der Port muss zwischen 1 und 65535 liegen.", _port); return; }
                if (address.Length == 0) { Fail("Bitte die Adresse für das Handy eingeben.", _address); return; }
                if (address.Contains("/") || address.Contains(" ")) { Fail("Bitte nur den Namen bzw. die IP eintragen, ohne http:// und ohne Pfad.", _address); return; }
                int min = _internet.Checked ? 12 : 8;
                if (pin.Length > 0 && pin.Trim().Length < min) { Fail("Das Passwort braucht mindestens " + min + " Zeichen – bitte unter „Ändern …“ ein längeres festlegen.", null); return; }
                if (pin.Length == 0 && !_r.HasPin) { Fail("Bitte unter „Festlegen …“ ein Passwort festlegen (mindestens " + min + " Zeichen).", null); return; }
                if (pin.Length == 0 && _internet.Checked && !_r.Internet)
                {
                    Fail("Für den Zugriff aus dem Internet bitte unter „Ändern …“ ein neues Passwort mit mindestens 12 Zeichen festlegen.", null);
                    return;
                }
                if (_https.Checked)
                {
                    string certFile = _cert.Text.Trim();
                    bool store = _srcStore.Checked;
                    if (store && _certName.Text.Trim().Length == 0) { Fail("Bitte die Domain des Zertifikats eintragen.", _certName); return; }
                    ModernTextBox certField = store ? _certName : _cert;
                    if (!store && certFile.Length == 0) { Fail("Bitte das Zertifikat auswählen.", certField); return; }
                    string password = _certPassword.Text.Length > 0 ? _certPassword.Text : _r.CertPassword;
                    List<string> names;
                    Cursor = Cursors.WaitCursor;
                    try
                    {
                        using (var cert = LoadFromForm(certFile, _key.Text.Trim(), password, store, _certName.Text.Trim()))
                        {
                            names = CertLoader.DnsNames(cert);
                            if (cert.NotAfter < DateTime.Now) { Fail("Das Zertifikat ist abgelaufen (" + cert.NotAfter.ToString("dd.MM.yyyy") + ").", certField); return; }
                        }
                    }
                    catch (Exception ex) { Fail(ex.Message, certField); return; }
                    finally { Cursor = Cursors.Default; CertLoader.DeleteTestKey(); }
                    if (!MatchesCertificate(address, names) &&
                        MessageBox.Show(this, "Die Adresse „" + address + "“ steht nicht im Zertifikat (gilt für " + string.Join(", ", names) + ").\r\n\r\n" +
                            "Das Handy zeigt dann eine Sicherheitswarnung. Trotzdem speichern?", Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        _address.Box.Focus();
                        return;
                    }
                }
            }

            // übernehmen
            var r = _store.Remote;
            r.Enabled = on;
            if (port > 0) r.Port = port;
            r.Address = address;
            r.Background = _background.Checked;
            r.Https = _https.Checked;
            r.Internet = _internet.Checked && _https.Checked;
            r.CertFile = _cert.Text.Trim();
            r.KeyFile = CertLoader.IsPfx(r.CertFile) ? "" : _key.Text.Trim();
            r.CertMode = _srcStore.Checked ? "Speicher" : "Datei";
            r.CertName = _certName.Text.Trim();
            if (_certPassword.Text.Length > 0) r.CertPassword = _certPassword.Text;
            if (pin.Length > 0) r.SetPin(pin.Trim());
            else if (_logout || (r.HasPin && r.Token() == null)) r.NewSecret();
            if (!_demo)
            {
                try { Autostart.Set(on && _autostart.Checked); }
                catch (Exception ex) { MessageBox.Show(this, "Autostart konnte nicht eingerichtet werden: " + ex.Message, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                if (on)
                {
                    string problem = Firewall.Problem(r.Port);
                    if (problem != null && MessageBox.Show(this, problem + ".\r\n\r\nDamit das Handy diesen PC erreicht, muss die Windows-Firewall Port " + r.Port +
                        " freigeben. Jetzt freigeben? (Windows fragt nach Administratorrechten.)", Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        string error = Firewall.Allow(r.Port);
                        if (error != null) MessageBox.Show(this, error, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Fail(string text, ModernTextBox field)
        {
            _error.Text = text;
            if (field != null) { field.Box.Focus(); field.Box.SelectAll(); }
        }
    }

    /// <summary>Passwort für die Handy-Steuerung festlegen: zweimal eingeben (wie in Sync Pilot).</summary>
    internal sealed class PinForm : FixedForm
    {
        private readonly float k;
        private readonly ModernTextBox _p1, _p2;
        private readonly Label _error;
        private readonly int _min;

        public string Pin { get { return _p1.Text.Trim(); } }

        public PinForm(int minLength, bool change)
        {
            _min = minLength;
            Theme.Refresh();
            Text = change ? "Passwort ändern" : "Passwort festlegen";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int pad = S(24), w = S(360), y = S(20);
            var info = new Label
            {
                Text = "Mit diesem Passwort meldet sich ein Handy ohne Kamera an. Mindestens " + minLength + " Zeichen, am besten Buchstaben, " +
                    "Ziffern und Sonderzeichen gemischt." + (change ? " Angemeldete Handys müssen sich danach neu anmelden." : ""),
                ForeColor = Ui.Muted, BackColor = Ui.Back, AutoSize = false, Font = new Font("Segoe UI", 8.5f),
                Location = new Point(pad, y), Size = new Size(w, S(52))
            };
            Controls.Add(info);
            y += S(60);
            Controls.Add(new Label { Text = "Passwort", ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(pad, y), Size = new Size(w, S(22)) });
            y += S(24);
            _p1 = new ModernTextBox(k) { Location = new Point(pad, y), Width = w, Password = true };
            _p1.Box.AccessibleName = "Passwort";
            Controls.Add(_p1);
            y += _p1.Height + S(12);
            Controls.Add(new Label { Text = "Passwort wiederholen", ForeColor = Ui.Text, BackColor = Ui.Back, AutoSize = false, Location = new Point(pad, y), Size = new Size(w, S(22)) });
            y += S(24);
            _p2 = new ModernTextBox(k) { Location = new Point(pad, y), Width = w, Password = true };
            _p2.Box.AccessibleName = "Passwort wiederholen";
            Controls.Add(_p2);
            y += _p2.Height + S(8);
            _error = new Label { ForeColor = Ui.Red, BackColor = Ui.Back, AutoSize = false, Location = new Point(pad, y), Size = new Size(w, S(22)) };
            Controls.Add(_error);
            y += S(30);
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            var ok = new ModernButton("OK", '\0', true, k) { Margin = Padding.Empty };
            ok.Click += (s, e) => Accept();
            cancel.Location = new Point(pad + w - cancel.Width, y);
            ok.Location = new Point(cancel.Left - S(10) - ok.Width, y);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(w + 2 * pad, y + ok.Height + pad);
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private void Accept()
        {
            if (_p1.Text.Trim().Length < _min) { _error.Text = "Mindestens " + _min + " Zeichen."; _p1.Box.Focus(); return; }
            if (_p1.Text != _p2.Text) { _error.Text = "Die beiden Eingaben sind nicht gleich."; _p2.Box.Focus(); return; }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>QR-Code als Bild (dunkle Module auf weißem Grund, auch im Dunkelmodus).</summary>
    internal sealed class QrBox : Control
    {
        private QrCode _code;

        public QrBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            try { _code = string.IsNullOrEmpty(Text) ? null : QrCode.Encode(Text); }
            catch { _code = null; }
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            if (_code == null || !Enabled)
            {
                TextRenderer.DrawText(g, Enabled ? "" : "Ausgeschaltet", Font, ClientRectangle, Color.FromArgb(157, 157, 157),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int modules = _code.Size + 8; // 4 Module Ruhezone ringsum
            int cell = Math.Max(1, Math.Min(Width, Height) / modules);
            int total = cell * modules;
            int ox = (Width - total) / 2 + 4 * cell, oy = (Height - total) / 2 + 4 * cell;
            using (var b = new SolidBrush(Color.FromArgb(20, 20, 20)))
                for (int y = 0; y < _code.Size; y++)
                    for (int x = 0; x < _code.Size; x++)
                        if (_code[x, y]) g.FillRectangle(b, ox + x * cell, oy + y * cell, cell, cell);
        }
    }

    /// <summary>Windows-Firewall: Freigabe "Wake on LAN (Handy)" für den Port prüfen und (mit Administratorrechten) anlegen.</summary>
    internal static class Firewall
    {
        public const string RuleName = "Wake on LAN (Handy)";

        /// <summary>null = alles in Ordnung (oder nicht prüfbar), sonst eine kurze Beschreibung des Problems.</summary>
        public static string Problem(int port)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return null;
                dynamic policy = Activator.CreateInstance(t);
                int current = policy.CurrentProfileTypes;
                bool active = false;
                foreach (int profile in new[] { 1, 2, 4 })
                {
                    if ((current & profile) == 0) continue;
                    object on = t.InvokeMember("FirewallEnabled", System.Reflection.BindingFlags.GetProperty, null, (object)policy, new object[] { profile });
                    if (on is bool && (bool)on) active = true;
                }
                if (!active) return null; // Windows-Firewall aus (z. B. anderes Firewall-Programm)
                dynamic rule;
                try { rule = policy.Rules.Item(RuleName); }
                catch { return "Die Windows-Firewall-Freigabe fehlt"; }
                bool enabled = rule.Enabled;
                int action = rule.Action, direction = rule.Direction, profiles = rule.Profiles;
                string ports = rule.LocalPorts;
                string app = rule.ApplicationName;
                if (!enabled || action != 1 || direction != 1) return "Die Firewall-Freigabe ist ausgeschaltet";
                if (ports != port.ToString()) return "Die Firewall-Freigabe gilt für einen anderen Port";
                if (!string.IsNullOrEmpty(app) && !string.Equals(app, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    return "Die Firewall-Freigabe gilt für einen anderen Programmordner";
                // nur warnen, wenn ausschließlich "Öffentlich" aktiv ist – ein öffentlicher Zusatzadapter
                // (z. B. Hyper-V, VPN, VirtualBox) ändert nichts am privaten Heimnetz
                if (current == 4 && (profiles & 4) == 0)
                    return "Das Netzwerk ist in Windows als „Öffentlich“ eingestuft – bitte auf „Privat“ umstellen";
                return null;
            }
            catch
            {
                return "Die Windows-Firewall konnte nicht geprüft werden";
            }
        }

        /// <summary>Ist die Windows-Firewall für ein aktives Netzwerkprofil eingeschaltet?</summary>
        public static bool WindowsFirewallOn()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return false;
                object policy = Activator.CreateInstance(t);
                int current = (int)t.InvokeMember("CurrentProfileTypes", System.Reflection.BindingFlags.GetProperty, null, policy, null);
                foreach (int profile in new[] { 1, 2, 4 })
                {
                    if ((current & profile) == 0) continue;
                    object on = t.InvokeMember("FirewallEnabled", System.Reflection.BindingFlags.GetProperty, null, policy, new object[] { profile });
                    if (on is bool && (bool)on) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Weitere Firewall-Programme laut Windows-Sicherheitscenter (z. B. ESET), ohne die Windows-Firewall.</summary>
        public static List<string> OtherFirewalls()
        {
            var names = new List<string>();
            try
            {
                Type t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (t == null) return names;
                dynamic locator = Activator.CreateInstance(t);
                dynamic service = locator.ConnectServer(".", "root\\SecurityCenter2");
                foreach (dynamic product in service.ExecQuery("SELECT displayName FROM FirewallProduct"))
                {
                    string name = product.displayName;
                    if (!string.IsNullOrEmpty(name) && name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) < 0 && !names.Contains(name)) names.Add(name);
                }
            }
            catch { }
            return names;
        }

        /// <summary>Freigabe (neu) anlegen – mit Administratorrechten (Windows fragt nach). Rückgabe: Fehlertext oder null.</summary>
        public static string Allow(int port)
        {
            string exe = Application.ExecutablePath;
            if (exe.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%' }) >= 0)
                return "Der Programmpfad enthält Sonderzeichen – bitte die Freigabe von Hand anlegen (TCP-Port " + port + ").";
            string inner = "netsh advfirewall firewall delete rule name=\"" + RuleName + "\" >nul 2>&1 & " +
                "netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow protocol=TCP localport=" + port +
                " profile=private,domain program=\"" + exe + "\" enable=yes";
            var psi = new ProcessStartInfo("cmd.exe", "/s /c \"" + inner + "\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "Die Freigabe konnte nicht angelegt werden.";
                    p.WaitForExit(30000);
                    if (!p.HasExited || p.ExitCode != 0) return "Die Freigabe konnte nicht angelegt werden (netsh).";
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return "Freigabe abgebrochen (keine Administratorrechte erteilt).";
                return "Die Freigabe konnte nicht angelegt werden: " + ex.Message;
            }
            History.Add("Handy", "Firewall-Freigabe für TCP-Port " + port + " angelegt");
            return null;
        }
    }

    /// <summary>Mit Windows starten: Eintrag "Wake on LAN" unter HKCU\…\Run mit /hintergrund.</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Wake on LAN";

        private static string Command { get { return "\"" + Application.ExecutablePath + "\" /hintergrund"; } }

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        string v = key == null ? null : key.GetValue(ValueName) as string;
                        return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(ValueName, Command);
                else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>Nach dem Verschieben des Programms: vorhandenen Autostart-Eintrag auf den neuen Pfad umstellen.</summary>
        public static bool Repair()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string v = key == null ? null : key.GetValue(ValueName) as string;
                    if (v == null || v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    int a = v.IndexOf('"'), b = a >= 0 ? v.IndexOf('"', a + 1) : -1;
                    string old = b > a ? v.Substring(a + 1, b - a - 1) : v;
                    if (File.Exists(old)) return false; // gehört zu einer anderen Kopie des Programms
                    key.SetValue(ValueName, Command);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
