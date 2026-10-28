using System.Net.Http.Json;
using Aura.Contracts;
using InboxMock.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace InboxMock.Services;

public enum LiveState
{
    Connecting,
    Live,
    Reconnecting,
    Offline,
}

/// <summary>
/// Inbox data from Aura.MailIngest.Api: the initial list over HTTP, then live updates over its SignalR hub.
/// After a reconnect the list is reloaded, so nothing sent while disconnected is missed.
/// </summary>
public sealed class MessageService(HttpClient http) : IAsyncDisposable
{
    private HubConnection? _hub;

    /// <summary>A message was added or its status changed.</summary>
    public event Func<EmailMessage, Task>? MessageUpserted;

    /// <summary>The connection was re-established; reload the list to catch up.</summary>
    public event Func<Task>? Reconnected;

    public event Func<LiveState, Task>? StateChanged;

    public LiveState State { get; private set; } = LiveState.Connecting;

    public async Task<List<EmailMessage>> GetMessagesAsync()
    {
        var messages = await http.GetFromJsonAsync<List<InboxMessage>>("api/inbox") ?? [];
        return messages.Select(EmailMessage.From).ToList();
    }

    public async Task StartLiveUpdatesAsync()
    {
        if (_hub is not null)
        {
            return;
        }

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(http.BaseAddress!, "hubs/inbox"))
            .WithAutomaticReconnect(new KeepRetrying())
            .Build();

        _hub.On<InboxMessage>(InboxHubMethods.MessageUpserted, m => Raise(MessageUpserted, EmailMessage.From(m)));
        _hub.Reconnecting += _ => SetState(LiveState.Reconnecting);
        _hub.Reconnected += async _ =>
        {
            await SetState(LiveState.Live);
            if (Reconnected is not null) await Reconnected();
        };
        _hub.Closed += _ => SetState(LiveState.Offline);

        // The initial connection is not covered by automatic reconnect, so retry it here until the API is up.
        while (true)
        {
            try
            {
                await _hub.StartAsync();
                await SetState(LiveState.Live);
                return;
            }
            catch (Exception) when (_hub.State == HubConnectionState.Disconnected)
            {
                await SetState(LiveState.Offline);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private async Task SetState(LiveState state)
    {
        State = state;
        if (StateChanged is not null) await StateChanged(state);
    }

    private static Task Raise<T>(Func<T, Task>? handler, T arg) => handler is null ? Task.CompletedTask : handler(arg);

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }

    /// <summary>0s, 2s, 5s, then every 10s, indefinitely (the default policy gives up after four attempts).</summary>
    private sealed class KeepRetrying : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) => context.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10),
        };
    }
}
