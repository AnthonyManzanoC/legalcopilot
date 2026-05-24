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
        var envelope = WebhookParsers.FromGmailPubSub(payload);
        var syncState = await mailboxes.SyncProviderFromWebhook(tenantId, MailProvider.Gmail, envelope.Sender, cancellationToken);
        if (syncState is not null)
        {
            return Accepted("/api/webhooks/gmail", new { accepted = true, action = "mailbox-sync", syncState.Id, syncState.Status, syncState.Message });
        }

        var email = workflow.IngestWebhook(tenantId, MailProvider.Gmail, envelope);
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
        var syncState = await mailboxes.SyncProviderFromWebhook(tenantId, MailProvider.Outlook, null, cancellationToken);
        if (syncState is not null)
        {
            return Accepted("/api/webhooks/microsoft", new { accepted = true, action = "mailbox-sync", syncState.Id, syncState.Status, syncState.Message });
        }

        var email = workflow.IngestWebhook(tenantId, MailProvider.Outlook, WebhookParsers.FromMicrosoftNotification(document.RootElement));
        return Accepted("/api/webhooks/microsoft", new { accepted = true, action = "stored-webhook-notification", email.Id });
    }
}
