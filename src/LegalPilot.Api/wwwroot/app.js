const state = {
  token: localStorage.getItem("legalpilot.token"),
  refreshToken: localStorage.getItem("legalpilot.refresh"),
  user: null,
  view: "dashboard",
  cases: [],
  clients: [],
  users: []
};

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

const emptyGuid = null;

const escapeHtml = (value) => String(value ?? "")
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll("\"", "&quot;");

const toNullable = (value) => value ? value : emptyGuid;

const formatDateTime = (value) => {
  if (!value) return "-";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString("es-EC", { dateStyle: "medium", timeStyle: "short" });
};

const tagClass = (value) => {
  const raw = String(value ?? "").toLowerCase();
  if (["alta", "failed", "configurationmissing", "providernotconfigured", "overdue", "cancelled"].some((item) => raw.includes(item))) return "high";
  if (["confirmed", "sent", "configured", "oauthready", "acknowledged", "stored"].some((item) => raw.includes(item))) return "ok";
  return "";
};

const showToast = (message, kind = "ok") => {
  const toast = $("#toast");
  toast.textContent = message;
  toast.className = `toast ${kind}`;
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => toast.classList.add("hidden"), 3600);
};

const setLoading = (button, loading) => {
  if (!button) return;
  button.disabled = loading;
  button.dataset.originalText ||= button.textContent;
  button.textContent = loading ? "Procesando..." : button.dataset.originalText;
};

const refreshAccessToken = async () => {
  if (!state.refreshToken) return false;
  const response = await fetch("/api/auth/refresh", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ refreshToken: state.refreshToken })
  });
  if (!response.ok) return false;
  const data = await response.json();
  state.token = data.accessToken;
  state.refreshToken = data.refreshToken;
  localStorage.setItem("legalpilot.token", state.token);
  localStorage.setItem("legalpilot.refresh", state.refreshToken);
  return true;
};

const api = async (path, options = {}, retry = true) => {
  const headers = {
    "Content-Type": "application/json",
    ...(options.headers || {})
  };

  if (state.token) {
    headers.Authorization = `Bearer ${state.token}`;
  }

  const response = await fetch(path, { ...options, headers });
  const isJson = response.headers.get("content-type")?.includes("application/json");
  const data = isJson ? await response.json() : await response.text();

  if (response.status === 401 && retry && await refreshAccessToken()) {
    return api(path, options, false);
  }

  if (!response.ok) {
    throw new Error(data?.error || data || "Solicitud fallida");
  }

  return data;
};

const bindReferenceSelects = () => {
  $$("[data-case-select]").forEach((select) => {
    const current = select.value;
    const first = select.options[0]?.textContent || "Sin caso";
    select.innerHTML = `<option value="">${escapeHtml(first)}</option>` + state.cases.map((item) =>
      `<option value="${item.id}">${escapeHtml(item.caseNumber)} - ${escapeHtml(item.title)}</option>`
    ).join("");
    select.value = current;
  });

  $$("[data-client-select]").forEach((select) => {
    const current = select.value;
    const first = select.options[0]?.textContent || "Sin cliente";
    select.innerHTML = `<option value="">${escapeHtml(first)}</option>` + state.clients.map((item) =>
      `<option value="${item.id}">${escapeHtml(item.fullName)} - ${escapeHtml(item.phone)}</option>`
    ).join("");
    select.value = current;
  });
};

const loadReferences = async () => {
  const [cases, clients, users] = await Promise.all([
    api("/api/cases"),
    api("/api/clients"),
    api("/api/users")
  ]);
  state.cases = cases;
  state.clients = clients;
  state.users = users;
  bindReferenceSelects();
};

const showApp = async () => {
  if (!state.token) {
    $("#loginPanel").classList.remove("hidden");
    $("#appPanel").classList.add("hidden");
    $(".sidebar").classList.add("hidden");
    $("#userBox").textContent = "Sin sesion";
    $("#systemChip").textContent = "Sin sesion";
    $("#systemChip").className = "status-chip";
    return;
  }

  try {
    const me = await api("/api/me");
    state.user = me.user;
    $("#loginPanel").classList.add("hidden");
    $("#appPanel").classList.remove("hidden");
    $(".sidebar").classList.remove("hidden");
    $("#userBox").textContent = `${me.user.displayName} - ${me.tenant.name}`;
    $("#systemChip").textContent = "Conectado";
    $("#systemChip").className = "status-chip ok";
    await refreshAll();
  } catch (error) {
    localStorage.removeItem("legalpilot.token");
    localStorage.removeItem("legalpilot.refresh");
    state.token = null;
    state.refreshToken = null;
    $("#loginError").textContent = error.message;
    $("#loginPanel").classList.remove("hidden");
    $("#appPanel").classList.add("hidden");
    $(".sidebar").classList.add("hidden");
    $("#systemChip").textContent = "Sesion requerida";
    $("#systemChip").className = "status-chip";
  }
};

