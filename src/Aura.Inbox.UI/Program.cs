using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using InboxMock;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The inbox reads from Aura.MailIngest.Api (wwwroot/appsettings.json). The API must allow this app's origin
// in its Inbox:AllowedOrigins setting.
var ingestUrl = new Uri(builder.Configuration["MailIngest:BaseUrl"] ?? "http://localhost:5100");
builder.Services.AddSingleton(new HttpClient { BaseAddress = ingestUrl });

// Singleton: one SignalR connection for the lifetime of the browser tab.
builder.Services.AddSingleton<InboxMock.Services.MessageService>();

await builder.Build().RunAsync();
