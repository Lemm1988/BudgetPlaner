using System.Collections.Concurrent;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BudgetPlaner.Application.Services;

/// <summary>Lädt alle Daten eines Haushalts (ohne Change-Tracking) für Berechnungen und Synchronisation.</summary>
public sealed class HouseholdDataLoader(IAppDbContext db, HouseholdReadCache cache)
{
    public async Task<HouseholdData> LoadAsync(Guid householdId, bool includeDeleted, CancellationToken ct = default)
    {
        var household = await db.Households.AsNoTracking().FirstAsync(h => h.Id == householdId, ct);
        var data = new HouseholdData
        {
            Household = household,
            Categories = await Q(db.Categories).ToListAsync(ct),
            Entries = await Q(db.Entries).ToListAsync(ct),
            EntryChanges = await Q(db.EntryChanges).ToListAsync(ct),
            EntryMonths = await Q(db.EntryMonths).ToListAsync(ct),
            Debts = await Q(db.Debts).ToListAsync(ct),
            DebtPayments = await Q(db.DebtPayments).ToListAsync(ct),
            Buffers = await Q(db.Buffers).ToListAsync(ct),
            BufferTransactions = await Q(db.BufferTransactions).ToListAsync(ct),
            Snapshots = await Q(db.Snapshots).ToListAsync(ct),
            Checkpoints = await Q(db.Checkpoints).ToListAsync(ct),
        };
        data.Members = await db.Users.AsNoTracking()
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.JoinedAt)
            .Select(u => new Member { UserId = u.Id, Name = u.Name, Email = u.Email, Role = u.Role, JoinedAt = u.JoinedAt })
            .ToListAsync(ct);
        return data;

        IQueryable<T> Q<T>(DbSet<T> set) where T : HouseholdEntity =>
            includeDeleted
                ? set.AsNoTracking().Where(e => e.HouseholdId == householdId)
                : set.AsNoTracking().Where(e => e.HouseholdId == householdId && !e.IsDeleted);
    }

    /// <summary>Aktive Daten zum Lesen; wiederverwendet, solange sich die Änderungssequenz nicht geändert hat.</summary>
    public async Task<HouseholdData> LoadForReadAsync(Guid householdId, CancellationToken ct = default)
    {
        var seq = await db.Households.AsNoTracking().Where(h => h.Id == householdId).Select(h => h.ChangeSeq).FirstAsync(ct);
        var memberStamp = await db.Users.AsNoTracking().Where(u => u.HouseholdId == householdId).CountAsync(ct);
        if (cache.TryGet(householdId, seq, memberStamp, out var cached)) return cached;
        var data = await LoadAsync(householdId, includeDeleted: false, ct);
        cache.Set(householdId, seq, memberStamp, data);
        return data;
    }
}

/// <summary>Lesecache je Haushalt (unveränderliche Kopien, Schlüssel = Änderungssequenz).</summary>
public sealed class HouseholdReadCache
{
    private readonly ConcurrentDictionary<Guid, (long Seq, int Members, HouseholdData Data)> _cache = new();

    public bool TryGet(Guid householdId, long seq, int members, out HouseholdData data)
    {
        if (_cache.TryGetValue(householdId, out var entry) && entry.Seq == seq && entry.Members == members)
        {
            data = entry.Data;
            return true;
        }
        data = null!;
        return false;
    }

    public void Set(Guid householdId, long seq, int members, HouseholdData data) => _cache[householdId] = (seq, members, data);

    public void Invalidate(Guid householdId) => _cache.TryRemove(householdId, out _);
}
