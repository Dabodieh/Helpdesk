using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Organisation.Domain;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Messaging;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Organisation.Services;

/// <summary>Bound from <c>Invitations</c>. <see cref="PublicBaseUrl"/> is the address people open (required outside Development/Testing, where the request's own origin is used).</summary>
internal sealed class InvitationOptions
{
    public const string Section = "Invitations";

    public string? PublicBaseUrl { get; set; }

    /// <summary>Days an invitation link stays valid.</summary>
    public int ExpiryDays { get; set; } = 7;
}

internal sealed record InvitationDto(
    Guid Id,
    string DisplayName,
    string Email,
    Guid DepartmentId,
    string DepartmentName,
    string Role,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string EmailState,
    DateTimeOffset? AcceptedAt);

/// <summary>The link is returned once, when the invitation is created or re-issued; only its hash is stored, so it cannot be shown again.</summary>
internal sealed record InvitationIssuedDto(InvitationDto Invitation, string ActivationUrl);

internal sealed record CreateInvitationRequest(string? DisplayName, string? Email, Guid? DepartmentId, string? Role);

internal sealed record InvitationTokenRequest(string? Token);

internal sealed record InvitationPreviewDto(string DisplayName, string DepartmentName, string Role);

internal sealed record InvitationAcceptedDto(Guid DepartmentId, string DepartmentName, string Role, bool AlreadyMember);

