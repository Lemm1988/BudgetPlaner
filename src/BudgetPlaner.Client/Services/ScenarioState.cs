using BudgetPlaner.Domain.Scenarios;

namespace BudgetPlaner.Client.Services;

/// <summary>Aktives „Was-wäre-wenn“-Szenario (F-075). Lebt nur im Speicher und berührt die echten Daten nie.</summary>
public sealed class ScenarioState
{
    public Scenario Scenario { get; private set; } = new();
    public bool IsActive => Scenario.Changes.Count > 0;
    public event Action? Changed;

    public void Add(ScenarioChange change)
    {
        Scenario.Changes.Add(change);
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        Scenario.Changes.RemoveAll(c => c.Id == id);
        Changed?.Invoke();
    }

    /// <summary>„Verwerfen“: zurück auf exakt den Ausgangszustand.</summary>
    public void Clear()
    {
        Scenario = new Scenario();
        Changed?.Invoke();
    }
}
