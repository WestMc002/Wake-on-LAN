// Wake on LAN – Meldungen und Rückfragen im Programmdesign
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>
    /// Ersatz für System.Windows.Forms.MessageBox mit denselben Show-Überladungen (globale Vorgabe "Dialogfenster"):
    /// Im Namensraum WakeOnLan hat diese Klasse Vorrang, der vorhandene Code nutzt so automatisch das eigene Fenster.
    /// </summary>
    internal static class MessageBox
    {
        public static DialogResult Show(string text) { return Show(null, text, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption) { return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) { return Show(null, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def) { return Show(null, text, caption, buttons, icon, def); }
        public static DialogResult Show(IWin32Window owner, string text) { return Show(owner, text, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption) { return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) { return Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            try
            {
                using (var d = new MsgDialog(text, buttons, icon, def, null))
                    return d.ShowDialog(OwnerFor(owner));
            }
            catch (Exception)
            {
                // Rückfall nur, wenn das eigene Fenster nicht aufgebaut werden kann (z. B. sehr früh beim Start)
                return System.Windows.Forms.MessageBox.Show(owner, text, Ui.AppName, buttons, icon, def);
            }
        }

        /// <summary>Ohne Owner das aktive Programmfenster (nur im selben Thread), sonst bildschirmmittig.</summary>
        private static IWin32Window OwnerFor(IWin32Window owner)
        {
            if (owner != null) return owner;
            var active = Form.ActiveForm;
            if (active != null && !active.InvokeRequired && active.Visible) return active;
            return null;
        }
    }

    /// <summary>Das eigentliche Meldungsfenster: Symbol links, Text, Knöpfe rechts unten, optional ein Kästchen.</summary>
    internal sealed class MsgDialog : FixedForm
    {
        private readonly float k;
        private readonly MessageBoxIcon _icon;
        private readonly MessageBoxButtons _buttons;
        private readonly ModernCheck _check;
        private bool _pressed;

        /// <summary>Zustand des Kästchens (z. B. "Nicht mehr fragen").</summary>
        public bool Checked { get { return _check != null && _check.Checked; } }

        public MsgDialog(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def, string checkText)
        {
            _icon = icon;
            _buttons = buttons;
            Theme.Refresh();
            Text = Ui.AppName; // Fenstertitel = Programmname
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            int pad = S(24), iconSize = S(32), gap = S(16);
            text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");

            // Breite nach Textlänge: mindestens 420, höchstens 600
            int textX = pad + (HasIcon ? iconSize + gap : 0);
            int oneLine = TextRenderer.MeasureText(text, Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPrefix).Width;
            int W = Math.Max(S(420), Math.Min(S(600), textX + oneLine + pad + S(4)));
            int textW = W - textX - pad;
            int textH = TextRenderer.MeasureText(text, Font, new Size(textW, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;

            var label = new Label
            {
                Text = text,
                ForeColor = Ui.Text,
                BackColor = Ui.Back,
                AutoSize = false,
                UseMnemonic = false,
                Location = new Point(textX, pad + (HasIcon && textH < iconSize ? (iconSize - textH) / 2 : 0)),
                Size = new Size(textW, textH + S(2))
            };
            Controls.Add(label);
            int y = Math.Max(label.Bottom, HasIcon ? pad + iconSize : 0) + S(16);

            if (!string.IsNullOrEmpty(checkText))
            {
                _check = new ModernCheck(checkText, k) { Location = new Point(textX - S(2), y) };
                _check.Width = textW;
                Controls.Add(_check);
                y += _check.Height + S(12);
            }

            // Knöpfe wie bei Windows: Ja | Nein | Abbrechen bzw. OK | Abbrechen, rechtsbündig
            var spec = Spec(buttons);
            int index = def == MessageBoxDefaultButton.Button2 ? 1 : def == MessageBoxDefaultButton.Button3 ? 2 : 0;
            if (index >= spec.Count) index = 0;
            var list = new List<ModernButton>();
            for (int i = 0; i < spec.Count; i++)
            {
                var b = new ModernButton(spec[i].Key, '\0', i == index, k) { DialogResult = spec[i].Value, Margin = Padding.Empty };
                b.UpdateSize();
                b.Click += (s, e) => _pressed = true;
                if (b.Width < S(110)) b.Width = S(110);
                list.Add(b);
            }
            int x = W - pad;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                x -= list[i].Width;
                list[i].Location = new Point(x, y);
                x -= S(8);
                Controls.Add(list[i]);
            }
            AcceptButton = list[index];
            CancelButton = CancelFor(list);
            ClientSize = new Size(W, y + list[0].Height + pad);
            Shown += (s, e) => list[index].Focus();
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private bool HasIcon { get { return _icon != MessageBoxIcon.None; } }

        private static List<KeyValuePair<string, DialogResult>> Spec(MessageBoxButtons buttons)
        {
            var l = new List<KeyValuePair<string, DialogResult>>();
            Action<string, DialogResult> add = (t, r) => l.Add(new KeyValuePair<string, DialogResult>(t, r));
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel: add("OK", DialogResult.OK); add("Abbrechen", DialogResult.Cancel); break;
                case MessageBoxButtons.YesNo: add("Ja", DialogResult.Yes); add("Nein", DialogResult.No); break;
                case MessageBoxButtons.YesNoCancel: add("Ja", DialogResult.Yes); add("Nein", DialogResult.No); add("Abbrechen", DialogResult.Cancel); break;
                case MessageBoxButtons.RetryCancel: add("Wiederholen", DialogResult.Retry); add("Abbrechen", DialogResult.Cancel); break;
                case MessageBoxButtons.AbortRetryIgnore: add("Abbrechen", DialogResult.Abort); add("Wiederholen", DialogResult.Retry); add("Ignorieren", DialogResult.Ignore); break;
                default: add("OK", DialogResult.OK); break;
            }
            return l;
        }

        /// <summary>Esc bzw. X: bei Ja/Nein wie "Nein", bei OK wie "OK", sonst wie "Abbrechen".</summary>
        private IButtonControl CancelFor(List<ModernButton> list)
        {
            foreach (var b in list) if (b.DialogResult == DialogResult.Cancel) return b;
            if (_buttons == MessageBoxButtons.YesNo) return list[1];
            if (_buttons == MessageBoxButtons.AbortRetryIgnore) return list[0];
            return list[0];
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // X ohne Knopf: Ergebnis wie Esc
            if (!_pressed)
            {
                var c = CancelButton as Button;
                if (c != null) DialogResult = c.DialogResult;
            }
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!HasIcon) return;
            Color back;
            string sign;
            switch (_icon)
            {
                case MessageBoxIcon.Question: back = Ui.Primary; sign = "?"; break;          // auch MessageBoxIcon.Question
                case MessageBoxIcon.Warning: back = Theme.Dark ? Color.FromArgb(252, 160, 40) : Color.FromArgb(230, 126, 0); sign = "!"; break; // = Exclamation
                case MessageBoxIcon.Error: back = Theme.Dark ? Color.FromArgb(255, 99, 82) : Color.FromArgb(196, 43, 28); sign = "×"; break;     // = Hand, Stop
                default: back = Ui.Primary; sign = "i"; break;                                 // Information = Asterisk
            }
            int pad = S(24), size = S(32);
            var r = new Rectangle(pad, pad, size, size);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(back)) g.FillEllipse(brush, r);
            Color fore = back == Ui.Primary ? Ui.PrimaryText : Color.White;
            using (var font = new Font("Segoe UI Semibold", 13f))
                TextRenderer.DrawText(g, sign, font, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
