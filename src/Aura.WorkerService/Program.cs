using Aura.WorkerService.Tasks;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

// Each task is a hosted service with its own section under "Tasks" in appsettings.json.
// To add a task: derive from PeriodicTask<TOptions>, bind its options, and register it with AddHostedService.
builder.Services.AddOptions<SampleSubmissionOptions>().BindConfiguration(SampleSubmissionOptions.SectionName);
builder.Services.AddHttpClient(SampleSubmissionTask.HttpClientName);
builder.Services.AddHostedService<SampleSubmissionTask>();

builder.Services.AddOptions<ForwardPendingOptions>().BindConfiguration(ForwardPendingOptions.SectionName);
ForwardPendingTask.AddHttpClient(builder.Services);
builder.Services.AddHostedService<ForwardPendingTask>();

builder.Build().Run();
