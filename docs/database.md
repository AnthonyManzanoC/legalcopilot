# Base de datos PostgreSQL / Supabase

LegalPilot usa PostgreSQL como fuente de verdad cuando existe una de estas variables:

- `LEGALPILOT_DATABASE_URL`
- `ConnectionStrings__LegalPilotPostgres`

No coloque cadenas reales de Supabase en el repositorio. Use variables de entorno, Secret Manager o secretos del proveedor de despliegue.
LegalPilot no inicia sin PostgreSQL configurado salvo que se desactive explicitamente con `LEGALPILOT_STORAGE_REQUIRE_POSTGRES=false`; el fallback JSON queda reservado para pruebas locales.

## Supabase

Formato admitido:

```text
postgresql://USER:PASSWORD@HOST:5432/postgres
```

La aplicacion normaliza esa URL a una cadena Npgsql con SSL requerido. El valor real queda fuera del codigo y fuera de `appsettings.json`.

## Migraciones

Al arrancar, `PostgresLegalPilotPersistence` aplica migraciones idempotentes:

- `legalpilot_schema_migrations`
- tablas por agregado: tenants, users, clients, cases, mailboxes, OAuth tokens cifrados, emails, holidays, deadlines, calendar events, reminders, notifications, WhatsApp, chat, audit, reset tickets, refresh sessions y pipeline IA/RAG
- columnas tipadas para busqueda/indices: `tenant_id`, `email`, `case_number`, `external_id`, `due_date`, `starts_at`, `status`, `provider`, `name`
- `payload jsonb` con el contrato completo de dominio
- indices y claves unicas para usuarios, clientes, causas, buzones, correos externos, plazos derivados de correo, calendario, recordatorios, notificaciones, auditoria y ejecuciones IA
- constraints/FKs principales entre tenant, usuario, cliente, caso, correo, plazo, evento y recordatorio

## Migracion desde JSON

El JSON local no se importa por defecto. Si necesita una migracion controlada desde `App_Data/legalpilot-store.json`, defina `LEGALPILOT_MIGRATE_LOCAL_JSON=true`; solo entonces, con PostgreSQL vacio, el arranque importa ese snapshot a Supabase y despues opera sobre PostgreSQL. La fuente activa se confirma en:

```text
GET /health
GET /api/status
```

`/api/status` debe reportar:

```json
{
  "storage": {
    "provider": "postgresql",
    "postgres": { "status": "Active" }
  }
}
```

## Validacion minima

1. Definir `LEGALPILOT_DATABASE_URL` en el entorno.
2. Ejecutar la API.
3. Confirmar `/health` con `persistence = postgresql`.
4. Entrar con el usuario bootstrap configurado en `LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL`.
5. Crear cliente, caso, plazo y evento.
6. Reiniciar la API.
7. Buscar el caso creado y revisar auditoria/plazos.

## Respaldos

Use backups administrados de Supabase para produccion. Para export manual:

```powershell
pg_dump --dbname "$env:LEGALPILOT_DATABASE_URL" --format custom --file legalpilot.backup
```

Para restaurar:

```powershell
pg_restore --dbname "$env:LEGALPILOT_DATABASE_URL" --clean --if-exists legalpilot.backup
```
