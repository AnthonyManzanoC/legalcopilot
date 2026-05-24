# LegalPilot Ecuador

LegalPilot Ecuador es un monolito modular ASP.NET Core para operar inbox legal, casos, clientes, plazos judiciales de Ecuador, calendario, alertas, chat, auditoria e integraciones preparadas para Gmail, Microsoft Graph y OpenWA.

## Estado implementado

- API ASP.NET Core 8 con minimal APIs por modulo funcional.
- Frontend web responsive servido desde `wwwroot`.
- Autenticacion con access token, refresh token rotativo, logout y recuperacion de contrasena.
- Roles base: `SuperAdmin`, `Lawyer`, `Assistant`, `Client`.
- Persistencia real en PostgreSQL/Supabase via `Npgsql`; en `Production` es obligatoria por `LEGALPILOT_DATABASE_URL` o `ConnectionStrings__LegalPilotPostgres`.
- Fallback local durable en JSON atomico solo para desarrollo/pruebas sin base externa.
- Migracion automatica desde JSON a PostgreSQL si la base esta vacia.
- Motor deterministico de plazos Ecuador con sabados, domingos, feriados, feriados trasladados y excepciones de dia habil.
- Ingesta manual de correos, webhooks Gmail/Microsoft, OAuth con exchange de token, `users.watch` Gmail y suscripciones Graph.
- Normalizacion de adjuntos con hash, persistencia y OCR/text extraction basico para texto/PDF/imagenes con punto de extension OCR real.
- Creacion automatica de plazos, eventos, recordatorios, alertas, sincronizacion Google/Outlook Calendar para eventos confirmados y bitacora.
- Chat interno/WhatsApp auditado.
- OpenWA no simula envios: si faltan credenciales queda `ProviderNotConfigured`; el asistente de cliente responde solo estados operativos y deriva temas sensibles.
- Worker en segundo plano para recordatorios, revision de buzones registrados y plazos vencidos.
- Pipeline IA/RAG preparado con fuentes de conocimiento, ejecuciones auditadas, feedback para fine-tuning ligero y guardrail estricto: la IA no calcula vencimientos.
- Pruebas smoke sin dependencias externas.
- Docker y Docker Compose para ejecucion reproducible.

## Arquitectura

```text
src/LegalPilot.Api/
  Domain/             Entidades, enums y EcuadorDeadlineEngine.
  Application/        Auth, casos, clientes, inbox, workflow, jobs, reportes.
  Infrastructure/     OpenWA, Gmail y Microsoft Graph adapters.
  wwwroot/            Frontend web operativo.
src/LegalPilot.Tests/ Pruebas smoke del motor, auth e inteligencia legal.
docs/                 Blueprint, prompt base y notas de producto.
```

El monolito mantiene separacion por modulos: Auth, Users/Roles, Clients, Cases, Legal Inbox, Legal Intelligence, Deadline Engine, Calendar, Notifications, WhatsApp, Audit, Settings, Reports e Integrations.

## Ejecutar local

```powershell
dotnet run --project src/LegalPilot.Api/LegalPilot.Api.csproj
```

Abre:

```text
http://localhost:5056
```

Credenciales demo:

```text
admin@legalpilot.ec
LegalPilot#2026

abogado@legalpilot.ec
Abogado#2026
```

## Pruebas

```powershell
dotnet run --project src/LegalPilot.Tests/LegalPilot.Tests.csproj
```

Si ya hay una API corriendo y Windows bloquea `bin/Debug`, usa una salida temporal:

```powershell
dotnet run --project src/LegalPilot.Tests/LegalPilot.Tests.csproj -p:BaseOutputPath=.build/test-bin/ -p:UseAppHost=false
```

## Docker

```powershell
copy .env.example .env
# Complete LEGALPILOT_DATABASE_URL, LEGALPILOT_TOKEN_SIGNING_KEY,
# LEGALPILOT_DATA_PROTECTION_KEY y credenciales bootstrap antes de produccion.
docker compose up --build
```

El contenedor expone `http://localhost:5056` y guarda datos en el volumen `legalpilot_data`.

## Configuracion

Variables principales:

- `LegalPilot__TokenSigningKey` o `LEGALPILOT_TOKEN_SIGNING_KEY`: secreto HS256 de al menos 32 caracteres; en produccion use 64+ aleatorios.
- `LEGALPILOT_DATA_PROTECTION_KEY`: secreto de 32+ caracteres o base64 de 32 bytes para cifrar tokens OAuth.
- `LEGALPILOT_DATABASE_URL`: URL PostgreSQL/Supabase. No la guarde en el repositorio.
- `ConnectionStrings__LegalPilotPostgres`: alternativa a `LEGALPILOT_DATABASE_URL` desde Secret Manager/configuracion protegida.
- `LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL`, `LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD`: primer usuario si la base productiva esta vacia.
- `LegalPilot__Storage__Path`: ruta del snapshot durable local y origen de migracion JSON si PostgreSQL esta vacio.
- `LegalPilot__Gmail__ClientId`, `ClientSecret`, `RedirectUri`, `PubSubTopicName`, `WebhookSecret`: credenciales OAuth Gmail y topic Pub/Sub.
- `LegalPilot__Microsoft__ClientId`, `ClientSecret`, `TenantId`, `RedirectUri`, `WebhookClientState`, `WebhookNotificationUrl`: credenciales Microsoft Graph y webhook.
- `LegalPilot__OpenWa__BaseUrl`, `ApiKey`, `SessionId`, `WebhookSecret`: servicio OpenWA.
- `LegalPilot__Calendar__PreferredProvider`: `auto`, `Gmail` u `Outlook` para sincronizacion externa.
- `LegalPilot__AI__Provider`, `Model`, `EmbeddingModel`, `ApiKey`: proveedor IA/RAG cuando se conecte un gateway LLM.

