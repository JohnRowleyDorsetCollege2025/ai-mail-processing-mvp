// Starts all three APIs with the Aspire dashboard (logs, traces, metrics, health) for local development.
// Each API keeps the fixed port from its launchSettings.json (5100/5200/5300), so requests.http works unchanged.

var builder = DistributedApplication.CreateBuilder(args);

var receiver = builder.AddProject<Projects.Aura_AiReceiver_Api>("ai-receiver")
    .WithHttpHealthCheck("/health")
    .WithUrl("/openapi/v1.json", "OpenAPI");

// Downstream URLs are passed as the same configuration keys the APIs read from appsettings.json,
// so each API still runs standalone without Aspire.
var ingest = builder.AddProject<Projects.Aura_MailIngest_Api>("mail-ingest")
    .WithEnvironment("AiReceiver__BaseUrl", receiver.GetEndpoint("http"))
    .WaitFor(receiver)
    .WithHttpHealthCheck("/health")
    .WithUrl("/openapi/v1.json", "OpenAPI")
    .WithUrl("/api/submissions", "Stored submissions");

var generator = builder.AddProject<Projects.Aura_SampleMailGenerator_Api>("sample-generator")
    .WithEnvironment("MailIngest__BaseUrl", ingest.GetEndpoint("http"))
    .WaitFor(ingest)
    .WithHttpHealthCheck("/health")
    .WithUrl("/openapi/v1.json", "OpenAPI")
    .WithUrl("/api/samples/email", "Download sample .eml");

// Background tasks: SampleSubmission asks the generator for a sample email (default every 30s);
// ForwardPending asks ingest to send pending emails to the AI (default every 2 minutes, 5 at a time).
builder.AddProject<Projects.Aura_WorkerService>("worker")
    .WithEnvironment("Tasks__SampleSubmission__BaseUrl", generator.GetEndpoint("http"))
    .WithEnvironment("Tasks__ForwardPending__BaseUrl", ingest.GetEndpoint("http"))
    .WaitFor(generator)
    .WaitFor(ingest);

builder.AddProject<Projects.EnterpriseSearch>("ui")
    .WithHttpHealthCheck("/health");

// Standalone Blazor WebAssembly app served by the WebAssembly dev server. It runs in the browser, so it has no
// service defaults (server-side telemetry/health checks). It reads the ingest API URL from its wwwroot/appsettings.json.
var inboxUi = builder.AddProject<Projects.InboxMock>("inbox-ui")
    .WaitFor(ingest);

// The inbox runs in the browser on its own origin and calls the ingest API (HTTP + SignalR), so ingest must allow it.
ingest.WithEnvironment("Inbox__AllowedOrigins__0", inboxUi.GetEndpoint("http"));

builder.Build().Run();
