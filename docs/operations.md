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
10. Registre un buzon en Integraciones y pulse OAuth seguro; el link usa `/api/auth/{provider}/login`.
11. Ejecute `dotnet run --project src/LegalPilot.Tests/LegalPilot.Tests.csproj`.

## Gmail

Configuracion requerida:

- `LegalPilot:Gmail:ClientId`
- `LegalPilot:Gmail:ClientSecret`
- `LegalPilot:Gmail:RedirectUri`
- `LegalPilot:Gmail:PubSubTopicName`
- `LegalPilot:Gmail:WebhookSecret`
- `LEGALPILOT_DATA_PROTECTION_KEY`

Flujo implementado:

- `GET /api/auth/gmail/login?email=...` inicia OAuth y redirige a Google.
- `GET /api/auth/gmail/callback` intercambia `code` con `Google.Apis.Auth`, guarda tokens cifrados y llama `users.watch`.
- `users.watch` usa el topic `projects/legalcopilot-497022/topics/notificaciones-gmail` salvo override por variable.
- `POST /api/webhooks/gmail` valida secreto, intenta sincronizar el buzon real y si no existe guarda la notificacion como entrada auditada.
- `Infrastructure/GmailEmailConnector.SyncAsync`
- `POST /api/webhooks/gmail`

Pendiente para produccion:

- Crear el topic Pub/Sub en Google Cloud y conceder a Gmail permisos de publicacion.
- Agregar `history.list` para sincronizacion incremental exacta desde `historyId`.
- Renovacion de watch antes de expirar.

## Microsoft Graph / Outlook / Hotmail

Configuracion requerida:

- `LegalPilot:Microsoft:ClientId`
- `LegalPilot:Microsoft:ClientSecret`
- `LegalPilot:Microsoft:TenantId` opcional; default `common`
- `LegalPilot:Microsoft:RedirectUri`
- `LEGALPILOT_DATA_PROTECTION_KEY`
- `LegalPilot:Microsoft:WebhookClientState`
- `LegalPilot:Microsoft:WebhookNotificationUrl`

Flujo implementado:

- `GET /api/auth/microsoft/login?email=...` inicia OAuth y redirige a Microsoft.
- `GET /api/auth/microsoft/callback` intercambia `code` con MSAL, guarda tokens cifrados y crea suscripcion Graph.
- `POST /api/webhooks/microsoft` devuelve `validationToken` en texto plano cuando Graph valida la URL.
- Las notificaciones reales validan `clientState` antes de sincronizar.
- `Infrastructure/MicrosoftGraphEmailConnector.SyncAsync`
- `GET|POST /api/webhooks/microsoft`

Pendiente para produccion:

- Delta query para recuperacion de fallos.
- Renovar subscriptions antes de `expirationDateTime`.

## Calendario externo

Configuracion requerida:

- OAuth Gmail con scope `calendar.events` o Microsoft con scope `Calendars.ReadWrite`.
- `LegalPilot:Calendar:PreferredProvider` opcional: `auto`, `Gmail` u `Outlook`.

Comportamiento:

- Solo sincroniza eventos confirmados por abogado.
- `POST /api/calendar/events/{id}/sync` intenta crear el evento en Google Calendar u Outlook Calendar.
- `CalendarExternalSyncWorker` revisa eventos confirmados sin `ExternalEventId`.
- Si no hay token OAuth, queda `OAuthCalendarNotConnected`; no se simula exito.

## OpenWA

Configuracion requerida:

- `LegalPilot:OpenWa:BaseUrl`
- `LegalPilot:OpenWa:ApiKey`
- `LegalPilot:OpenWa:SessionId`
- `LegalPilot:OpenWa:WebhookSecret`

Punto de extension:

- `Infrastructure/OpenWaClient.SendMessageAsync`
- `POST /api/webhooks/openwa`

Comportamiento actual:

- Si falta `BaseUrl` o `ApiKey`, el envio queda `ProviderNotConfigured`.
- Si `SessionId` existe, el envio usa `/api/sessions/{sessionId}/messages/send-text`; sin `SessionId` conserva fallback `/api/messages/send`.
- Si `WebhookSecret` esta configurado, `/api/webhooks/openwa` acepta HMAC `X-OpenWA-Signature`/`X-Hub-Signature-256` o secreto compartido.
- Todo mensaje entrante crea `ChatMessage`, alerta al abogado si requiere revision y registra auditoria.
- El asistente solo contesta estado operativo, proximo evento/plazo y deriva asuntos sensibles al abogado.

## IA y entrenamiento futuro

- `GET /api/ai/dataset.jsonl` exporta ejemplos instruccion/input/output para clasificacion y extraccion.
- El dataset es compatible con un flujo de entrenamiento/fine-tuning ligero inspirado en repositorios educativos como `train-llm-from-scratch`, pero produccion debe empezar con modelo base evaluado + RAG.
- La IA nunca calcula plazos; solo expone `TermDays` mencionado para que `EcuadorDeadlineEngine` calcule.

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

Cuando `LEGALPILOT_DATABASE_URL` esta definido, PostgreSQL/Supabase es la fuente de verdad. Al iniciar, la aplicacion crea/aplica migraciones idempotentes y confirma estado en `/health` y `/api/status`. El JSON local queda fuera por defecto; solo se importa si se define `LEGALPILOT_MIGRATE_LOCAL_JSON=true`.

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
