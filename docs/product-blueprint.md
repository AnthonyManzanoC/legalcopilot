# LegalPilot Ecuador - Blueprint tecnico y producto

## 1. Vision del producto

LegalPilot Ecuador ayuda a abogados a convertir correos judiciales, fiscales y mensajes de clientes en acciones controladas: plazos, audiencias, diligencias, alertas, calendario, bitacora y comunicacion segura. La IA extrae y resume; el motor juridico calcula; el abogado aprueba lo sensible.

El producto debe funcionar para abogados independientes, estudios pequenos y firmas con varios equipos, buzones, asistentes y clientes.

## 2. Arquitectura propuesta

Capas:

- Presentacion: panel web, app movil futura, app escritorio futura, portal cliente.
- Aplicacion: casos de uso, permisos, ingesta, calendario, alertas, reportes.
- Dominio: casos, clientes, actos procesales, plazos, reglas, auditoria.
- Infraestructura: PostgreSQL, Redis, cola, object storage, observabilidad.
- Integraciones: Gmail API, Microsoft Graph, OpenWA, calendarios, correo saliente.
- IA/LLM services: OCR, extraccion, clasificacion, resumen, borradores, RAG.
- Motor juridico: reglas deterministicas, feriados, traslados, excepciones, bitacora.
- Scheduler/Jobs: ingesta, renovacion OAuth, recordatorios, reintentos.
- Auditoria: cambios, accesos, aprobaciones, eventos sensibles.
- Notificaciones: panel, email, push, WhatsApp.
- Almacenamiento documental: originales, adjuntos, hashes, OCR y versiones.

Recomendacion inicial: monolito modular ASP.NET Core + PostgreSQL + Redis + Hangfire/Quartz + S3-compatible. Mas adelante, separar ingestion/AI/notifications si el volumen lo exige.

## 3. MVP por fases

Fase 1, 0-30 dias:

- Auth, roles, multitenant base.
- Casos, clientes, buzones.
- Ingesta manual y conectores OAuth preparados.
- Clasificacion legal inicial por reglas + LLM opcional.
- Motor Ecuador auditable con feriados configurables.
- Calendario interno, alertas y auditoria.
- Panel web basico.

Fase 2, 31-60 dias:

- Gmail API real: OAuth, watch Pub/Sub, history sync.
- Microsoft Graph real: OAuth, subscriptions, delta sync.
- OpenWA real: envio, webhooks, plantillas, handoff humano.
- OCR para PDF/imagenes.
- RAG legal con documentos autorizados.
- Reportes por abogado, caso, plazo y riesgo.

Fase 3, 61-90 dias:

- App movil PWA/React Native.
- Portal cliente con permisos granulares.
- MFA, cifrado de secretos, KMS.
- Analitica de riesgo, SLA, salud de integraciones.
- Marketplace de plantillas y reglas.

## 4. Modelo de datos inicial

Entidades base:

- Tenant: estudio juridico o abogado independiente.
- UserAccount: usuario con roles y MFA opcional.
- ClientProfile: cliente del estudio.
- LegalCase: causa, materia, juzgado/fiscalia, responsables.
- MailboxConnection: Gmail/Outlook/Hotmail conectado.
- LegalEmail: correo normalizado con raw, asunto, remitente, adjuntos.
- DocumentAttachment: adjunto original, hash, OCR, metadata.
- LegalExtraction: datos extraidos y confianza.
- Deadline: plazo calculado, estado, responsable, revision humana.
- DeadlineCalculation: regla, feriados usados, pasos de computo.
- CalendarEvent: audiencia, diligencia, vencimiento o tarea.
- Reminder: T-30, T-15, T-7, T-3, T-1, dia del evento.
- Notification: panel, email, push, WhatsApp.
- WhatsAppTemplate/Message: plantillas aprobables y mensajes enviados.
- Holiday: feriados nacionales/locales/excepciones.
- AuditEntry: accion, actor, objeto, timestamp, metadata.

## 5. Flujos principales

Correo judicial:

1. Gmail/Outlook avisa cambio o job sincroniza.
2. Backend descarga mensaje, headers y adjuntos.
3. Se guarda original y hash.
4. OCR extrae texto de adjuntos si hace falta.
5. IA/reglas clasifican acto procesal.
6. Motor calcula plazo si hay termino.
7. Se crean evento, recordatorios y alertas.
8. Abogado revisa y confirma.
9. Cliente recibe mensaje autorizado si aplica.
10. Auditoria registra cada decision.

WhatsApp cliente:

1. OpenWA recibe mensaje por webhook.
2. Sistema identifica cliente/caso si hay contexto.
3. Bot responde solo informacion autorizada.
4. Si hay riesgo legal, deriva a abogado.
5. Mensaje queda auditado.

## 6. Reglas de negocio del motor de plazos

- El computo inicia el dia habil siguiente a la notificacion, salvo regla especial.
- Sabados, domingos y feriados no se cuentan.
- Feriados nacionales, locales, trasladados y descansos obligatorios se cargan como calendario auditable.
- Cada plazo conserva regla aplicada, fecha base, dias requeridos, dias incluidos y excluidos.
- Cualquier excepcion debe tener actor, razon y timestamp.
- El resultado de IA nunca reemplaza el motor deterministico.
- Si la confianza de extraccion es baja, el plazo queda en revision humana.

## 7. Servicios y endpoints

Servicios:

