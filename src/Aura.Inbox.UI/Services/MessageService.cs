using System.Net.Http.Json;
using InboxMock.Models;

namespace InboxMock.Services;

public class MessageService(HttpClient http)
{
    public Task<List<EmailMessage>?> GetMessagesAsync() =>
        http.GetFromJsonAsync<List<EmailMessage>>("data/messages.json");
}
