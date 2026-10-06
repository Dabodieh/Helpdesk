using System.Reflection;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;

namespace Helpdesk.ArchitectureTests;

/// <summary>
/// The fail-closed department scope filter (TICKETING.md "Department scope") may only be bypassed through the named
/// <c>IgnoreDepartmentScope()</c>, and only from the reviewed call sites below. The scan decodes call instructions in the IL of
/// every method of the Ticketing assembly (including compiler-generated async state machines, attributed to their declaring type).
/// </summary>
public sealed class DepartmentScopeBypassTests
{
    /// <summary>System work with an explicit department (ingestion). Add a type here only after review.</summary>
    private static readonly Type[] AllowedBypassCallers =
        [
            typeof(TicketIngressService), typeof(InboundTicketSink), typeof(DeliveryStateSink), typeof(PeopleUpsert),
            // Phase 5: system work that always carries an explicit department (SLA clocks, automation, the SLA monitor).
            typeof(Helpdesk.Modules.Ticketing.Sla.SlaEngine), typeof(Helpdesk.Modules.Ticketing.Automation.AutomationEngine),
            typeof(Helpdesk.Modules.Ticketing.Automation.SystemTicketOps), typeof(Helpdesk.Modules.Ticketing.Automation.TicketLifecycle),
            typeof(Helpdesk.Modules.Ticketing.Automation.SlaMonitor),
            // Phase 6: requester portal (every query constrained to the caller's linked requesters), form administration and the requester-safe realtime fan-out.
            typeof(Helpdesk.Modules.Ticketing.Portal.PortalRequestService), typeof(Helpdesk.Modules.Ticketing.Fields.TicketFieldsService), typeof(Helpdesk.Modules.Ticketing.Fields.FieldValueStore),
            typeof(Helpdesk.Modules.Ticketing.Fields.TicketPropertiesService),
            typeof(TicketEventPublisher),
        ];

    private static readonly BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static List<Type> CallersOf(string methodName)
    {
        var callers = new HashSet<Type>();
        foreach (var type in Modules.Ticketing.GetTypes())
        {
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
                        if (target?.Name == methodName)
                        {
                            callers.Add(Outermost(type));
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or NotSupportedException)
                    {
                        // not a real call operand
                    }
                }
            }
        }

        return [.. callers];
    }

    [Fact]
    public void The_scan_finds_the_known_call_site_so_the_rule_is_not_vacuous()
    {
        Assert.Contains(typeof(TicketIngressService), CallersOf(nameof(DepartmentScopeBypass.IgnoreDepartmentScope)));
        Assert.Contains(typeof(DepartmentScopeBypass), CallersOf("IgnoreQueryFilters"));
    }

    [Fact]
    public void IgnoreDepartmentScope_is_called_only_from_the_reviewed_call_sites()
    {
        var callers = CallersOf(nameof(DepartmentScopeBypass.IgnoreDepartmentScope));
        Assert.Equal(AllowedBypassCallers.Select(t => t.FullName).Order(), callers.Select(t => t.FullName).Order());
    }

    [Fact]
    public void IgnoreQueryFilters_is_called_only_by_the_named_bypass()
    {
        var callers = CallersOf("IgnoreQueryFilters");
        Assert.Equal([typeof(DepartmentScopeBypass).FullName], callers.Select(t => t.FullName));
    }
}
