using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Infrastructure.Persistence;
using BudgetPlaner.Infrastructure.Security;
using BudgetPlaner.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BudgetPlaner.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registriert Datenbank, Verschlüsselung, Mail, Push, Passkeys und Tabellen-Ein-/Ausgabe.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddSingleton(sp => new KeyStore(config, dataDirectory, sp.GetService<ILogger<KeyStore>>()));
        services.AddSingleton<IDataProtector, AesDataProtector>();

        var connection = config.GetConnectionString("Default") ?? $"Data Source={Path.Combine(dataDirectory, "budgetplaner.db")}";
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.Configure<SmtpOptions>(config.GetSection("Smtp"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<EmailQueue>();
        services.AddSingleton<IEmailQueue>(sp => sp.GetRequiredService<EmailQueue>());
        services.AddSingleton<IEmailSender, EmailSender>();
        services.AddHttpClient("webpush");
        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddScoped<IPasskeyService, PasskeyService>();
        services.AddSingleton<ISpreadsheetIO, SpreadsheetIO>();
        return services;
    }
}
