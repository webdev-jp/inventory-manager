using InventoryManager.Core.Models;

namespace InventoryManager.Core;

public sealed record PersonDevice(string DeviceName, string StatusKey, string StatusLabel);

/// <summary>One person as seen across the whole inventory: who they are and every real device
/// recorded against them. A person with no real device is in Limbo.</summary>
public sealed record PersonSummary(
    string Username,
    string FirstName,
    string LastName,
    IReadOnlyList<PersonDevice> Devices,
    string CellPhone,
    string CellPhoneModel)
{
    /// <summary>The user exists in the inventory but has no real device (only a "No PC" placeholder).</summary>
    public bool InLimbo => Devices.Count == 0;
}

/// <summary>
/// The "People" view: the inventory regrouped by person instead of by device, without changing how
/// anything is stored. Rows are grouped by Username; a row with only a name joins the username group
/// that has the same Last + First Name, else stands as its own person. "No PC" placeholder rows give
/// a person a presence but are not devices.
/// </summary>
public static class PeopleView
{
    private sealed class Group
    {
        public string Username = "";
        public string FirstName = "";
        public string LastName = "";
        public string CellPhone = "";
        public string CellPhoneModel = "";
        public readonly List<PersonDevice> Devices = [];

        public void Absorb(InventoryItem item)
        {
            if (Username.Length == 0) { Username = item.Username.Trim(); }
            if (FirstName.Length == 0) { FirstName = item.FirstName.Trim(); }
            if (LastName.Length == 0) { LastName = item.LastName.Trim(); }
            if (CellPhone.Length == 0) { CellPhone = item["CellPhone"].Trim(); }
            if (CellPhoneModel.Length == 0) { CellPhoneModel = item["DeviceModel"].Trim(); }
            if (!InventorySchema.IsNoDevice(item.DeviceName))
            {
                var name = item.DeviceName.Trim();
                if (!Devices.Any(d => string.Equals(d.DeviceName, name, StringComparison.OrdinalIgnoreCase) && d.StatusKey == item.Status))
                {
                    var label = InventorySchema.Statuses.FirstOrDefault(s => s.Key == item.Status)?.Label ?? item.Status;
                    Devices.Add(new PersonDevice(name, item.Status, label));
                }
            }
        }
    }

    private static string NameKey(InventoryItem item) =>
        $"{item.LastName.Trim()}|{item.FirstName.Trim()}".ToLowerInvariant();

    public static IReadOnlyList<PersonSummary> Build(IEnumerable<InventoryItem> items)
    {
        var rows = items.Where(UserHasSomeone).ToList();
        var groups = new Dictionary<string, Group>();
        var byName = new Dictionary<string, Group>();

        foreach (var item in rows.Where(i => i.Username.Trim().Length > 0))
        {
            var key = "u:" + item.Username.Trim().ToLowerInvariant();
            if (!groups.TryGetValue(key, out var group))
            {
                group = new Group();
                groups[key] = group;
            }
            group.Absorb(item);
        }
        foreach (var group in groups.Values)
        {
            var nameKey = $"{group.LastName}|{group.FirstName}".ToLowerInvariant();
            if (nameKey != "|")
            {
                byName.TryAdd(nameKey, group);
            }
        }

        foreach (var item in rows.Where(i => i.Username.Trim().Length == 0))
        {
            var nameKey = NameKey(item);
            if (!byName.TryGetValue(nameKey, out var group))
            {
                group = new Group();
                groups["n:" + nameKey + "#" + groups.Count] = group;
                byName[nameKey] = group;
            }
            group.Absorb(item);
        }

        return groups.Values
            .Select(g => new PersonSummary(g.Username, g.FirstName, g.LastName, g.Devices, g.CellPhone, g.CellPhoneModel))
            .OrderBy(p => p.LastName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.FirstName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool UserHasSomeone(InventoryItem item) =>
        !string.IsNullOrWhiteSpace(item.Username)
        || !string.IsNullOrWhiteSpace(item.FirstName)
        || !string.IsNullOrWhiteSpace(item.LastName);
}
