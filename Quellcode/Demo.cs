// Wake on LAN – Screenshots mit Beispieldaten (hell und dunkel) für GitHub
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WakeOnLan
{
    /// <summary>
    /// Erzeugt die Screenshots für die README aus Beispieldaten (Resources\Demo.xml, in die EXE eingebettet).
    /// Aufruf: "Wake on LAN.exe" --screenshots [Zielordner]. Die echte Geräteliste wird dabei nicht angefasst,
    /// es wird nichts gesendet und kein Gerät angepingt.
    /// Ablage: hell.png / dunkel.png (Hauptfenster) und &lt;name&gt;-hell.png / &lt;name&gt;-dunkel.png.
    /// Jedes Bild wird einmal vor schwarzem und einmal vor weißem Hintergrund aufgenommen; daraus ergeben sich
    /// durchsichtige runde Ecken und der Windows-Schatten. (Vorlage: Monitor Dimmer, Demo.cs)
    /// </summary>
    internal static class Demo
    {
        /// <summary>true, solange die Screenshots erstellt werden (z. B. kein privater Programmpfad im Info-Fenster).</summary>
        public static bool Active;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        public static bool RunScreenshots(string outDir)
        {
            string temp = Path.Combine(Path.GetTempPath(), "Wake on LAN Screenshots");
            try
            {
                Active = true;
                History.Disabled = true;
                Directory.CreateDirectory(outDir);
                try { File.Delete(Path.Combine(outDir, "Fehler.txt")); } catch { }
                Directory.CreateDirectory(temp);
                string demoPath = Path.Combine(temp, "Demo.xml");
                using (var rs = typeof(Demo).Assembly.GetManifestResourceStream("Demo.xml"))
                {
                    if (rs == null) throw new FileNotFoundException("Demo.xml ist nicht in die EXE eingebettet.");
                    using (var fs = File.Create(demoPath)) rs.CopyTo(fs);
                }
                var store = new DeviceStore(demoPath); // nur lesen
                store.Load();
                foreach (var d in store.Devices.Where(x => x.UseIpmi)) d.SetIpmiPassword("demo");
                var edit = store.Find("Büro-Laptop") ?? store.Devices.FirstOrDefault();

                // Maus aus dem Bild (rechts unten auf dem Hauptbildschirm); Fenster öffnen dort, wo die Maus ist
                var area = Screen.PrimaryScreen.WorkingArea;
                Cursor.Position = new Point(area.Right - 2, area.Bottom - 2);

                using (var backdrop = new Backdrop())
                {
                    // Hintergrundfläche einmal vorab anzeigen (das erste Anzeigen kann sonst ein Menü schließen)
                    backdrop.Cover(new Rectangle(area.Right - 2, area.Bottom - 2, 1, 1), Color.Black, IntPtr.Zero);
                    Pump(300);
                    backdrop.Hide();

                    foreach (bool dark in new[] { false, true })
                    {
                        Theme.Override = dark;
                        Theme.Refresh();
                        string suffix = dark ? "dunkel" : "hell";

                        // Hauptfenster (Titelbild) und Kontextmenü eines Geräts
                        using (var main = new MainForm(store, true))
                        {
                            main.Size = new Size(S(main, 1260), S(main, 800));
                            main.StartPosition = FormStartPosition.Manual;
                            main.Location = new Point(area.Left + (area.Width - main.Width) / 2, area.Top + Math.Max(0, (area.Height - main.Height) / 2));
                            main.ApplyDemoStatus();
                            ShowForShot(main);
                            Save(Capture(backdrop, main.Handle, FrameBounds(main), Margin(main)), Path.Combine(outDir, suffix + ".png"));

                            using (var menu = main.DemoRowMenu("Büro-Laptop"))
                            {
                                if (menu != null)
                                {
                                    var row = main.DemoRowScreenBounds("Büro-Laptop");
                                    var pos = new Point(row.Right - S(main, 260), row.Top + row.Height / 2);
                                    menu.Show(pos);
                                    Pump(800);
                                    if (!menu.Visible) { menu.Show(pos); Pump(800); } // falls Windows es wieder geschlossen hat
                                    Save(Capture(backdrop, menu.Handle, menu.Bounds, Margin(menu)), Path.Combine(outDir, "menue-" + suffix + ".png"));
                                    menu.Close();
                                    Pump(200);
                                }
                            }

                            // Die Ansichten so, wie man sie im Programm sieht: im Hauptfenster mit Navigation links.
                            // Breite Ansichten (drei Spalten) bekommen ein breiteres Fenster; reicht die Höhe nicht,
                            // wächst das Fenster, soweit der Bildschirm reicht.
                            int edge = Margin(main);
                            int maxW = area.Width - 2 * edge, maxH = area.Height - 2 * edge;
                            foreach (var view in new[] {
                                new { Name = "zeitplaene", File = "zeitplaene", Width = 1260 },
                                new { Name = "geraet", File = "geraet", Width = 1560 },
                                new { Name = "handy", File = "handy", Width = 1900 } })
                            {
                                main.DemoShowView("", null);
                                main.Size = new Size(Math.Min(maxW, S(main, view.Width)), Math.Min(maxH, S(main, 800)));
                                main.Location = new Point(area.Left + (area.Width - main.Width) / 2, area.Top + (area.Height - main.Height) / 2);
                                Pump(200);
                                main.DemoShowView(view.Name, edit != null ? edit.Name : null);
                                Pump(1200); // Aufbau, Symbole, QR-Code
                                if (main.DemoFitView(maxH))
                                {
                                    main.Location = new Point(main.Left, area.Top + (area.Height - main.Height) / 2);
                                    Pump(600);
                                }
                                Cursor.Position = new Point(area.Right - 2, area.Bottom - 2);
                                Pump(200);
                                Save(Capture(backdrop, main.Handle, FrameBounds(main), edge), Path.Combine(outDir, view.File + "-" + suffix + ".png"));
                            }
                            main.DemoShowView("", null);
                            main.Close();
                        }
                        Pump(300);

                        Shot(backdrop, () => new AboutDialog(null), Path.Combine(outDir, "info-" + suffix + ".png"));
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(outDir, "Fehler.txt"), ex.ToString()); } catch { }
                return false;
            }
            finally
            {
                Theme.Override = null;
                Active = false;
                History.Disabled = false;
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        private static int S(Control c, float v)
        {
            try { using (var g = c.CreateGraphics()) return (int)Math.Round(v * g.DpiX / 96f); }
            catch { return (int)v; }
        }

        private static void ShowForShot(Form f)
        {
            f.ShowInTaskbar = false;
            f.Show();
            f.TopMost = true;
            f.Activate();
            Pump(1500); // Aufbau und Symbole
        }

        private static void Shot(Backdrop backdrop, Func<Form> create, string file)
        {
            using (var f = create())
            {
                ShowForShot(f);
                Save(Capture(backdrop, f.Handle, FrameBounds(f), Margin(f)), file);
                f.Close();
            }
            Pump(300);
        }

        /// <summary>Sichtbarer Fensterrahmen ohne den unsichtbaren Größenänderungsrand.</summary>
        private static Rectangle FrameBounds(Form f)
        {
            RECT r;
            return DwmGetWindowAttribute(f.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0
                ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                : f.Bounds;
        }

        /// <summary>Rand um das Fenster für den Schatten (24 Einheiten).</summary>
        private static int Margin(Control c)
        {
            return S(c, 24);
        }

        /// <summary>Nimmt den Bereich vor schwarzem und vor weißem Hintergrund auf und berechnet daraus die Durchsichtigkeit.</summary>
        private static Bitmap Capture(Backdrop backdrop, IntPtr target, Rectangle bounds, int margin)
        {
            // nur der sichtbare Bildschirm: liegt das Fenster am Rand, wäre der Bereich daneben sonst schwarz
            var r = Rectangle.Intersect(Rectangle.Inflate(bounds, margin, margin), Screen.FromHandle(target).Bounds);
            backdrop.Cover(r, Color.Black, target);
            Pump(300);
            using (var onBlack = Grab(r))
            {
                backdrop.Cover(r, Color.White, target);
                Pump(300);
                using (var onWhite = Grab(r))
                {
                    backdrop.Hide();
                    return Combine(onBlack, onWhite);
                }
            }
        }

        private static Bitmap Grab(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            return bmp;
        }

        private static Bitmap Combine(Bitmap onBlack, Bitmap onWhite)
        {
            int w = onBlack.Width, h = onBlack.Height;
            var rect = new Rectangle(0, 0, w, h);
            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = onBlack.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var wd = onWhite.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var od = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = bd.Stride * h;
                var b = new byte[n];
                var wh = new byte[n];
                var o = new byte[n];
                Marshal.Copy(bd.Scan0, b, 0, n);
                Marshal.Copy(wd.Scan0, wh, 0, n);
                for (int i = 0; i + 3 < n; i += 4)
                {
                    int diff = ((wh[i] - b[i]) + (wh[i + 1] - b[i + 1]) + (wh[i + 2] - b[i + 2])) / 3;
                    int a = Math.Max(0, Math.Min(255, 255 - diff));
                    if (a == 0) continue; // ganz durchsichtig
                    for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Min(255, b[i + c] * 255 / a);
                    o[i + 3] = (byte)a;
                }
                Marshal.Copy(o, 0, od.Scan0, n);
            }
            finally
            {
                onBlack.UnlockBits(bd);
                onWhite.UnlockBits(wd);
                result.UnlockBits(od);
            }
            return result;
        }

        private static void Save(Bitmap bmp, string file)
        {
            using (bmp) bmp.Save(file, ImageFormat.Png);
        }

        /// <summary>Wartet und verarbeitet dabei die Fensternachrichten (Zeichnen, Laden).</summary>
        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        /// <summary>Einfarbige Fläche hinter dem aufgenommenen Bereich.</summary>
        private sealed class Backdrop : Form
        {
            public Backdrop()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
                    return cp;
                }
            }

            public void Cover(Rectangle r, Color color, IntPtr target)
            {
                BackColor = color;
                Bounds = r;
                if (!Visible) Show();
                // vor alle anderen Fenster legen, danach das aufgenommene Fenster bzw. Menü wieder davor
                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                SetWindowPos(target, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                Refresh();
            }

            private static readonly IntPtr HwndTopmost = new IntPtr(-1);
            private const uint NoMoveSizeActivate = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }
    }
}
