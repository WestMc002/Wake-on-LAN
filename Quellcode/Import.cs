// Wake on LAN – Geräte aus den alten Batch-Skripten "Wake on LAN (<Gerät>).bat" übernehmen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WakeOnLan
{
    /// <summary>
    /// Liest die alten Skripte ein:
    /// - "%WOL%" &lt;MAC&gt; ...                 → Weckpaket
    /// - SET "BMC=...", SET "BENUTZER=...", SET "IPMITOOL=...", SET "KENNWORTDATEI=%~dp0..." → IPMI
    /// - "Alle"-Skript: FOR %%P IN (...) und CALL :Anstossen "Name" IP → "Alle wecken" und Anstoßen
    /// </summary>
    internal static class ScriptImport
    {
        private static readonly Regex FileName = new Regex(@"^Wake on LAN \((?<name>.+)\)\.bat$", RegexOptions.IgnoreCase);
        private static readonly Regex MacLine = new Regex(@"^\s*""%WOL%""\s+(?<mac>[0-9A-F]{2}([:-][0-9A-F]{2}){5})", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex SetLine = new Regex(@"^\s*SET\s+""(?<key>[A-Z]+)=(?<value>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex ForLine = new Regex(@"^\s*FOR\s+%%\w\s+IN\s+\((?<list>[^)]*)\)\s+DO\s+CALL", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex NudgeLine = new Regex(@"^\s*CALL\s+:Anstossen\s+""(?<name>[^""]+)""\s+(?<host>\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>Liest alle Skripte im Ordner. Vorhandene Geräte (gleicher Name) werden nicht verändert.</summary>
        public static List<Device> Read(string folder, out string summary)
        {
            var found = new List<Device>();
            HashSet<string> allMembers = null;
            var nudges = new List<KeyValuePair<string, string>>();

            foreach (string file in Directory.GetFiles(folder, "*.bat").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var m = FileName.Match(Path.GetFileName(file));
                if (!m.Success) continue;
                string name = m.Groups["name"].Value.Trim();
                string text = File.ReadAllText(file, Encoding.Default);

                var forMatch = ForLine.Match(text);
                var nudgeMatches = NudgeLine.Matches(text);
                if (forMatch.Success || nudgeMatches.Count > 0)
                {
                    // Sammel-Skript ("Alle PCs")
                    if (forMatch.Success)
                    {
                        if (allMembers == null) allMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string n in forMatch.Groups["list"].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                            allMembers.Add(n.Trim('"'));
                    }
                    foreach (Match nm in nudgeMatches)
                        nudges.Add(new KeyValuePair<string, string>(nm.Groups["name"].Value.Trim(), nm.Groups["host"].Value.Trim()));
                    continue;
                }

                var d = new Device { Name = name };
                foreach (Match mm in MacLine.Matches(text))
                {
                    string mac = Device.NormalizeMac(mm.Groups["mac"].Value);
                    if (mac != null && !d.Macs.Contains(mac)) d.Macs.Add(mac);
                }
                d.UseMagic = d.Macs.Count > 0;

                var sets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match sm in SetLine.Matches(text)) sets[sm.Groups["key"].Value] = sm.Groups["value"].Value.Trim();
                string bmc;
                if (sets.TryGetValue("BMC", out bmc) && bmc.Length > 0)
                {
                    d.UseIpmi = true;
                    d.IpmiHost = bmc;
                    string v;
                    if (sets.TryGetValue("BENUTZER", out v)) d.IpmiUser = v;
                    if (sets.TryGetValue("IPMITOOL", out v) && v.Length > 0) d.IpmiTool = v;
                    if (sets.TryGetValue("KENNWORTDATEI", out v) && v.Length > 0)
                    {
                        string pwFile = v.Replace("%~dp0", folder.TrimEnd('\\') + "\\");
                        try
                        {
                            if (File.Exists(pwFile))
                            {
                                string pw = File.ReadAllLines(pwFile, Encoding.Default).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                                if (!string.IsNullOrEmpty(pw)) d.SetIpmiPassword(pw);
                            }
                        }
                        catch { }
                    }
                }
                if (d.UseMagic || d.UseIpmi) found.Add(d);
            }

            // Anstoßen: zum Gerät mit gleichem Namen hinzufügen oder neues Gerät anlegen
            foreach (var n in nudges)
            {
                var d = found.FirstOrDefault(x => string.Equals(x.Name, n.Key, StringComparison.OrdinalIgnoreCase));
                if (d == null)
                {
                    d = new Device { Name = n.Key };
                    found.Add(d);
                    if (allMembers == null) allMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    allMembers.Add(n.Key); // stand im Sammel-Skript
                }
                else if (allMembers != null) allMembers.Add(n.Key);
                d.UsePort = true;
                d.Port = Device.DefaultPort;
                if (d.Host.Length == 0) d.Host = n.Value;
            }

            // Ohne Sammel-Skript gehören alle zu "Alle wecken", sonst nur die dort genannten
            foreach (var d in found) d.InAll = allMembers == null || allMembers.Contains(d.Name);

            int ipmiWithoutPw = found.Count(d => d.UseIpmi && d.GetIpmiPassword() == null);
            summary = found.Count == 1 ? "1 Gerät gefunden" : found.Count + " Geräte gefunden";
            if (ipmiWithoutPw > 0) summary += " – bei IPMI-Geräten bitte das Kennwort eintragen";
            return found;
        }
    }
}
