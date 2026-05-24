using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

TestPasswordHasher();
TestLegalIntelligence();
TestDeadlineEngineWithEcuadorHoliday();
TestBusinessDayOverride();
TestWorkflowAuditReminderAndSanitization();
TestIntegrationReadinessContracts();
TestAiPipelineGuardrails();

Console.WriteLine("LegalPilot smoke tests passed.");

static void TestPasswordHasher()
{
    var hasher = new PasswordHasher();
    var (hash, salt) = hasher.HashPassword("LegalPilot#2026");
    Assert(hasher.Verify("LegalPilot#2026", hash, salt), "Password hash should verify the original password.");
    Assert(!hasher.Verify("wrong-password", hash, salt), "Password hash should reject a wrong password.");
}

static void TestLegalIntelligence()
{
    var service = new LegalIntelligenceService();
    var extraction = service.Extract(
        "Providencia causa 17230-2026-00001 - termino de 5 dias",
        """
        Unidad Judicial Civil de Quito
        Causa 17230-2026-00001
        Se concede el termino de 5 dias para presentar documentacion.
        Audiencia convocada para el 10/06/2026 a las 09:30 en sala 3.
        """);

    Assert(extraction.CaseNumber == "17230-2026-00001", "Case number should be extracted.");
    Assert(extraction.TermDays == 5, "Term days should be extracted.");
    Assert(extraction.ActType is LegalActType.Hearing or LegalActType.Ruling, "Legal act type should be classified.");
    Assert(extraction.Confidence >= 0.75m, "Extraction confidence should reflect multiple signals.");
}

static void TestDeadlineEngineWithEcuadorHoliday()
{
    var engine = new EcuadorDeadlineEngine();
    var holidays = EcuadorHolidaySeed.National2026(Guid.Parse("11111111-1111-1111-1111-111111111111")).ToArray();
    var calculation = engine.Calculate(
        new DeadlineRequest(new DateOnly(2026, 5, 22), 1, "Civil", Province: "Pichincha", Canton: "Quito"),
        holidays);

    Assert(calculation.DueDate == new DateOnly(2026, 5, 26), "One business day after Friday May 22 2026 should skip weekend and May 25 holiday.");
    Assert(calculation.Steps.Count(s => !s.Included) == 3, "Weekend and holiday exclusions should be traced.");
    Assert(calculation.HolidaysApplied.Any(h => h.Contains("Batalla de Pichincha")), "Applied holiday should be listed.");
}

static void TestBusinessDayOverride()
{
    var engine = new EcuadorDeadlineEngine();
    var holidays = new[]
    {
        new Holiday(
            Guid.NewGuid(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new DateOnly(2026, 5, 23),
            "Jornada judicial extraordinaria",
            HolidayScope.TenantException,
            null,
            null,
            "Test",
            true,
            DateTimeOffset.UtcNow)
    };

    var calculation = engine.Calculate(new DeadlineRequest(new DateOnly(2026, 5, 22), 1, "Civil"), holidays);
    Assert(calculation.DueDate == new DateOnly(2026, 5, 23), "Business day override should allow a normally excluded Saturday.");
}

static void TestWorkflowAuditReminderAndSanitization()
{
    using var loggerFactory = LoggerFactory.Create(_ => { });
    var tempRoot = Path.Combine(Path.GetTempPath(), "legalpilot-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegalPilot:Storage:Path"] = Path.Combine(tempRoot, "store.json"),
            ["LegalPilot:Bootstrap:AdminEmail"] = "admin@legalpilot.ec",
            ["LegalPilot:Bootstrap:AdminPassword"] = "LegalPilot#2026"
        })
        .Build();

    var store = new LegalPilotStore(configuration, new TestEnvironment(tempRoot), loggerFactory.CreateLogger<LegalPilotStore>());
    var hasher = new PasswordHasher();
    SeedData.Seed(store, hasher, configuration, new TestEnvironment(tempRoot));

    var admin = store.Users.First(u => u.Email == "admin@legalpilot.ec");
    var principal = new AuthPrincipal(admin.Id, admin.TenantId, admin.Email, admin.Roles);
    var clients = new ClientService(store);
    var cases = new CaseService(store);
    var workflow = new LegalWorkflowService(store, new LegalIntelligenceService(), new EcuadorDeadlineEngine(), configuration);
    var chat = new ChatService(store);

    var client = clients.Create(principal, new CreateClientRequest("Cliente Test", "cliente-test@example.com", "+593999222333", "TEST-001"));
    var legalCase = cases.CreateCase(principal, new CreateCaseRequest("Caso Test", "17230-2026-99991", "Civil", "Unidad Judicial Civil de Quito", client.Id, null));
    var deadline = workflow.CreateDeadline(principal, new CreateDeadlineRequest("Plazo Test", legalCase.Id, new DateOnly(2026, 5, 22), 1, "Civil", "Pichincha", "Quito", null, null, true));
    var message = chat.Create(principal, new CreateChatMessageRequest(client.Id, legalCase.Id, ChatDirection.Outbound, NotificationChannel.Panel, "password token contrasena sensible", true));

    Assert(deadline.DueDate == new DateOnly(2026, 5, 26), "Deadline workflow should use Ecuador holidays.");
    Assert(store.CalendarEvents.Any(e => e.DeadlineId == deadline.Id), "Deadline workflow should create a calendar event.");
    Assert(store.Reminders.Any(r => r.CalendarEventId == store.CalendarEvents.First(e => e.DeadlineId == deadline.Id).Id), "Deadline workflow should create reminders.");
    Assert(store.AuditEntries.Any(a => a.EntityId == legalCase.Id.ToString()), "Case creation should be audited.");
    Assert(message.Body.Contains("[dato protegido]"), "Chat sanitization should minimize sensitive terms.");
    Assert(File.Exists(Path.Combine(tempRoot, "store.json")), "JSON fallback should persist workflow data for local tests.");
}

