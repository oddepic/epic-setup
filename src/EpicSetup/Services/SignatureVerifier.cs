using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EpicSetup.Services;

/// <summary>
/// Verifies an installer is digitally signed with a valid Authenticode
/// certificate that chains to a trusted root, and reports the signer so the
/// engine can confirm it matches the expected publisher.
///
/// Identity is read through structured certificate APIs only — never by
/// splitting the raw DN string on commas, which truncates quoted CN values
/// like CN="Zoom Video Communications, Inc." at the comma. Matching accepts
/// the expected publisher against either the certificate's CN or its O
/// (organization), case-insensitively, so legal-entity and product-name
/// variants both work. If a publisher is pinned but the identity can't be
/// read, verification fails closed (the file is not installed).
/// </summary>
public sealed class SignatureVerifier
{
    public sealed class Result
    {
        public bool Trusted { get; init; }
        public string? Signer { get; init; }        // CN of the signing cert
        public string? Organization { get; init; }  // O of the signing cert
        public string? Subject { get; init; }       // full subject (diagnostics)
        public string? Thumbprint { get; init; }    // cert thumbprint (diagnostics)
        public string? Error { get; init; }
    }

    public Result Verify(string filePath)
    {
        if (!File.Exists(filePath))
            return new Result { Error = "File does not exist." };

        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var trustData = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            pPolicyCallbackData = IntPtr.Zero,
            pSIPDataCallback = IntPtr.Zero,
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoiceField = WTD_CHOICE_FILE,
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>()),
            dwStateAction = WTD_STATEACTION_VERIFY,
            hWVTStateData = IntPtr.Zero,
            pwstrBufferText = IntPtr.Zero,
            fdwProvFlags = WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT | WTD_CACHE_ONLY_URL_RETRIEVAL,
            dwUIContext = 0,
            pSigStateData = IntPtr.Zero
        };

        var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        try
        {
            Marshal.StructureToPtr(fileInfo, trustData.pFile, false);
            int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);

            if (rc == 0)
            {
                // Read identity while the WinTrust state is still alive so the
                // state-data fallback has access to the signer cert chain.
                using var cert = TryLoadSignerCert(filePath, trustData.hWVTStateData);
                if (cert is null)
                    return new Result { Trusted = true }; // trust ok, identity unreadable

                var cn = NormalizeName(cert.GetNameInfo(X509NameType.SimpleName, false));
                var org = NormalizeName(ReadRdnValue(cert, "O"));
                return new Result
                {
                    Trusted = true,
                    Signer = string.IsNullOrEmpty(cn) ? cert.Subject : cn,
                    Organization = string.IsNullOrEmpty(org) ? null : org,
                    Subject = cert.Subject,
                    Thumbprint = cert.Thumbprint
                };
            }

            return new Result { Trusted = false, Error = DescribeError(rc) };
        }
        finally
        {
            trustData.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            if (trustData.pFile != IntPtr.Zero)
                Marshal.FreeHGlobal(trustData.pFile);
        }
    }

    /// <summary>
    /// Loads the leaf signing certificate. Preferred path: .NET's
    /// CreateFromSignedFile for embedded Authenticode signatures. If that
    /// fails (e.g. catalog-signed files), extracts the cert from the
    /// WinVerifyTrust state data.
    /// </summary>
    private static X509Certificate2? TryLoadSignerCert(string filePath, IntPtr hStateData)
    {
        try
        {
            using var baseCert = X509Certificate.CreateFromSignedFile(filePath);
            if (baseCert is not null)
                return new X509Certificate2(baseCert); // copy: independent of baseCert
        }
        catch
        {
            // fall through to the WinTrust state-data path
        }

        if (hStateData != IntPtr.Zero)
        {
            try
            {
                var provData = WTHelperProvDataFromStateData(hStateData);
                if (provData != IntPtr.Zero)
                {
                    var signer = WTHelperGetProvSignerFromChain(provData, 0, false);
                    if (signer != IntPtr.Zero)
                    {
                        var certEntry = WTHelperGetProvCertFromChain(signer, 0);
                        if (certEntry != IntPtr.Zero)
                        {
                            var pCertContext = Marshal.ReadIntPtr(certEntry, 0);
                            // Duplicate (AddRef) so the cert outlives the state close.
                            var dup = CertDuplicateCertificateContext(pCertContext);
                            if (dup != IntPtr.Zero)
                                return new X509Certificate2(dup);
                        }
                    }
                }
            }
            catch
            {
                // identity unavailable -> caller fails closed when a pin exists
            }
        }

        return null;
    }

    private static string? ReadRdnValue(X509Certificate2 cert, string rdnKey)
    {
        try
        {
            var decoded = cert.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines);
            foreach (var rawLine in decoded.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith(rdnKey + "=", StringComparison.OrdinalIgnoreCase))
                    return line[(rdnKey.Length + 1)..].Trim();
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>Trim, strip surrounding quotes, and undo backslash escapes from a DN value.</summary>
    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Trim().Trim('"');
        value = value.Replace(@"\,", ",").Replace(@"\;", ";").Replace(@"\+", "+");
        return value.Trim().Trim('"');
    }

    public static bool MatchesAnySigner(Result verify, IEnumerable<string> expected)
    {
        foreach (var candidate in expected)
            if (MatchesExpectedSigner(verify, candidate))
                return true;
        return false;
    }

    public static bool MatchesExpectedSigner(Result verify, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true; // no pin: trust validity only
        return Contains(verify.Signer, expected) || Contains(verify.Organization, expected);
    }

    private static bool Contains(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var haystack = NormalizeName(actual);
        var needle = NormalizeName(expected);
        return needle.Length > 0 && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string DescribeError(int rc) => rc switch
    {
        unchecked((int)TRUST_E_PROVIDER_UNKNOWN) => "Trust provider unknown (file is not signed).",
        unchecked((int)TRUST_E_NOSIGNATURE) => "File is not digitally signed.",
        unchecked((int)TRUST_E_EXPLICIT_DISTRUST) => "Signature has been explicitly distrusted (revoked/blocked).",
        unchecked((int)CERT_E_EXPIRED) => "Signature certificate has expired.",
        unchecked((int)CERT_E_REVOKED) => "Signature certificate has been revoked.",
        unchecked((int)CERT_E_UNTRUSTEDROOT) => "Signature chains to an untrusted root certificate.",
        unchecked((int)CERT_E_UNTRUSTEDTESTROOT) => "Signature chains to a test root that is not trusted.",
        unchecked((int)TRUST_E_SUBJECT_NOT_TRUSTED) => "Subject failed the specified verification policy.",
        unchecked((int)CERT_E_CHAINING) => "Certificate chain could not be built to a trusted root.",
        unchecked((int)TRUST_E_BAD_DIGEST) => "Signature digest mismatch - file has been modified.",
        unchecked((int)TRUST_E_SUBJECT_FORM_UNKNOWN) => "Subject form is not recognized (untrusted file type).",
        unchecked((int)CERT_E_WRONG_USAGE) => "Certificate is not valid for Authenticode usage.",
        _ => $"Signature verification failed (WinVerifyTrust 0x{rc & 0xFFFFFFFF:X8})."
    };

    #region WinVerifyTrust

    private const int WTD_UI_NONE = 2;
    private const int WTD_REVOKE_NONE = 0;
    private const int WTD_CHOICE_FILE = 1;
    private const int WTD_STATEACTION_VERIFY = 1;
    private const int WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x00000080;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    // HRESULTs
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x800B0003);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0006);
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);
    private const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_UNTRUSTEDTESTROOT = unchecked((int)0x800B010D);
    private const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    private const int CERT_E_WRONG_USAGE = unchecked((int)0x800B0110);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPDataCallback;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoiceField;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwstrBufferText;
        public uint fdwProvFlags;
        public uint dwUIContext;
        public IntPtr pSigStateData;
    }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd,
        [In] ref Guid pgActionID, ref WINTRUST_DATA pWinTrustData);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, bool fCounterSigner);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSigner, uint idxCert);

    [DllImport("crypt32.dll")]
    private static extern IntPtr CertDuplicateCertificateContext(IntPtr pCertContext);

    #endregion
}
