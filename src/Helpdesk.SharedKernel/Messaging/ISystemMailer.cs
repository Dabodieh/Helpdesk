namespace Helpdesk.SharedKernel.Messaging;

/// <summary>An email that belongs to no ticket (for example an account invitation). Never carries ticket content.</summary>
public sealed record SystemMail(string ToAddress, string ToName, string Subject, string HtmlBody);

/// <summary>
/// What is known after one attempt. <see cref="Accepted"/> means the mail service accepted it for delivery, not that it arrived. <see cref="Uncertain"/> means the outcome is
/// unknown (it may have been sent): callers must not send it again automatically.
/// </summary>
public enum SystemMailOutcome
{
    NotConfigured,
    Accepted,
    Failed,
    Uncertain,
}

/// <summary>
/// Sends mail that is not part of a ticket from the dedicated system mailbox (<c>Mail:SystemMailbox</c>), never from a department mailbox. One attempt, no automatic retry.
/// The Mail module implements it; modules that cannot reference Mail (Organisation) depend on this seam.
/// </summary>
public interface ISystemMailer
{
    bool IsConfigured { get; }

    Task<SystemMailOutcome> SendAsync(SystemMail mail, CancellationToken cancellationToken);
}