const switchView = async (view) => {
  state.view = view;
  $$(".nav").forEach((button) => button.classList.toggle("active", button.dataset.view === view));
  $$(".view").forEach((section) => section.classList.toggle("active-view", section.id === view));
  $("#viewTitle").textContent = $(`.nav[data-view="${view}"]`)?.textContent || "LegalPilot";
  await refreshView(view);
};

const refreshAll = async () => {
  await loadReferences();
  await Promise.allSettled([
    loadOverview(),
    loadInbox(),
    loadDeadlines(),
    loadCalendar(),
    loadCases(),
    loadClients(),
    loadChat(),
    loadAi(),
    loadAlerts(),
    loadIntegrations(),
    loadReports(),
    loadSystemStatus(),
    loadSettings(),
    loadAudit()
  ]);
};

const refreshView = async (view) => {
  const map = {
    dashboard: loadOverview,
    inbox: loadInbox,
    deadlines: loadDeadlines,
    calendar: loadCalendar,
    cases: async () => { await loadReferences(); await loadCases(); },
    clients: async () => { await loadReferences(); await loadClients(); },
    chat: loadChat,
    ai: loadAi,
    alerts: loadAlerts,
    integrations: loadIntegrations,
    reports: loadReports,
    system: loadSystemStatus,
    settings: loadSettings,
    audit: loadAudit
  };
  if (map[view]) {
    await map[view]();
  }
};

const loadOverview = async () => {
  const data = await api("/api/reports/overview");
  const items = [
    ["Casos", data.cases],
    ["Clientes", data.clients],
    ["Correos", data.emails],
    ["Plazos", data.deadlines],
    ["Riesgo 72h", data.deadlinesDueSoon],
    ["Revision", data.pendingReview],
    ["Eventos", data.events],
    ["Alertas", data.alerts],
    ["Chat", data.chats],
    ["Sync issues", data.syncIssues]
  ];

  $("#metrics").innerHTML = items.map(([label, value]) => `
    <article class="metric">
      <strong>${value ?? 0}</strong>
      <span>${label}</span>
    </article>
  `).join("");

  const [deadlines, alerts, sync] = await Promise.all([
    api("/api/deadlines"),
    api("/api/alerts"),
    api("/api/mailboxes/sync-states")
  ]);

  const due = deadlines.slice(0, 3).map((deadline) => itemTemplate(
    deadline.title,
    `Vence ${deadline.dueDate} - ${deadline.status}`,
    deadline.calculation?.explanation,
    deadline.status
  )).join("");
  const alertItems = alerts.slice(0, 2).map((alert) => itemTemplate(alert.title, formatDateTime(alert.createdAt), alert.message, alert.status)).join("");
  const syncItems = sync.slice(0, 2).map((entry) => itemTemplate(entry.provider, formatDateTime(entry.checkedAt), entry.message, entry.status)).join("");
  $("#dashboardFocus").innerHTML = due || alertItems || syncItems ? due + alertItems + syncItems : `<p class="muted">Sin riesgos pendientes.</p>`;
};

const itemTemplate = (title, subtitle, body, tag) => `
  <article class="item">
    <div class="item-row">
      <strong>${escapeHtml(title)}</strong>
      ${tag ? `<span class="tag ${tagClass(tag)}">${escapeHtml(tag)}</span>` : ""}
    </div>
    <span>${escapeHtml(subtitle)}</span>
    ${body ? `<p>${escapeHtml(body)}</p>` : ""}
  </article>
`;

