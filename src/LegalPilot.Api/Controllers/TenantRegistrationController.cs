using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using Microsoft.AspNetCore.Mvc;

namespace LegalPilot.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class TenantRegistrationController(
    LegalPilotStore store,
    PasswordHasher hasher,
    TokenService tokens) : ControllerBase
{
    [HttpPost("register-studio")]
    public IActionResult RegisterStudio([FromBody] StudioRegistrationRequest request)
    {
        var lawyerName = InputGuard.Required(request.LawyerName, "Nombre del abogado", 140);
        var email = InputGuard.Email(request.Email);
        var password = ValidatePassword(request.Password);
        var studioName = InputGuard.Required(request.StudioName, "Nombre del estudio juridico", 160);
        var studioWhatsApp = InputGuard.EcuadorWhatsApp(request.StudioWhatsApp, "WhatsApp del estudio");
        var now = DateTimeOffset.UtcNow;

        if (SuperAdminAccess.IsMasterOwnerEmail(email))
        {
            throw new ForbiddenOperationException("El correo maestro debe iniciar sesion; no puede registrarse por onboarding de clientes.");
        }

        if (IsReservedSystemStudioName(studioName))
        {
            throw new ForbiddenOperationException("El tenant userlegal es la base del sistema y no puede registrarse como estudio cliente.");
        }

        var created = store.Write(() =>
        {
            if (store.Users.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase) && u.IsActive))
            {
                throw new ConflictException("Ya existe una cuenta activa con ese correo.");
            }

            if (store.Tenants.Any(t => t.IsActive && t.Name.Equals(studioName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("Ya existe un estudio juridico registrado con ese nombre.");
            }

            var tenant = new Tenant(Guid.NewGuid(), studioName, now, studioWhatsApp, true);
            var (passwordHash, passwordSalt) = hasher.HashPassword(password);
            var user = new UserAccount(
                Guid.NewGuid(),
                tenant.Id,
                email,
                lawyerName,
                passwordHash,
                passwordSalt,
                [UserRole.Admin, UserRole.Lawyer],
                false,
                now,
                true);

            var refresh = TokenService.BuildRefreshSession(user, HttpContext.Connection.RemoteIpAddress?.ToString());
            store.Tenants.Add(tenant);
            store.Users.Add(user);
            store.RefreshTokenSessions.Add(refresh.Session);
            foreach (var holiday in EcuadorHolidaySeed.National2026(tenant.Id))
            {
                store.Holidays.Add(holiday);
            }

            store.WhatsAppTemplates.Add(new WhatsAppTemplate(
                Guid.NewGuid(),
                tenant.Id,
                "recordatorio-audiencia",
                "Estimado/a {{cliente}}, le recordamos su audiencia del caso {{caso}} el {{fecha}} a las {{hora}}. Por favor confirme recepcion.",
                true,
                true,
                now));

            store.WhatsAppTemplates.Add(new WhatsAppTemplate(
                Guid.NewGuid(),
                tenant.Id,
                "notificacion-estudio",
                "Se notifico a {{cliente}} sobre {{evento}} del caso {{caso}}.",
                false,
                true,
                now));

            store.AuditEntries.Insert(0, new AuditEntry(
                Guid.NewGuid(),
                tenant.Id,
                user.Id,
                AuditAction.Create,
                nameof(Tenant),
                tenant.Id.ToString(),
                $"Estudio juridico registrado desde onboarding: {tenant.Name}.",
                new Dictionary<string, string>
                {
                    ["studioWhatsApp"] = tenant.WhatsAppNumber,
                    ["adminEmail"] = user.Email
                },
                now));

            return new RegisteredStudio(tenant, user, refresh.RawToken, refresh.Session.ExpiresAt);
        });

        var principal = new AuthPrincipal(created.User.Id, created.User.TenantId, created.User.Email, created.User.Roles);
        var accessToken = tokens.Create(principal, TimeSpan.FromHours(10));
        return Created("/api/me", new
        {
            message = "Estudio juridico registrado y tenant creado.",
            accessToken,
            refreshToken = created.RefreshToken,
            expiresInSeconds = (int)TimeSpan.FromHours(10).TotalSeconds,
            refreshExpiresAt = created.RefreshExpiresAt,
            user = new
            {
                created.User.Id,
                created.User.Email,
                created.User.DisplayName,
                Roles = created.User.Roles.Select(r => r.ToString()).ToArray(),
                created.User.TenantId,
                created.User.MfaEnabled
            },
            tenant = new
            {
                created.Tenant.Id,
                created.Tenant.Name,
                created.Tenant.WhatsAppNumber,
                created.Tenant.IsActive
            }
        });
    }

    private static string ValidatePassword(string password)
    {
        password = InputGuard.Required(password, "Contrasena", 256);
        if (password.Length < 10)
        {
            throw new ArgumentException("La contrasena debe tener al menos 10 caracteres.");
        }

        return password;
    }

    private static bool IsReservedSystemStudioName(string value)
    {
        var normalized = value.Trim().Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        return normalized.Equals("userlegal", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("legalpilot", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("legalpilotecuador", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RegisteredStudio(Tenant Tenant, UserAccount User, string RefreshToken, DateTimeOffset RefreshExpiresAt);
}

public sealed record StudioRegistrationRequest(
    string LawyerName,
    string Email,
    string Password,
    string StudioName,
    string StudioWhatsApp);
