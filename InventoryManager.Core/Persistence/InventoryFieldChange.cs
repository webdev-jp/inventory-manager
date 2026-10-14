namespace InventoryManager.Core.Persistence;

/// <summary>
/// One field-level change detected by InventoryStore.Save() - the raw material for the "meticulous"
/// per-field audit trail Francois asked for on 2026-09-15 (2 people, 1 SU account, both know exactly
/// what changed on any record and by whom). Deliberately only ever produced for an item that
/// already existed in the prior snapshot - a brand-new item (Add Item, CSV import, Onboarding) isn't
/// a "modification," so it's never diffed field-by-field here.
/// </summary>
/// <param name="FieldKey">The FieldDefinition.Key this change came from (e.g. "Description"), or
/// the literal "Status" for the synthetic Status pseudo-field - lets a caller identify a
/// specific field (e.g. to auto-push a Description change to AD) without a fragile Label-based
/// reverse lookup.</param>
public sealed record InventoryFieldChange(
    int ItemId,
    string DeviceName,
    string Username,
    string FieldKey,
    string FieldLabel,
    string OldValue,
    string NewValue);
