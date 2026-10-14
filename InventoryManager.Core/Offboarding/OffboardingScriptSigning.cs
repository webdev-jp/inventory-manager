namespace InventoryManager.Core.Offboarding;

/// <summary>
/// The certificate(s) allowed to have signed Offboarding2026.ps1. This is the "Contoso IT Scripts"
/// self-signed code-signing cert - the same one that signs the ClickOnce manifest (see
/// ClickOnce.pubxml's ManifestCertificateThumbprint). It expires 2027-09-15: when it is renewed,
/// ADD the new thumbprint here before re-signing the script (keep the old one until every admin has
/// the new build), or the app will refuse to launch the tool.
/// </summary>
public static class OffboardingScriptSigning
{
    public static readonly IReadOnlyList<string> AllowedSignerThumbprints =
    [
        "0000000000000000000000000000000000000000",
    ];
}
