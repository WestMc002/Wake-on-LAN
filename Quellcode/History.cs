// Wake on LAN – Verlauf (wann welches Gerät geweckt wurde, online kam, heruntergefahren wurde)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>Verlauf in %LOCALAPPDATA%\Wake on LAN\Verlauf.log (eine Zeile je Ereignis, höchstens 2000 Zeilen).</summary>
    internal static class History
    {
        private const int MaxLines = 2000;
        private static readonly object Sync = new object();

        /// <summary>Beim Erstellen der Screenshots nichts schreiben.</summary>
        public static bool Disabled;

        public static string FilePath { get { return Path.Combine(DeviceStore.DataFolder, "Verlauf.log"); } }

        public static void Add(string device, string text)
        {
            if (Disabled) return;
            string line = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + "\t" + device + "\t" + (text ?? "").Replace("\r", " ").Replace("\n", " ");
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(DeviceStore.DataFolder);
                    File.AppendAllText(FilePath, line + "\r\n", Encoding.UTF8);
                    // gelegentlich kürzen
                    var info = new FileInfo(FilePath);
                    if (info.Length > 400 * 1024)
                    {
                        var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                        if (lines.Length > MaxLines) File.WriteAllLines(FilePath, lines.Skip(lines.Length - MaxLines), Encoding.UTF8);
                    }
                }
                catch { }
            }
        }

        /// <summary>Einträge, neueste zuerst: Zeit, Gerät, Text.</summary>
        public static List<string[]> Read()
        {
            var result = new List<string[]>();
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(FilePath)) return result;
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8).Reverse())
                    {
                        var parts = line.Split('\t');
                        if (parts.Length >= 3) result.Add(new[] { parts[0], parts[1], string.Join(" ", parts.Skip(2)) });
                    }
                }
                catch { }
            }
            return result;
        }

        public static void Clear()
        {
            lock (Sync)
            {
                try { File.Delete(FilePath); } catch { }
            }
        }
    }

    /// <summary>Fenster "Verlauf": Liste der Ereignisse, neueste oben.</summary>
    internal sealed class HistoryForm : Form
    {
        private readonly float k;
        private readonly ListView _list;

        public HistoryForm()
        {
            Theme.Refresh();
            Text = Ui.AppName + " – Verlauf";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            var bar = new Panel { Dock = DockStyle.Bottom, Height = S(72), BackColor = Ui.Back, Padding = new Padding(S(24), S(16), S(24), S(16)) };
            var close = new ModernButton("Schließen", '\0', true, k) { Dock = DockStyle.Right, Margin = Padding.Empty, DialogResult = DialogResult.Cancel };
            var clear = new ModernButton("Verlauf leeren", MenuHelper.Delete, false, k) { Dock = DockStyle.Left, Margin = Padding.Empty };
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Den ganzen Verlauf löschen?", Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                History.Clear();
                Fill();
            };
            bar.Controls.Add(clear);
            bar.Controls.Add(close);
            CancelButton = close;

            var host = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back, Padding = new Padding(S(24), S(20), S(24), 0) };
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Ui.Card,
                ForeColor = Ui.Text
            };
            _list.Columns.Add("Zeit", S(150));
            _list.Columns.Add("Gerät", S(170));
            _list.Columns.Add("Ereignis", S(520));
            host.Controls.Add(_list);

            Controls.Add(host);
            Controls.Add(bar);
            ClientSize = new Size(S(900), S(560));
            MinimumSize = new Size(S(600), S(360));
            Fill();
            Theme.Apply(this);
            if (Theme.Dark) _list.HandleCreated += (s, e) => DarkList();
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private void Fill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in History.Read()) _list.Items.Add(new ListViewItem(e));
            if (_list.Items.Count == 0) _list.Items.Add(new ListViewItem(new[] { "", "", "Noch keine Einträge." }));
            _list.EndUpdate();
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        private void DarkList()
        {
            SetWindowTheme(_list.Handle, "DarkMode_Explorer", null);
        }
    }
}
