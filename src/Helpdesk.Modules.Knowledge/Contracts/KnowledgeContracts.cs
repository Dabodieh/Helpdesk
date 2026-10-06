namespace Helpdesk.Modules.Knowledge.Contracts;

/// <summary>
/// Which departments the signed-in user is a requester in (has raised, or been recorded as the requester of, a ticket). Declared here and
/// implemented by Ticketing, so Knowledge never references Ticketing. Used only for articles whose audience is "requesters of the department".
/// </summary>
public interface IRequesterAudience
{
    Task<IReadOnlyCollection<Guid>> GetRequesterDepartmentIdsAsync(Guid userId, CancellationToken cancellationToken = default);
}
