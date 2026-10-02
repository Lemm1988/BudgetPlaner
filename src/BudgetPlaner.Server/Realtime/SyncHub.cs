using System.Collections.Concurrent;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BudgetPlaner.Server.Realtime;

/// <summary>
/// Echtzeit-Kanal (F-131): meldet Änderungen („changed“ mit neuer Sequenz), Benachrichtigungen, Profiländerungen
/// und den Entzug des Zugriffs. Daten selbst werden über die REST-Sync-Endpunkte geholt.
/// </summary>
[Authorize]
public sealed class SyncHub(IAppDbContext db, HubGroups groups) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await JoinAsync();
        await base.OnConnectedAsync();
    }

    /// <summary>Gruppen neu bestimmen (z. B. nach Haushaltswechsel).</summary>
    public async Task JoinAsync()
    {
        if (Context.User?.UserId() is not { } userId) return;
        var householdId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (Guid?)u.HouseholdId).FirstOrDefaultAsync();
        if (householdId is null) return;
        if (groups.Households.TryGetValue(Context.ConnectionId, out var old) && old != householdId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Household(old));
        groups.Households[Context.ConnectionId] = householdId.Value;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Household(householdId.Value));
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.User(userId));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        groups.Households.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class HubGroups
{
    public ConcurrentDictionary<string, Guid> Households { get; } = new();
    public static string Household(Guid id) => $"hh:{id}";
    public static string User(Guid id) => $"user:{id}";
}

public sealed class SignalRRealtimeNotifier(IHubContext<SyncHub> hub) : IRealtimeNotifier
{
    public Task HouseholdChangedAsync(Guid householdId, long seq) => hub.Clients.Group(HubGroups.Household(householdId)).SendAsync("changed", seq);
    public Task NotificationAsync(Guid userId, NotificationDto notification) => hub.Clients.Group(HubGroups.User(userId)).SendAsync("notification", notification);
    public Task AccessRevokedAsync(Guid userId) => hub.Clients.Group(HubGroups.User(userId)).SendAsync("revoked");
    public Task ProfileChangedAsync(Guid userId) => hub.Clients.Group(HubGroups.User(userId)).SendAsync("profile");
}
