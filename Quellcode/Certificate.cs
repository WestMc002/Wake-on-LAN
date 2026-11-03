// Wake on LAN – Zertifikat für die verschlüsselte Handy-Steuerung (HTTPS)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace WakeOnLan
{
    /// <summary>
    /// Lädt das Zertifikat für HTTPS, z. B. von Let's Encrypt:
    /// .pfx/.p12 (mit Kennwort) oder .crt/.pem/.cer mit privatem Schlüssel (.key/.pem, unverschlüsselt, RSA oder ECDSA),
    /// oder aus dem Windows-Zertifikatspeicher (nach Domain).
    /// Der Schlüssel aus einer PEM-Datei wird als benannter Schlüssel des aktuellen Benutzers abgelegt
    /// (nötig für Windows-HTTPS) und beim nächsten Laden überschrieben.
    /// Zwischenzertifikate kommen in den Speicher "Zwischenzertifizierungsstellen" des Benutzers, damit Windows sie mitschickt
    /// (Android prüft ohne sie nicht).
    /// </summary>
    internal static class CertLoader
    {
        private const string KeyName = "Wake on LAN HTTPS";
        /// <summary>Eigener Schlüsselname zum Prüfen im Dialog, damit der laufende Server seinen Schlüssel behält.</summary>
        public const string TestKeyName = "Wake on LAN HTTPS (Prüfung)";
        private static readonly object ImportLock = new object();

        public static bool IsPfx(string file)
        {
            string ext = Path.GetExtension(file ?? "").ToLowerInvariant();
            return ext == ".pfx" || ext == ".p12";
        }

        /// <summary>Zertifikat mit privatem Schlüssel laden. keyFile leer: Schlüssel steht in der Zertifikatsdatei (oder .pfx).</summary>
        public static X509Certificate2 Load(string certFile, string keyFile, string password, string keyName = KeyName)
        {
            if (string.IsNullOrEmpty(certFile)) throw new InvalidOperationException("Bitte eine Zertifikatsdatei auswählen.");
            if (!File.Exists(certFile)) throw new FileNotFoundException("Die Zertifikatsdatei wurde nicht gefunden: " + certFile);
            X509Certificate2 cert;
            var chain = new List<X509Certificate2>();
            if (IsPfx(certFile))
            {
                var all = new X509Certificate2Collection();
                try { all.Import(File.ReadAllBytes(certFile), password ?? "", X509KeyStorageFlags.UserKeySet); }
                catch (CryptographicException ex) { throw new InvalidOperationException("Die Datei lässt sich nicht öffnen – Kennwort falsch? (" + ex.Message.Trim() + ")"); }
                cert = all.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey);
                if (cert == null) throw new InvalidOperationException("In der Datei ist kein privater Schlüssel enthalten.");
                chain.AddRange(all.Cast<X509Certificate2>().Where(c => c != cert));
            }
            else
            {
                byte[] raw = File.ReadAllBytes(certFile);
                string text = Encoding.ASCII.GetString(raw);
                var certs = Blocks(text, "CERTIFICATE");
                if (certs.Count > 0)
                {
                    cert = new X509Certificate2(certs[0]);
                    for (int i = 1; i < certs.Count; i++) chain.Add(new X509Certificate2(certs[i]));
                }
                else cert = new X509Certificate2(raw); // DER (.cer/.crt binär)

                string keyText = null;
                if (!string.IsNullOrEmpty(keyFile))
                {
                    if (!File.Exists(keyFile)) throw new FileNotFoundException("Die Schlüsseldatei wurde nicht gefunden: " + keyFile);
                    keyText = File.ReadAllText(keyFile, Encoding.ASCII);
                }
                else if (text.Contains("PRIVATE KEY-----")) keyText = text;
                if (keyText == null) throw new InvalidOperationException("Bitte die Datei mit dem privaten Schlüssel (.key bzw. privkey.pem) auswählen.");
                cert = AttachKey(cert, keyText, keyName);
            }

            AddIntermediates(cert, chain);
            return cert;
        }

        /// <summary>
        /// Zertifikat aus dem Windows-Zertifikatspeicher (erst Benutzer, dann Computer – "Eigene Zertifikate" und "Webhosting",
        /// dort legt win-acme sie oft ab): das neueste gültige mit privatem Schlüssel für die Domain (auch über *.domain).
        /// Verlängerungen, z. B. durch win-acme, werden so automatisch übernommen.
        /// </summary>
        public static X509Certificate2 FromStore(string domain)
        {
            domain = (domain ?? "").Trim();
            if (domain.Length == 0) throw new InvalidOperationException("Bitte die Domain des Zertifikats eintragen.");
            X509Certificate2 best = null;
            var stores = new[]
            {
                new X509Store(StoreName.My, StoreLocation.CurrentUser),
                new X509Store(StoreName.My, StoreLocation.LocalMachine),
                new X509Store("WebHosting", StoreLocation.LocalMachine)
            };
            foreach (var store in stores)
            {
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    foreach (X509Certificate2 c in store.Certificates)
                    {
                        if (!c.HasPrivateKey || c.NotBefore > DateTime.Now || c.NotAfter < DateTime.Now) continue;
                        if (!RemoteForm.MatchesCertificate(domain, DnsNames(c))) continue;
                        if (best == null || c.NotAfter > best.NotAfter) best = c;
                    }
                }
                catch (CryptographicException) { }
                finally { store.Close(); }
            }
            if (best == null)
                throw new InvalidOperationException("Im Windows-Zertifikatspeicher wurde kein gültiges Zertifikat mit privatem Schlüssel für „" + domain + "“ gefunden.");
            try
            {
                // Zugriff auf den Schlüssel prüfen (RSA oder ECDSA, auch CNG-Schlüssel)
                using (var rsa = best.GetRSAPrivateKey())
                    if (rsa == null)
                        using (var ec = best.GetECDsaPrivateKey())
                            if (ec == null) throw new InvalidOperationException("Der Schlüsseltyp des Zertifikats wird nicht unterstützt.");
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Kein Zugriff auf den privaten Schlüssel (Zertifikat liegt beim Computer). In certlm.msc beim Zertifikat " +
                    "„Alle Aufgaben › Private Schlüssel verwalten …“ dem eigenen Benutzer Lesen erlauben – oder das Zertifikat als Datei angeben.");
            }
            AddIntermediates(best, new List<X509Certificate2>());
            return best;
        }

        /// <summary>Datei mit dem privaten Schlüssel neben dem Zertifikat suchen (gleicher Name .key, privkey.pem, key.pem).</summary>
        public static string GuessKeyFile(string certFile)
        {
            try
            {
                if (string.IsNullOrEmpty(certFile) || IsPfx(certFile)) return "";
                string dir = Path.GetDirectoryName(certFile);
                string name = Path.GetFileNameWithoutExtension(certFile);
                var candidates = new List<string> { Path.Combine(dir, name + ".key"), Path.Combine(dir, name + "-key.pem"), Path.Combine(dir, name + ".key.pem") };
                // certbot/acme.sh: privkey.pem neben cert.pem/fullchain.pem, win-acme: <name>-key.pem
                candidates.Add(Path.Combine(dir, "privkey.pem"));
                candidates.Add(Path.Combine(dir, "key.pem"));
                candidates.Add(Path.Combine(dir, "private.key"));
                foreach (string c in candidates) if (File.Exists(c) && !string.Equals(c, certFile, StringComparison.OrdinalIgnoreCase)) return c;
                if (File.ReadAllText(certFile, Encoding.ASCII).Contains("PRIVATE KEY-----")) return "";
            }
            catch { }
            return "";
        }

        /// <summary>DNS-Namen des Zertifikats (Subject Alternative Name, sonst CN).</summary>
        public static List<string> DnsNames(X509Certificate2 cert)
        {
            var names = new List<string>();
            try
            {
                foreach (var ext in cert.Extensions)
                {
                    if (ext.Oid == null || ext.Oid.Value != "2.5.29.17") continue;
                    var r = new Der(ext.RawData);
                    var seq = r.Read(0x30);
                    while (!seq.End)
                    {
                        int tag;
                        byte[] v = seq.ReadAny(out tag);
                        if (tag == 0x82) names.Add(Encoding.ASCII.GetString(v)); // dNSName
                    }
                }
            }
            catch { }
            if (names.Count == 0)
            {
                string cn = cert.GetNameInfo(X509NameType.DnsName, false);
                if (!string.IsNullOrEmpty(cn)) names.Add(cn);
            }
            return names;
        }

        /// <summary>Kurzbeschreibung für den Dialog: "wol.example.de · gültig bis 31.01.2027".</summary>
        public static string Describe(X509Certificate2 cert)
        {
            var names = DnsNames(cert);
            string who = names.Count > 0 ? string.Join(", ", names.Take(3)) : cert.Subject;
            return who + " · gültig bis " + cert.NotAfter.ToString("dd.MM.yyyy");
        }

        // ------------------------------------------------------------------ PEM

        private static List<byte[]> Blocks(string text, string label)
        {
            var list = new List<byte[]>();
            var rx = new Regex("-----BEGIN " + Regex.Escape(label) + "-----([A-Za-z0-9+/=\\s]+)-----END " + Regex.Escape(label) + "-----");
            foreach (Match m in rx.Matches(text))
                list.Add(Convert.FromBase64String(Regex.Replace(m.Groups[1].Value, "\\s", "")));
            return list;
        }

        private static X509Certificate2 AttachKey(X509Certificate2 cert, string keyText, string keyName)
        {
            if (keyText.Contains("BEGIN ENCRYPTED PRIVATE KEY") || keyText.Contains("Proc-Type: 4,ENCRYPTED"))
                throw new InvalidOperationException("Der private Schlüssel ist mit einem Kennwort verschlüsselt. Bitte einen unverschlüsselten Schlüssel oder eine .pfx-Datei mit Kennwort verwenden.");
            byte[] pkcs8;
            var p8 = Blocks(keyText, "PRIVATE KEY");
            var rsa = Blocks(keyText, "RSA PRIVATE KEY");
            var ec = Blocks(keyText, "EC PRIVATE KEY");
            bool isEc = cert.PublicKey.Oid.Value == OidEc;
            if (p8.Count > 0) pkcs8 = isEc ? EcPkcs8(cert, Pkcs8Inner(p8[0])) : p8[0];
            else if (rsa.Count > 0) pkcs8 = RsaPkcs8(rsa[0]);
            else if (ec.Count > 0) pkcs8 = EcPkcs8(cert, ec[0]);
            else throw new InvalidOperationException("In der Schlüsseldatei wurde kein privater Schlüssel gefunden.");

            using (var key = ImportPersisted(pkcs8, keyName))
            {
                try
                {
                    if (key.AlgorithmGroup == CngAlgorithmGroup.Rsa)
                        using (var r = new RSACng(key)) return cert.CopyWithPrivateKey(r);
                    using (var e = new ECDsaCng(key)) return cert.CopyWithPrivateKey(e);
                }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException("Zertifikat und privater Schlüssel passen nicht zusammen.");
                }
            }
        }

        private const string OidEc = "1.2.840.10045.2.1";
        private static readonly byte[] OidRsaDer = { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01 };
        private static readonly byte[] OidEcDer = { 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01 };

        /// <summary>Inhalt (privater Schlüssel) aus einer PKCS#8-Struktur.</summary>
        private static byte[] Pkcs8Inner(byte[] pkcs8)
        {
            var seq = new Der(pkcs8).Read(0x30);
            seq.Read(0x02);       // Version
            seq.Read(0x30);       // Algorithmus
            return seq.ReadBytes(0x04);
        }

        private static byte[] RsaPkcs8(byte[] pkcs1)
        {
            return Der.Tlv(0x30, Der.Concat(
                new byte[] { 0x02, 0x01, 0x00 },
                Der.Tlv(0x30, Der.Concat(OidRsaDer, new byte[] { 0x05, 0x00 })),
                Der.Tlv(0x04, pkcs1)));
        }

        /// <summary>
        /// EC-Schlüssel (SEC1) vollständig neu aufbauen – mit Kurve und öffentlichem Schlüssel aus dem Zertifikat –
        /// und in PKCS#8 verpacken. Windows lehnt EC-Schlüssel ohne öffentlichen Teil teils ab.
        /// </summary>
        private static byte[] EcPkcs8(X509Certificate2 cert, byte[] sec1)
        {
            var seq = new Der(sec1).Read(0x30);
            seq.Read(0x02);                       // Version 1
            byte[] d = seq.ReadBytes(0x04);       // privater Wert
            byte[] curve = cert.PublicKey.EncodedParameters.RawData; // OID der Kurve (06 ...)
            byte[] point = cert.PublicKey.EncodedKeyValue.RawData;   // 04 || X || Y
            byte[] full = Der.Tlv(0x30, Der.Concat(
                new byte[] { 0x02, 0x01, 0x01 },
                Der.Tlv(0x04, d),
                Der.Tlv(0xA0, curve),
                Der.Tlv(0xA1, Der.Tlv(0x03, Der.Concat(new byte[] { 0x00 }, point)))));
            return Der.Tlv(0x30, Der.Concat(
                new byte[] { 0x02, 0x01, 0x00 },
                Der.Tlv(0x30, Der.Concat(OidEcDer, curve)),
                Der.Tlv(0x04, full)));
        }

        // ------------------------------------------------------------------ Schlüssel in Windows (CNG) ablegen

        [StructLayout(LayoutKind.Sequential)]
        private struct NCryptBuffer { public int cbBuffer; public int BufferType; public IntPtr pvBuffer; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NCryptBufferDesc { public int ulVersion; public int cBuffers; public IntPtr pBuffers; }

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptOpenStorageProvider(out IntPtr phProvider, string pszProviderName, int dwFlags);

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptImportKey(IntPtr hProvider, IntPtr hImportKey, string pszBlobType, ref NCryptBufferDesc pParameterList,
            out IntPtr phKey, byte[] pbData, int cbData, int dwFlags);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptFreeObject(IntPtr hObject);

        private const int NCRYPTBUFFER_PKCS_KEY_NAME = 45;
        private const int NCRYPT_OVERWRITE_KEY_FLAG = 0x80;
        private const int NCRYPT_SILENT_FLAG = 0x40;

        /// <summary>PKCS#8-Schlüssel als benannten, gespeicherten Schlüssel importieren (überschreibt den vorigen) und öffnen.</summary>
        private static CngKey ImportPersisted(byte[] pkcs8, string keyName)
        {
            lock (ImportLock) return ImportPersistedLocked(pkcs8, keyName);
        }

        /// <summary>Prüfschlüssel nach dem Prüfen wieder löschen.</summary>
        public static void DeleteTestKey()
        {
            try
            {
                lock (ImportLock)
                    if (CngKey.Exists(TestKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                        using (var k = CngKey.Open(TestKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)) k.Delete();
            }
            catch { }
        }

        private static CngKey ImportPersistedLocked(byte[] pkcs8, string keyName)
        {
            IntPtr provider, key = IntPtr.Zero;
            int hr = NCryptOpenStorageProvider(out provider, "Microsoft Software Key Storage Provider", 0);
            if (hr != 0) throw new CryptographicException(hr);
            IntPtr name = Marshal.StringToHGlobalUni(keyName);
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NCryptBuffer)));
            try
            {
                var b = new NCryptBuffer { cbBuffer = (keyName.Length + 1) * 2, BufferType = NCRYPTBUFFER_PKCS_KEY_NAME, pvBuffer = name };
                Marshal.StructureToPtr(b, buffer, false);
                var desc = new NCryptBufferDesc { ulVersion = 0, cBuffers = 1, pBuffers = buffer };
                hr = NCryptImportKey(provider, IntPtr.Zero, "PKCS8_PRIVATEKEY", ref desc, out key, pkcs8, pkcs8.Length, NCRYPT_OVERWRITE_KEY_FLAG | NCRYPT_SILENT_FLAG);
                if (hr != 0) throw new InvalidOperationException("Der private Schlüssel konnte nicht übernommen werden (Fehler 0x" + hr.ToString("X8") + ").");
            }
            finally
            {
                if (key != IntPtr.Zero) NCryptFreeObject(key);
                NCryptFreeObject(provider);
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(name);
            }
            return CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        }

        // ------------------------------------------------------------------ Zwischenzertifikate

        /// <summary>
        /// Zwischenzertifikate (aus der Datei, sonst über Windows aus dem Internet nachgeladen) in den Benutzer-Speicher
        /// "Zwischenzertifizierungsstellen" legen, damit Windows sie beim Verbindungsaufbau mitschickt.
        /// </summary>
        private static void AddIntermediates(X509Certificate2 cert, List<X509Certificate2> fromFile)
        {
            var intermediates = fromFile.Where(c => c.Subject != c.Issuer).ToList();
            if (intermediates.Count == 0)
            {
                try
                {
                    using (var chain = new X509Chain())
                    {
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
                        chain.Build(cert);
                        for (int i = 1; i < chain.ChainElements.Count; i++)
                        {
                            var c = chain.ChainElements[i].Certificate;
                            if (c.Subject != c.Issuer) intermediates.Add(c);
                        }
                    }
                }
                catch { }
            }
            if (intermediates.Count == 0) return;
            try
            {
                var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                try
                {
                    foreach (var c in intermediates)
                        if (store.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false).Count == 0) store.Add(c);
                }
                finally { store.Close(); }
            }
            catch { }
        }
    }

    /// <summary>Kleiner DER-Leser und -Schreiber (ASN.1) für Schlüssel und Zertifikatserweiterungen.</summary>
    internal sealed class Der
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly int _end;

        public Der(byte[] data) : this(data, 0, data.Length) { }

        private Der(byte[] data, int start, int end)
        {
            _data = data;
            _pos = start;
            _end = end;
        }

        public bool End { get { return _pos >= _end; } }

        /// <summary>Nächstes Element mit dem erwarteten Tag als eigener Leser (für SEQUENCE usw.).</summary>
        public Der Read(int tag)
        {
            int start, len;
            Header(tag, out start, out len);
            return new Der(_data, start, start + len);
        }

        public byte[] ReadBytes(int tag)
        {
            int start, len;
            Header(tag, out start, out len);
            var v = new byte[len];
            Array.Copy(_data, start, v, 0, len);
            return v;
        }

        public byte[] ReadAny(out int tag)
        {
            tag = _data[_pos];
            return ReadBytes(tag);
        }

        private void Header(int tag, out int start, out int len)
        {
            if (_pos >= _end || _data[_pos] != tag) throw new InvalidOperationException("Unerwarteter Aufbau des Schlüssels.");
            _pos++;
            int b = _data[_pos++];
            if (b < 0x80) len = b;
            else
            {
                int n = b & 0x7F;
                if (n < 1 || n > 3) throw new InvalidOperationException("Unerwarteter Aufbau des Schlüssels.");
                len = 0;
                for (int i = 0; i < n; i++) len = (len << 8) | _data[_pos++];
            }
            if (_pos + len > _end) throw new InvalidOperationException("Unerwarteter Aufbau des Schlüssels.");
            start = _pos;
            _pos += len;
        }

        public static byte[] Tlv(int tag, byte[] content)
        {
            var r = new List<byte> { (byte)tag };
            int n = content.Length;
            if (n < 0x80) r.Add((byte)n);
            else if (n < 0x100) { r.Add(0x81); r.Add((byte)n); }
            else if (n < 0x10000) { r.Add(0x82); r.Add((byte)(n >> 8)); r.Add((byte)n); }
            else { r.Add(0x83); r.Add((byte)(n >> 16)); r.Add((byte)(n >> 8)); r.Add((byte)n); }
            r.AddRange(content);
            return r.ToArray();
        }

        public static byte[] Concat(params byte[][] parts)
        {
            var r = new List<byte>();
            foreach (var p in parts) r.AddRange(p);
            return r.ToArray();
        }
    }
}
