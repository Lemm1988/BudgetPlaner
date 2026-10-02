using Microsoft.JSInterop;

namespace BudgetPlaner.Client.Services;

/// <summary>Zugriff auf js/app.js (Browser-Funktionen).</summary>
public sealed class BrowserJs(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/app.js").AsTask();

    public async ValueTask<T> Call<T>(string identifier, params object?[] args) => await (await Module).InvokeAsync<T>(identifier, args);

    public async ValueTask Run(string identifier, params object?[] args) => await (await Module).InvokeVoidAsync(identifier, args);

    public async ValueTask<string?> GetLocal(string key) => await Call<string?>("lsGet", key);
    public ValueTask SetLocal(string key, string value) => Run("lsSet", key, value);
    public ValueTask RemoveLocal(string key) => Run("lsRemove", key);

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true }) await _module.Result.DisposeAsync();
    }
}

public sealed record DbItem(string K, string V);

/// <summary>Zugriff auf js/db.js (IndexedDB, F-132).</summary>
public sealed class LocalDb(IJSRuntime js)
{
    public const string Entities = "entities";
    public const string Outbox = "outbox";
    public const string Meta = "meta";

    private Task<IJSObjectReference>? _module;
    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/db.js").AsTask();

    public async Task<List<DbItem>> GetAllAsync(string store) => await (await Module).InvokeAsync<List<DbItem>>("getAll", store);
    public async Task<string?> GetAsync(string store, string key) => await (await Module).InvokeAsync<string?>("get", store, key);
    public async Task PutAsync(string store, string key, string value) => await (await Module).InvokeVoidAsync("put", store, key, value);

    public async Task PutManyAsync(string store, IReadOnlyCollection<DbItem> items)
    {
        if (items.Count == 0) return;
        await (await Module).InvokeVoidAsync("putMany", store, items.Select(i => new { k = i.K, v = i.V }));
    }

    public async Task DeleteManyAsync(string store, IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return;
        await (await Module).InvokeVoidAsync("deleteMany", store, keys);
    }

    public async Task ClearAllAsync() => await (await Module).InvokeVoidAsync("clearAll");
}
