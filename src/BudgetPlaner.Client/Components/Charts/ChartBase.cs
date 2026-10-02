using BudgetPlaner.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BudgetPlaner.Client.Components.Charts;

/// <summary>Gemeinsame Basis: rendert in echter Pixelbreite (lesbare Achsen auf allen Geräten).</summary>
public abstract class ChartBase : ComponentBase, IAsyncDisposable
{
    private DotNetObjectReference<ChartBase>? _ref;

    [Inject] protected BrowserJs Browser { get; set; } = default!;
    [Inject] protected Loc L { get; set; } = default!;

    [Parameter] public string Title { get; set; } = "";
    [Parameter] public int Height { get; set; } = 240;

    protected ElementReference Container;
    protected int Width { get; private set; } = 600;
    protected readonly string Uid = "c" + Guid.NewGuid().ToString("N")[..8];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _ref = DotNetObjectReference.Create(this);
        var w = await Browser.Call<int>("observeWidth", Container, _ref);
        if (w > 0 && w != Width)
        {
            Width = w;
            StateHasChanged();
        }
    }

    [JSInvokable]
    public void OnWidth(int width)
    {
        if (width <= 0 || width == Width) return;
        Width = width;
        StateHasChanged();
    }

    protected static string F(double v) => ChartMath.F(v);

    /// <summary>Rechteck mit 4px gerundetem Datenende und eckiger Basis.</summary>
    protected static string BarPath(double x, double y, double w, double h, bool up, double r = 4)
    {
        if (h <= 0 || w <= 0) return "";
        r = Math.Min(r, Math.Min(w / 2, h));
        if (up)
            return $"M{F(x)},{F(y + h)} L{F(x)},{F(y + r)} Q{F(x)},{F(y)} {F(x + r)},{F(y)} L{F(x + w - r)},{F(y)} Q{F(x + w)},{F(y)} {F(x + w)},{F(y + r)} L{F(x + w)},{F(y + h)} Z";
        return $"M{F(x)},{F(y)} L{F(x + w)},{F(y)} L{F(x + w)},{F(y + h - r)} Q{F(x + w)},{F(y + h)} {F(x + w - r)},{F(y + h)} L{F(x + r)},{F(y + h)} Q{F(x)},{F(y + h)} {F(x)},{F(y + h - r)} Z";
    }

    public async ValueTask DisposeAsync()
    {
        try { await Browser.Run("unobserveWidth", Container); }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
        _ref?.Dispose();
        GC.SuppressFinalize(this);
    }
}