const loadMailboxes = async () => {
  const items = await api("/api/mailboxes");
  $("#mailboxes").innerHTML = items.length ? items.map((mailbox) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(mailbox.email)}</strong>
        <span class="tag ${tagClass(mailbox.status)}">${escapeHtml(mailbox.provider)}</span>
      </div>
      <span>${escapeHtml(mailbox.status)} - Ultima revision: ${formatDateTime(mailbox.lastSyncAt)}</span>
      <button class="compact fit" data-sync-mailbox="${mailbox.id}">Sincronizar</button>
    </article>
  `).join("") : `<p class="muted">No hay buzones registrados.</p>`;
};

const loadIntegrations = async () => {
  const [status, syncStates] = await Promise.all([
    api("/api/integrations/status"),
    api("/api/mailboxes/sync-states"),
    loadMailboxes()
  ]);

  const mail = status.mail.map((item) => itemTemplate(
    item.provider,
    item.status,
    `${item.message}${item.requiredSettings?.length ? " Faltan: " + item.requiredSettings.join(", ") : ""}`,
    item.status
  )).join("");
  const openWa = itemTemplate("OpenWA", status.openWa.status, status.openWa.requiredSettings.join(", "), status.openWa.status);
  $("#integrationStatus").innerHTML = mail + openWa;
  $("#syncStates").innerHTML = syncStates.length ? syncStates.slice(0, 8).map((entry) => itemTemplate(
    `${entry.provider} sync`,
    formatDateTime(entry.checkedAt),
    `${entry.message} Fallos: ${entry.failureCount}`,
    entry.status
  )).join("") : `<p class="muted">Sin intentos de sincronizacion.</p>`;
};

const loadReports = async () => {
  const [overview, deadlines, events, inbox] = await Promise.all([
    api("/api/reports/overview"),
    api("/api/deadlines"),
    api("/api/calendar/events"),
    api("/api/inbox")
  ]);

  const ratios = [
    ["Casos activos", overview.cases, "Expedientes con responsable y trazabilidad."],
    ["Plazos abiertos", overview.deadlines, `${overview.deadlinesDueSoon} vencen en 72h.`],
    ["Eventos", overview.events, "Audiencias, diligencias y tareas registradas."],
    ["Correos", overview.emails, "Notificaciones procesadas por el inbox legal."],
    ["Auditoria", overview.audit, "Acciones trazables para cumplimiento."],
    ["Alertas", overview.alerts, "Pendientes de revision del equipo."]
  ];

  $("#reportOverview").innerHTML = ratios.map(([label, value, detail]) => `
    <article class="report-card">
      <span>${escapeHtml(label)}</span>
      <strong>${value ?? 0}</strong>
      <p>${escapeHtml(detail)}</p>
    </article>
  `).join("");

  const pending = deadlines.filter((item) => item.status === "PendingReview").length;
  const confirmed = deadlines.filter((item) => item.status === "Confirmed").length;
  const nextEvent = events[0];
  const latestEmail = inbox[0];

  $("#reportRisk").innerHTML = [
    itemTemplate("Plazos confirmados", `${confirmed} listos para seguimiento`, "Validacion juridica registrada por el equipo.", confirmed ? "Confirmed" : ""),
    itemTemplate("Pendientes de revision", `${pending} requieren abogado`, "Los resultados ambiguos quedan visibles para decision humana.", pending ? "Alta" : "Confirmed"),
    nextEvent ? itemTemplate("Proximo evento", formatDateTime(nextEvent.startsAt), nextEvent.title, nextEvent.confirmed ? "Confirmed" : "PendingReview") : "",
    latestEmail ? itemTemplate("Ultimo inbox", formatDateTime(latestEmail.receivedAt), latestEmail.subject, latestEmail.processingStatus) : ""
  ].filter(Boolean).join("");
};

const loadSystemStatus = async () => {
  const status = await api("/api/status");
  const storageOk = status.storage?.provider === "postgresql";
  $("#systemChip").textContent = storageOk ? "PostgreSQL activo" : "Modo local";
  $("#systemChip").className = `status-chip ${storageOk ? "ok" : ""}`;

  const counts = status.counts || {};
  $("#systemStatus").innerHTML = Object.entries(counts).map(([key, value]) => `
    <article class="system-card">
      <span>${escapeHtml(key)}</span>
      <strong>${value}</strong>
    </article>
  `).join("");

  const mail = status.integrations?.mail || [];
  const openWa = status.integrations?.openWa;
  const ai = status.integrations?.ai;
  $("#systemInfra").innerHTML = [
    itemTemplate("Persistencia", status.storage.provider, `${status.storage.postgres.message} Fuente: ${status.storage.dataSource}`, status.storage.postgres.status),
    itemTemplate("Seguridad", status.security.auth, `${status.security.roles} usuarios activos / ${status.security.activeRefreshSessions} sesiones refresh activas.`, "Configured"),
    itemTemplate("Jobs", "Recordatorios y buzones", `${status.jobs.remindersPending} recordatorios pendientes / ${status.jobs.mailboxSyncStates} estados de sync.`, "Configured"),
    ...mail.map((item) => itemTemplate(item.provider, item.status, item.message, item.status)),
    openWa ? itemTemplate("OpenWA", openWa.status, openWa.message, openWa.status) : "",
    ai ? itemTemplate("IA/RAG", `${ai.provider} / ${ai.model}`, `${ai.knowledgeDocuments} fuentes / ${ai.processingRuns} ejecuciones auditadas.`, "Configured") : ""
  ].filter(Boolean).join("");
};

const loadInbox = async () => {
  const items = await api("/api/inbox");
  $("#inboxList").innerHTML = items.length ? items.map((email) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(email.subject)}</strong>
        <span class="tag ${tagClass(email.extraction?.priority)}">${escapeHtml(email.extraction?.actType || "Sin clasificar")}</span>
      </div>
      <span>${escapeHtml(email.sender)} - ${formatDateTime(email.receivedAt)}</span>
      <p>${escapeHtml(email.extraction?.lawyerSummary || "Sin resumen")}</p>
      <p class="muted">${escapeHtml(email.extraction?.clientSummary || "")}</p>
    </article>
  `).join("") : `<p class="muted">Aun no hay correos procesados.</p>`;
};

