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

// Background tasks. Task 1 calls the generator's submit endpoint every Tasks:SampleSubmission:Interval (default 30s).
builder.AddProject<Projects.Aura_WorkerService>("worker")
    .WithEnvironment("Tasks__SampleSubmission__BaseUrl", generator.GetEndpoint("http"))
    .WaitFor(generator);

builder.AddProject<Projects.EnterpriseSearch>("ui")
    .WithHttpHealthCheck("/health");

// Standalone Blazor WebAssembly app served by the WebAssembly dev server. It runs in the browser, so it has no
// service defaults (server-side telemetry/health checks); Aspire just starts it and links to it.
builder.AddProject<Projects.InboxMock>("inbox-ui");

builder.Build().Run();
