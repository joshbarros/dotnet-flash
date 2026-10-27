using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Flash.Identity;

public record MailMessageData(string To, string Subject, string Body, string? Link)
{
    private static long _next;

    /// <summary>Orders messages in the dev mailbox; not part of the message.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long Sequence { get; } = Interlocked.Increment(ref _next);
}

/// <summary>Sends the emails the sign-in flows need: confirm an address, reset a password.</summary>
public interface IAuthMailer
{
    Task SendAsync(MailMessageData message, CancellationToken ct);
}

/// <summary>Writes the message to the log. The default when no SMTP host is configured, so nothing leaves the machine by accident.</summary>
public sealed class LogMailer(ILogger<LogMailer> logger) : IAuthMailer
{
    public Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        logger.LogInformation("Mail to {To}: {Subject}", message.To, message.Subject);
        return Task.CompletedTask;
    }
}

public sealed class SmtpMailer(IOptions<MailOptions> options) : IAuthMailer
{
    public async Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        var o = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(o.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host!, o.Port, o.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(o.User))
            await client.AuthenticateAsync(o.User, o.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Keeps the most recent messages so a browser test or a demo can read them. Registered only when Mail:DevMailbox is on.</summary>
public sealed class DevMailbox
{
    private readonly ConcurrentQueue<MailMessageData> _messages = new();

    public void Add(MailMessageData message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > 200 && _messages.TryDequeue(out _)) { }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<MailMessageData> For(string to) =>
        _messages.Where(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Sequence).ToList();
}

/// <summary>Records every message in the dev mailbox, then hands it to the real mailer.</summary>
public sealed class RecordingMailer(IAuthMailer inner, DevMailbox mailbox) : IAuthMailer
{
    public Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        mailbox.Add(message);
        return inner.SendAsync(message, ct);
    }
}

[ApiController]
[Route("api/dev/mailbox")]
[AllowAnonymous]
public class DevMailboxController(IOptions<MailOptions> options, DevMailbox? mailbox = null) : ControllerBase
{
    /// <summary>The messages sent to one address, newest first. 404 unless Mail:DevMailbox is on.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<MailMessageData>> Get([FromQuery] string to) =>
        options.Value.DevMailbox && mailbox is not null ? Ok(mailbox.For(to)) : NotFound();
}
