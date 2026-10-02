using BudgetPlaner.Client.Services;
using BudgetPlaner.Domain;
using Microsoft.AspNetCore.Components;

namespace BudgetPlaner.Client.Shared;

/// <summary>Basis für Seiten und Komponenten: aktualisiert sich bei Daten-, Sprach- und UI-Änderungen.</summary>
public abstract class AppComponent : ComponentBase, IDisposable
{
    [Inject] protected DataStore Store { get; set; } = default!;
    [Inject] protected Loc L { get; set; } = default!;
    [Inject] protected UiState Ui { get; set; } = default!;
    [Inject] protected Actions Act { get; set; } = default!;
    [Inject] protected ToastService Toasts { get; set; } = default!;
    [Inject] protected ConfirmService Confirm { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected AppSession Session { get; set; } = default!;
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected SyncClient Sync { get; set; } = default!;

    protected HouseholdEngine Engine => Store.Engine;
    protected HouseholdIndex Ix => Store.Data.Index;
    protected Period Current => Store.Engine.CurrentPeriod;
    protected Period Selected => Ui.SelectedPeriod(Store);
    protected bool IsOwner => Session.IsOwner;

    protected override void OnInitialized()
    {
        Store.Changed += Refresh;
        L.Changed += Refresh;
        Ui.Changed += Refresh;
    }

    protected virtual void OnDataChanged() { }

    private void Refresh()
    {
        OnDataChanged();
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Fehler einheitlich melden (Offline-Hinweis bei fehlender Verbindung).</summary>
    protected void ShowError(Exception ex)
    {
        Toasts.Error(ex switch
        {
            OfflineException => L["error.offline"],
            ApiException api => api.Message,
            _ => L["err.generic"],
        });
    }

    public virtual void Dispose()
    {
        Store.Changed -= Refresh;
        L.Changed -= Refresh;
        Ui.Changed -= Refresh;
        GC.SuppressFinalize(this);
    }
}
