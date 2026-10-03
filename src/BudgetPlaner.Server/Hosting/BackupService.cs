using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Server.Hosting;

/// <summary>
/// SQLite-Sicherungen per <c>VACUUM INTO</c> (konsistent im laufenden Betrieb) nach App_Data/backups,
/// Aufbewahrung App:BackupRetentionDays. Täglich durch den <see cref="MaintenanceWorker"/>, zusätzlich auf Knopfdruck
/// im Verwaltungsbereich. Die Dateien bleiben verschlüsselt – zum Wiederherstellen werden dieselben Schlüssel gebraucht.
/// </summary>
public sealed class BackupService(IServiceScopeFactory scopes, IOptions<AppOptions> options, IConfiguration config, IHostEnvironment env,
    ILogger<BackupService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public sealed record Info(int Count, DateTimeOffset? LastAt, long TotalBytes);

    public string Folder => Path.Combine(Path.GetFullPath(options.Value.DataDirectory, env.ContentRootPath), "backups");

    /// <summary>Nur für dateibasierte SQLite-Datenbanken (nicht für In-Memory-Tests).</summary>
    public bool Supported => config.GetConnectionString("Default") is not { } cs || cs.Contains(".db", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> CreateAsync(CancellationToken ct = default)
    {
        if (!Supported) return null;
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"budgetplaner-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            if (File.Exists(file)) return file;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                await db.Database.ExecuteSqlAsync($"VACUUM INTO {file}", ct);
            }
            var cutoff = DateTime.UtcNow.AddDays(-options.Value.BackupRetentionDays);
            foreach (var old in Files().Where(f => f.LastWriteTimeUtc < cutoff))
                old.Delete();
            logger.LogInformation("Backup erstellt: {File}", file);
            return file;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Info Describe()
    {
        var files = Files();
        return new Info(files.Count, files.Count == 0 ? null : new DateTimeOffset(files.Max(f => f.LastWriteTimeUtc), TimeSpan.Zero),
            files.Sum(f => f.Length));
    }

    private List<FileInfo> Files() =>
        Directory.Exists(Folder)
            ? new DirectoryInfo(Folder).GetFiles("budgetplaner-*.db").ToList()
            : [];
}
