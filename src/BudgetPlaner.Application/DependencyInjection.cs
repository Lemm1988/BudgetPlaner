using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services;
using BudgetPlaner.Application.Services.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BudgetPlaner.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<HouseholdLocks>();
        services.AddSingleton<HouseholdReadCache>();
        services.AddSingleton<NotificationTrigger>();
        services.AddScoped<HouseholdDataLoader>();
        services.AddScoped<SyncService>();
        services.AddScoped<AuthService>();
        services.AddScoped<HouseholdService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationEvaluator>();
        services.AddScoped<MonthService>();
        services.AddScoped<DemoService>();
        services.AddScoped<ChangeLogService>();
        services.AddScoped<AccountService>();
        services.AddScoped<RegistrationPolicy>();
        services.AddScoped<AdminService>();
        services.AddScoped<ExportService>();
        services.AddScoped<ImportService>();
        return services;
    }
}
