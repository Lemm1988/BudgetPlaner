using BudgetPlaner.Client;
using BudgetPlaner.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserJs>();
builder.Services.AddScoped<LocalDb>();
builder.Services.AddScoped<AppSession>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<DataStore>();
builder.Services.AddScoped<Loc>();
builder.Services.AddScoped<UiState>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<ConfirmService>();
builder.Services.AddScoped<NotificationCenter>();
builder.Services.AddScoped<SyncClient>();
builder.Services.AddScoped<Actions>();
builder.Services.AddScoped<ScenarioState>();
builder.Services.AddScoped<AppStartup>();

var host = builder.Build();
await host.Services.GetRequiredService<AppStartup>().InitializeAsync();
await host.RunAsync();