const loadDeadlines = async () => {
  const items = await api("/api/deadlines");
  $("#deadlineList").innerHTML = items.length ? items.map((deadline) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(deadline.title)}</strong>
        <span class="tag ${tagClass(deadline.status)}">${escapeHtml(deadline.status)}</span>
      </div>
      <span>Vence: ${deadline.dueDate} - ${deadline.termDays} dias - confianza ${Math.round(deadline.confidence * 100)}%</span>
      <p>${escapeHtml(deadline.calculation?.explanation || "")}</p>
      <div class="actions">
        <button class="compact" data-review-deadline="${deadline.id}" data-approved="true">Aprobar</button>
        <button class="compact ghost inline" data-review-deadline="${deadline.id}" data-approved="false">Cancelar</button>
      </div>
    </article>
  `).join("") : `<p class="muted">No hay plazos calculados.</p>`;
};

const loadCalendar = async () => {
  const items = await api("/api/calendar/events");
  $("#calendarList").innerHTML = items.length ? items.map((event) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(event.title)}</strong>
        <span class="tag ${event.confirmed ? "ok" : ""}">${escapeHtml(event.type)}</span>
      </div>
      <span>${formatDateTime(event.startsAt)} - ${event.confirmed ? "Confirmado" : "Pendiente"}</span>
      <p>${escapeHtml(event.location || "Sin ubicacion")}</p>
      ${event.confirmed ? "" : `<button class="compact fit" data-confirm-event="${event.id}">Confirmar</button>`}
    </article>
  `).join("") : `<p class="muted">No hay eventos.</p>`;
};

const loadCases = async () => {
  const items = await api("/api/cases");
  state.cases = items;
  bindReferenceSelects();
  $("#caseList").innerHTML = items.length ? items.map((legalCase) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(legalCase.title)}</strong>
        <span class="tag ${tagClass(legalCase.status)}">${escapeHtml(legalCase.status)}</span>
      </div>
      <span>${escapeHtml(legalCase.caseNumber)} - ${escapeHtml(legalCase.matter)}</span>
      <p>${escapeHtml(legalCase.courtOrOffice)}</p>
    </article>
  `).join("") : `<p class="muted">No hay casos.</p>`;
};

const loadClients = async () => {
  const items = await api("/api/clients");
  state.clients = items;
  bindReferenceSelects();
  $("#clientList").innerHTML = items.length ? items.map((client) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(client.fullName)}</strong>
        <span class="tag">${escapeHtml(client.identification)}</span>
      </div>
      <span>${escapeHtml(client.email)} - ${escapeHtml(client.phone)}</span>
    </article>
  `).join("") : `<p class="muted">No hay clientes.</p>`;
};

