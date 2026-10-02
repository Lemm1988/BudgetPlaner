using BudgetPlaner.Application.Abstractions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BudgetPlaner.Server.Hosting;

/// <summary>Basis-URL: konfigurierte <c>App:PublicBaseUrl</c> oder Schema+Host der aktuellen Anfrage.</summary>
public sealed class PublicUrlProvider(IHttpContextAccessor http, IConfiguration config) : IPublicUrlProvider
{
    public string BaseUrl
    {
        get
        {
            var configured = config["App:PublicBaseUrl"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
            var req = http.HttpContext?.Request;
            return req is null ? "http://localhost:5278" : $"{req.Scheme}://{req.Host}";
        }
    }
}

/// <summary>OpenAPI-Dokument: Titel, Beschreibung, JWT-Sicherheitsschema (F-130, DoD „vollständig dokumentiert“).</summary>
public sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "BudgetPlaner API",
            Version = "v1",
            Description = """
                REST-API des BudgetPlaners (Familienbudget nach Zero-Based-Budgeting).

                **Authentifizierung:** `POST /api/v1/auth/request` sendet Magic Link + Code per E-Mail, `POST /api/v1/auth/verify`
                liefert ein JWT. Für externe Nutzung kann im Profil ein persönlicher API-Zugang erstellt werden
                (`POST /api/v1/me/api-tokens`). Token als `Authorization: Bearer <token>` senden. Ohne Token → 401.

                **Versionierung:** alle Endpunkte unter `/api/v1/`.

                **Nebenläufigkeit:** Einzelressourcen liefern einen `ETag` (Version); `If-Match` bei PUT/PATCH/DELETE verhindert
                das Überschreiben fremder Änderungen (412).

                **Perioden:** Budgetmonate im Format `JJJJ-MM`; der Monatsstart-Tag des Haushalts bestimmt die Tagesgrenzen.
                """,
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT aus /api/v1/auth/verify oder persönlicher API-Zugang.",
        };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
        return Task.CompletedTask;
    }
}
