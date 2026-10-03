using System.Net;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Shared.Localization;

namespace BudgetPlaner.Application.Services;

/// <summary>Schlichte, barrierearme HTML-Mails mit Textalternative.</summary>
public static class EmailTemplates
{
    private const string Green = "#1F5F4A";

    public static EmailMessage Login(string lang, string to, string link, string code)
    {
        var html = Layout(lang, Texts.Get(lang, "mail.login.preheader", code), $"""
            <p>{E(Texts.Get(lang, "mail.login.intro"))}</p>
            {Button(link, Texts.Get(lang, "mail.login.button"))}
            <p>{E(Texts.Get(lang, "mail.login.code"))}</p>
            <p style="font-size:28px;letter-spacing:6px;font-weight:700;margin:8px 0 16px">{E(code)}</p>
            <p style="color:#555">{E(Texts.Get(lang, "mail.login.validity"))}</p>
            <p style="color:#555">{E(Texts.Get(lang, "mail.login.ignore"))}</p>
            """);
        var text = $"{Texts.Get(lang, "mail.login.intro")}\n{link}\n\n{Texts.Get(lang, "mail.login.code")} {code}\n\n{Texts.Get(lang, "mail.login.validity")}\n{Texts.Get(lang, "mail.login.ignore")}";
        return new EmailMessage(to, Texts.Get(lang, "mail.login.subject"), html, text);
    }

    public static EmailMessage Invitation(string lang, string to, string inviter, string household, string link)
    {
        var html = Layout(lang, Texts.Get(lang, "mail.invite.intro", inviter, household), $"""
            <p>{E(Texts.Get(lang, "mail.invite.intro", inviter, household))}</p>
            {Button(link, Texts.Get(lang, "mail.invite.button"))}
            <p style="color:#555">{E(Texts.Get(lang, "mail.invite.validity"))}</p>
            """);
        var text = $"{Texts.Get(lang, "mail.invite.intro", inviter, household)}\n{link}\n\n{Texts.Get(lang, "mail.invite.validity")}";
        return new EmailMessage(to, Texts.Get(lang, "mail.invite.subject", inviter, household), html, text);
    }

    public static EmailMessage EmailChange(string lang, string to, string code)
    {
        var html = Layout(lang, code, $"""
            <p>{E(Texts.Get(lang, "mail.emailchange.intro"))}</p>
            <p style="font-size:28px;letter-spacing:6px;font-weight:700;margin:8px 0 16px">{E(code)}</p>
            <p style="color:#555">{E(Texts.Get(lang, "mail.login.validity"))}</p>
            """);
        return new EmailMessage(to, Texts.Get(lang, "mail.emailchange.subject"), html, $"{Texts.Get(lang, "mail.emailchange.intro")} {code}");
    }

    public static EmailMessage Notification(string lang, string to, string title, string body, string? link)
    {
        var button = link is null ? "" : Button(link, Texts.Get(lang, "mail.notification.open"));
        var html = Layout(lang, body, $"""
            <h2 style="font-size:20px;margin:0 0 12px">{E(title)}</h2>
            <p>{E(body)}</p>
            {button}
            <p style="color:#555;font-size:13px">{E(Texts.Get(lang, "mail.notification.footer"))}</p>
            """);
        return new EmailMessage(to, title, html, $"{title}\n\n{body}\n{link}");
    }

    /// <summary>An die Administratoren: neue Registrierungsanfrage (Modus „Auf Anfrage“, höchstens alle 30 Minuten).</summary>
    public static EmailMessage RegistrationRequested(string lang, string to, string requester, string link)
    {
        var intro = Texts.Get(lang, "mail.regrequest.intro", requester);
        var note = Texts.Get(lang, "mail.regrequest.note");
        var html = Layout(lang, intro, $"""
            <p>{E(intro)}</p>
            {Button(link, Texts.Get(lang, "mail.regrequest.button"))}
            <p style="color:#555">{E(note)}</p>
            """);
        return new EmailMessage(to, Texts.Get(lang, "mail.regrequest.subject"), html, $"{intro}\n{link}\n\n{note}");
    }

    /// <summary>An die freigeschaltete Person: Link zur Anmeldung mit vorausgefüllter Adresse.</summary>
    public static EmailMessage RegistrationApproved(string lang, string to, string link)
    {
        var intro = Texts.Get(lang, "mail.regapproved.intro");
        var html = Layout(lang, intro, $"""
            <p>{E(intro)}</p>
            {Button(link, Texts.Get(lang, "mail.regapproved.button"))}
            """);
        return new EmailMessage(to, Texts.Get(lang, "mail.regapproved.subject"), html, $"{intro}\n{link}");
    }

    public static EmailMessage AccountDeleted(string lang, string to)
    {
        var body = Texts.Get(lang, "mail.deleted.body");
        return new EmailMessage(to, Texts.Get(lang, "mail.deleted.subject"), Layout(lang, body, $"<p>{E(body)}</p>"), body);
    }

    private static string Button(string href, string label) =>
        $"""<p style="margin:20px 0"><a href="{E(href)}" style="background:{Green};color:#fff;text-decoration:none;padding:12px 22px;border-radius:10px;font-weight:600;display:inline-block">{E(label)}</a></p>""";

    private static string Layout(string lang, string preheader, string content) => $"""
        <!doctype html>
        <html lang="{lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>BudgetPlaner</title></head>
        <body style="margin:0;background:#F7F3EA;font-family:'Source Sans 3',Segoe UI,Arial,sans-serif;color:#1E2522;font-size:16px;line-height:1.5">
          <span style="display:none;max-height:0;overflow:hidden">{E(preheader)}</span>
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="100%" style="max-width:560px;background:#fff;border-radius:16px;padding:28px" cellpadding="0" cellspacing="0"><tr><td>
              <p style="font-weight:700;font-size:20px;color:{Green};margin:0 0 16px">BudgetPlaner</p>
              {content}
              <hr style="border:none;border-top:1px solid #E7E1D3;margin:24px 0 12px">
              <p style="color:#6B6B6B;font-size:12px;margin:0">{E(Texts.Get(lang, "mail.footer"))}</p>
            </td></tr></table>
          </td></tr></table>
        </body></html>
        """;

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
