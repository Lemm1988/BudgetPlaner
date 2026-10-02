using System.Security.Cryptography;
using System.Text;

namespace BudgetPlaner.Domain;

/// <summary>
/// Namensbasierte GUIDs (UUID v5-Verfahren). Datensätze mit natürlichem Schlüssel – z. B. Monatswerte einer Position –
/// erhalten auf allen Geräten dieselbe Id; gleichzeitige Offline-Anlage führt so zu einem Merge statt zu Duplikaten (F-131).
/// </summary>
public static class DeterministicId
{
    public static Guid Create(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray(true);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, input, 0, nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, nsBytes.Length, nameBytes.Length);
        var hash = SHA1.HashData(input);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, true);
    }

    /// <summary>Id der Monatsdaten einer Position.</summary>
    public static Guid ForEntryMonth(Guid entryId, Period period) => Create(entryId, $"month:{period}");

    /// <summary>Id einer Betragsänderung „ab Monat“.</summary>
    public static Guid ForEntryChange(Guid entryId, Period from) => Create(entryId, $"change:{from}");

    /// <summary>Id eines Monatsabschlusses.</summary>
    public static Guid ForSnapshot(Guid householdId, Period period) => Create(householdId, $"snapshot:{period}");
}
