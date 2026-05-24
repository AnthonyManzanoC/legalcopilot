using System.Net;
using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace LegalPilot.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    TokenService tokens,
    ICloudOAuthWebhookService oauth) : ControllerBase
{
    [HttpGet("gmail/login")]
    public IActionResult GmailLogin([FromQuery] string email, [FromQuery] string? mode)
    {
        var principal = HttpAuth.RequirePrincipal(Request, tokens);
        var start = oauth.Start(principal, MailProvider.Gmail, email);
        return WantsJson(mode) ? Ok(start) : Redirect(start.AuthorizationUrl);
    }

    [HttpGet("microsoft/login")]
    public IActionResult MicrosoftLogin([FromQuery] string email, [FromQuery] string? mode)
    {
        var principal = HttpAuth.RequirePrincipal(Request, tokens);
        var start = oauth.Start(principal, MailProvider.Outlook, email);
        return WantsJson(mode) ? Ok(start) : Redirect(start.AuthorizationUrl);
    }

    [HttpGet("gmail/callback")]
    public async Task<IActionResult> GmailCallback([FromQuery] string? state, [FromQuery] string? code, [FromQuery] string? error, CancellationToken cancellationToken)
    {
        var result = await oauth.CompleteAsync(MailProvider.Gmail, state, code, error, cancellationToken);
        return Content(BuildCallbackHtml(result), "text/html");
    }

    [HttpGet("microsoft/callback")]
    public async Task<IActionResult> MicrosoftCallback([FromQuery] string? state, [FromQuery] string? code, [FromQuery] string? error, CancellationToken cancellationToken)
    {
        var result = await oauth.CompleteAsync(MailProvider.Outlook, state, code, error, cancellationToken);
        return Content(BuildCallbackHtml(result), "text/html");
    }

    private static string BuildCallbackHtml(CloudOAuthCallbackResult result)
    {
        var title = result.Accepted ? "Integracion conectada" : "Integracion rechazada";
        return $$"""
            <!doctype html>
            <html lang="es">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>LegalPilot OAuth</title>
              <style>
                body { margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: system-ui, sans-serif; background: #f6f7f9; color: #171c22; }
                main { width: min(620px, calc(100vw - 32px)); background: white; border: 1px solid #dce2e7; border-radius: 8px; padding: 28px; box-shadow: 0 24px 70px rgba(15,23,42,.14); }
                h1 { margin: 0 0 12px; font-size: 24px; }
                p { line-height: 1.55; color: #64707d; }
                strong { color: #171c22; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                <p><strong>{{WebUtility.HtmlEncode(result.Provider.ToString())}}</strong> / {{WebUtility.HtmlEncode(result.Email)}}</p>
                <p>{{WebUtility.HtmlEncode(result.Message)}}</p>
                <p>Estado: <strong>{{WebUtility.HtmlEncode(result.Status)}}</strong></p>
                <p>Webhook: {{WebUtility.HtmlEncode(result.SubscriptionId ?? "pendiente")}}</p>
              </main>
            </body>
            </html>
            """;
    }

    private bool WantsJson(string? mode)
    {
        return string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase) ||
               Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }
}
