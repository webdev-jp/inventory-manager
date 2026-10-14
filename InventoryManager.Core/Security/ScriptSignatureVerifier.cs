using System.Runtime.InteropServices;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace InventoryManager.Core.Security;

public enum ScriptSignatureStatus
{
    Valid,
    NotFound,
    NotSigned,
    /// <summary>Signed, but the signature doesn't verify: contents changed since signing, or the
    /// signing certificate doesn't chain to a root this machine trusts.</summary>
    Invalid,
    /// <summary>Signature verifies, but it was made by a certificate that isn't on the allow-list.</summary>
    WrongSigner,
}

public sealed record ScriptSignatureResult(ScriptSignatureStatus Status, string? SignerThumbprint, string Detail)
{
    public bool IsValid => Status == ScriptSignatureStatus.Valid;
}

/// <summary>
/// Checks a script's Authenticode signature ourselves, so the offboarding tool can be launched with
/// <c>-ExecutionPolicy Bypass</c> without losing its tamper protection. Why not just keep
/// <c>AllSigned</c>: that policy applies to EVERY script and module the process loads, so each
/// Microsoft Graph / Exchange module import made PowerShell stop and ask "run software from this
/// untrusted publisher?" in a console window hidden behind the tool - which, with the tool's UI
/// thread blocked on it, looked exactly like a freeze (found live 2026-10-08), and recurs whenever
/// Microsoft rotates its signing certificate.
/// <para>The check is two independent things that must both pass: Windows' own WinVerifyTrust
/// (contents still match the signed hash, and the signer chains to a trusted root - the same
/// engine Get-AuthenticodeSignature uses), AND the signer's thumbprint must be on a pinned
/// allow-list. The pin is the stronger half: a script re-signed by any other certificate, even one
/// this machine trusts, is refused.</para>
/// </summary>
public static class ScriptSignatureVerifier
{
    private const string BeginMarker = "# SIG # Begin signature block";
    private const string EndMarker = "# SIG # End signature block";

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    // Never let a verification wait on the network (CRL/OCSP fetch): cache only, no revocation.
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private const uint WtdRevocationCheckNone = 0x00000010;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId, IntPtr pWvtData);

    public static ScriptSignatureResult Verify(string path, IReadOnlyCollection<string> allowedThumbprints)
    {
        if (!File.Exists(path))
        {
            return new ScriptSignatureResult(ScriptSignatureStatus.NotFound, null, $"File not found: {path}");
        }

        var thumbprint = TryGetSignerThumbprint(path);
        var trust = RunWinVerifyTrust(path);

        if (trust == TrustENoSignature || trust == TrustEProviderUnknown || trust == TrustESubjectFormUnknown || thumbprint is null && trust != 0)
        {
            return new ScriptSignatureResult(ScriptSignatureStatus.NotSigned, null, "The script carries no Authenticode signature.");
        }
        if (trust != 0)
        {
            return new ScriptSignatureResult(ScriptSignatureStatus.Invalid, thumbprint,
                $"The script's signature does not verify (WinVerifyTrust 0x{trust:X8}) - it was modified after signing, or its signing certificate is not trusted on this PC.");
        }
        if (thumbprint is null)
        {
            return new ScriptSignatureResult(ScriptSignatureStatus.Invalid, null, "The signature verified but its signer could not be read.");
        }
        if (!allowedThumbprints.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
        {
            return new ScriptSignatureResult(ScriptSignatureStatus.WrongSigner, thumbprint,
                $"The script is validly signed, but by an unexpected certificate ({thumbprint}).");
        }
        return new ScriptSignatureResult(ScriptSignatureStatus.Valid, thumbprint, "Signature valid and signer pinned.");
    }

    /// <summary>The thumbprint of the certificate that made the script's embedded signature, or null
    /// when there is no readable signature block. Reads the PKCS#7 blob PowerShell appends as
    /// commented base64; says nothing about whether the signature is actually valid.</summary>
    public static string? TryGetSignerThumbprint(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var begin = Array.FindLastIndex(lines, l => l.TrimEnd() == BeginMarker);
            if (begin < 0)
            {
                return null;
            }
            var base64 = new System.Text.StringBuilder();
            for (var i = begin + 1; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                if (line == EndMarker)
                {
                    break;
                }
                if (!line.StartsWith('#'))
                {
                    return null;
                }
                base64.Append(line.TrimStart('#').Trim());
            }
            var cms = new SignedCms();
            cms.Decode(Convert.FromBase64String(base64.ToString()));
            return cms.SignerInfos.Count == 0 ? null : cms.SignerInfos[0].Certificate?.Thumbprint;
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int RunWinVerifyTrust(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        var pData = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());
        var stateOpened = false;
        var data = default(WintrustData);
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = pFile,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone,
            };
            Marshal.StructureToPtr(data, pData, false);
            var result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, pData);
            stateOpened = true;
            return result;
        }
        finally
        {
            if (stateOpened)
            {
                // Release the provider state WinVerifyTrust allocated for the VERIFY call.
                var back = Marshal.PtrToStructure<WintrustData>(pData);
                back.dwStateAction = WtdStateActionClose;
                Marshal.StructureToPtr(back, pData, false);
                WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, pData);
            }
            Marshal.DestroyStructure<WintrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pData);
        }
    }
}
