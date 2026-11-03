// Wake on LAN – Sprungliste der Taskleiste und Windows-Benachrichtigungen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>
    /// Sprungliste (Rechtsklick auf das Programm in der Taskleiste bzw. im Startmenü):
    /// "Alle wecken", je Gruppe "Gruppe wecken" und je Gerät "… wecken". Ruft das Programm mit der Kommandozeile auf.
    /// </summary>
    internal static class JumpListHelper
    {
        public static void Update(DeviceStore store)
        {
            try
            {
                string exe = Application.ExecutablePath;
                var list = new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
                if (store.Devices.Any(d => d.InAll))
                    list.JumpItems.Add(MakeTask(exe, "Alle wecken", "/alle", "Alle Geräte mit „Alle wecken“", "Wecken"));
                foreach (string g in store.Groups())
                {
                    if (!store.Devices.Any(d => Device.SameGroup(d.Group, g))) continue;
                    list.JumpItems.Add(MakeTask(exe, g, "\"/gruppe:" + g + "\"", "Alle Geräte der Gruppe „" + g + "“ wecken", "Gruppen"));
                }
                foreach (var d in store.Devices)
                    list.JumpItems.Add(MakeTask(exe, d.Name, "\"" + d.Name + "\"", d.Name + " wecken", "Geräte"));
                list.Apply();
            }
            catch
            {
                // Sprungliste ist Komfort – ohne geht es auch (z. B. ältere Windows-Versionen)
            }
        }

        private static System.Windows.Shell.JumpTask MakeTask(string exe, string title, string args, string description, string category)
        {
            return new System.Windows.Shell.JumpTask
            {
                Title = title,
                Arguments = args,
                Description = description,
                ApplicationPath = exe,
                IconResourcePath = exe,
                IconResourceIndex = 0,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
                CustomCategory = category
            };
        }
    }

    /// <summary>
    /// Symbol im Infobereich: Windows-Benachrichtigungen (Symbol nur, solange eine Meldung angezeigt wird)
    /// oder dauerhaft mit Menü, wenn das Programm im Hintergrund weiterläuft.
    /// </summary>
    internal sealed class Notifier : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Timer _hide = new Timer { Interval = 12000 };
        private bool _persistent;

        public Notifier()
        {
            _icon = new NotifyIcon { Text = Ui.AppName, Visible = false };
            try { _icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _hide.Tick += (s, e) => { _hide.Stop(); if (!_persistent) _icon.Visible = false; };
            _icon.BalloonTipClicked += (s, e) => { if (Clicked != null) Clicked(this, EventArgs.Empty); };
            _icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && Clicked != null) Clicked(this, EventArgs.Empty); };
        }

        /// <summary>Klick auf die Meldung oder (mit der linken Maustaste) auf das Symbol.</summary>
        public event EventHandler Clicked;

        /// <summary>Symbol dauerhaft anzeigen (mit Menü und Hinweistext) oder nur für Meldungen.</summary>
        public void SetPersistent(bool on, ContextMenuStrip menu, string tooltip)
        {
            _persistent = on;
            _icon.ContextMenuStrip = on ? menu : null;
            string t = string.IsNullOrEmpty(tooltip) ? Ui.AppName : tooltip;
            _icon.Text = t.Length > 63 ? t.Substring(0, 63) : t; // Windows erlaubt höchstens 63 Zeichen
            _icon.Visible = on || _hide.Enabled; // gerade angezeigte Meldung nicht wegnehmen
        }

        public void Show(string title, string text, ToolTipIcon icon)
        {
            try
            {
                _icon.Visible = true;
                _icon.ShowBalloonTip(8000, title, text, icon);
                _hide.Stop();
                _hide.Start();
            }
            catch { }
        }

        public void Dispose()
        {
            _hide.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
