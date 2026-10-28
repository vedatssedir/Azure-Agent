using System.Net;
using System.Net.Mail;
using AzureAgent.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureAgent.Infrastructure.Identity;

public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "azure-agent@localhost";
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public bool Enabled => options.Value.Host.Length > 0;

    public async Task SendAsync(string to, string subject, string html, CancellationToken ct)
    {
        var o = options.Value;
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.EnableSsl };
        if (o.Username is { Length: > 0 }) client.Credentials = new NetworkCredential(o.Username, o.Password);
        using var message = new MailMessage(o.From, to, subject, html) { IsBodyHtml = true };
        await client.SendMailAsync(message, ct);
    }
}

public sealed class AppLinkOptions
{
    public const string Section = "App";
    /// <summary>Public URL of the web app, e.g. https://agent.contoso.com.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";
}

public sealed class AppLinks(IOptions<AppLinkOptions> options) : IAppLinks
{
    public string InvitationUrl(string token) => $"{options.Value.PublicUrl.TrimEnd('/')}/invite/{Uri.EscapeDataString(token)}";
}
