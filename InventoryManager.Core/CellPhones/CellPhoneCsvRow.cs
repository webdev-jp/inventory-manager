namespace InventoryManager.Core.CellPhones;

/// <summary>One row from a carrier "device report" CSV (e.g. Verizon/AT&amp;T wireless account
/// export) - just the three columns this feature actually needs, by header name rather than fixed
/// column position, since a real carrier export like this carries 20+ columns whose order isn't
/// guaranteed report to report.</summary>
public sealed record CellPhoneCsvRow(string RawUserName, string WirelessNumber, string DeviceModel);