## Endpoints principales

- `GET /health`
- `POST /api/auth/login`
- `POST /api/auth/refresh`
- `POST /api/auth/logout`
- `POST /api/auth/forgot-password`
- `POST /api/auth/reset-password`
- `GET /api/auth/gmail/login`
- `GET /api/auth/gmail/callback`
- `GET /api/auth/microsoft/login`
- `GET /api/auth/microsoft/callback`
- `GET /api/me`
- `GET /api/users`
- `GET|POST /api/cases`
- `GET /api/cases/{id}`
- `GET|POST /api/clients`
- `GET /api/clients/{id}`
- `GET /api/mailboxes`
- `POST /api/mailboxes/connect`
- `POST /api/mailboxes/{id}/sync`
- `GET /api/mailboxes/sync-states`
- `GET /api/integrations/status`
- `POST /api/inbox/manual`
- `GET /api/inbox/{id}/attachments`
- `POST /api/webhooks/gmail`
- `GET|POST /api/webhooks/microsoft`
- `POST /api/webhooks/openwa`
- `GET|POST /api/deadlines`
- `POST /api/deadlines/calculate`
- `PATCH /api/deadlines/{id}/review`
- `GET|POST /api/calendar/events`
- `POST /api/calendar/events/{id}/confirm`
- `POST /api/calendar/events/{id}/sync`
- `GET /api/reminders`
- `GET /api/alerts`
- `POST /api/alerts/{id}/ack`
- `GET /api/chat/messages`
- `POST /api/chat/messages`
- `GET /api/whatsapp/templates`
- `GET /api/whatsapp/messages`
- `POST /api/whatsapp/send-client-message`
- `GET /api/audit`
- `GET|POST /api/settings/holidays`
- `GET /api/reports/overview`
- `GET /api/diagnostics`
- `GET /api/status`
- `GET /openapi.json`
- `GET /api/ai/status`
- `POST /api/ai/analyze`
- `POST /api/ai/knowledge`
- `POST /api/ai/feedback`
- `GET /api/ai/dataset.jsonl`
- `POST /api/oauth/start`
- `GET /api/oauth/{provider}/callback`

## Integraciones

Gmail y Microsoft Graph tienen inicio OAuth real por controllers (`/api/auth/gmail/login` y `/api/auth/microsoft/login`), `state` temporal, callback validado, exchange de token con `Google.Apis.Auth` y MSAL, almacenamiento cifrado con `LEGALPILOT_DATA_PROTECTION_KEY`, refresh token, creacion inmediata de webhook (`users.watch` con Pub/Sub y Graph `/subscriptions`) y sincronizacion inicial de mensajes (`messages.get` / Graph `/me/messages`). Sin credenciales quedan marcados como `ConfigurationMissing`.

OpenWA usa `LegalPilot:OpenWa:BaseUrl`, `ApiKey`, `SessionId` y `WebhookSecret`. Si faltan, los mensajes quedan fallidos con `ProviderNotConfigured`; no se reportan como enviados.

## Persistencia y PostgreSQL

La implementacion activa usa `Npgsql` y migraciones SQL idempotentes en `Infrastructure/PostgresLegalPilotPersistence.cs`. Crea tablas por agregado, columnas tipadas, `payload jsonb`, indices y relaciones principales. Configure Supabase con `LEGALPILOT_DATABASE_URL`; la aplicacion aplicara migraciones al iniciar y `/api/status` confirmara `provider = postgresql`.

Detalle operativo: [docs/database.md](docs/database.md).

## Flujo completo demo

1. Iniciar API y entrar con `admin@legalpilot.ec`.
2. Cargar demo en Tablero y procesar correo.
3. Revisar Inbox legal: clasificacion, causa, resumen y datos extraidos.
4. Revisar Plazos: vence con calculo auditable y feriados Ecuador.
5. Revisar Calendario y Recordatorios generados.
6. Confirmar alerta/evento o aprobar/cancelar plazo.
7. Crear cliente/caso manual.
8. Registrar buzon en Integraciones e iniciar OAuth seguro; el callback crea webhook Gmail/Graph.
9. En Chat, registrar mensaje o intentar envio OpenWA.
10. Revisar Auditoria.

## Limites operativos honestos

- Gmail Pub/Sub activa `users.watch`; la sincronizacion directa ya descarga mensajes recientes y adjuntos. `history.list`, Graph delta query, OCR avanzado y object storage documental quedan como extensiones siguientes.
- La capa IA actual registra fuentes RAG, ejecuciones y feedback; si no hay gateway LLM configurado usa clasificacion deterministica local.
- No hay MFA ni KMS administrado; use Secret Manager/variables protegidas y agregue MFA antes de datos sensibles reales.
