namespace InventoryManager.Core.Ad;

/// <summary>
/// Direct port of the PowerShell build's Add-AdUserNoteLine: appends one timestamped entry to
/// the AD user's "info" attribute (the Notes box under ADUC's Telephones tab) rather than
/// overwriting it, so onboarding/transfer/offboarding events accumulate into a running history on
/// the account itself. "info" is schema-capped at 1024 Unicode characters - if the running
/// history would exceed a safe margin below that, the OLDEST entries are dropped first so the
/// newest entry (the one this call is actually here to add) is never the one that gets cut.
/// </summary>
public static class AdNoteAppender
{
    private const string Divider = "----------";
    private const int MaxLength = 1000;

    public static void Append(IAdService adService, string identity, string actionText)
    {
        var newEntry = AdNoteFormatter.FormatEntry(actionText);
        var existing = adService.GetUser(identity)?.Info ?? "";
        adService.SetUserInfo(identity, Combine(existing, newEntry));
    }

    /// <summary>Exposed separately from <see cref="Append"/> so the merge/truncation logic - the
    /// highest-risk part of this class - can be unit-tested without a fake AD round-trip.</summary>
    public static string Combine(string existing, string newEntry)
    {
        var combined = string.IsNullOrWhiteSpace(existing) ? newEntry : $"{existing}\r\n{Divider}\r\n{newEntry}";
        if (combined.Length <= MaxLength)
        {
            return combined;
        }

        var entries = combined.Split($"\r\n{Divider}\r\n").ToList();
        while (entries.Count > 1 && string.Join($"\r\n{Divider}\r\n", entries).Length > MaxLength)
        {
            entries.RemoveAt(0);
        }
        combined = string.Join($"\r\n{Divider}\r\n", entries);

        if (combined.Length > MaxLength)
        {
            // Even the single newest entry alone doesn't fit (pathological) - keep the tail end
            // of it rather than fail the write entirely.
            combined = combined[^MaxLength..];
        }
        return combined;
    }
}
