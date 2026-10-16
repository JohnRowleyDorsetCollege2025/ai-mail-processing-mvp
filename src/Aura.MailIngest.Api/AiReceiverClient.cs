using System.Net.Http.Json;
using Aura.Contracts;
using Microsoft.Extensions.Options;

namespace Aura.MailIngest.Api;

public sealed class AiReceiverClient
{
    private readonly HttpClient _httpClient;
    private readonly AiReceiverOptions _options;

    public AiReceiverClient(HttpClient httpClient, IOptions<AiReceiverOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<AiSubmissionAcknowledgement> SubmitAsync(AiSubmissionPayload payload, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(_options.SubmissionsPath, payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AiReceiverException(
                $"AI receiver returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return await response.Content.ReadFromJsonAsync<AiSubmissionAcknowledgement>(cancellationToken)
            ?? throw new AiReceiverException("AI receiver returned an empty acknowledgement.");
    }
}

public sealed class AiReceiverException : Exception
{
    public AiReceiverException(string message) : base(message)
    {
    }
}
