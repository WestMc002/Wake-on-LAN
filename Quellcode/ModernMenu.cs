// Wake on LAN – Menüs im Windows-11-Stil (Vorlage: Monitor Dimmer, ModernMenu.cs)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WakeOnLan
{
    /// <summary>Farben für das Menü, passend zum hellen oder dunklen Windows-Design.</summary>
    internal sealed class MenuTheme
    {
        public bool Dark;
        public Color Back, Border, Text, Muted, Hover, Separator, Accent, AccentText, Section;

        public static MenuTheme Current()
        {
            bool dark = false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    if (v is int) dark = (int)v == 0;
                }
            }
            catch { }
            if (Theme.Override.HasValue) dark = Theme.Override.Value;
            var t = new MenuTheme { Dark = dark };
            if (dark)
            {
                t.Back = Color.FromArgb(44, 44, 44);
                t.Border = Color.FromArgb(70, 70, 70);
                t.Text = Color.FromArgb(240, 240, 240);
                t.Muted = Color.FromArgb(160, 160, 160);
                t.Hover = Color.FromArgb(62, 62, 62);
                t.Separator = Color.FromArgb(68, 68, 68);
                t.Section = Color.FromArgb(130, 130, 130);
            }
            else
            {
                t.Back = Color.FromArgb(249, 249, 249);
                t.Border = Color.FromArgb(220, 220, 220);
                t.Text = Color.FromArgb(28, 28, 28);
                t.Muted = Color.FromArgb(110, 110, 110);
                t.Hover = Color.FromArgb(234, 234, 234);
                t.Separator = Color.FromArgb(226, 226, 226);
                t.Section = Color.FromArgb(150, 150, 150);
            }
            t.Accent = Ui.Primary;
            t.AccentText = Ui.PrimaryText;
            return t;
        }
    }

    /// <summary>Zeichnet Menüs im Stil von Windows 11 (abgerundete Markierung, dezente Trennlinien).</summary>
    internal sealed class ModernRenderer : ToolStripRenderer
    {
        private readonly MenuTheme _t;
        private readonly float _k;

        public ModernRenderer(MenuTheme theme, float scale)
        {
            _t = theme;
            _k = scale;
        }

        private float S(float v) { return v * _k; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(_t.Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(_t.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        // Breite der Symbolspalte je Menü (wird beim Zeichnen des Menühintergrunds ermittelt)
        private readonly Dictionary<ToolStrip, int> _marginWidth = new Dictionary<ToolStrip, int>();

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            _marginWidth[e.ToolStrip] = e.AffectedBounds.Right;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Selected && e.Item.Enabled)
            {
                // Text steht senkrecht in der Zeilenmitte (OnRenderItemText), daher Markierung symmetrisch zur Zeile
                var r = new RectangleF(S(4), S(1), e.Item.Width - S(8), e.Item.Height - S(2));
                using (var path = Ui.Rounded(r, S(4)))
                using (var b = new SolidBrush(_t.Hover))
                    g.FillPath(b, path);
            }

            // Symbol, Haken und Punkt zeichnet OnRenderItemText - auf Höhe der Schrift
        }

        // Senkrechte Mitte der Schrift je Eintrag (für Pfeil und Schalter derselben Zeile)
        private readonly Dictionary<ToolStripItem, float> _textMiddle = new Dictionary<ToolStripItem, float>();

        /// <summary>
        /// Mitte der Buchstaben (ohne Unterlängen wie g, p) so, wie Windows den Text in textRect zeichnet:
        /// die Textzeile steht senkrecht mittig im Textbereich, die Mitte liegt eine halbe Versalhöhe über der Grundlinie.
        /// </summary>
        private static float TextMiddle(Graphics g, Font f, Rectangle textRect)
        {
            var ff = f.FontFamily;
            float em = ff.GetEmHeight(f.Style);
            float px = f.SizeInPoints * g.DpiY / 72f;
            float ascent = ff.GetCellAscent(f.Style) / em * px;
            float descent = ff.GetCellDescent(f.Style) / em * px;
            float top = textRect.Y + (textRect.Height - (ascent + descent)) / 2f;
            return top + ascent - 0.35f * px; // Versalhöhe Segoe UI ca. 0,7 em
        }

        private void DrawGlyph(Graphics g, GlyphMenuItem item, float cy)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int margin;
            if (item.Owner == null || !_marginWidth.TryGetValue(item.Owner, out margin)) margin = (int)S(36);
            float cx = margin / 2f + S(2);
            if (item.Checked && !item.IsToggle)
            {
                if (item.Radio)
                {
                    float d = S(8);
                    using (var b = new SolidBrush(_t.Accent))
                        g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                }
                else
                {
                    float s = S(5);
                    using (var p = new Pen(_t.Accent, S(1.8f)))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        g.DrawLines(p, new[] { new PointF(cx - s, cy), new PointF(cx - s / 3, cy + s * 0.7f), new PointF(cx + s, cy - s * 0.7f) });
                    }
                }
            }
            else if (item.Glyph != null)
            {
                g.DrawImage(item.Glyph, cx - item.Glyph.Width / 2f, cy - item.Glyph.Height / 2f, item.Glyph.Width, item.Glyph.Height);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Schalter (z. B. "Mit Windows starten") statt Tastenkürzel-Text zeichnen
            var gi = e.Item as GlyphMenuItem;
            // Text senkrecht exakt in die Zeilenmitte, gemessen an der Mitte der Buchstaben (globale Vorgabe)
            if (e.Item.IsOnDropDown && e.TextRectangle.Height > 0)
            {
                var tr = e.TextRectangle;
                float dy = e.Item.Height / 2f - TextMiddle(e.Graphics, e.TextFont, tr);
                tr.Offset(0, (int)Math.Round(dy));
                e.TextRectangle = tr;
            }
            if (gi != null && gi.IsToggle && e.Text == gi.ShortcutKeyDisplayString)
            {
                float mid;
                if (!_textMiddle.TryGetValue(e.Item, out mid)) mid = TextMiddle(e.Graphics, e.TextFont, e.TextRectangle);
                DrawToggle(e.Graphics, e.TextRectangle, gi.ToggleOn, mid);
                return;
            }
            e.TextColor = e.Item.Enabled ? _t.Text : _t.Muted;
            var item = e.Item as ToolStripMenuItem;
            if (item != null && e.TextRectangle.X > 0 && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString) && e.Text == item.ShortcutKeyDisplayString)
                e.TextColor = _t.Muted;
            e.TextFormat |= TextFormatFlags.EndEllipsis;
            // fester Textbeginn (Menüs ohne Symbolspalte): Windows ignoriert dort den Innenabstand
            if (gi != null && gi.TextLeft > 0 && e.Text == gi.Text)
                e.TextRectangle = new Rectangle(gi.TextLeft, e.TextRectangle.Y, Math.Max(0, gi.Width - gi.TextLeft), e.TextRectangle.Height);
            else if (e.Item.IsOnDropDown && e.Text == e.Item.Text && e.TextRectangle.X > S(20))
            {
                // Abstand Symbol -> Text verkleinern: Windows setzt den Text 14 Einheiten hinter die Symbolspalte,
                // wir 8 (MenuHelper.TextGap); gilt nur für den Haupttext, nicht für Tastenkürzel rechts
                var ddm = e.ToolStrip as ToolStripDropDownMenu;
                if (ddm != null && ddm.ShowImageMargin)
                {
                    int shift = (int)S(14 - MenuHelper.TextGap);
                    var tr = e.TextRectangle;
                    e.TextRectangle = new Rectangle(tr.X - shift, tr.Y, tr.Width + shift, tr.Height);
                }
            }
            base.OnRenderItemText(e);

            // Haupttext (nicht der Tastenkürzel-Text rechts): Symbol auf Höhe der Schrift zeichnen
            bool isShortcut = item != null && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString) && e.Text == item.ShortcutKeyDisplayString;
            if (!isShortcut)
            {
                float mid = TextMiddle(e.Graphics, e.TextFont, e.TextRectangle);
                _textMiddle[e.Item] = mid;
                if (gi != null) DrawGlyph(e.Graphics, gi, mid);
            }
        }

        private void DrawToggle(Graphics g, Rectangle area, bool on, float cy)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float sw = S(26), sh = S(14);
            var sr = new RectangleF(area.Right - sw, cy - sh / 2, sw, sh);
            using (var path = Ui.Rounded(sr, sh / 2))
            {
                if (on)
                    using (var b = new SolidBrush(_t.Accent)) g.FillPath(b, path);
                else
                    using (var p = new Pen(_t.Muted, S(1.2f))) g.DrawPath(p, path);
            }
            float kd = on ? S(9) : S(8);
            float kx = on ? sr.Right - S(2.5f) - kd : sr.X + S(3);
            using (var b = new SolidBrush(on ? Color.White : _t.Muted))
                g.FillEllipse(b, kx, cy - kd / 2, kd, kd);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(_t.Separator))
                e.Graphics.DrawLine(p, S(12), y, e.Item.Width - S(12), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ArrowRectangle;
            // Pfeil nach innen gerückt (auf Höhe der Tastenkürzel statt am Rand)
            float mid;
            float cx = r.X + r.Width / 2f - S(8), cy = _textMiddle.TryGetValue(e.Item, out mid) ? mid : r.Y + r.Height / 2f, s = S(3.5f);
            using (var p = new Pen(e.Item.Enabled ? _t.Text : _t.Muted, S(1.3f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLines(p, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
            }
        }

        // Haken und Symbole zeichnet OnRenderMenuItemBackground selbst (einheitlich ausgerichtet)
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
    }

    /// <summary>Menüeintrag mit selbst gezeichnetem Symbol und Haken bzw. Auswahlpunkt.</summary>
    internal sealed class GlyphMenuItem : ToolStripMenuItem
    {
        public Image Glyph;
        public bool Radio { get; set; }
        public bool IsToggle { get; set; }
        public bool ToggleOn { get; set; }
        /// <summary>Feste Zeilenhöhe (0 = Windows entscheidet). Sorgt für gleiche Abstände in Haupt- und Untermenüs.</summary>
        public int FixedHeight;
        /// <summary>Fester Beginn des Textes in Pixeln ab dem linken Rand (0 = Windows entscheidet).</summary>
        public int TextLeft;

        public override Size GetPreferredSize(Size constrainingSize)
        {
            var size = base.GetPreferredSize(constrainingSize);
            if (FixedHeight > 0) size.Height = FixedHeight;
            return size;
        }

        public GlyphMenuItem(string text) : base(text) { }
    }

    /// <summary>Unsichtbare Leerzeile mit fester Höhe (Abstand, den Windows nicht verändert).</summary>
    internal sealed class MenuSpacer : ToolStripItem
    {
        private readonly int _height;

        public MenuSpacer(int height)
        {
            _height = height;
            AutoSize = false;
            Size = new Size(1, height);
        }

        public override bool CanSelect { get { return false; } }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            return new Size(1, _height);
        }

        protected override void OnPaint(PaintEventArgs e) { }
    }

    /// <summary>Hilfen für Symbole (Windows-Symbolschrift) und runde Ecken.</summary>
    internal static class MenuHelper
    {
        private static string _iconFont;

        // Zeichen aus "Segoe Fluent Icons" (Windows 11) bzw. "Segoe MDL2 Assets" (Windows 10)
        public const char Power = '\uE7E8';
        public const char Add = '\uE710';
        public const char Refresh = '\uE72C';
        public const char More = '\uE712';
        public const char Edit = '\uE70F';
        public const char Delete = '\uE74D';
        public const char Up = '\uE74A';
        public const char Down = '\uE74B';
        public const char Link = '\uE71B';
        public const char Copy = '\uE8C8';
        public const char Document = '\uE8A5';
        public const char Folder = '\uE838';
        public const char Info = '\uE946';
        public const char Sort = '\uE8CB';
        public const char Group = '\uE902';
        public const char ChevronDown = '\uE70D';
        public const char ChevronRight = '\uE76C';
        public const char Clock = '\uE823';
        public const char History = '\uE81C';
        public const char Bell = '\uEA8F';
        public const char Save = '\uE74E';
        public const char Open = '\uE8E5';

        internal static string IconFont()
        {
            if (_iconFont != null) return _iconFont;
            _iconFont = "";
            using (var fonts = new InstalledFontCollection())
            {
                foreach (var f in fonts.Families)
                {
                    if (f.Name == "Segoe Fluent Icons") { _iconFont = f.Name; break; }
                    if (f.Name == "Segoe MDL2 Assets") _iconFont = f.Name;
                }
            }
            return _iconFont;
        }

        public static Image Glyph(char c, Color color, int size)
        {
            return Glyph(c, color, size, size);
        }

        /// <summary>
        /// Symbol mittig in einem breiteren Bild (= fester Abstand links und rechts). Zentriert wird nach den
        /// tatsächlich gezeichneten Pixeln, nicht nach der Zeichenbox der Schrift (globale Vorgabe).
        /// </summary>
        public static Image Glyph(char c, Color color, int size, int width)
        {
            string font = IconFont();
            if (font.Length == 0) return null;
            // zuerst auf eine größere Fläche zeichnen, damit nichts abgeschnitten wird
            int big = size * 2;
            using (var tmp = new Bitmap(big, big))
            {
                using (var g = Graphics.FromImage(tmp))
                using (var f = new Font(font, size * 0.78f, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(color))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(c.ToString(), f, b, new RectangleF(0, 0, big, big), sf);
                }
                // sichtbaren Bereich ermitteln
                int x0 = big, y0 = big, x1 = -1, y1 = -1;
                for (int y = 0; y < big; y++)
                    for (int x = 0; x < big; x++)
                        if (tmp.GetPixel(x, y).A > 16)
                        {
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (y < y0) y0 = y;
                            if (y > y1) y1 = y;
                        }
                var bmp = new Bitmap(width, size);
                if (x1 < 0) return bmp;
                var src = Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
                using (var g = Graphics.FromImage(bmp))
                    g.DrawImage(tmp, new Rectangle((width - src.Width) / 2, (size - src.Height) / 2, src.Width, src.Height), src, GraphicsUnit.Pixel);
                return bmp;
            }
        }

        private static Bitmap _blank;

        /// <summary>Durchsichtiges Bild als Platzhalter für die Breite der Symbolspalte.</summary>
        public static Image Blank(int width, int height)
        {
            if (_blank == null || _blank.Width != width || _blank.Height != height) _blank = new Bitmap(width, height);
            return _blank;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// Windows 11 zeichnet um runde Menüs selbst einen passenden Schatten. Der alte WinForms-Schatten
        /// (CS_DROPSHADOW) ist dagegen eckig und steht unten rechts als dunkle Ecke über die Rundung hinaus.
        /// Muss vor dem ersten Anzeigen gesetzt werden.
        /// </summary>
        public static void NoLegacyShadow(ToolStripDropDown dd)
        {
            if (Environment.OSVersion.Version.Build >= 22000 && dd.DropShadowEnabled) dd.DropShadowEnabled = false;
        }

        /// <summary>
        /// Oben eine feste, unsichtbare Leerzeile (Innenabstand selbst = 0, Windows übernimmt ihn bei Menüs nicht).
        /// Unten keine: Der Text sitzt in jeder Zeile oberhalb der Mitte (Platz für Unterlängen wie g, p),
        /// unter der letzten Zeile ist dadurch schon genug Luft. So ist der sichtbare Abstand Rahmen bis Schrift
        /// oben und unten gleich (bei 175 % je etwa 22 Bildschirmpixel).
        /// </summary>
        public static void AddEdgeSpacers(ToolStrip strip, int height)
        {
            var items = strip.Items;
            if (items.Count == 0 || !(items[0] is MenuSpacer)) items.Insert(0, new MenuSpacer(height));
            while (items.Count > 1 && items[items.Count - 1] is MenuSpacer) items.RemoveAt(items.Count - 1);
        }

        /// <summary>Abstand von der Symbolspalte (36 × Skalierung) bis zum Text in Einheiten (Windows: 14).</summary>
        public const int TextGap = 8;

        /// <summary>Höhe der unsichtbaren Leerzeile unter dem letzten Eintrag (globale Vorgabe: 6 × Skalierung).</summary>
        public const int BottomSpace = 6;

        /// <summary>Unten eine unsichtbare Leerzeile anhängen (Windows ignoriert Padding.Bottom bei Menüs).</summary>
        public static void AddBottomSpacer(ToolStrip strip, float scale)
        {
            var items = strip.Items;
            while (items.Count > 1 && items[items.Count - 1] is MenuSpacer) items.RemoveAt(items.Count - 1);
            items.Add(new MenuSpacer((int)Math.Round(BottomSpace * scale)));
        }

        public static void RoundCorners(ToolStripDropDown dd)
        {
            try
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(dd.Handle, 33, ref pref, sizeof(int));
            }
            catch { }
        }
    }

    /// <summary>
    /// Baut Kontextmenüs in den Fenstern im gleichen Stil wie das Tray-Menü
    /// (Symbolspalte, feste Zeilenhöhe, Abstände, runde Ecken, hell/dunkel).
    /// </summary>
    internal sealed class ModernMenuBuilder
    {
        public readonly MenuTheme Theme;
        public readonly float Scale;
        private readonly ModernRenderer _renderer;
        private readonly Font _font = new Font("Segoe UI", 9.5f);

        public ModernMenuBuilder(Control owner)
        {
            Scale = 1f;
            try { using (var g = owner.CreateGraphics()) Scale = g.DpiX / 96f; } catch { }
            Theme = MenuTheme.Current();
            _renderer = new ModernRenderer(Theme, Scale);
        }

        private int IconWidth { get { return (int)(36 * Scale); } }
        private int RowHeight { get { return TextRenderer.MeasureText("Ag", _font).Height + (int)(8 * Scale); } }

        public ContextMenuStrip CreateMenu()
        {
            var menu = new ContextMenuStrip
            {
                Renderer = _renderer,
                Font = _font,
                ShowCheckMargin = false,
                ShowImageMargin = true,
                ShowItemToolTips = false,
                ImageScalingSize = new Size(IconWidth, (int)(16 * Scale)),
                Padding = Padding.Empty
            };
            MenuHelper.NoLegacyShadow(menu);
            menu.Opening += (s, e) =>
            {
                MenuHelper.AddEdgeSpacers(menu, (int)(4 * Scale));
                MenuHelper.AddBottomSpacer(menu, Scale);
                Compact(menu);
            };
            menu.Opened += (s, e) => MenuHelper.RoundCorners(menu);
            return menu;
        }

        /// <summary>
        /// Menü ohne Symbole, Untermenüs und Tastenkürzel (z. B. reine Profilliste): keine leere Symbolspalte,
        /// Text links und rechts mit gleichem Abstand zum Rahmen (16 Einheiten, wie bis zum Symbol in den anderen Menüs).
        /// </summary>
        private void Compact(ContextMenuStrip menu)
        {
            var items = menu.Items.OfType<GlyphMenuItem>().ToList();
            if (items.Count == 0 || items.Any(i => i.Glyph != null || i.Checked || i.IsToggle || i.HasDropDownItems
                || !string.IsNullOrEmpty(i.ShortcutKeyDisplayString))) return;
            int side = (int)(16 * Scale);
            int textWidth = 0;
            foreach (var i in items)
            {
                i.Image = null;
                i.TextLeft = side;
                textWidth = Math.Max(textWidth, TextRenderer.MeasureText(i.Text, _font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
            }
            menu.ShowImageMargin = false;
            var pref = menu.GetPreferredSize(Size.Empty);
            menu.AutoSize = false;
            menu.Size = new Size(side + textWidth + side, pref.Height);
        }

        public GlyphMenuItem Item(string text, char glyph, Action action)
        {
            var item = new GlyphMenuItem(text);
            int pad = (int)(5 * Scale);
            item.Padding = new Padding(0, pad, (int)(8 * Scale), pad);
            item.FixedHeight = RowHeight;
            if (glyph != '\0') item.Glyph = MenuHelper.Glyph(glyph, Theme.Text, (int)(16 * Scale));
            item.Image = MenuHelper.Blank(IconWidth, (int)(16 * Scale));
            item.ImageScaling = ToolStripItemImageScaling.None;
            if (action != null) item.Click += (s, e) => action();
            return item;
        }

        /// <summary>Hinweiszeile (grau, nicht anklickbar).</summary>
        public GlyphMenuItem Info(string text)
        {
            var item = Item(text, MenuHelper.Info, null);
            item.Enabled = false;
            return item;
        }

        /// <summary>Untermenü im gleichen Stil (oben unsichtbare Leerzeile, unten 4 Einheiten Rand, runde Ecken).</summary>
        public void StyleDropDown(ToolStripMenuItem parent)
        {
            var dd = parent.DropDown;
            dd.Renderer = _renderer;
            dd.ShowItemToolTips = false;
            dd.Font = _font;
            var ddm = dd as ToolStripDropDownMenu;
            if (ddm != null)
            {
                ddm.ShowCheckMargin = false;
                ddm.ShowImageMargin = true;
                ddm.ImageScalingSize = new Size(IconWidth, (int)(16 * Scale));
            }
            dd.Padding = Padding.Empty;
            MenuHelper.NoLegacyShadow(dd);
            MenuHelper.AddEdgeSpacers(dd, (int)(4 * Scale));
            MenuHelper.AddBottomSpacer(dd, Scale);
            dd.Opened += (s, e) => MenuHelper.RoundCorners(dd);
        }
    }
}