const loadChat = async () => {
  const [messages, whatsApp] = await Promise.all([
    api("/api/chat/messages"),
    api("/api/whatsapp/messages")
  ]);

  const chatItems = messages.map((message) => `
    <article class="item ${message.direction === "Outbound" ? "outbound" : ""}">
      <div class="item-row">
        <strong>${escapeHtml(message.authorName)}</strong>
        <span class="tag ${tagClass(message.status)}">${escapeHtml(message.channel)} / ${escapeHtml(message.direction)}</span>
      </div>
      <span>${formatDateTime(message.createdAt)} - ${escapeHtml(message.status)}</span>
      <p>${escapeHtml(message.body)}</p>
    </article>
  `).join("");

  const waItems = whatsApp.slice(0, 4).map((message) => itemTemplate(
    `WhatsApp a ${message.to}`,
    formatDateTime(message.createdAt),
    message.body,
    message.status
  )).join("");

  $("#chatList").innerHTML = chatItems || waItems ? chatItems + waItems : `<p class="muted">Sin mensajes.</p>`;
};

const loadAlerts = async () => {
  const [alerts, reminders] = await Promise.all([
    api("/api/alerts"),
    api("/api/reminders")
  ]);
  $("#alertList").innerHTML = alerts.length ? alerts.map((alert) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(alert.title)}</strong>
        <span class="tag ${tagClass(alert.status)}">${escapeHtml(alert.status)}</span>
      </div>
      <span>${formatDateTime(alert.createdAt)} - ${escapeHtml(alert.channel)}</span>
      <p>${escapeHtml(alert.message)}</p>
      ${alert.status === "Acknowledged" ? "" : `<button class="compact fit" data-ack-alert="${alert.id}">Marcar revisada</button>`}
    </article>
  `).join("") : `<p class="muted">Sin alertas.</p>`;

  $("#reminderList").innerHTML = reminders.length ? reminders.map((reminder) => itemTemplate(
    reminder.channel,
    formatDateTime(reminder.sendAt),
    reminder.message,
    reminder.status
  )).join("") : `<p class="muted">Sin recordatorios programados.</p>`;
};

const loadAi = async () => {
  const status = await api("/api/ai/status");
  const guardrails = status.guardrails || {};
  const runs = status.recentRuns || [];
  $("#aiStatus").innerHTML = [
    itemTemplate("Proveedor IA", `${status.provider} / ${status.model}`, "La IA clasifica, extrae y resume. El motor juridico calcula plazos de forma deterministica.", "Configured"),
    itemTemplate("RAG", `${status.rag?.documents ?? 0} fuentes registradas`, `Modelo embeddings: ${status.rag?.embeddingModel || "no configurado"}. Ready: ${status.rag?.ready ? "si" : "no"}.`, status.rag?.ready ? "Configured" : "Pending"),
    itemTemplate("Fine-tuning ligero", `${status.fineTuning?.feedbackSamples ?? 0} muestras de feedback`, `Etiquetas: ${(status.fineTuning?.labels || []).join(", ")}`, status.fineTuning?.status || "Prepared"),
    itemTemplate("Guardrail plazos", guardrails.noDeadlineCalculationByLlm ? "Activo" : "Revisar", "Ningun vencimiento se calcula por LLM; se delega al backend auditable EcuadorDeadlineEngine.", guardrails.noDeadlineCalculationByLlm ? "Configured" : "Alta"),
    ...runs.slice(0, 5).map((run) => itemTemplate(run.purpose, formatDateTime(run.createdAt), `${run.status} / revision humana: ${run.requiresHumanReview ? "si" : "no"}`, run.status))
  ].join("");
};

const loadSettings = async () => {
  const items = await api("/api/settings/holidays");
  $("#holidayList").innerHTML = items.length ? items.map((holiday) => itemTemplate(
    holiday.name,
    `${holiday.date} - ${holiday.scope}`,
    `${holiday.source}${holiday.isBusinessDayOverride ? " - dia habil manual" : ""}`,
    holiday.isBusinessDayOverride ? "Configured" : holiday.scope
  )).join("") : `<p class="muted">No hay feriados configurados.</p>`;
};

const loadAudit = async () => {
  const items = await api("/api/audit");
  $("#auditList").innerHTML = items.length ? items.map((entry) => `
    <article class="item">
      <div class="item-row">
        <strong>${escapeHtml(entry.action)}</strong>
        <span class="tag">${formatDateTime(entry.createdAt)}</span>
      </div>
      <span>${escapeHtml(entry.entityType)} / ${escapeHtml(entry.entityId)}</span>
      <p>${escapeHtml(entry.summary)}</p>
    </article>
  `).join("") : `<p class="muted">Sin registros.</p>`;
};

const submitJson = async (event, path, payloadFactory, after) => {
  event.preventDefault();
  const formElement = event.currentTarget;
  const button = $("button[type='submit']", formElement);
  setLoading(button, true);
  try {
    const form = new FormData(formElement);
    await api(path, {
      method: "POST",
      body: JSON.stringify(payloadFactory(form))
    });
    formElement.reset();
    setDefaultDates();
    showToast("Operacion completada.");
    if (after) await after();
  } catch (error) {
    showToast(error.message, "error");
  } finally {
    setLoading(button, false);
  }
};

$("#loginForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("#loginError").textContent = "";
  const form = new FormData(event.currentTarget);

  try {
    const result = await api("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({
        email: form.get("email"),
        password: form.get("password")
      })
    });
    state.token = result.accessToken;
    state.refreshToken = result.refreshToken;
    localStorage.setItem("legalpilot.token", state.token);
    localStorage.setItem("legalpilot.refresh", state.refreshToken);
    await showApp();
  } catch (error) {
    $("#loginError").textContent = error.message;
  }
});

$("#forgotToggle").addEventListener("click", () => {
  $("#loginForm").classList.add("hidden");
  $("#forgotForm").classList.remove("hidden");
});

$("#backToLogin").addEventListener("click", () => {
  $("#forgotForm").classList.add("hidden");
  $("#loginForm").classList.remove("hidden");
});

$("#forgotForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  try {
    const result = await api("/api/auth/forgot-password", {
      method: "POST",
      body: JSON.stringify({ email: form.get("email") })
    });
    $("#forgotMessage").textContent = result.devResetToken
      ? `${result.message} Token dev: ${result.devResetToken}`
      : result.message;
    if (result.devResetToken) {
      $("#forgotForm [name='token']").value = result.devResetToken;
    }
  } catch (error) {
    $("#forgotMessage").textContent = error.message;
  }
});

$("#resetPasswordBtn").addEventListener("click", async () => {
  const form = new FormData($("#forgotForm"));
  try {
    const result = await api("/api/auth/reset-password", {
      method: "POST",
      body: JSON.stringify({ token: form.get("token"), newPassword: form.get("newPassword") })
    });
    $("#forgotMessage").textContent = result.message;
  } catch (error) {
    $("#forgotMessage").textContent = error.message;
  }
});

$("#logoutBtn").addEventListener("click", async () => {
  try {
    if (state.token) {
      await api("/api/auth/logout", {
        method: "POST",
        body: JSON.stringify({ refreshToken: state.refreshToken })
      });
    }
  } catch {
    // Local logout still wins when the server session is already gone.
  }
  localStorage.removeItem("legalpilot.token");
  localStorage.removeItem("legalpilot.refresh");
  state.token = null;
  state.refreshToken = null;
  state.user = null;
  await showApp();
});

$$(".nav").forEach((button) => {
  button.addEventListener("click", () => switchView(button.dataset.view));
});

$$("[data-refresh]").forEach((button) => {
  button.addEventListener("click", () => refreshView(button.dataset.refresh));
});

$("#demoEmailBtn").addEventListener("click", () => {
  $("#manualEmailForm [name='subject']").value = "Providencia causa 17230-2026-00001 - termino de 5 dias";
  $("#manualEmailForm [name='bodyText']").value = [
    "Unidad Judicial Civil de Quito",
    "Causa 17230-2026-00001",
    "Se notifica providencia. Se concede el termino de 5 dias para que la parte actora presente la documentacion requerida.",
    "Audiencia convocada para el 10/06/2026 a las 09:30 en sala 3.",
    "Comparezca y cumpla lo dispuesto."
  ].join("\n");
});

$("#manualEmailForm").addEventListener("submit", (event) => submitJson(event, "/api/inbox/manual", (form) => ({
  provider: form.get("provider"),
  mailboxConnectionId: null,
  caseId: toNullable(form.get("caseId")),
  subject: form.get("subject"),
  sender: form.get("sender"),
  recipients: [state.user?.email || "admin@legalpilot.ec"],
  bodyText: form.get("bodyText"),
  rawReference: "panel-web"
}), async () => {
  await refreshAll();
  await switchView("inbox");
}));

$("#mailboxForm").addEventListener("submit", (event) => submitJson(event, "/api/mailboxes/connect", (form) => ({
  provider: form.get("provider"),
  email: form.get("email")
}), async () => {
  await Promise.all([loadIntegrations(), loadOverview()]);
}));

$("#oauthStartBtn").addEventListener("click", async () => {
  const form = new FormData($("#mailboxForm"));
  const button = $("#oauthStartBtn");
  setLoading(button, true);
  try {
    const result = await api("/api/oauth/start", {
      method: "POST",
      body: JSON.stringify({
        provider: form.get("provider"),
        email: form.get("email")
      })
    });

    $("#oauthBox").innerHTML = `
      <strong>OAuth listo para ${escapeHtml(result.provider)}</strong>
      <span>Estado expira: ${formatDateTime(result.expiresAt)}</span>
      <a href="${escapeHtml(result.authorizationUrl)}" target="_blank" rel="noreferrer">Abrir autorizacion del proveedor</a>
    `;
    $("#oauthBox").className = "oauth-box";
    showToast("OAuth preparado.");
  } catch (error) {
    $("#oauthBox").textContent = error.message;
    $("#oauthBox").className = "oauth-box error";
    showToast(error.message, "error");
  } finally {
    setLoading(button, false);
  }
});

$("#caseForm").addEventListener("submit", (event) => submitJson(event, "/api/cases", (form) => ({
  title: form.get("title"),
  caseNumber: form.get("caseNumber"),
  matter: form.get("matter"),
  courtOrOffice: form.get("courtOrOffice"),
  clientId: toNullable(form.get("clientId")),
  responsibleUserId: null
}), async () => {
  await loadReferences();
  await Promise.all([loadCases(), loadOverview()]);
}));

$("#clientForm").addEventListener("submit", (event) => submitJson(event, "/api/clients", (form) => ({
  fullName: form.get("fullName"),
  email: form.get("email"),
  phone: form.get("phone"),
  identification: form.get("identification")
}), async () => {
  await loadReferences();
  await Promise.all([loadClients(), loadOverview()]);
}));

$("#deadlineForm").addEventListener("submit", (event) => submitJson(event, "/api/deadlines", (form) => ({
  title: form.get("title"),
  caseId: toNullable(form.get("caseId")),
  notificationDate: form.get("notificationDate"),
  termDays: Number(form.get("termDays")),
  matter: form.get("matter"),
  province: form.get("province") || null,
  canton: form.get("canton") || null,
  ruleCode: "EC-COGEP-TERM-BUSINESS-DAYS-V1",
  responsibleUserId: null,
  confirmed: form.get("confirmed") === "on"
}), async () => {
  await Promise.all([loadDeadlines(), loadCalendar(), loadOverview()]);
}));

$("#calendarForm").addEventListener("submit", (event) => submitJson(event, "/api/calendar/events", (form) => ({
  caseId: toNullable(form.get("caseId")),
  type: form.get("type"),
  title: form.get("title"),
  location: form.get("location") || null,
  startsAt: new Date(form.get("startsAt")).toISOString(),
  endsAt: new Date(form.get("endsAt")).toISOString(),
  responsibleUserId: null,
  requiresConfirmation: true
}), async () => {
  await Promise.all([loadCalendar(), loadOverview()]);
}));

$("#chatForm").addEventListener("submit", (event) => submitJson(event, "/api/chat/messages", (form) => ({
  clientId: toNullable(form.get("clientId")),
  caseId: toNullable(form.get("caseId")),
  direction: form.get("direction"),
  channel: form.get("channel"),
  body: form.get("body"),
  requiresHumanReview: form.get("requiresHumanReview") === "on"
}), async () => {
  await Promise.all([loadChat(), loadOverview()]);
}));

$("#whatsappForm").addEventListener("submit", (event) => submitJson(event, "/api/whatsapp/send-client-message", (form) => ({
  clientId: toNullable(form.get("clientId")),
  caseId: null,
  to: form.get("to") || null,
  body: form.get("body"),
  approved: form.get("approved") === "on"
}), async () => {
  await Promise.all([loadChat(), loadOverview(), loadIntegrations()]);
}));

$("#aiAnalyzeForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  const formElement = event.currentTarget;
  const button = $("button[type='submit']", formElement);
  setLoading(button, true);
  try {
    const form = new FormData(formElement);
    const result = await api("/api/ai/analyze", {
      method: "POST",
      body: JSON.stringify({
        subject: form.get("subject"),
        bodyText: form.get("bodyText"),
        legalEmailId: null
      })
    });
    $("#aiResult").innerHTML = itemTemplate(
      result.extraction.actType,
      `Prioridad ${result.extraction.priority} / confianza ${Math.round(result.extraction.confidence * 100)}%`,
      `${result.extraction.lawyerSummary} ${result.deadlinePolicy}`,
      result.extraction.priority
    );
    await loadAi();
    showToast("Analisis IA registrado.");
  } catch (error) {
    $("#aiResult").textContent = error.message;
    showToast(error.message, "error");
  } finally {
    setLoading(button, false);
  }
});

$("#aiKnowledgeForm").addEventListener("submit", (event) => submitJson(event, "/api/ai/knowledge", (form) => ({
  title: form.get("title"),
  sourceType: form.get("sourceType"),
  sourceReference: form.get("sourceReference"),
  tags: String(form.get("tags") || "").split(",").map((item) => item.trim()).filter(Boolean),
  contentHash: null,
  chunkCount: Number(form.get("chunkCount") || 0),
  indexed: form.get("indexed") === "on"
}), async () => {
  await loadAi();
}));

$("#holidayForm").addEventListener("submit", (event) => submitJson(event, "/api/settings/holidays", (form) => ({
  date: form.get("date"),
  name: form.get("name"),
  scope: form.get("scope"),
  province: form.get("province") || null,
  canton: form.get("canton") || null,
  source: "Panel LegalPilot",
  isBusinessDayOverride: form.get("isBusinessDayOverride") === "on"
}), async () => {
  await Promise.all([loadSettings(), loadDeadlines()]);
}));

document.addEventListener("click", async (event) => {
  const review = event.target.closest("[data-review-deadline]");
  const ack = event.target.closest("[data-ack-alert]");
  const confirm = event.target.closest("[data-confirm-event]");
  const sync = event.target.closest("[data-sync-mailbox]");

  try {
    if (review) {
      await api(`/api/deadlines/${review.dataset.reviewDeadline}/review`, {
        method: "PATCH",
        body: JSON.stringify({ approved: review.dataset.approved === "true", comment: "Revision desde panel" })
      });
      await Promise.all([loadDeadlines(), loadOverview()]);
      showToast("Plazo actualizado.");
    }

    if (ack) {
      await api(`/api/alerts/${ack.dataset.ackAlert}/ack`, { method: "POST" });
      await Promise.all([loadAlerts(), loadOverview()]);
      showToast("Alerta revisada.");
    }

    if (confirm) {
      await api(`/api/calendar/events/${confirm.dataset.confirmEvent}/confirm`, { method: "POST" });
      await Promise.all([loadCalendar(), loadOverview()]);
      showToast("Evento confirmado.");
    }

    if (sync) {
      await api(`/api/mailboxes/${sync.dataset.syncMailbox}/sync`, { method: "POST" });
      await Promise.all([loadIntegrations(), loadOverview()]);
      showToast("Sincronizacion registrada.");
    }
  } catch (error) {
    showToast(error.message, "error");
  }
});

const setDefaultDates = () => {
  const today = new Date();
  const date = today.toISOString().slice(0, 10);
  const start = new Date(today.getTime() + 24 * 60 * 60 * 1000);
  const end = new Date(start.getTime() + 60 * 60 * 1000);
  $("#deadlineForm [name='notificationDate']").value = date;
  $("#calendarForm [name='startsAt']").value = start.toISOString().slice(0, 16);
  $("#calendarForm [name='endsAt']").value = end.toISOString().slice(0, 16);
};

setDefaultDates();
showApp();
