using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

namespace EpicSetup.Services;

/// <summary>
/// Verifies an installer is digitally signed with a valid Authenticode
/// certificate that chains to a trusted root, and reports the signer so the
/// engine can confirm it matches the expected publisher.
/// </summary>
public sealed class SignatureVerifier
{
    public sealed class Result
    {
        public bool Trusted { get; init; }
        public string? Signer { get; init; }
        public string? Error { get; init; }
    }

    public Result Verify(string filePath)
    {
        if (!File.Exists(filePath))
            return new Result { Error = "File does not exist." };

        var rc = WinVerifyFile(filePath);
        if (rc == 0)
        {
            string? signer = TryReadSubject(filePath);
            return new Result { Trusted = true, Signer = signer, Error = null };
        }

        var err = rc switch
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
        return new Result { Trusted = false, Signer = null, Error = err };
    }

    private static string? TryReadSubject(string filePath)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromSignedFile(filePath);
            if (cert.Subject == null) return null;
            var subject = cert.Subject;
            var cn = subject.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
            return cn ?? subject;
        }
        catch
        {
            return null;
        }
    }

    public static bool MatchesExpectedSigner(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true; // no publisher pin: only trust validity required
        if (actual == null) return false;
        return actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool MatchesAnySigner(string? actual, IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
            if (MatchesExpectedSigner(actual, c)) return true;
        return false;
    }

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

    private static int WinVerifyFile(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
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
        try
        {
            Marshal.StructureToPtr(fileInfo, trustData.pFile, false);
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);

            // close the state to release resources
            trustData.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            return rc;
        }
        finally
        {
            if (trustData.pFile != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(trustData.pFile);
            }
        }
    }

    #endregion
}