static void TestIntegrationReadinessContracts()
{
    using var loggerFactory = LoggerFactory.Create(_ => { });
    var tempRoot = Path.Combine(Path.GetTempPath(), "legalpilot-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegalPilot:Storage:Path"] = Path.Combine(tempRoot, "store.json"),
            ["LegalPilot:Bootstrap:AdminEmail"] = "admin@legalpilot.ec",
            ["LegalPilot:Bootstrap:AdminPassword"] = "LegalPilot#2026"
        })
        .Build();
    var environment = new TestEnvironment(tempRoot);
    var store = new LegalPilotStore(configuration, environment, loggerFactory.CreateLogger<LegalPilotStore>());
    SeedData.Seed(store, new PasswordHasher(), configuration, environment);
    var workflow = new LegalWorkflowService(store, new LegalIntelligenceService(), new EcuadorDeadlineEngine(), configuration);
    var secretProtector = new SecretProtector(configuration, environment);
    var httpClientFactory = new TestHttpClientFactory();
    var gmail = new GmailEmailConnector(configuration, loggerFactory.CreateLogger<GmailEmailConnector>(), store, workflow, secretProtector, httpClientFactory);
    var microsoft = new MicrosoftGraphEmailConnector(configuration, loggerFactory.CreateLogger<MicrosoftGraphEmailConnector>(), store, workflow, secretProtector, httpClientFactory);
    var openWa = new OpenWaClient(new HttpClient(), configuration, loggerFactory.CreateLogger<OpenWaClient>());

    Assert(!gmail.GetReadiness().Configured, "Gmail should report missing configuration without credentials.");
    Assert(!microsoft.GetReadiness().Configured, "Microsoft should report missing configuration without credentials.");
    Assert(!openWa.GetReadiness().Configured, "OpenWA should report missing configuration without credentials.");
    Assert(gmail.GetReadiness().RequiredSettings.Length > 0, "Gmail readiness should expose required settings.");
    Assert(openWa.GetReadiness().RequiredSettings.Contains("LegalPilot:OpenWa:WebhookSecret"), "OpenWA readiness should require webhook secret.");
}

static void TestAiPipelineGuardrails()
{
    using var loggerFactory = LoggerFactory.Create(_ => { });
    var tempRoot = Path.Combine(Path.GetTempPath(), "legalpilot-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegalPilot:Storage:Path"] = Path.Combine(tempRoot, "store.json"),
            ["LegalPilot:Bootstrap:AdminEmail"] = "admin@legalpilot.ec",
            ["LegalPilot:Bootstrap:AdminPassword"] = "LegalPilot#2026"
        })
        .Build();
    var environment = new TestEnvironment(tempRoot);
    var store = new LegalPilotStore(configuration, environment, loggerFactory.CreateLogger<LegalPilotStore>());
    SeedData.Seed(store, new PasswordHasher(), configuration, environment);
    var admin = store.Users.First(u => u.Email == "admin@legalpilot.ec");
    var principal = new AuthPrincipal(admin.Id, admin.TenantId, admin.Email, admin.Roles);
    var ai = new LegalAiPipelineService(store, new LegalIntelligenceService(), configuration);
    var result = ai.Analyze(principal, new AiAnalyzeRequest(
        "Providencia causa 17230-2026-00001 termino de 3 dias",
        "Se concede termino de 3 dias para presentar escrito. Audiencia 10/06/2026 09:30.",
        null));

    Assert(store.AiProcessingRuns.Count == 1, "AI analysis should be audited as a processing run.");
    Assert(store.AuditEntries.Any(a => a.Action == AuditAction.AiRun), "AI run should create audit entry.");
    Assert(result.ToString()?.Contains("No se calculan vencimientos con IA") == true, "AI response should expose deadline guardrail.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "LegalPilot.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
    public string WebRootPath { get; set; } = contentRoot;
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
}

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
