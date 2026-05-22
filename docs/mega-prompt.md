# Mega prompt maestro para construir LegalPilot Ecuador desde cero

Actua como arquitecto principal, tech lead full-stack senior, especialista en IA aplicada, ingeniero de seguridad, arquitecto de datos y disenador de producto legal para Ecuador.

Tu mision es construir LegalPilot Ecuador, una plataforma legal inteligente, multitenant y lista para produccion que ayude a abogados de Ecuador a gestionar correos judiciales/fiscales, causas, plazos, audiencias, diligencias, calendario, WhatsApp con clientes, auditoria y asistencia por IA.

## Principio central

La IA interpreta, extrae, resume y redacta. El calculo juridico de plazos lo hace un motor deterministico, versionado, auditable y configurable. Ningun vencimiento critico puede depender solo del LLM.

## Objetivos funcionales

Construye una plataforma que permita:

1. Autenticacion segura con roles: superadmin, abogado, asistente y cliente.
2. Multitenancy para estudios juridicos.
3. Gestion de casos, clientes, responsables y permisos por caso.
4. Conexion Gmail via OAuth, lectura de mensajes, adjuntos y push notifications con Pub/Sub.
5. Conexion Outlook/Hotmail/Microsoft 365 via Microsoft Graph, mail, calendario y change notifications.
6. Ingesta normalizada de correos, raw email, headers, adjuntos, hashes, estado y trazabilidad.
7. OCR de PDFs escaneados e imagenes.
8. Clasificacion legal inicial: audiencia, providencia, citacion, oficio, fiscalia, pericia, version, plazo, diligencia, escrito pendiente.
9. Extraccion estructurada: numero de causa, juzgado/fiscalia, materia, partes, fecha, hora, lugar, termino, obligacion, prioridad, requiere respuesta.
10. Motor de plazos Ecuador con sabados, domingos, feriados nacionales/locales, traslados, descansos obligatorios, excepciones y bitacora.
11. Calendario interno y sincronizacion Google/Microsoft.
12. Recordatorios T-30, T-15, T-7, T-3, T-1 y mismo dia.
13. Notificaciones por panel, email, push y WhatsApp.
14. Integracion OpenWA self-hosted con API key, webhooks, HMAC y plantillas aprobables.
15. Comunicacion con clientes sin revelar informacion sensible innecesaria.
16. Resumen ejecutivo para abogado y version simple para cliente.
17. Borradores de respuesta legal asistidos por IA con aprobacion humana.
18. Auditoria completa de accesos, cambios, calculos, aprobaciones y notificaciones.
19. Reportes operativos: plazos por vencer, audiencias, casos en riesgo, fallos de integracion.
20. Observabilidad: logs estructurados, metricas, trazas, health checks, jobs y reintentos.

## Stack recomendado

Backend:

- ASP.NET Core 8 o 9 como monolito modular.
- PostgreSQL como base principal.
- Redis para cache, locks, rate limit y jobs ligeros.
- Hangfire o Quartz para jobs.
- RabbitMQ/Kafka solo si el volumen exige desacoplar ingesta, IA y notificaciones.
- S3/MinIO para documentos.
- OpenTelemetry para observabilidad.

Frontend:

- Web dashboard con React/Next.js o Razor/SPA inicial.
- PWA o React Native para movil.
- Electron/Tauri solo si escritorio aporta valor operativo real.

IA:

- Modelo base via API o self-hosted pequeno.
- RAG con normas, documentos internos, plantillas y causas autorizadas.
- Extraccion por JSON schema.
- Fine-tuning pequeno solo para clasificacion/extraccion si hay dataset etiquetado.
- No entrenar LLM general desde cero para MVP; usar el repo de entrenamiento desde cero solo como laboratorio, no como dependencia productiva inicial.

Integraciones:

- Gmail API: `users.watch`, Pub/Sub, `history.list`, `messages.get`, `attachments.get`.
- Microsoft Graph: subscriptions/webhooks, messages, delta query, calendar events.
- OpenWA: sessions, messages, webhooks, templates internas.

## Arquitectura obligatoria

Divide el sistema en:

- Presentation
- Application
- Domain
- Infrastructure
- Integrations
- AI/LLM Services
- Legal Deadline Engine
- Scheduler/Jobs
- Audit
- Notifications
- Document Storage

Usa puertos/adaptadores. El dominio no debe conocer Gmail, Graph, OpenWA, EF Core ni proveedores de IA.

## Reglas del motor Ecuador

Implementa:

- Computo desde el dia habil siguiente a la notificacion, salvo regla especial.
- Excluir sabados, domingos y feriados.
- Soportar feriados trasladados y descansos obligatorios configurables.
- Soportar feriados locales/provinciales.
- Permitir excepciones manuales con razon y actor.
- Guardar detalle de cada dia evaluado: fecha, incluido/excluido, motivo y numero de dia habil contado.
- Versionar reglas.
- Marcar baja confianza cuando la fecha, el termino o el tipo de acto venga solo de IA sin validacion.

## Entregables esperados

1. Vision producto.
2. Arquitectura.
3. MVP por fases.
4. Modelo de datos.
5. Flujos.
6. Reglas de negocio.
7. Servicios y endpoints.
8. Diseno IA/RAG.
9. Diseno integraciones.
10. Plan de pruebas.
11. Riesgos y mitigaciones.
12. Roadmap 90 dias.
13. Monetizacion.
14. Backlog tecnico.
15. Codigo base ejecutable.

## Criterios de calidad

- No rompas codigo existente.
- No inventes resultados legales no verificables.
- Donde falte fuente oficial o configuracion, deja el dato configurable y auditado.
- Prioriza seguridad, trazabilidad y confiabilidad sobre magia de IA.
- Agrega pruebas del motor de plazos.
- Todo endpoint sensible requiere autenticacion y tenant isolation.
- Todo webhook valida origen o queda marcado como no confiable.
- Todo mensaje a cliente debe pasar por politica de minimizacion.

## Primera version que debes construir

Construye un monolito que ya permita:

- Login.
- Roles.
- Casos.
- Clientes.
- Conexion declarativa Gmail/Outlook.
- Ingesta manual y webhooks preparados.
- Clasificacion legal inicial.
- Motor de plazos Ecuador.
- Calendario interno.
- Alertas.
- OpenWA adapter.
- Auditoria.
- Panel web basico.

Despues evoluciona a:

- PostgreSQL real.
- OAuth real.
- OCR.
- RAG.
- Calendarios externos.
- App movil.
- Portal cliente.
- Fine-tuning pequeno.
