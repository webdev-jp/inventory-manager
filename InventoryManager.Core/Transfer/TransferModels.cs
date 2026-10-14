namespace InventoryManager.Core.Transfer;

/// <summary>Everything the Transfer User dialog collected for the AD-user-attribute side of the
/// change (Manager/OU/Description/Office) - Computer is handled separately by
/// ComputerIdentityHandoff since it also mutates local inventory rows, not just AD.</summary>
public sealed record TransferRequest(
    string SamAccountName,
    bool HasAdUser,
    string CurrentUserDistinguishedName,
    string TicketNumber,
    bool ManagerChanged,
    string? NewManagerDn,
    string? NewManagerDisplay,
    string CurrentManagerDisplay,
    bool OuChanged,
    string? NewOuDn,
    string CurrentOuDn,
    bool DescriptionChanged,
    string? NewDescription,
    string CurrentDescription,
    bool OfficeChanged,
    string? NewOffice,
    string CurrentOffice,
    bool ComputerChanged,
    string CurrentDeviceName,
    string NewDeviceName,
    string SuUsername);

public sealed record TransferFieldOutcome(string FieldName, bool Success, string Detail);

public sealed record TransferResult(IReadOnlyList<TransferFieldOutcome> Outcomes, bool NoteAppended, IReadOnlyList<string> ChangeSummaryLines)
{
    public bool AnyFailure => Outcomes.Any(o => !o.Success);
}
