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
                $"AI receiver returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<AiSubmissionAcknowledgement>(cancellationToken)
            ?? throw new AiReceiverException("AI receiver returned an empty acknowledgement.");
    }
}

public sealed class AiReceiverException : Exception
{
    public AiReceiverException(string message, System.Net.HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The receiver's HTTP status, or null when no usable response was received.</summary>
    public System.Net.HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// True when the receiver rejected this submission itself (a 4xx other than 408/429), so retrying it would
    /// fail again. Server errors, timeouts and throttling are temporary and worth retrying later.
    /// </summary>
    public bool IsPermanent => StatusCode is { } code && (int)code is >= 400 and < 500 && (int)code is not 408 and not 429;
}