- AuthService
- CaseService
- MailIngestionService
- LegalIntelligenceService
- EcuadorDeadlineEngine
- CalendarService
- ReminderDispatcher
- NotificationService
- WhatsAppService
- AuditService
- HolidayAdminService
- ReportService

Endpoints iniciales ya creados en el backend:

- Auth: login, recuperar contrasena, reset, perfil.
- Casos/clientes: CRUD basico.
- Buzones: conectar/listar.
- Inbox: ingesta manual y webhooks Gmail/Microsoft.
- Legal: calcular plazo.
- Calendario/alertas: eventos, confirmaciones, ACK.
- WhatsApp: plantillas, envio, webhook OpenWA.
- Auditoria/reportes/settings.

## 8. Diseno IA/RAG

Ruta recomendada:

- No entrenar un LLM general desde cero para el MVP.
- Usar modelo base con JSON schema/structured outputs para extraccion.
- RAG con normas, plantillas del estudio, causas, documentos autorizados y politicas internas.
- Fine-tuning pequeno solo para clasificacion/extraccion cuando exista dataset etiquetado.
- Evaluacion con corpus propio: precision de tipo de acto, fecha, termino, juzgado, causa, obligacion.

Componentes:

- OCR service.
- Document normalizer.
- Entity extractor.
- Legal act classifier.
- RAG retriever.
- Draft generator.
- Safety guard: permisos, PII, secreto profesional, confirmacion humana.

## 9. Integracion Gmail/Outlook/OpenWA

Gmail:

- OAuth con scopes minimos.
- `users.watch` publica cambios en Pub/Sub.
- Webhook recibe `emailAddress` y `historyId`.
- Backend usa `history.list`, `messages.get` y `attachments.get`.
- Renovar watch antes de expiracion.

Microsoft:

- OAuth Microsoft identity platform.
- Graph mail messages + change notifications.
- Webhook HTTPS valida `clientState`.
- Delta query para recuperacion de fallos.
- Calendario por `POST /me/calendar/events`.

OpenWA:

- Servicio self-hosted con API key.
- Webhooks para `message.received` y `session.status`.
- HMAC/secret para validar origen.
- Plantillas aprobadas y handoff humano.

## 10. Plan de pruebas

- Unitarias: motor de plazos, feriados, extraccion, permisos.
- Integracion: login, ingesta, clasificacion, plazo, evento, alerta, auditoria.
- Contratos: Gmail Pub/Sub, Graph webhook, OpenWA webhook.
- Seguridad: roles, tenant isolation, secretos, rate limit.
- E2E: correo real de audiencia -> calendario -> WhatsApp -> auditoria.
- Legal QA: casos etiquetados por abogado ecuatoriano.
- Observabilidad: logs, metricas, trazas, dead-letter queue.

## 11. Riesgos y mitigaciones

- Riesgo: plazos mal calculados. Mitigacion: motor deterministico, bitacora, revision humana y calendario oficial actualizable.
- Riesgo: IA alucina. Mitigacion: IA solo extrae/resume, validacion por schema y confianza.
- Riesgo: OAuth o webhooks caen. Mitigacion: polling de respaldo, reintentos, delta sync.
- Riesgo: WhatsApp bloquea sesiones. Mitigacion: politicas de uso, plantillas, monitoreo y fallback email/push.
- Riesgo: datos sensibles. Mitigacion: cifrado, permisos por caso, auditoria y minimizacion de mensajes a clientes.

## 12. Roadmap 90 dias

Dias 1-15:

- Completar persistencia PostgreSQL.
- Migraciones, seeds, Docker Compose.
- Auth JWT real, refresh tokens, roles.
- Pruebas de motor.

Dias 16-30:

- OAuth Gmail/Microsoft.
- Ingesta real y almacenamiento de adjuntos.
- Panel operativo para inbox, casos, plazos.

Dias 31-45:

- OCR, extraccion LLM con JSON schema.
- RAG legal inicial.
- Calendario Google/Microsoft.

Dias 46-60:

- OpenWA real, plantillas, handoff.
- Notificaciones multi-canal.
- Auditoria avanzada.

Dias 61-75:

- Portal cliente.
- Reportes y salud de integraciones.
- MFA y hardening.

Dias 76-90:

- Beta con abogados.
- Dataset etiquetado.
- Fine-tuning pequeno si los resultados lo justifican.
- Preparacion comercial.

## 13. Plan de monetizacion

- Gratis para abogados independientes con limite de casos/buzones.
- Plan Pro por abogado/mes: mas buzones, recordatorios, WhatsApp y OCR.
- Plan Estudio: usuarios, roles, auditoria, reportes y soporte.
- Plan Enterprise: on-prem/private cloud, SSO, SLA y custom rules.
- Publicidad solo en capa gratuita y nunca dentro de mensajes sensibles ni documentos del cliente.

## 14. Backlog tecnico

- PostgreSQL + EF Core o Dapper.
- Redis para locks, cache y rate limiting.
- Hangfire/Quartz para jobs.
- OpenTelemetry.
- Object storage con hash y antivirus.
- OAuth providers.
- OCR pipeline.
- LLM gateway con prompts versionados.
- RAG vector store.
- Tests automatizados.
- CI/CD y Docker Compose.

## 15. Codigo base inicial

Incluido en este repositorio:

- API ASP.NET Core.
- Auth con roles.
- Motor de plazos Ecuador.
- Ingesta y clasificacion inicial.
- Calendario, recordatorios, alertas.
- WhatsApp/OpenWA adapter.
- Auditoria.
- Panel web basico.
