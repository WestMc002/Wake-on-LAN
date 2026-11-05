// Wake on LAN – Hell- und Dunkelmodus
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WakeOnLan
{
    /// <summary>
    /// Helles/dunkles Design für die Fenster. Folgt der Windows-Einstellung "App-Modus".
    /// Die Fenster sind hell gestaltet; im dunklen Modus werden die hellen Farben über Map() übersetzt.
    /// (Vorlage: Monitor Dimmer)
    /// </summary>
    internal static class Theme
    {
        private static bool? _dark;

        /// <summary>Feste Vorgabe statt der Windows-Einstellung (sonst null).</summary>
        public static bool? Override { get; set; }
        private static readonly Dictionary<int, Color> DarkMap = new Dictionary<int, Color>();

        static Theme()
        {
            Add(Color.FromArgb(243, 243, 243), Color.FromArgb(32, 32, 32));    // Fensterhintergrund
            Add(Color.White, Color.FromArgb(43, 43, 43));                       // Karten, Listen
            Add(Color.FromArgb(229, 229, 229), Color.FromArgb(61, 61, 61));    // Rahmen
            Add(Color.FromArgb(27, 27, 27), Color.FromArgb(250, 250, 250));    // Text
            Add(Color.FromArgb(97, 97, 97), Color.FromArgb(206, 206, 206));    // Grauer Text
            Add(Color.FromArgb(15, 123, 15), Color.FromArgb(108, 203, 95));    // Grün
            Add(Color.FromArgb(196, 43, 28), Color.FromArgb(255, 153, 164));   // Rot
            Add(Color.FromArgb(157, 93, 0), Color.FromArgb(252, 225, 0));      // Gelb/Hinweis (Windows 11)
            Add(Color.FromArgb(157, 157, 157), Color.FromArgb(110, 110, 110)); // deaktiviert
            Add(Color.FromArgb(246, 246, 246), Color.FromArgb(52, 52, 52));    // Knopf: Maus darüber
            Add(Color.FromArgb(236, 236, 236), Color.FromArgb(62, 62, 62));    // Knopf: gedrückt
            Add(Color.FromArgb(249, 249, 249), Color.FromArgb(50, 50, 50));    // Karte: Maus darüber
            Add(Color.FromArgb(234, 234, 234), Color.FromArgb(45, 45, 45));    // Navigation: ausgewählt
            Add(Color.FromArgb(29, 78, 216), Color.FromArgb(147, 180, 255));   // Versions-Plakette
            Add(SystemColors.Control, Color.FromArgb(32, 32, 32));
            Add(SystemColors.Window, Color.FromArgb(43, 43, 43));
            Add(SystemColors.WindowText, Color.FromArgb(250, 250, 250));
            Add(SystemColors.ControlText, Color.FromArgb(250, 250, 250));
        }

        private static void Add(Color light, Color dark)
        {
            DarkMap[Key(light)] = dark;
        }

        private static int Key(Color c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B).ToArgb();
        }

        /// <summary>Windows-Einstellung "App-Modus: Dunkel".</summary>
        public static bool Dark
        {
            get
            {
                if (Override.HasValue) return Override.Value;
                if (_dark.HasValue) return _dark.Value;
                bool dark = false;
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                        if (v is int) dark = (int)v == 0;
                    }
                }
                catch { }
                _dark = dark;
                return dark;
            }
        }

        /// <summary>Beim Öffnen eines Fensters neu lesen (Einstellung kann sich geändert haben).</summary>
        public static void Refresh()
        {
            _dark = null;
            Accent.Refresh();
        }

        /// <summary>Helle Farbe -> passende dunkle Farbe (im hellen Modus unverändert).</summary>
        public static Color Map(Color c)
        {
            if (!Dark || c == Color.Transparent || c.A == 0) return c;
            Color d;
            return DarkMap.TryGetValue(Key(c), out d) ? d : c;
        }

        // ------------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        /// <summary>Dunkle Titelleiste und dunkle Bildlaufleisten (nur im dunklen Modus).</summary>
        public static void Apply(Form form)
        {
            if (!Dark) return;
            // jetzt und bei jedem neuen Fenster-Handle (Windows baut das Fenster z. B. bei ShowInTaskbar neu auf)
            Action<IntPtr> dark = h =>
            {
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(h, 19, ref on, sizeof(int)); // ältere Windows-10-Versionen
            };
            if (form.IsHandleCreated) dark(form.Handle);
            form.HandleCreated += (s, e) => { if (Dark) dark(form.Handle); };
            ApplyTo(form);
        }

        private static void ApplyTo(Control c)
        {
            var scrollable = c as ScrollableControl;
            if (scrollable != null && scrollable.AutoScroll) WhenHandle(c, h => SetWindowTheme(h, "DarkMode_Explorer", null));
            var text = c as TextBox;
            if (text != null && text.Multiline) WhenHandle(c, h => SetWindowTheme(h, "DarkMode_Explorer", null));
            foreach (Control child in c.Controls) ApplyTo(child);
        }

        private static void WhenHandle(Control c, Action<IntPtr> action)
        {
            if (c.IsHandleCreated) action(c.Handle);
            else c.HandleCreated += (s, e) => action(c.Handle);
        }
    }
}
