using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Localization;

namespace BudgetPlaner.Client.Services;

/// <summary>Fehler der API mit übersetzter Meldung.</summary>
public sealed class ApiException(int status, string? key, string message, int? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Key { get; } = key;
    public int? RetryAfterSeconds { get; } = retryAfter;
}

/// <summary>Keine Verbindung zum Server.</summary>
public sealed class OfflineException() : Exception("offline");

/// <summary>Typisierter Zugriff auf /api/v1 mit automatischer Token-Erneuerung.</summary>
public sealed class ApiClient(HttpClient http, AppSession session)
{
    public string Language { get; set; } = Texts.German;

    public Task<T> GetAsync<T>(string url, bool auth = true) => SendAsync<T>(HttpMethod.Get, url, null, auth);
    public Task<T> PostAsync<T>(string url, object? body, bool auth = true) => SendAsync<T>(HttpMethod.Post, url, body, auth);
    public Task<T> PutAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Put, url, body);
    public Task<T> PatchAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Patch, url, body);

    public async Task PostAsync(string url, object? body = null, bool auth = true) => (await SendRawAsync(HttpMethod.Post, url, body, auth)).Dispose();
    public async Task PutAsync(string url, object? body) => (await SendRawAsync(HttpMethod.Put, url, body)).Dispose();
    public async Task DeleteAsync(string url) => (await SendRawAsync(HttpMethod.Delete, url, null)).Dispose();

    public async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, bool auth = true)
    {
        using var res = await SendRawAsync(method, url, body, auth);
        return (await res.Content.ReadFromJsonAsync<T>(JsonDefaults.Options))!;
    }

    public async Task<T> UploadAsync<T>(string url, Stream file, string fileName)
    {
        var res = await SendWithRetryAsync(() =>
        {
            var content = new MultipartFormDataContent();
            var stream = new StreamContent(file);
            stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(stream, "file", fileName);
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        }, auth: true, allowRetry: false);
        using (res) return (await res.Content.ReadFromJsonAsync<T>(JsonDefaults.Options))!;
    }

    public Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object? body, bool auth = true) =>
        SendWithRetryAsync(() => new HttpRequestMessage(method, url)
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: JsonDefaults.Options),
        }, auth, allowRetry: true);

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, bool auth, bool allowRetry)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = factory();
            req.Headers.AcceptLanguage.ParseAdd(Language);
            if (auth)
            {
                var token = await session.GetAccessTokenAsync(forceRefresh: attempt > 0);
                if (token is null) throw new ApiException(401, "err.auth.required", Texts.Get(Language, "err.auth.required"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            HttpResponseMessage res;
            try { res = await http.SendAsync(req); }
            catch (HttpRequestException) { throw new OfflineException(); }
            catch (TaskCanceledException) { throw new OfflineException(); }
            if (res.StatusCode == HttpStatusCode.Unauthorized && auth && attempt == 0 && allowRetry)
            {
                res.Dispose();
                continue;
            }
            if (res.IsSuccessStatusCode) return res;
            throw await ToExceptionAsync(res);
        }
    }

    private async Task<ApiException> ToExceptionAsync(HttpResponseMessage res)
    {
        string? key = null;
        var message = Texts.Get(Language, "err.generic");
        try
        {
            var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (problem.TryGetProperty("key", out var k)) key = k.GetString();
            if (problem.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } title) message = title;
        }
        catch (Exception) { /* kein JSON */ }
        if (res.StatusCode == HttpStatusCode.Unauthorized && key is "err.auth.sessionRevoked" or "err.auth.required" or "err.account.blocked")
            await session.SignOutAsync(expired: true);
        int? retry = res.Headers.RetryAfter?.Delta is { } d ? (int)d.TotalSeconds : null;
        res.Dispose();
        return new ApiException((int)res.StatusCode, key, message, retry);
    }
}
