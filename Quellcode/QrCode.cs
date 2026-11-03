// Wake on LAN – QR-Code für die Adresse der Handy-Steuerung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;

namespace WakeOnLan
{
    /// <summary>
    /// Kleiner QR-Code-Erzeuger (ISO/IEC 18004) für kurze Texte wie eine Adresse:
    /// Byte-Modus, Fehlerkorrektur M, Version 1 bis 10 (bis 213 Zeichen), Maske nach den Strafpunkt-Regeln.
    /// </summary>
    internal sealed class QrCode
    {
        /// <summary>Kantenlänge in Modulen (ohne Ruhezone).</summary>
        public readonly int Size;
        public readonly int Version;
        public readonly int Mask;
        private readonly bool[,] _dark;
        private readonly bool[,] _function;

        public bool this[int x, int y] { get { return _dark[y, x]; } }

        // Fehlerkorrektur M: Fehlerkorrektur-Bytes je Block, Blöcke Gruppe 1, Datenbytes Gruppe 1, Blöcke Gruppe 2 (je ein Byte mehr)
        private static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] Blocks1 = { 0, 1, 1, 1, 2, 2, 4, 4, 2, 3, 4 };
        private static readonly int[] Data1 = { 0, 16, 28, 44, 32, 43, 27, 31, 38, 36, 43 };
        private static readonly int[] Blocks2 = { 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 1 };
        private static readonly int[][] Align =
        {
            new int[0], new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
            new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 }
        };
        public const int MaxVersion = 10;

        private static int DataCapacity(int v) { return Blocks1[v] * Data1[v] + Blocks2[v] * (Data1[v] + 1); }

        /// <summary>QR-Code für einen Text (UTF-8). forceMask: 0–7 für Tests, sonst -1.</summary>
        public static QrCode Encode(string text, int forceMask = -1)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? "");
            int version = 0;
            for (int v = 1; v <= MaxVersion; v++)
            {
                int bits = 4 + (v < 10 ? 8 : 16) + data.Length * 8;
                if (bits <= DataCapacity(v) * 8) { version = v; break; }
            }
            if (version == 0) throw new ArgumentException("Der Text ist zu lang für den QR-Code.");
            return new QrCode(version, data, forceMask);
        }

        private QrCode(int version, byte[] data, int forceMask)
        {
            Version = version;
            Size = version * 4 + 17;
            _dark = new bool[Size, Size];
            _function = new bool[Size, Size];
            DrawFunctionPatterns();
            byte[] codewords = AddErrorCorrection(BuildData(data));
            DrawCodewords(codewords);

            int best = forceMask;
            if (best < 0)
            {
                long min = long.MaxValue;
                for (int m = 0; m < 8; m++)
                {
                    ApplyMask(m);
                    DrawFormat(m);
                    long p = Penalty();
                    if (p < min) { min = p; best = m; }
                    ApplyMask(m); // zurücknehmen (XOR)
                }
            }
            Mask = best;
            ApplyMask(best);
            DrawFormat(best);
        }

        // ------------------------------------------------------------------ Daten

        private byte[] BuildData(byte[] data)
        {
            var bits = new List<bool>();
            Action<int, int> put = (value, count) => { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); };
            put(4, 4); // Byte-Modus
            put(data.Length, Version < 10 ? 8 : 16);
            foreach (byte b in data) put(b, 8);
            int capacity = DataCapacity(Version) * 8;
            put(0, Math.Min(4, capacity - bits.Count)); // Abschluss
            while (bits.Count % 8 != 0) bits.Add(false);
            var bytes = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int b = 0;
                for (int j = 0; j < 8; j++) b = (b << 1) | (bits[i + j] ? 1 : 0);
                bytes.Add((byte)b);
            }
            for (int pad = 0; bytes.Count < DataCapacity(Version); pad ^= 1) bytes.Add((byte)(pad == 0 ? 0xEC : 0x11));
            return bytes.ToArray();
        }

        private byte[] AddErrorCorrection(byte[] data)
        {
            int ec = EcPerBlock[Version];
            int blockCount = Blocks1[Version] + Blocks2[Version];
            byte[] divisor = RsDivisor(ec);
            var blocks = new List<byte[]>();
            var eccs = new List<byte[]>();
            int pos = 0;
            for (int i = 0; i < blockCount; i++)
            {
                int len = Data1[Version] + (i < Blocks1[Version] ? 0 : 1);
                var block = new byte[len];
                Array.Copy(data, pos, block, 0, len);
                pos += len;
                blocks.Add(block);
                eccs.Add(RsRemainder(block, divisor));
            }
            var result = new List<byte>();
            int maxLen = Data1[Version] + (Blocks2[Version] > 0 ? 1 : 0);
            for (int i = 0; i < maxLen; i++)
                foreach (var b in blocks) if (i < b.Length) result.Add(b[i]);
            for (int i = 0; i < ec; i++)
                foreach (var e in eccs) result.Add(e[i]);
            return result.ToArray();
        }

        private static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 0x02);
            }
            return result;
        }

        private static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }

        private static byte Multiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        // ------------------------------------------------------------------ Muster

        private void Set(int x, int y, bool dark)
        {
            _dark[y, x] = dark;
            _function[y, x] = true;
        }

        private void DrawFunctionPatterns()
        {
            for (int i = 0; i < Size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);
            var align = Align[Version];
            int n = align.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            Set(align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            DrawFormat(0); // Platz reservieren, wird später überschrieben
            DrawVersion();
        }

        private void DrawFinder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(x, y, d != 2 && d != 4);
                }
        }

        private void DrawFormat(int mask)
        {
            int data = (0 << 3) | mask; // Fehlerkorrektur M = 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;
            for (int i = 0; i <= 5; i++) Set(8, i, Bit(bits, i));
            Set(8, 7, Bit(bits, 6));
            Set(8, 8, Bit(bits, 7));
            Set(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(bits, i));
            for (int i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(bits, i));
            Set(8, Size - 8, true); // dunkles Modul
        }

        private void DrawVersion()
        {
            if (Version < 7) return;
            int rem = Version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (Version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool b = Bit(bits, i);
                int a = Size - 11 + i % 3, c = i / 3;
                Set(a, c, b);
                Set(c, a, b);
            }
        }

        private static bool Bit(int x, int i) { return ((x >> i) & 1) != 0; }

        private void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!_function[y, x] && i < data.Length * 8)
                        {
                            _dark[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
                }
            }
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (_function[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) _dark[y, x] = !_dark[y, x];
                }
        }

        // ------------------------------------------------------------------ Strafpunkte (Maskenwahl)

        private long Penalty()
        {
            long result = 0;
            for (int y = 0; y < Size; y++) result += LinePenalty(i => _dark[y, i]);
            for (int x = 0; x < Size; x++) result += LinePenalty(i => _dark[i, x]);
            for (int y = 0; y < Size - 1; y++)
                for (int x = 0; x < Size - 1; x++)
                {
                    bool c = _dark[y, x];
                    if (c == _dark[y, x + 1] && c == _dark[y + 1, x] && c == _dark[y + 1, x + 1]) result += 3;
                }
            int dark = 0;
            foreach (bool b in _dark) if (b) dark++;
            int total = Size * Size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += k * 10;
            return result;
        }

        /// <summary>Regel 1 (Folgen gleicher Farbe) und Regel 3 (Muster 1:1:3:1:1 mit 4 hellen Modulen) für eine Zeile/Spalte.</summary>
        private long LinePenalty(Func<int, bool> at)
        {
            long result = 0;
            int run = 0;
            bool color = false;
            var history = new int[7];
            int padRun = Size; // helle Ruhezone vor der Zeile
            for (int i = 0; i < Size; i++)
            {
                bool c = at(i);
                if (c == color)
                {
                    run++;
                    if (run == 5) result += 3;
                    else if (run > 5) result++;
                }
                else
                {
                    AddHistory(history, run + padRun);
                    padRun = 0;
                    if (!color) result += FinderLike(history) * 40;
                    color = c;
                    run = 1;
                }
            }
            // Zeilenende: Folge abschließen, Ruhezone dahinter
            if (color) { AddHistory(history, run + padRun); padRun = 0; run = 0; }
            run += Size;
            AddHistory(history, run + padRun);
            result += FinderLike(history) * 40;
            return result;
        }

        private static void AddHistory(int[] history, int run)
        {
            Array.Copy(history, 0, history, 1, history.Length - 1);
            history[0] = run;
        }

        private static int FinderLike(int[] h)
        {
            int n = h[1];
            bool core = n > 0 && h[2] == n && h[3] == n * 3 && h[4] == n && h[5] == n;
            return (core && h[0] >= n * 4 && h[6] >= n ? 1 : 0) + (core && h[6] >= n * 4 && h[0] >= n ? 1 : 0);
        }
    }
}
