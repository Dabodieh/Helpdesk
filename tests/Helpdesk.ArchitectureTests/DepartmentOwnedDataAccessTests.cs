using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.ArchitectureTests;

/// <summary>
/// Knowledge, Mail and Attachments hold Department-owned data but, unlike Ticketing, have no global scope filter: every query repeats its own department condition by
/// convention. Convention cannot be asserted by a test, so this pins the next best thing: which types may read the module's tables at all. The scan decodes call
/// instructions in the IL of every method (including async state machines, attributed to their outermost type) and finds the callers of the module DbContext's DbSet
/// properties and of <c>Set&lt;T&gt;()</c>. A new type that starts reading these tables fails here until it is reviewed for its department condition (and for the
/// content boundary: Department content needs ticket read by membership, see PERMISSIONS.md rule 10) and added to the list.
/// </summary>
public sealed class DepartmentOwnedDataAccessTests
{
    private static readonly BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // Baseline recorded 2026-10-07 (WS-1, decision D30): the types that read these tables today. Each is expected to carry its own department condition; the Mail webhook and
    // sync jobs take the department from the mailbox, never from message content.
    private static readonly string[] KnowledgeReaders =
    [
        "Helpdesk.Modules.Knowledge.Services.KnowledgeAdminService",
        "Helpdesk.Modules.Knowledge.Services.KnowledgePortalService",
        "Helpdesk.Modules.Knowledge.Services.ReplyLinkArticles",
    ];

    private static readonly string[] MailReaders =
    [
        "Helpdesk.Modules.Mail.Endpoints.MailEndpoints",
        "Helpdesk.Modules.Mail.Services.CorrelationEngine",
        "Helpdesk.Modules.Mail.Services.InboundProcessor",
        "Helpdesk.Modules.Mail.Services.MailJobRunner",
        "Helpdesk.Modules.Mail.Services.MailSyncAgeSignal", // operational metric: oldest sync age across active mailboxes; a number only, no address or id
        "Helpdesk.Modules.Mail.Services.MailSyncService",
        "Helpdesk.Modules.Mail.Services.MailboxDirectory",
        "Helpdesk.Modules.Mail.Services.MailboxService",
        "Helpdesk.Modules.Mail.Services.Microsoft365Service", // platform-administrator page (platform.departments.manage); mailbox health across Departments, no ticket content (ADR-035)
        "Helpdesk.Modules.Mail.Services.NdrCorrelation",
        "Helpdesk.Modules.Mail.Services.NotificationService",
        "Helpdesk.Modules.Mail.Services.OutboundNeedingAttentionSignal", // operational metric: a count only
        "Helpdesk.Modules.Mail.Services.OutboundQueueAgeSignal", // operational metric: an age only
        "Helpdesk.Modules.Mail.Services.OutboundSender",
        "Helpdesk.Modules.Mail.Services.SubscriptionService",
    ];

    private static readonly string[] AttachmentsReaders =
    [
        "Helpdesk.Modules.Attachments.Services.AttachmentStoreService",
    ];

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static bool IsTableAccess(MethodBase target, Assembly module)
    {
        if (target.DeclaringType is not { } declaring || target is not MethodInfo method)
        {
            return false;
        }

        // db.Articles: the getter of a DbSet<T> property declared on the module's own DbContext.
        if (declaring.Assembly == module && typeof(DbContext).IsAssignableFrom(declaring) && method.Name.StartsWith("get_", StringComparison.Ordinal)
            && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(DbSet<>))
        {
            return true;
        }

        // db.Set<T>() on any context.
        return declaring == typeof(DbContext) && method.Name == "Set";
    }

    private static List<string> ReadersOf(Assembly module)
    {
        var readers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in module.GetTypes())
        {
            if (typeof(DbContext).IsAssignableFrom(Outermost(type)) || type.Namespace?.EndsWith(".Migrations", StringComparison.Ordinal) == true)
            {
                continue; // the context itself (model building) and migrations do not query
            }

            foreach (var method in type.GetMembers(AllMembers).OfType<MethodBase>())
            {
                byte[]? il;
                try
                {
                    il = method.GetMethodBody()?.GetILAsByteArray();
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (il is null)
                {
                    continue;
                }

                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] is not (0x28 or 0x6F))
                    {
                        continue; // call, callvirt
                    }

                    var token = BitConverter.ToInt32(il, i + 1);
                    if ((token >> 24) is not (0x06 or 0x0A or 0x2B))
                    {
                        continue; // MethodDef, MemberRef, MethodSpec
                    }

                    try
                    {
                        var target = method.Module.ResolveMethod(
                            token,
                            type.IsGenericType ? type.GetGenericArguments() : null,
                            method.IsGenericMethod ? method.GetGenericArguments() : null);
                        if (target is not null && IsTableAccess(target, module))
                        {
                            readers.Add(Outermost(type).FullName!);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or NotSupportedException)
                    {
                        // not a real call operand
                    }
                }
            }
        }

        return [.. readers.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void Knowledge_tables_are_read_only_by_the_reviewed_types()
    {
        var actual = ReadersOf(Modules.Knowledge);
        Assert.NotEmpty(actual); // the scan sees the known readers, so the rule is not vacuous
        Assert.Equal([.. KnowledgeReaders.Order(StringComparer.Ordinal)], actual);
    }

    [Fact]
    public void Mail_tables_are_read_only_by_the_reviewed_types()
    {
        var actual = ReadersOf(Modules.Mail);
        Assert.NotEmpty(actual);
        Assert.Equal([.. MailReaders.Order(StringComparer.Ordinal)], actual);
    }

    [Fact]
    public void Attachments_tables_are_read_only_by_the_reviewed_types()
    {
        var actual = ReadersOf(Modules.Attachments);
        Assert.NotEmpty(actual);
        Assert.Equal([.. AttachmentsReaders.Order(StringComparer.Ordinal)], actual);
    }
}
