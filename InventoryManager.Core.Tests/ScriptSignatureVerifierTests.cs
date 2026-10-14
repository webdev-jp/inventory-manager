using InventoryManager.Core.Offboarding;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Tests;

public class ScriptSignatureVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sigtest_{Guid.NewGuid():N}");

    public ScriptSignatureVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string RealScriptPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Offboarding2026.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Offboarding2026.ps1 not found above the test output folder.");
    }

    [Fact]
    public void MissingFile_IsNotFound()
    {
        var result = ScriptSignatureVerifier.Verify(Path.Combine(_dir, "nope.ps1"), OffboardingScriptSigning.AllowedSignerThumbprints);
        Assert.Equal(ScriptSignatureStatus.NotFound, result.Status);
    }

    [Fact]
    public void UnsignedScript_IsNotSigned_AndNeverValid()
    {
        var path = Path.Combine(_dir, "unsigned.ps1");
        File.WriteAllText(path, "Write-Host 'hello'\r\n");

        var result = ScriptSignatureVerifier.Verify(path, OffboardingScriptSigning.AllowedSignerThumbprints);

        Assert.Equal(ScriptSignatureStatus.NotSigned, result.Status);
        Assert.False(result.IsValid);
    }

    [Fact(Skip = "Needs the original signed script; the published copy is unsigned.")]
    public void ShippedScript_SignerIsThePinnedCertificate()
    {
        // Machine-independent: reads the embedded signature only, so it holds on a PC that doesn't
        // trust the cert too. Guards against the script being re-signed without updating the pin.
        var signer = ScriptSignatureVerifier.TryGetSignerThumbprint(RealScriptPath());

        Assert.NotNull(signer);
        Assert.Contains(signer!, OffboardingScriptSigning.AllowedSignerThumbprints, StringComparer.OrdinalIgnoreCase);
    }

    [Fact(Skip = "Needs the original signed script; the published copy is unsigned.")]
    public void ShippedScript_ModifiedAfterSigning_IsRefused()
    {
        var tampered = Path.Combine(_dir, "tampered.ps1");
        File.WriteAllText(tampered, File.ReadAllText(RealScriptPath()).Replace("Write-GuiLog", "Write-GuiLoG", StringComparison.Ordinal));

        var result = ScriptSignatureVerifier.Verify(tampered, OffboardingScriptSigning.AllowedSignerThumbprints);

        Assert.False(result.IsValid);
        Assert.Equal(ScriptSignatureStatus.Invalid, result.Status);
    }

    [Fact(Skip = "Needs the original signed script; the published copy is unsigned.")]
    public void ShippedScript_SignerNotOnAllowList_IsRefused()
    {
        var result = ScriptSignatureVerifier.Verify(RealScriptPath(), ["0000000000000000000000000000000000000000"]);

        Assert.False(result.IsValid);
    }
}
