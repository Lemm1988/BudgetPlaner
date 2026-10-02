using System.Text.Json;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Shared.Localization;
using Microsoft.AspNetCore.Diagnostics;

namespace BudgetPlaner.Server.Hosting;

/// <summary>Fachliche Fehler → RFC-7807-ProblemDetails mit übersetzter Meldung und Schlüssel („key“).</summary>
public static class ErrorHandling
{
    public static void UseAppErrors(this WebApplication app)
    {
        app.UseExceptionHandler(builder => builder.Run(async http =>
        {
            var ex = http.Features.Get<IExceptionHandlerFeature>()?.Error;
            var lang = Language(http);
            int status;
            string key;
            object?[] args = [];
            switch (ex)
            {
                case AppException app:
                    status = app.StatusCode;
                    key = app.ErrorKey;
                    args = app.Args;
                    if (app.RetryAfterSeconds is { } retry) http.Response.Headers.RetryAfter = retry.ToString();
                    break;
                case BadHttpRequestException or JsonException or FormatException:
                    status = 400;
                    key = "err.validation";
                    break;
                default:
                    status = 500;
                    key = "err.generic";
                    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BudgetPlaner").LogError(ex, "Unbehandelter Fehler");
                    break;
            }
            var message = Texts.Get(lang, key, args);
            await Results.Problem(title: message, detail: message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["key"] = key, ["args"] = args.Select(a => a?.ToString()).ToArray() }).ExecuteAsync(http);
        }));
    }

    public static string Language(HttpContext http) =>
        http.RequestServices.GetService<RequestUser>()?.Optional?.Language
        ?? Texts.Normalize(http.Request.Headers.AcceptLanguage.ToString());

    /// <summary>Sicherheits-Header (NFR Sicherheit). Blazor WebAssembly benötigt 'wasm-unsafe-eval'.</summary>
    public static void UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var h = http.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h.XFrameOptions = "DENY";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), publickey-credentials-get=(self), publickey-credentials-create=(self)";
            if (!http.Request.Path.StartsWithSegments("/api/docs"))
            {
                // SignalR-WebSocket nur zum eigenen Host (ältere Safari-Versionen decken ws/wss nicht über 'self' ab).
                var socket = $"wss://{http.Request.Host.Value} ws://{http.Request.Host.Value}";
                h.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; " +
                                          $"style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self' {socket}; " +
                                          "worker-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            }
            await next();
        });
    }
}
