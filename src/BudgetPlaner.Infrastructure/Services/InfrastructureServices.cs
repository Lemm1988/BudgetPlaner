using System.Text.Json;
using System.Threading.Channels;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Infrastructure.Security;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BudgetPlaner.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly TodayIn(string? timeZone) => DateOnly.FromDateTime(NowIn(timeZone).DateTime);

    public DateTimeOffset NowIn(string? timeZone)
    {
        try { return TimeZoneInfo.ConvertTime(UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "Europe/Berlin")); }
        catch (Exception) { return UtcNow; }
    }
}

/// <summary>Mail-Warteschlange; ein Hintergrunddienst versendet sofort (F-001: innerhalb 60 Sekunden).</summary>
public sealed class EmailQueue : IEmailQueue
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateUnbounded<EmailMessage>();
    public ChannelReader<EmailMessage> Reader => _channel.Reader;
    public void Enqueue(EmailMessage message) => _channel.Writer.TryWrite(message);
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "noreply@budgetplaner.local";
    public string FromName { get; set; } = "BudgetPlaner";

    /// <summary>„StartTls“ (Standard), „SslOnConnect“ oder „None“.</summary>
    public string Security { get; set; } = "StartTls";
}

/// <summary>Versand per SMTP (MailKit), zusätzlich Ablage im Entwicklungs-Postfach.</summary>
public sealed class EmailSender(IOptions<SmtpOptions> smtp, IOptions<AppOptions> app, IServiceScopeFactory scopes, ILogger<EmailSender> logger) : IEmailSender
{
    public bool IsSmtpConfigured => !string.IsNullOrWhiteSpace(smtp.Value.Host);

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (app.Value.DevMailbox)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.DevMails.Add(new DevMail { Id = Guid.NewGuid(), To = message.To, Subject = message.Subject, Html = message.Html, Text = message.Text, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Dev-Postfach: Mail an {To} – {Subject}", message.To, message.Subject);
        }
        if (!IsSmtpConfigured) return;

        var o = smtp.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();
        using var client = new SmtpClient();
        var security = o.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(o.Host!, o.Port, security, ct);
        if (!string.IsNullOrEmpty(o.User)) await client.AuthenticateAsync(o.User, o.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Web-Push mit VAPID (F-080/F-081 als Push-Mitteilung).</summary>
public sealed class WebPushSender : IPushSender
{
    private readonly PushServiceClient _client;

    public WebPushSender(KeyStore keys, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        PublicKey = keys.VapidPublicKey;
        _client = new PushServiceClient(httpClientFactory.CreateClient("webpush"))
        {
            DefaultAuthentication = new VapidAuthentication(keys.VapidPublicKey, keys.VapidPrivateKey)
            {
                Subject = config["Push:Subject"] ?? "mailto:admin@budgetplaner.local",
            },
        };
    }

    public bool IsConfigured => true;
    public string? PublicKey { get; }

    public async Task<bool> SendAsync(PushSubscriptionEntity subscription, PushPayload payload, CancellationToken ct = default)
    {
        var sub = new PushSubscription { Endpoint = subscription.Endpoint };
        sub.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        sub.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
        var json = JsonSerializer.Serialize(new { title = payload.Title, body = payload.Body, url = payload.Url ?? "/", tag = payload.Tag });
        var message = new PushMessage(json) { TimeToLive = 86400, Urgency = PushMessageUrgency.Normal };
        try
        {
            await _client.RequestPushMessageDeliveryAsync(sub, message, ct);
            return true;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return false;
        }
    }
}
