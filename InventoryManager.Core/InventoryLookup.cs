using InventoryManager.Core.Models;

namespace InventoryManager.Core;

/// <summary>Small local (non-AD) lookup helpers used by the Active Directory Sync dashboard: port
/// of Get-InventoryMatchesForName and the local half of Get-AdComputersNotInInventory.</summary>
public static class InventoryLookup
{
    public static IReadOnlyList<InventoryItem> FindByName(IEnumerable<InventoryItem> items, string lastName, string firstName)
    {
        var ln = lastName.Trim();
        var fn = firstName.Trim();
        if (string.IsNullOrWhiteSpace(ln) || string.IsNullOrWhiteSpace(fn))
        {
            return [];
        }
        return items
            .Where(i => string.Equals(i.LastName.Trim(), ln, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(i.FirstName.Trim(), fn, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static IReadOnlyList<string> FindComputersNotInInventory(IEnumerable<InventoryItem> items, IEnumerable<string> adComputerNames)
    {
        var inventoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var name = item.DeviceName.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                inventoryNames.Add(name);
            }
        }
        return adComputerNames
            .Where(n => !inventoryNames.Contains(n.Trim()))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
