using System.Text.Json;
using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace LegalPilot.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
public sealed class WebhooksController(
    LegalWorkflowService workflow,
    MailboxService mailboxes,
    LegalPilotStore store,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost("gmail")]
    public async Task<IActionResult> Gmail([FromBody] GmailWebhookRequest payload, CancellationToken cancellationToken)
    {
        WebhookSecurity.RequireSharedSecret(Request, configuration, "LegalPilot:Gmail:WebhookSecret", "Gmail");
        var tenantId = store.Read(() => store.Tenants.First().Id);
        var raw = JsonSerializer.Serialize(payload);
        var payloadHash = TokenService.Sha256(raw);
        var externalEventId = payload.Message?.MessageId ?? payloadHash;
        var duplicate = store.Read(() => store.MailWebhookEvents.Any(e =>
            e.TenantId == tenantId &&
            e.Provider == MailProvider.Gmail &&
            e.ExternalEventId == externalEventId &&
            e.PayloadHash == payloadHash &&
            e.Status == "Processed"));
        if (duplicate)
        {
            return Accepted("/api/webhooks/gmail", new { accepted = true, action = "duplicate-webhook" });
        }

        var envelope = WebhookParsers.FromGmailPubSub(payload);
        var mailbox = store.Read(() => store.Mailboxes.FirstOrDefault(m =>
            m.TenantId == tenantId &&
            m.Provider == MailProvider.Gmail &&
            m.Email.Equals(envelope.Sender, StringComparison.OrdinalIgnoreCase)));
        var webhookEventId = store.Write(() =>
        {
            var item = new MailWebhookEvent(Guid.NewGuid(), tenantId, MailProvider.Gmail, mailbox?.Id, externalEventId, payloadHash, "Received", "Gmail Pub/Sub recibido.", DateTimeOffset.UtcNow, null);
            store.MailWebhookEvents.Insert(0, item);
            return item.Id;
        });

        var syncState = await mailboxes.SyncProviderFromWebhook(tenantId, MailProvider.Gmail, envelope.Sender, cancellationToken);
        if (syncState is not null)
        {
            MarkWebhookProcessed(webhookEventId, $"Sync Gmail ejecutado: {syncState.Status}.");
            return Accepted("/api/webhooks/gmail", new { accepted = true, action = "mailbox-sync", syncState.Id, syncState.Status, syncState.Message });
        }

        var email = workflow.IngestWebhook(tenantId, MailProvider.Gmail, envelope);
        MarkWebhookProcessed(webhookEventId, $"Notificacion Gmail persistida como correo: {email.Id}.");
        return Accepted("/api/webhooks/gmail", new { accepted = true, action = "stored-webhook-notification", email.Id });
    }

    [HttpGet("microsoft")]
    public IActionResult MicrosoftValidation([FromQuery] string? validationToken)
    {
        return string.IsNullOrWhiteSpace(validationToken)
            ? BadRequest(new { error = "validationToken requerido para validacion Graph." })
            : Content(validationToken, "text/plain");
    }

    [HttpPost("microsoft")]
    public async Task<IActionResult> Microsoft(CancellationToken cancellationToken)
    {
        if (Request.Query.TryGetValue("validationToken", out var validationToken))
        {
            return Content(validationToken.ToString(), "text/plain");
        }

        using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        WebhookSecurity.RequireMicrosoftClientState(document.RootElement, configuration);
        var tenantId = store.Read(() => store.Tenants.First().Id);
        var raw = document.RootElement.GetRawText();
        var payloadHash = TokenService.Sha256(raw);
        var externalEventId = MicrosoftWebhookId(document.RootElement, payloadHash);
        var duplicate = store.Read(() => store.MailWebhookEvents.Any(e =>
            e.TenantId == tenantId &&
            e.Provider == MailProvider.Outlook &&
            e.ExternalEventId == externalEventId &&
            e.PayloadHash == payloadHash &&
            e.Status == "Processed"));
        if (duplicate)
        {
            return Accepted("/api/webhooks/microsoft", new { accepted = true, action = "duplicate-webhook" });
        }

        var mailbox = ResolveGraphMailbox(tenantId, document.RootElement);
        var webhookEventId = store.Write(() =>
        {
            var item = new MailWebhookEvent(Guid.NewGuid(), tenantId, MailProvider.Outlook, mailbox?.Id, externalEventId, payloadHash, "Received", "Microsoft Graph webhook recibido.", DateTimeOffset.UtcNow, null);
            store.MailWebhookEvents.Insert(0, item);
            return item.Id;
        });

        var syncState = await mailboxes.SyncProviderFromWebhook(tenantId, MailProvider.Outlook, null, cancellationToken);
        if (syncState is not null)
        {
            MarkWebhookProcessed(webhookEventId, $"Sync Microsoft ejecutado: {syncState.Status}.");
            return Accepted("/api/webhooks/microsoft", new { accepted = true, action = "mailbox-sync", syncState.Id, syncState.Status, syncState.Message });
        }

        var email = workflow.IngestWebhook(tenantId, MailProvider.Outlook, WebhookParsers.FromMicrosoftNotification(document.RootElement));
        MarkWebhookProcessed(webhookEventId, $"Notificacion Microsoft persistida como correo: {email.Id}.");
        return Accepted("/api/webhooks/microsoft", new { accepted = true, action = "stored-webhook-notification", email.Id });
    }

    private void MarkWebhookProcessed(Guid webhookEventId, string message)
    {
        store.Write(() =>
        {
            var index = store.MailWebhookEvents.FindIndex(e => e.Id == webhookEventId);
            if (index >= 0)
            {
                store.MailWebhookEvents[index] = store.MailWebhookEvents[index] with
                {
                    Status = "Processed",
                    Message = message,
                    ProcessedAt = DateTimeOffset.UtcNow
                };
            }
        });
    }

    private MailboxConnection? ResolveGraphMailbox(Guid tenantId, JsonElement body)
    {
        var subscriptionId = body.TryGetProperty("value", out var values)
            ? values.EnumerateArray()
                .Select(v => v.TryGetProperty("subscriptionId", out var id) ? id.GetString() : null)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            : null;
        return store.Read(() => store.Mailboxes.FirstOrDefault(m =>
            m.TenantId == tenantId &&
            m.Provider == MailProvider.Outlook &&
            !string.IsNullOrWhiteSpace(subscriptionId) &&
            m.WebhookSubscriptionId == subscriptionId));
    }

    private static string MicrosoftWebhookId(JsonElement body, string fallback)
    {
        if (!body.TryGetProperty("value", out var values))
        {
            return fallback;
        }

        var parts = values.EnumerateArray()
            .Select(value =>
            {
                var sub = value.TryGetProperty("subscriptionId", out var subscriptionId) ? subscriptionId.GetString() : null;
                var resource = value.TryGetProperty("resource", out var resourceElement) ? resourceElement.GetString() : null;
                return $"{sub}:{resource}";
            })
            .Where(value => value.Length > 1)
            .ToArray();

        return parts.Length == 0 ? fallback : TokenService.Sha256(string.Join('|', parts));
    }
}
