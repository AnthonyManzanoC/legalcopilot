# LegalPilot Ecuador - Operacion e integraciones

## Checklist local

1. Configure `LegalPilot:TokenSigningKey` con un secreto largo.
2. Configure `LEGALPILOT_DATABASE_URL` para cualquier despliegue productivo.
3. Configure `LEGALPILOT_DATA_PROTECTION_KEY` para cifrar tokens OAuth.
4. Configure `LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL` y `LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD` si la base esta vacia en produccion.
5. Ejecute `dotnet run --project src/LegalPilot.Api/LegalPilot.Api.csproj`.
6. Entre al panel en `http://localhost:5056`.
7. Revise Estado del sistema: debe indicar `PostgreSQL activo` cuando Supabase este configurado.
8. Procese un correo demo.
9. Confirme que se crearon inbox, plazo, calendario, recordatorios y auditoria.
10. Ejecute `dotnet run --project src/LegalPilot.Tests/LegalPilot.Tests.csproj`.

## Gmail

Configuracion requerida:

- `LegalPilot:Gmail:ClientId`
- `LegalPilot:Gmail:ClientSecret`
- `LegalPilot:Gmail:RedirectUri`
- `LEGALPILOT_DATA_PROTECTION_KEY`

Punto de extension:

- `Infrastructure/GmailEmailConnector.SyncAsync`
- `POST /api/webhooks/gmail`

Pendiente para produccion:

- OAuth callback con exchange de token.
- Guardar refresh token cifrado.
- `messages.list` y `messages.get` para sincronizacion directa.
- `users.watch` con Pub/Sub para notificaciones.
- `history.list` y `attachments.get` para sincronizacion incremental y adjuntos.
- Renovacion de watch antes de expirar.

## Microsoft Graph / Outlook / Hotmail

Configuracion requerida:

- `LegalPilot:Microsoft:ClientId`
- `LegalPilot:Microsoft:ClientSecret`
- `LegalPilot:Microsoft:TenantId`
- `LegalPilot:Microsoft:RedirectUri`
- `LEGALPILOT_DATA_PROTECTION_KEY`
- `LegalPilot:Microsoft:WebhookClientState` si se usa webhook Graph.

Punto de extension:

- `Infrastructure/MicrosoftGraphEmailConnector.SyncAsync`
- `GET|POST /api/webhooks/microsoft`

Pendiente para produccion:

- OAuth callback con exchange de token.
- Validar `clientState`.
- Subscriptions para change notifications.
- Delta query para recuperacion de fallos.
- Almacenamiento cifrado de tokens.

## OpenWA

Configuracion requerida:

- `LegalPilot:OpenWa:BaseUrl`
- `LegalPilot:OpenWa:ApiKey`
- `LegalPilot:OpenWa:WebhookSecret`

Punto de extension:

- `Infrastructure/OpenWaClient.SendMessageAsync`
- `POST /api/webhooks/openwa`

Comportamiento actual:

- Si falta `BaseUrl` o `ApiKey`, el envio queda `ProviderNotConfigured`.
- Si `WebhookSecret` esta configurado, `/api/webhooks/openwa` exige `X-OpenWA-Webhook-Secret` o `X-Webhook-Secret`.
- Todo mensaje entrante crea `ChatMessage`, alerta al abogado y registra auditoria.

## Motor de plazos Ecuador

Archivo principal:

- `Domain/EcuadorDeadlineEngine.cs`

Garantias:

- Cuenta desde el dia siguiente a la notificacion.
- Excluye sabados, domingos y feriados aplicables.
- Soporta feriados nacionales, provincia, canton, judicatura y excepciones del estudio.
- Soporta `IsBusinessDayOverride` para habilitar un dia normalmente excluido.
- Devuelve pasos, feriados aplicados y explicacion.

## Persistencia

Archivos actuales:

- `Application/LegalPilotStore.cs`
- `Infrastructure/PostgresLegalPilotPersistence.cs`
- `docs/database.md`

Cuando `LEGALPILOT_DATABASE_URL` esta definido, PostgreSQL/Supabase es la fuente de verdad. Al iniciar, la aplicacion crea/aplica migraciones idempotentes, importa el JSON local si la base esta vacia y confirma estado en `/health` y `/api/status`.

Sin esa variable, conserva fallback JSON atomico solo para desarrollo local y pruebas. En `Production`, el arranque falla si PostgreSQL no esta configurado.

## Seguridad antes de produccion real

- Cambiar secreto JWT y no usar claves efimeras de desarrollo.
- Usar Secret Manager, variables protegidas del host o secret store del proveedor para la URL de Supabase.
- Usar Secret Manager para `LEGALPILOT_DATA_PROTECTION_KEY`, OAuth client secrets y OpenWA API key.
- Forzar HTTPS y cookies seguras si se mueve refresh token a cookie.
- Cifrar tokens OAuth y secretos de integracion.
- Agregar MFA para abogados/admins.
- Agregar rate limit por IP/cuenta.
- Revisar politicas de retencion de correos, adjuntos y mensajes.
- Completar tenant isolation en cada nueva entidad que se agregue.
