using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

internal sealed partial class TicketService
{
    /// <summary>
    /// D22-B: an authorised user explicitly gives a ticket that has no stored mailbox its sender identity, or corrects such an assignment while nothing has been sent from it.
    /// Authority: <c>department.mailboxes.manage</c> AND normal access to the ticket by membership (the endpoint resolves the ticket through the department scope, so a Platform Admin
    /// without membership gets 404); <c>tickets.reply</c> alone is not enough. Eligible: an active mailbox of the ticket's department that the user chooses (the default is never
    /// applied silently). Once a public reply was submitted, or may have been, the mailbox is permanently fixed; mailboxes chosen at creation are never changed. No reason is
    /// asked for; the audit entry carries the before and after.
    /// </summary>
    public async Task<TicketDetailDto> AssignMailboxAsync(string route, AssignMailboxRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        if (request.MailboxId is null)
        {
            errors["mailboxId"] = ["Choose a mailbox."];
        }

        TicketValidation.ThrowIfAny(errors);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await authorizer.RequireAsync(PermissionCodes.DepartmentMailboxesManage, key.DepartmentId, ct);

        // Eligible = an active mailbox of THIS department (another department's, inactive and unavailable ones answer alike).
        var chosen = await mailboxes.FindReplyMailboxAsync(key.DepartmentId, request.MailboxId, ct)
            ?? throw new RequestValidationException("mailboxId", "Choose an active mailbox of this department.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAndLoadAsync(key.Id, version, ct); // the row lock also keeps a reply from being queued while this runs

        string action;
        Guid? previousId = ticket.MailboxId;
        if (ticket.MailboxId is null)
        {
            action = TicketAuditActions.MailboxAssigned;
        }
        else if (ticket.MailboxAssignedAt is not null && ticket.MailboxId != chosen.MailboxId)
        {
            if (await mailboxes.HasExternalSendAsync(ticket.DepartmentId, ticket.Id, ct))
            {
                throw new ConflictException("An email has already been sent from this ticket's mailbox, so it can no longer be changed.", ProblemCodes.InvalidState);
            }

            action = TicketAuditActions.MailboxCorrected;
        }
        else
        {
            throw new ConflictException("This ticket's mailbox is fixed.", ProblemCodes.InvalidState);
        }

        var before = previousId is { } p ? (await mailboxes.GetManyAsync(ticket.DepartmentId, [p], ct)).GetValueOrDefault(p) : null;
        ticket.MailboxId = chosen.MailboxId;
        ticket.MailboxAssignedAt = clock.GetUtcNowMicroseconds();
        ticket.UpdatedAt = clock.GetUtcNowMicroseconds();
        await SaveAsync(ct);
        await audit.WriteAsync(
            action, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId,
            new { reference = ticket.Reference, mailboxId = previousId, address = before?.Address },
            new { reference = ticket.Reference, mailboxId = chosen.MailboxId, address = chosen.Address }, ct);
        await tx.CommitAsync(ct);
        await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Updated, current.UserId, ct);
        return await projection.DetailAsync(ticket, ticket.Requester, ct);
    }
}
