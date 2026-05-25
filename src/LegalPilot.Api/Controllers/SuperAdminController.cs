using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegalPilot.Api.Controllers;

[ApiController]
[Route("api/superadmin")]
[Authorize]
public sealed class SuperAdminController(
    LegalPilotStore store,
    TokenService tokens) : ControllerBase
{
    private static readonly Guid BootstrapTenantId = Guid.Parse("a3eb2579-63c9-4e34-9f13-d9f5f67ad001");

    [HttpGet("tenants")]
    public IActionResult ListTenants()
    {
        var principal = RequireReservedSuperAdmin();
        store.Audit(principal.TenantId, principal.UserId, AuditAction.View, nameof(Tenant), "all", "Panel SuperAdmin consultado.");
        return Ok(store.Read(() => store.Tenants
            .OrderByDescending(t => IsSystemTenant(t, store.Users.Where(u => u.TenantId == t.Id)))
            .ThenByDescending(t => t.CreatedAt)
            .Select(t => BuildTenantRow(t))
            .ToArray()));
    }

    [HttpPatch("tenants/{tenantId:guid}/subscription")]
    public IActionResult UpdateSubscription(Guid tenantId, [FromBody] UpdateTenantSubscriptionRequest request)
    {
        var principal = RequireReservedSuperAdmin();
        var updated = store.Write(() =>
        {
            var index = store.Tenants.FindIndex(t => t.Id == tenantId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Tenant no encontrado.");
            }

            var current = store.Tenants[index];
            if (IsSystemTenant(current, store.Users.Where(u => u.TenantId == tenantId)))
            {
                throw new ForbiddenOperationException("El tenant base del sistema no puede tratarse como suscripcion cliente.");
            }

            var next = current with { IsActive = request.IsActive };
            store.Tenants[index] = next;

            for (var i = 0; i < store.Users.Count; i++)
            {
                var user = store.Users[i];
                if (user.TenantId == tenantId && !user.Roles.Contains(UserRole.SuperAdmin))
                {
                    store.Users[i] = user with { IsActive = request.IsActive };
                }
            }

            store.RefreshTokenSessions.RemoveAll(s => s.TenantId == tenantId && !request.IsActive);
            store.AuditEntries.Insert(0, new AuditEntry(
                Guid.NewGuid(),
                principal.TenantId,
                principal.UserId,
                AuditAction.Update,
                nameof(Tenant),
                tenantId.ToString(),
                request.IsActive ? $"Suscripcion activada: {current.Name}." : $"Suscripcion bloqueada: {current.Name}.",
                new Dictionary<string, string>
                {
                    ["targetTenantId"] = tenantId.ToString(),
                    ["isActive"] = request.IsActive.ToString()
                },
                DateTimeOffset.UtcNow));
            return next;
        });

        return Ok(BuildTenantRow(updated));
    }

    private object BuildTenantRow(Tenant tenant)
    {
        var users = store.Users.Where(u => u.TenantId == tenant.Id).ToArray();
        var isSystemTenant = IsSystemTenant(tenant, users);
        var mailboxes = store.Mailboxes.Where(m => m.TenantId == tenant.Id && m.Status != "Disconnected").ToArray();
        var google = ResolveProviderStatus(mailboxes, MailProvider.Gmail);
        var microsoft = ResolveProviderStatus(mailboxes, MailProvider.Outlook);
        var latestSyncIssue = store.MailboxSyncStates
            .Where(s => s.TenantId == tenant.Id && s.FailureCount > 0)
            .OrderByDescending(s => s.CheckedAt)
            .FirstOrDefault();

        return new
        {
            tenant.Id,
            tenant.Name,
            tenant.WhatsAppNumber,
            tenant.CreatedAt,
            tenant.IsActive,
            isSystemTenant,
            tenantScope = isSystemTenant ? "SystemBase" : "ClientTenant",
            systemLabel = isSystemTenant ? "MASTER / SISTEMA BASE" : null,
            subscriptionStatus = tenant.IsActive ? "Active" : "Blocked",
            admins = users
                .Where(u => u.IsActive && (u.Roles.Contains(UserRole.Admin) || u.Roles.Contains(UserRole.Lawyer)))
                .Select(u => new { u.Id, u.Email, u.DisplayName })
                .ToArray(),
            counts = new
            {
                users = users.Count(u => u.IsActive),
                clients = store.Clients.Count(c => c.TenantId == tenant.Id),
                cases = store.Cases.Count(c => c.TenantId == tenant.Id),
                emails = store.Emails.Count(e => e.TenantId == tenant.Id && e.ProcessingStatus != "IgnoredNonLegal"),
                deadlines = store.Deadlines.Count(d => d.TenantId == tenant.Id),
                events = store.CalendarEvents.Count(e => e.TenantId == tenant.Id)
            },
            integrations = new
            {
                google,
                microsoft,
                latestSyncIssue = latestSyncIssue is null
                    ? null
                    : new
                    {
                        latestSyncIssue.Provider,
                        latestSyncIssue.Status,
                        latestSyncIssue.Message,
                        latestSyncIssue.CheckedAt
                    }
            }
        };
    }

    private static object ResolveProviderStatus(IEnumerable<MailboxConnection> mailboxes, MailProvider provider)
    {
        var providerMailboxes = mailboxes.Where(m => m.Provider == provider).ToArray();
        if (providerMailboxes.Length == 0)
        {
            return new { connected = false, status = "NotConnected", accounts = Array.Empty<object>() };
        }

        var connected = providerMailboxes.Any(m => m.Status is "Connected" or "OAuthConnected" or "WatchActive" or "Active");
        return new
        {
            connected,
            status = connected ? "Connected" : providerMailboxes[0].Status,
            accounts = providerMailboxes
                .Select(m => new
                {
                    m.Id,
                    m.Email,
                    m.Status,
                    m.LastSyncAt,
                    m.WatchExpiresAt
                })
                .ToArray()
        };
    }

    private AuthPrincipal RequireReservedSuperAdmin()
    {
        var principal = HttpAuth.RequirePrincipal(Request, tokens);
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin);
        if (!SuperAdminAccess.IsMasterOwnerEmail(principal.Email))
        {
            throw new ForbiddenOperationException("SuperAdmin reservado para el correo maestro configurado.");
        }

        return principal;
    }

    private static bool IsSystemTenant(Tenant tenant, IEnumerable<UserAccount> users)
    {
        return tenant.Id == BootstrapTenantId ||
               tenant.Name.Equals("userlegal", StringComparison.OrdinalIgnoreCase) ||
               users.Any(user => SuperAdminAccess.IsMasterOwnerEmail(user.Email));
    }
}

public sealed record UpdateTenantSubscriptionRequest(bool IsActive);
