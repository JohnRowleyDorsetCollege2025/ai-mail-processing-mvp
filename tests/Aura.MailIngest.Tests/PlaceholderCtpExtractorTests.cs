using System.Text.Json;
using Aura.Contracts;
using Aura.MailIngest.Api;
using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;

namespace Aura.MailIngest.Tests;

public class PlaceholderCtpExtractorTests
{
    [Theory]
    [InlineData("Hotel", "Renewal", "55.10")]
    [InlineData("Professional Office", "New Business", "69.20")]
    public async Task Extracts_ctp_fields_from_generated_submission(string trade, string requestType, string expectedOccupation)
    {
        var scenario = new ScenarioGenerator(TimeProvider.System).Generate(new ScenarioRequest { Seed = 3, Trade = trade, RequestType = requestType });
        var payload = await ParseAsync(scenario);

        var ctp = PlaceholderCtpExtractor.Extract(payload);

        Assert.Equal(("CSR", "BMR", "Aura"), (ctp.Channel, ctp.Product, ctp.User));
        Assert.Equal(payload.Context.SubmissionId, ctp.SubmissionId);
        Assert.Equal(scenario.Insured.DisplayName, ctp.CustomerName);
        Assert.Equal(scenario.Broker.ContactName, ctp.BrokerName);
        Assert.Equal(scenario.Broker.Email, ctp.BrokerEmail);
        Assert.Matches("^DN[0-9]{7}$", ctp.AgencyCode);
        Assert.Equal(ctp.AgencyCode, ctp.AgentKey);
        Assert.Equal(scenario.InceptionDate, ctp.CoverDate);
        Assert.Equal(scenario.InceptionDate, DateOnly.FromDateTime(ctp.InceptionDate!.Value.UtcDateTime));
        Assert.Equal(scenario.CurrentInsurer ?? "UN - Unknown", ctp.HoldingInsurerName);
        Assert.Equal(expectedOccupation, ctp.OccupationCode);
        Assert.Equal("A = Not Declined", ctp.Appetite);
    }

    [Fact]
    public void Serialises_with_the_field_names_of_the_sample_ctp_payload()
    {
        var samplePath = Path.Combine(FindRepoRoot(), "data", "sample_ctp_payload.json");
        using var sample = JsonDocument.Parse(File.ReadAllText(samplePath));

        // Round-trip the sample through the model: every field must survive, with the same names.
        var model = JsonSerializer.Deserialize<AiCtpModel>(sample.RootElement.GetRawText())!;
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(model));

        Assert.Equal(
            sample.RootElement.EnumerateObject().Select(p => p.Name),
            written.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["CMF", "BI", "GAP", "EL", "PRL", "PL"], model.Covers);
        Assert.Equal(new DateOnly(2025, 8, 27), model.CoverDate);
        Assert.Equal("93eaf376-0134-4ce0-b248-477b4d781c5d", model.CytoraId);
        Assert.Equal("DN0012123", model.AgentKey);
        Assert.Equal("DAO", model.UnderWriter);
    }

    private static async Task<AiSubmissionPayload> ParseAsync(SubmissionScenario scenario)
    {
        var message = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System).Build(scenario);
        using var eml = new MemoryStream();
        await message.WriteToAsync(eml);
        eml.Position = 0;
        return (await new EmlSubmissionParser(TimeProvider.System).ParseAsync(eml, "traded", ["agent"])).Payload;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AuraMailToAIApi.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