/// <summary>
/// Account invitations (platform administrators only; the endpoints enforce <c>platform.users.manage</c>). An invitation grants nothing by itself: the person opens the emailed link,
/// signs in with their own Microsoft 365 account and, only if that account's address is the invited one, is added to the department with the invited role. The link is single use,
/// expires, and is stored only as a hash. Nothing here creates or stores a password or a Microsoft account.
/// </summary>
internal sealed partial class InvitationService(
    OrganisationDbContext db,
    OrganisationAudit audit,
    MembershipService memberships,
    ICurrentUser current,
    ISystemMailer mailer,
    IOptions<InvitationOptions> options,
    IHttpContextAccessor http,
    IHostEnvironment environment,
    TimeProvider clock)
{
    private const int ListLimit = 200;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RoleLabel(string role) => role switch
    {
        DepartmentRoles.DepartmentAdmin => "Department administrator",
        DepartmentRoles.TeamLead => "Team lead",
        DepartmentRoles.Agent => "Agent",
        _ => "Viewer",
    };

    public async Task<IReadOnlyList<InvitationDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.Invitations.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(ListLimit).ToListAsync(ct);
        var names = await DepartmentNamesAsync(rows.Select(r => r.DepartmentId).Distinct().ToList(), ct);
        return [.. rows.Select(r => ToDto(r, names))];
    }

    public async Task<InvitationIssuedDto> CreateAsync(CreateInvitationRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.DisplayName?.Trim() ?? "";
        var email = request.Email?.Trim() ?? "";
        if (name.Length is 0 or > 100 || name.Any(char.IsControl))
        {
            errors["displayName"] = ["Enter the person's name (up to 100 characters)."];
        }

        if (email.Length is 0 or > 320 || !EmailPattern().IsMatch(email) || email.Any(char.IsControl))
        {
            errors["email"] = ["Enter a valid email address."];
        }

        if (!RoleCatalogue.IsValid(request.Role))
        {
            errors["role"] = [$"role must be one of {string.Join(", ", RoleCatalogue.Roles)}."];
        }

        if (request.DepartmentId is null)
        {
            errors["departmentId"] = ["Choose a department."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        var department = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == request.DepartmentId, ct)
            ?? throw new RequestValidationException("departmentId", "Choose an existing department.");
        if (!department.IsActive)
        {
            throw new ConflictException("The department is inactive.", ProblemCodes.Inactive);
        }

        var baseUrl = BaseUrl();
        var normalised = Normalise(email);
        var now = clock.GetUtcNow();
        var token = NewToken();
        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(),
            DisplayName = name,
            Email = email,
            EmailNormalised = normalised,
            DepartmentId = department.Id,
            Role = request.Role!,
            TokenHash = Hash(token),
            Status = InvitationStatuses.Pending,
            ExpiresAt = now.AddDays(options.Value.ExpiryDays),
            CreatedBy = current.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // An invitation that ran out is replaced; one that is still open must be resent or revoked instead.
            var open = await db.Invitations.SingleOrDefaultAsync(i => i.EmailNormalised == normalised && i.DepartmentId == department.Id && i.Status == InvitationStatuses.Pending, ct);
            if (open is not null)
            {
                if (open.ExpiresAt > now)
                {
                    throw new ConflictException("An invitation for this address is already open in this department. Resend or revoke it.", ProblemCodes.Duplicate);
                }

                open.Status = InvitationStatuses.Revoked;
                open.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }

            db.Invitations.Add(invitation);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("invitation.created", "invitation", invitation.Id, department.Id, null, new { role = invitation.Role }, ct);
            await tx.CommitAsync(ct);
        }

        var url = $"{baseUrl}/activate#{token}";
        await SendAsync(invitation, department.Name, url, ct);
        return new InvitationIssuedDto(ToDto(invitation, new Dictionary<Guid, string> { [department.Id] = department.Name }), url);
    }

    /// <summary>Issues a new link (the old one stops working) and sends it again. For an invitation whose email failed or never arrived, or that expired.</summary>
    public async Task<InvitationIssuedDto> ResendAsync(Guid id, CancellationToken ct)
    {
        var baseUrl = BaseUrl();
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        if (invitation.Status != InvitationStatuses.Pending)
        {
            throw new ConflictException("Only an open invitation can be resent.", ProblemCodes.InvalidState);
        }

        var department = await db.Departments.AsNoTracking().SingleAsync(d => d.Id == invitation.DepartmentId, ct);
        if (!department.IsActive)
        {
            throw new ConflictException("The department is inactive.", ProblemCodes.Inactive);
        }

        var now = clock.GetUtcNow();
        var token = NewToken();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            invitation.TokenHash = Hash(token);
            invitation.ExpiresAt = now.AddDays(options.Value.ExpiryDays);
            invitation.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("invitation.resent", "invitation", invitation.Id, invitation.DepartmentId, null, null, ct);
            await tx.CommitAsync(ct);
        }

        var url = $"{baseUrl}/activate#{token}";
        await SendAsync(invitation, department.Name, url, ct);
        return new InvitationIssuedDto(ToDto(invitation, new Dictionary<Guid, string> { [department.Id] = department.Name }), url);
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        if (invitation.Status != InvitationStatuses.Pending)
        {
            throw new ConflictException("Only an open invitation can be revoked.", ProblemCodes.InvalidState);
        }

        invitation.Status = InvitationStatuses.Revoked;
        invitation.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("invitation.revoked", "invitation", invitation.Id, invitation.DepartmentId, null, null, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>What a signed-in person is being invited to. A link that is unknown, used, revoked or expired is simply "not found".</summary>
    public async Task<InvitationPreviewDto> PreviewAsync(string? token, CancellationToken ct)
    {
        var (invitation, department) = await FindOpenAsync(token, ct);
        return new InvitationPreviewDto(invitation.DisplayName, department.Name, invitation.Role);
    }

    /// <summary>
    /// Activates the invitation for the signed-in person. The signed-in account's address must be the invited address (the link alone is not enough: a forwarded link
    /// grants nothing to somebody else). An existing membership is left exactly as it is.
    /// </summary>
    public async Task<InvitationAcceptedDto> AcceptAsync(string? token, CancellationToken ct)
    {
        var (invitation, department) = await FindOpenAsync(token, ct);
        if (current.Email is not { Length: > 0 } signedInAs || Normalise(signedInAs) != invitation.EmailNormalised)
        {
            throw new ConflictException("This invitation was issued for a different email address. Sign in with the Microsoft 365 account it was sent to.", ProblemCodes.InvalidState);
        }

        var now = clock.GetUtcNow();
        (string Role, bool AlreadyMember) member;
        // Claim, membership and both audit events are one durable operation. Recheck the hash in case a resend replaced the link after lookup.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var claimed = await db.Invitations
                .Where(i => i.Id == invitation.Id && i.TokenHash == invitation.TokenHash && i.Status == InvitationStatuses.Pending && i.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InvitationStatuses.Accepted).SetProperty(i => i.AcceptedAt, now).SetProperty(i => i.AcceptedUserId, current.UserId).SetProperty(i => i.UpdatedAt, now), ct);
            if (claimed == 0)
            {
                throw new NotFoundException();
            }

            member = await memberships.AddInvitedMemberAsync(invitation.DepartmentId, current.UserId, invitation.Role, ct);
            await audit.WriteAsync("invitation.accepted", "invitation", invitation.Id, invitation.DepartmentId, null, new { role = member.Role }, ct);
            await tx.CommitAsync(ct);
        }

        if (!member.AlreadyMember)
        {
            await memberships.InvitationCommittedAsync(current.UserId);
        }

        return new InvitationAcceptedDto(department.Id, department.Name, member.Role, member.AlreadyMember);
    }

    private async Task<(Invitation Invitation, Department Department)> FindOpenAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            throw new NotFoundException();
        }

        var hash = Hash(token.Trim());
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || invitation.Status != InvitationStatuses.Pending || invitation.ExpiresAt <= clock.GetUtcNow())
        {
            throw new NotFoundException();
        }

        var department = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == invitation.DepartmentId && d.IsActive, ct) ?? throw new NotFoundException();
        return (invitation, department);
    }

    private async Task SendAsync(Invitation invitation, string departmentName, string url, CancellationToken ct)
    {
        var html =
            $"<p>Hello {WebUtility.HtmlEncode(invitation.DisplayName)},</p>"
            + $"<p>A Helpdesk account has been set up for you in <strong>{WebUtility.HtmlEncode(departmentName)}</strong> as {WebUtility.HtmlEncode(RoleLabel(invitation.Role))}.</p>"
            + $"<p><a href=\"{WebUtility.HtmlEncode(url)}\">Activate your account</a></p>"
            + $"<p>You will be asked to sign in with your Microsoft 365 account ({WebUtility.HtmlEncode(invitation.Email)}). The link works once and expires on {invitation.ExpiresAt:d MMMM yyyy}.</p>"
            + $"<p style=\"color:#666\">If the link does not open, copy this address into your browser:<br>{WebUtility.HtmlEncode(url)}</p>"
            + "<p style=\"color:#666\">If you were not expecting this email, you can ignore it.</p>";
        var outcome = await mailer.SendAsync(new SystemMail(invitation.Email, invitation.DisplayName, "Your Helpdesk account is ready to activate", html), ct);
        invitation.EmailState = outcome switch
        {
            SystemMailOutcome.Accepted => InvitationEmailStates.Accepted,
            SystemMailOutcome.Uncertain => InvitationEmailStates.Uncertain,
            SystemMailOutcome.Failed => InvitationEmailStates.Failed,
            _ => InvitationEmailStates.NotConfigured,
        };
        invitation.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The address the link points at. Production must configure it; Development and Testing fall back to the address the administrator is using.</summary>
    private string BaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl))
        {
            if (Uri.TryCreate(options.Value.PublicBaseUrl.Trim(), UriKind.Absolute, out var configured) && configured.Scheme is "https" or "http")
            {
                return configured.GetLeftPart(UriPartial.Authority);
            }

            throw new ConflictException("Invitations:PublicBaseUrl is not a valid address.", ProblemCodes.InvalidState);
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            var request = http.HttpContext?.Request;
            if (request is not null)
            {
                if (Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) && origin.Scheme is "https" or "http")
                {
                    return origin.GetLeftPart(UriPartial.Authority);
                }

                return $"{request.Scheme}://{request.Host}";
            }
        }

        throw new ConflictException("Invitations cannot be sent until Invitations:PublicBaseUrl is configured.", ProblemCodes.InvalidState);
    }

    private async Task<Dictionary<Guid, string>> DepartmentNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Departments.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

    private InvitationDto ToDto(Invitation i, IReadOnlyDictionary<Guid, string> names) =>
        new(i.Id, i.DisplayName, i.Email, i.DepartmentId, names.GetValueOrDefault(i.DepartmentId, "Department"),
            i.Role, i.Status == InvitationStatuses.Pending && i.ExpiresAt <= clock.GetUtcNow() ? "Expired" : i.Status, i.ExpiresAt, i.CreatedAt, i.EmailState, i.AcceptedAt);
}
