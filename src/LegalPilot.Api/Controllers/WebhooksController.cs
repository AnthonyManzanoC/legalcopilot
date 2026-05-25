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
    IConfiguration configuration,
    ILogger<WebhooksController> logger) : ControllerBase
{
    [HttpPost("gmail")]
    public IActionResult Gmail([FromBody] GmailWebhookRequest payload)
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
            e.Status is "Queued" or "Processing" or "Processed"));
        if (duplicate)
        {
            return Ok(new { accepted = true, action = "duplicate-webhook" });
        }

        var envelope = WebhookParsers.FromGmailPubSub(payload);
        var mailbox = store.Read(() => store.Mailboxes.FirstOrDefault(m =>
            m.TenantId == tenantId &&
            m.Provider == MailProvider.Gmail &&
            m.Email.Equals(envelope.Sender, StringComparison.OrdinalIgnoreCase)));
        var webhookEventId = store.Write(() =>
        {
            var item = new MailWebhookEvent(Guid.NewGuid(), tenantId, MailProvider.Gmail, mailbox?.Id, externalEventId, payloadHash, "Queued", "Gmail Pub/Sub recibido; procesamiento en segundo plano encolado.", DateTimeOffset.UtcNow, null);
            store.MailWebhookEvents.Insert(0, item);
            return item.Id;
        });

        QueueWebhookProcessing(tenantId, MailProvider.Gmail, envelope, webhookEventId, envelope.Sender);
        return Ok(new { accepted = true, action = "queued-background-processing", webhookEventId });
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
        var root = document.RootElement.Clone();
        var tenantId = store.Read(() => store.Tenants.First().Id);
        var raw = root.GetRawText();
        var payloadHash = TokenService.Sha256(raw);
        var externalEventId = MicrosoftWebhookId(root, payloadHash);
        var duplicate = store.Read(() => store.MailWebhookEvents.Any(e =>
            e.TenantId == tenantId &&
            e.Provider == MailProvider.Outlook &&
            e.ExternalEventId == externalEventId &&
            e.PayloadHash == payloadHash &&
            e.Status is "Queued" or "Processing" or "Processed"));
        if (duplicate)
        {
            return Ok(new { accepted = true, action = "duplicate-webhook" });
        }

        var mailbox = ResolveGraphMailbox(tenantId, root);
        var webhookEventId = store.Write(() =>
        {
            var item = new MailWebhookEvent(Guid.NewGuid(), tenantId, MailProvider.Outlook, mailbox?.Id, externalEventId, payloadHash, "Queued", "Microsoft Graph webhook recibido; procesamiento en segundo plano encolado.", DateTimeOffset.UtcNow, null);
            store.MailWebhookEvents.Insert(0, item);
            return item.Id;
        });

        QueueWebhookProcessing(tenantId, MailProvider.Outlook, WebhookParsers.FromMicrosoftNotification(root), webhookEventId, mailbox?.Email);
        return Ok(new { accepted = true, action = "queued-background-processing", webhookEventId });
    }

    private void QueueWebhookProcessing(Guid tenantId, MailProvider provider, WebhookEmailEnvelope envelope, Guid webhookEventId, string? mailboxEmail)
    {
        MarkWebhookProcessing(webhookEventId, $"Procesamiento {provider} iniciado en segundo plano.");
        _ = Task.Run(async () =>
        {
            try
            {
                var syncState = await mailboxes.SyncProviderFromWebhook(tenantId, provider, mailboxEmail, CancellationToken.None);
                if (syncState is not null)
                {
                    if (syncState.FailureCount > 0)
                    {
                        MarkWebhookFailed(webhookEventId, $"Sync {provider} fallo de forma controlada: {syncState.Status}. {syncState.Message}");
                        logger.LogWarning("Webhook {Provider} {WebhookEventId} sync failed safely: {Status} {Message}.", provider, webhookEventId, syncState.Status, syncState.Message);
                    }
                    else
                    {
                        MarkWebhookProcessed(webhookEventId, $"Sync {provider} ejecutado: {syncState.Status}. {syncState.Message}");
                    }

                    return;
                }

                var email = workflow.IngestWebhook(tenantId, provider, envelope);
                MarkWebhookProcessed(webhookEventId, $"Notificacion {provider} persistida como correo: {email.Id}.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook {Provider} {WebhookEventId} failed in background processing.", provider, webhookEventId);
                MarkWebhookFailed(webhookEventId, $"Error controlado procesando {provider}: {ex.Message}");
            }
        });
    }

    private void MarkWebhookProcessing(Guid webhookEventId, string message)
    {
        store.Write(() =>
        {
            var index = store.MailWebhookEvents.FindIndex(e => e.Id == webhookEventId);
            if (index >= 0)
            {
                store.MailWebhookEvents[index] = store.MailWebhookEvents[index] with
                {
                    Status = "Processing",
                    Message = message
                };
            }
        });
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

    private void MarkWebhookFailed(Guid webhookEventId, string message)
    {
        store.Write(() =>
        {
            var index = store.MailWebhookEvents.FindIndex(e => e.Id == webhookEventId);
            if (index >= 0)
            {
                store.MailWebhookEvents[index] = store.MailWebhookEvents[index] with
                {
                    Status = "Failed",
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
