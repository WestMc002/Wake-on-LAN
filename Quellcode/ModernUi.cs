// Wake on LAN – Bausteine der Oberfläche im Windows-11-Stil (Kopf, Karten, Knöpfe, Eingabefelder, Footer, Info-Fenster)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>Gemeinsame Farben und Hilfen für das moderne Fensterdesign (Vorlage: Monitor Dimmer).</summary>
    internal static class Ui
    {
        public const string AppName = "Wake on LAN";

        public static Color Back { get { return Theme.Map(Color.FromArgb(243, 243, 243)); } }
        public static Color Card { get { return Theme.Map(Color.White); } }
        public static Color CardHover { get { return Theme.Map(Color.FromArgb(249, 249, 249)); } }
        public static Color Border { get { return Theme.Map(Color.FromArgb(229, 229, 229)); } }
        public static Color Text { get { return Theme.Map(Color.FromArgb(27, 27, 27)); } }
        public static Color Muted { get { return Theme.Map(Color.FromArgb(97, 97, 97)); } }
        public static Color Green { get { return Theme.Map(Color.FromArgb(15, 123, 15)); } }
        public static Color Red { get { return Theme.Map(Color.FromArgb(196, 43, 28)); } }
        public static Color Yellow { get { return Theme.Map(Color.FromArgb(157, 93, 0)); } }
        // Akzentfarbe: Windows-Akzentfarbe (Einstellungen > Personalisierung > Farben) oder festes Blau
        public static Color Primary { get { var p = Accent.Palette; return p != null ? (Theme.Dark ? p[1] : p[4]) : Color.FromArgb(37, 99, 235); } }
        public static Color PrimaryHover { get { var p = Accent.Palette; return p != null ? (Theme.Dark ? p[2] : p[5]) : Color.FromArgb(29, 78, 216); } }
        public static Color PrimaryDown { get { var p = Accent.Palette; return p != null ? (Theme.Dark ? p[3] : p[6]) : Color.FromArgb(30, 64, 175); } }
        /// <summary>Schrift auf dem Akzent-Knopf: weiß, bei hellen Akzentfarben dunkel.</summary>
        public static Color PrimaryText { get { return Accent.Luminance(Primary) > 0.5 ? Color.FromArgb(20, 20, 20) : Color.White; } }
        /// <summary>Akzentfarbe für Schrift und Symbole (Versionsnummer, Link).</summary>
        public static Color AccentText
        {
            get
            {
                var p = Accent.Palette;
                if (p == null) return Theme.Map(Color.FromArgb(29, 78, 216));
                return Theme.Dark ? p[1] : p[5];
            }
        }
        public static Color Hover { get { return Theme.Map(Color.FromArgb(246, 246, 246)); } }
        public static Color Down { get { return Theme.Map(Color.FromArgb(236, 236, 236)); } }
        public static Color Disabled { get { return Theme.Map(Color.FromArgb(157, 157, 157)); } }
        public static Color InfoBack
        {
            get
            {
                var p = Accent.Palette;
                return p != null ? Accent.Mix(p[3], Back, Theme.Dark ? 0.20 : 0.10) : Theme.Dark ? Color.FromArgb(34, 46, 68) : Color.FromArgb(231, 240, 252);
            }
        }
        public static Color InfoBorder
        {
            get
            {
                var p = Accent.Palette;
                return p != null ? Accent.Mix(p[3], Back, Theme.Dark ? 0.42 : 0.30) : Theme.Dark ? Color.FromArgb(58, 78, 118) : Color.FromArgb(190, 210, 240);
            }
        }

        public static string Version
        {
            get { return AppInfo.Version; }
        }

        /// <summary>Programm-Icon in der gewünschten Größe (in die EXE eingebettet, sonst Icon der EXE).</summary>
        public static Image AppIcon(int size)
        {
            try
            {
                using (var rs = typeof(Ui).Assembly.GetManifestResourceStream("WakeOnLan.ico"))
                    if (rs != null)
                        using (var ico = new Icon(rs, size, size)) return ico.ToBitmap();
                using (var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    return new Bitmap(ico.ToBitmap(), size, size);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Programm-Icon für die Titelleiste.</summary>
        public static Icon WindowIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }

        /// <summary>Fenster mittig auf den Bildschirm mit dem Mauszeiger setzen.</summary>
        public static void CenterOnScreen(Form f)
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            f.Location = new Point(wa.X + Math.Max(0, (wa.Width - f.Width) / 2), wa.Y + Math.Max(0, (wa.Height - f.Height) / 2));
        }
    }

    /// <summary>
    /// Fenster mit normalem Windows-Rahmen und fester Größe (globale Vorgabe "Fensterrahmen"):
    /// Sizable-Rahmen, damit das Icon überall gleich sitzt, Größenänderung am Rand abgeschaltet.
    /// </summary>
    internal class FixedForm : Form
    {
        private const int WM_NCHITTEST = 0x0084, HTCLIENT = 1, HTLEFT = 10, HTBOTTOMRIGHT = 17;

        public FixedForm()
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            MinimizeBox = false;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size;
            MaximumSize = Size;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST)
            {
                int hit = m.Result.ToInt32();
                if (hit >= HTLEFT && hit <= HTBOTTOMRIGHT) m.Result = (IntPtr)HTCLIENT;
            }
        }
    }

    /// <summary>Kopfbereich: Programm-Icon, Titel, Versions-Plakette, Unterzeile; rechts optional ein Knopf.</summary>
    internal sealed class HeaderPanel : Panel
    {
        private readonly float k;
        private readonly Image _icon;
        public string Title;
        public string Subtitle;
        private RectangleF _pill;
        private bool _pillHover;

        public HeaderPanel(string title, string subtitle, float scale)
        {
            Title = title;
            Subtitle = subtitle;
            k = scale;
            _icon = Ui.AppIcon(64);
            Dock = DockStyle.Top;
            Height = (int)(108 * k);
            BackColor = Ui.Back;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Knopf rechts oben (z. B. "Gerät hinzufügen").</summary>
        public void SetRight(Control c)
        {
            c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(c);
            Layout += (s, e) => c.Location = new Point(Width - c.Width - (int)(28 * k), (int)(32 * k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float x = 28 * k, y = 24 * k, icon = 52 * k;
            if (_icon != null) g.DrawImage(_icon, x, y + 4 * k, icon, icon);
            float tx = x + icon + 18 * k;
            using (var fTitle = new Font("Segoe UI Semibold", 17f))
            using (var bText = new SolidBrush(Ui.Text))
            {
                g.DrawString(Title, fTitle, bText, tx - 3 * k, y - 2 * k);
                float tw = g.MeasureString(Title, fTitle).Width;
                // Versions-Plakette (Klick öffnet das Info-Fenster)
                using (var fPill = new Font("Segoe UI", 9f))
                {
                    string v = "Version " + Ui.Version;
                    var sz = g.MeasureString(v, fPill);
                    var r = new RectangleF(tx + tw + 6 * k, y + 6 * k, sz.Width + 16 * k, sz.Height + 6 * k);
                    _pill = r;
                    using (var p = Ui.Rounded(r, r.Height / 2))
                    {
                        using (var b = new SolidBrush(_pillHover ? Ui.InfoBorder : Ui.InfoBack)) g.FillPath(b, p);
                        using (var pen = new Pen(Ui.InfoBorder)) g.DrawPath(pen, p);
                    }
                    using (var b = new SolidBrush(Ui.AccentText))
                        g.DrawString(v, fPill, b, r.X + 8 * k, r.Y + 3 * k);
                }
            }
            if (!string.IsNullOrEmpty(Subtitle))
                using (var f = new Font("Segoe UI", 10f))
                using (var b = new SolidBrush(Ui.Muted))
                    g.DrawString(Subtitle, f, b, tx - 1 * k, y + 38 * k);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = _pill.Contains(e.Location);
            if (h != _pillHover) { _pillHover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_pillHover) { _pillHover = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _pill.Contains(e.Location)) AboutDialog.ShowFor(FindForm());
            base.OnMouseClick(e);
        }
    }

    /// <summary>Weiße Karte mit abgerundeten Ecken, feinem Rahmen und optionaler Überschrift.</summary>
    internal sealed class CardPanel : Panel
    {
        private readonly float k;
        public string Title;

        public CardPanel(float scale)
        {
            k = scale;
            BackColor = Ui.Card;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding((int)(20 * k), (int)(16 * k), (int)(20 * k), (int)(16 * k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Ui.Rounded(r, 8 * k))
            {
                using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, p);
            }
            if (!string.IsNullOrEmpty(Title))
                using (var f = new Font("Segoe UI Semibold", 10.5f))
                    TextRenderer.DrawText(g, Title, f, new Point((int)(20 * k), (int)(14 * k)), Ui.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Runder Knopf mit Symbol. Primary = Akzentfarbe ausgefüllt, sonst weiß mit Rahmen.</summary>
    internal sealed class ModernButton : Button
    {
        private readonly float k;
        private bool _hover, _down;
        public bool Primary;
        public char Glyph;
        /// <summary>Feste Höhe in Einheiten (Standard 40, in Listenzeilen kleiner).</summary>
        public int HeightUnits = 40;
        /// <summary>Als Auswahlfeld zeichnen: Text links, Pfeil nach unten rechts (wie eine Auswahlliste in Windows 11).</summary>
        public bool DropDown;

        public ModernButton(string text, char glyph, bool primary, float scale)
        {
            k = scale;
            Text = text;
            Glyph = glyph;
            Primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI Semibold", 9.5f);
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, (int)(10 * k), 0);
            AutoSize = false;
            UpdateSize();
        }

        public void UpdateSize()
        {
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font).Width;
            int icon = Glyph == '\0' ? 0 : (int)((string.IsNullOrEmpty(Text) ? 16 : 26) * k);
            int pad = (int)((string.IsNullOrEmpty(Text) ? 24 : 36) * k);
            Size = new Size(tw + icon + pad, (int)(HeightUnits * k));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (k > 0) UpdateSize();
        }

        protected override bool ShowFocusCues { get { return false; } }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, text, border;
            if (Primary && Enabled)
            {
                fill = _down ? Ui.PrimaryDown : _hover ? Ui.PrimaryHover : Ui.Primary;
                text = Ui.PrimaryText;
                border = fill;
            }
            else
            {
                fill = !Enabled ? Ui.Card : _down ? Ui.Down : _hover ? Ui.Hover : Ui.Card;
                text = Enabled ? Ui.Text : Ui.Disabled;
                border = Ui.Border;
            }
            using (var p = Ui.Rounded(r, 4 * k))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(border)) g.DrawPath(pen, p);
            }

            if (DropDown)
            {
                int pad = (int)(12 * k), gs2 = (int)(12 * k);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(pad, 0, Width - 2 * pad - gs2 - (int)(8 * k), Height), text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                var chevron = MenuHelper.Glyph(MenuHelper.ChevronDown, Enabled ? Ui.Muted : Ui.Disabled, gs2);
                if (chevron != null)
                {
                    g.DrawImage(chevron, Width - pad - gs2, (Height - gs2) / 2, gs2, gs2);
                    chevron.Dispose();
                }
                return;
            }
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font).Width;
            int icon = Glyph == '\0' ? 0 : (int)((string.IsNullOrEmpty(Text) ? 16 : 26) * k);
            int x = (Width - tw - icon) / 2;
            if (Glyph != '\0')
            {
                int gs = (int)(16 * k);
                var img = MenuHelper.Glyph(Glyph, text, gs);
                if (img != null)
                {
                    g.DrawImage(img, x, (Height - gs) / 2, gs, gs);
                    img.Dispose();
                }
                x += icon;
            }
            if (tw > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, tw + 2, Height), text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Eingabefeld im Windows-11-Stil: abgerundetes Feld mit feinem Rahmen, Akzent-Linie unten bei Fokus.
    /// Innen ein randloses TextBox-Steuerelement.
    /// </summary>
    internal sealed class ModernTextBox : Panel
    {
        private readonly float k;
        public readonly TextBox Box;
        private bool _hover;

        public ModernTextBox(float scale, bool multiline = false, int lines = 1)
        {
            k = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Card,
                ForeColor = Ui.Text,
                Font = new Font("Segoe UI", 9.5f),
                Multiline = multiline,
                AcceptsReturn = multiline,
                WordWrap = false,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
            };
            int lineH = TextRenderer.MeasureText("Ag", Box.Font).Height;
            Height = multiline ? lineH * lines + (int)(14 * k) : (int)(32 * k);
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Box.MouseEnter += (s, e) => SetHover(true);
            Box.MouseLeave += (s, e) => SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
            Cursor = Cursors.IBeam;
        }

        public override string Text
        {
            // Box ist während des Basis-Konstruktors noch null
            get { return Box != null ? Box.Text : base.Text; }
            set { if (Box != null) Box.Text = value ?? ""; else base.Text = value; }
        }

        public bool Password
        {
            get { return Box.UseSystemPasswordChar; }
            set { Box.UseSystemPasswordChar = value; }
        }

        private void SetHover(bool h)
        {
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { SetHover(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position))); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Box.BackColor = Enabled ? Ui.Card : Ui.Back;
            Box.ForeColor = Enabled ? Ui.Text : Ui.Disabled;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int padX = (int)(10 * k);
            if (Box.Multiline)
            {
                int padY = (int)(6 * k);
                Box.SetBounds(padX, padY, Math.Max(10, Width - padX - (int)(4 * k)), Math.Max(10, Height - 2 * padY));
            }
            else
            {
                int h = Box.PreferredHeight;
                Box.SetBounds(padX, (Height - h) / 2 + (int)(1 * k), Math.Max(10, Width - 2 * padX), h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = Enabled ? (_hover && !Box.Focused ? Ui.CardHover : Ui.Card) : Ui.Back;
            using (var p = Ui.Rounded(r, 4 * k))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, p);
            }
            if (Box.BackColor != fill) Box.BackColor = fill;
            if (Box.Focused && Enabled)
            {
                // Akzent-Linie unten, innerhalb der Rundung
                var clip = g.Clip;
                using (var p = Ui.Rounded(r, 4 * k))
                {
                    g.SetClip(p);
                    float lh = 2 * k;
                    using (var b = new SolidBrush(Ui.Primary)) g.FillRectangle(b, 0, Height - lh - 0.5f, Width, lh + 1);
                }
                g.Clip = clip;
            }
        }
    }

    /// <summary>Kontrollkästchen im Windows-11-Stil (abgerundet, Akzentfarbe, Haken).</summary>
    internal sealed class ModernCheck : Control
    {
        private readonly float k;
        private bool _checked, _hover;
        public event EventHandler CheckedChanged;

        public ModernCheck(string text, float scale)
        {
            k = scale;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Font = new Font("Segoe UI", 9.5f);
            Cursor = Cursors.Hand;
            TabStop = true;
            UpdateSize();
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        private void UpdateSize()
        {
            int tw = TextRenderer.MeasureText(Text, Font).Width;
            Size = new Size((int)(30 * k) + tw, (int)(28 * k));
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (k > 0) UpdateSize(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Focus(); Checked = !Checked; }
            base.OnMouseClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = 20 * k;
            var box = new RectangleF(1 * k, (Height - s) / 2f, s, s);
            using (var p = Ui.Rounded(box, 4 * k))
            {
                if (_checked && Enabled)
                {
                    using (var b = new SolidBrush(_hover ? Ui.PrimaryHover : Ui.Primary)) g.FillPath(b, p);
                }
                else
                {
                    using (var b = new SolidBrush(_hover && Enabled ? Ui.Hover : Ui.Card)) g.FillPath(b, p);
                    using (var pen = new Pen(Enabled ? Ui.Muted : Ui.Disabled, 1f * k)) g.DrawPath(pen, p);
                }
                if (_checked && !Enabled)
                    using (var b = new SolidBrush(Ui.Disabled)) g.FillPath(b, p);
            }
            if (_checked)
            {
                float cx = box.X + s / 2, cy = box.Y + s / 2, m = 4.5f * k;
                using (var pen = new Pen(Enabled ? Ui.PrimaryText : Ui.Card, 1.6f * k))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(cx - m, cy + 0.2f * k), new PointF(cx - m / 3, cy + m * 0.7f), new PointF(cx + m, cy - m * 0.7f) });
                }
            }
            if (Focused && ShowKeyboardCues)
                using (var pen = new Pen(Ui.Text, 1f) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(30 * k), 0, Width - (int)(30 * k), Height), Enabled ? Ui.Text : Ui.Disabled,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Schalter im Windows-11-Stil mit Beschriftung links davon (z. B. "Alle wecken").</summary>
    internal sealed class ToggleSwitch : Control
    {
        private readonly float k;
        private bool _on, _hover;
        public event EventHandler CheckedChanged;

        public ToggleSwitch(string text, float scale)
        {
            k = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = new Font("Segoe UI", 9f);
            Cursor = Cursors.Hand;
            Text = text;
            UpdateSize();
        }

        /// <summary>Ein/aus setzen, ohne CheckedChanged auszulösen.</summary>
        public void SetChecked(bool on)
        {
            if (_on != on) { _on = on; Invalidate(); }
        }

        public bool Checked { get { return _on; } }

        private int SwitchW { get { return (int)(36 * k); } }
        private int SwitchH { get { return (int)(18 * k); } }

        private void UpdateSize()
        {
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font).Width + (int)(8 * k);
            Size = new Size(tw + SwitchW + 2, (int)(28 * k));
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (k > 0) UpdateSize(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _on = !_on;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int sx = Width - SwitchW - 1;
            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, sx - (int)(8 * k), Height), _on ? Ui.Text : Ui.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var r = new RectangleF(sx + 0.5f, (Height - SwitchH) / 2f, SwitchW - 1, SwitchH);
            using (var p = Ui.Rounded(r, r.Height / 2))
            {
                if (_on)
                    using (var b = new SolidBrush(_hover ? Ui.PrimaryHover : Ui.Primary)) g.FillPath(b, p);
                else
                {
                    using (var b = new SolidBrush(_hover ? Ui.Hover : Ui.Card)) g.FillPath(b, p);
                    using (var pen = new Pen(Ui.Muted, 1f * k)) g.DrawPath(pen, p);
                }
            }
            float kd = (_on ? 12 : 10) * k * (_hover ? 1.1f : 1f);
            float kx = _on ? r.Right - 3.5f * k - kd : r.X + 4 * k;
            using (var b = new SolidBrush(_on ? Ui.PrimaryText : Ui.Muted))
                g.FillEllipse(b, kx, r.Y + (r.Height - kd) / 2, kd, kd);
        }
    }

    /// <summary>Fußzeile unten im Fenster: links Copyright, rechts GS-Logo mit Name und Website (anklickbar).</summary>
    internal sealed class SignatureFooter : Panel
    {
        public const string Url = "https://www.westmc002.de";
        private const string Name1 = "Günther Schoder";
        private const string Name2 = "www.westmc002.de";
        private static Image _logo;
        private static bool _logoLoaded;
        private readonly float k;
        private Rectangle _link;
        private bool _hover;

        public SignatureFooter(float scale)
        {
            k = scale;
            Dock = DockStyle.Bottom;
            Height = (int)(64 * k);
            BackColor = Ui.Back;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>GS-Logo: Resources\GS-Logo.png neben der EXE, sonst das von Erstellen.bat eingebettete Logo.</summary>
        public static Image Logo
        {
            get
            {
                if (_logoLoaded) return _logo;
                _logoLoaded = true;
                try
                {
                    string p = Path.Combine(Application.StartupPath, "Resources", "GS-Logo.png");
                    if (File.Exists(p))
                        using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var img = Image.FromStream(fs))
                            _logo = new Bitmap(img);
                    else
                        using (var rs = typeof(Ui).Assembly.GetManifestResourceStream("GS-Logo.png"))
                            if (rs != null) using (var img = Image.FromStream(rs)) _logo = new Bitmap(img);
                }
                catch { _logo = null; }
                return _logo;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int pad = (int)(28 * k);
            int right = Width - pad;
            using (var f1 = new Font("Segoe UI Semibold", 9.5f))
            using (var f2 = new Font("Segoe UI", 9f, _hover ? FontStyle.Underline : FontStyle.Regular))
            using (var f3 = new Font("Segoe UI", 9f))
            {
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var s1 = TextRenderer.MeasureText(g, Name1, f1, Size.Empty, flags);
                var s2 = TextRenderer.MeasureText(g, Name2, f2, Size.Empty, flags);
                int tw = Math.Max(s1.Width, s2.Width);
                int th = s1.Height + (int)(2 * k) + s2.Height;
                int top = (Height - th) / 2;
                int tx = right - tw;
                TextRenderer.DrawText(g, Name1, f1, new Point(tx, top), Ui.Text, flags);
                int y2 = top + s1.Height + (int)(2 * k);
                TextRenderer.DrawText(g, Name2, f2, new Point(tx, y2), _hover ? Ui.AccentText : Ui.Muted, flags);
                _link = new Rectangle(tx, top, tw, th);

                var logo = Logo;
                if (logo != null)
                {
                    int lh = (int)(26 * k);
                    int lw = logo.Width * lh / Math.Max(1, logo.Height);
                    var r = new Rectangle(tx - (int)(10 * k) - lw, (Height - lh) / 2, lw, lh);
                    g.DrawImage(logo, r);
                    _link = Rectangle.Union(_link, r);
                }

                string left = "© " + DateTime.Now.Year + " · " + Ui.AppName;
                var s3 = TextRenderer.MeasureText(g, left, f3, Size.Empty, flags);
                TextRenderer.DrawText(g, left, f3, new Point(pad, (Height - s3.Height) / 2), Ui.Muted, flags);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = _link.Contains(e.Location);
            if (h != _hover) { _hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover) { _hover = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _link.Contains(e.Location)) Ui.Open(Url);
            base.OnMouseClick(e);
        }
    }

    /// <summary>"Info über Wake on LAN": großes GS-Logo, Version, Eckdaten, Anleitung (Vorlage: Monitor Dimmer).</summary>
    internal sealed class AboutDialog : FixedForm
    {
        private static AboutDialog _open;
        private readonly float k;

        public static void ShowFor(Form owner)
        {
            if (_open != null) { _open.Activate(); return; }
            using (var d = new AboutDialog(owner))
            {
                _open = d;
                try { if (owner != null && owner.Visible) d.ShowDialog(owner); else d.ShowDialog(); }
                finally { _open = null; }
            }
        }

        internal AboutDialog(Form owner)
        {
            Theme.Refresh();
            Text = "Info über " + Ui.AppName;
            Icon = Ui.WindowIcon();
            ShowInTaskbar = owner == null;
            StartPosition = owner != null && owner.Visible ? FormStartPosition.CenterParent : FormStartPosition.Manual;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int W = S(480);

            int y = S(24);
            var logo = new PictureBox
            {
                Image = SignatureFooter.Logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(0, y),
                Size = new Size(W, S(130)),
                BackColor = Color.Transparent
            };
            if (logo.Image == null) logo.Image = Ui.AppIcon(128);
            Controls.Add(logo);
            y += S(146);

            Controls.Add(new Label
            {
                Text = Ui.AppName,
                Font = new Font("Segoe UI Semibold", 15f),
                ForeColor = Ui.Text,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, y),
                Size = new Size(W, S(36))
            });
            y += S(38);
            Controls.Add(new Label
            {
                Text = "Version " + Ui.Version,
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = Ui.AccentText,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, y),
                Size = new Size(W, S(26))
            });
            y += S(40);

            // ---------- Karte mit Eckdaten ----------
            var card = new CardPanel(k) { Location = new Point(S(24), y), Size = new Size(W - S(48), S(140)) };
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Card };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(118)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var tip = new ToolTip();
            string built;
            try { built = File.GetLastWriteTime(Application.ExecutablePath).ToString("dd.MM.yyyy"); }
            catch { built = "-"; }
            AddRow(table, tip, "Entwickler", "Günther Schoder");
            AddRow(table, tip, "Erstellt", built);
            AddRow(table, tip, "Farbschema", (Theme.Dark ? "dunkel" : "hell") + " (folgt Windows)");
            AddRow(table, tip, "Programm", Demo.Active ? @"C:\Tools\Wake on LAN" : Application.StartupPath); // Screenshots: kein privater Pfad
            card.Controls.Add(table);
            Controls.Add(card);
            y += card.Height + S(24);

            // ---------- Knöpfe ----------
            var manual = new ModernButton("Anleitung", MenuHelper.Document, false, k);
            manual.Click += (s, e) =>
            {
                string p = Path.Combine(Application.StartupPath, "Liesmich.txt");
                if (File.Exists(p)) Ui.Open(p);
                else MessageBox.Show(this, "Liesmich.txt wurde im Programmordner nicht gefunden.", Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            var close = new ModernButton("Schließen", '\0', true, k) { DialogResult = DialogResult.Cancel };
            int gap = S(14);
            int total = manual.Width + close.Width + gap;
            int bx = (W - total) / 2;
            manual.Location = new Point(bx, y);
            close.Location = new Point(manual.Right + gap, y);
            Controls.Add(manual);
            Controls.Add(close);
            CancelButton = close;
            AcceptButton = close;
            y += close.Height + S(22);

            Controls.Add(new Label
            {
                Text = "© " + DateTime.Now.Year + " Günther Schoder",
                ForeColor = Ui.Muted,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, y),
                Size = new Size(W, S(24))
            });
            y += S(24) + S(20);

            ClientSize = new Size(W, y);
            if (StartPosition == FormStartPosition.Manual) Load += (s, e) => Ui.CenterOnScreen(this);
            Shown += (s, e) => close.Focus();
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private void AddRow(TableLayoutPanel t, ToolTip tip, string name, string value)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
            t.Controls.Add(new Label { Text = name, ForeColor = Ui.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Card }, 0, row);
            var v = new Label { Text = value, ForeColor = Ui.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, BackColor = Ui.Card };
            tip.SetToolTip(v, value);
            t.Controls.Add(v, 1, row);
        }
    }

    /// <summary>
    /// Windows-Akzentfarbe. Palette wie in Windows 11 (7 Abstufungen):
    /// 0 Hell3, 1 Hell2, 2 Hell1, 3 Basis, 4 Dunkel1, 5 Dunkel2, 6 Dunkel3.
    /// Helles Design: Knöpfe in Dunkel1, dunkles Design: Knöpfe in Hell2 (wie Windows 11).
    /// null = nicht lesbar -> festes Blau.
    /// </summary>
    internal static class Accent
    {
        private static Color[] _palette;
        private static bool _loaded;

        public static void Refresh()
        {
            _loaded = false;
        }

        public static Color[] Palette
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _palette = Read();
                }
                return _palette;
            }
        }

        private static Color[] Read()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var b = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (b != null && b.Length >= 28)
                    {
                        var p = new Color[7];
                        for (int i = 0; i < 7; i++) p[i] = Color.FromArgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                        return p;
                    }
                }
            }
            catch { }
            try
            {
                // Ältere Windows-Versionen: nur die Grundfarbe (DWORD als ABGR)
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = key == null ? null : key.GetValue("AccentColor");
                    if (v is int)
                    {
                        int c = (int)v;
                        var baseColor = Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF);
                        return new[]
                        {
                            Mix(Color.White, baseColor, 0.75), Mix(Color.White, baseColor, 0.50), Mix(Color.White, baseColor, 0.25),
                            baseColor,
                            Mix(Color.Black, baseColor, 0.20), Mix(Color.Black, baseColor, 0.40), Mix(Color.Black, baseColor, 0.60)
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Mischt a in b: t = 0 ergibt b, t = 1 ergibt a.</summary>
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(b.R + (a.R - b.R) * t),
                (int)Math.Round(b.G + (a.G - b.G) * t),
                (int)Math.Round(b.B + (a.B - b.B) * t));
        }

        /// <summary>Relative Helligkeit 0..1 (sRGB).</summary>
        public static double Luminance(Color c)
        {
            Func<double, double> lin = v => { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * lin(c.R) + 0.7152 * lin(c.G) + 0.0722 * lin(c.B);
        }
    }
}
