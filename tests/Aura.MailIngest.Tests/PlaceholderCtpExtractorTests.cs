using Aura.MailIngest.Api;
using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;
using MimeKit;

namespace Aura.MailIngest.Tests;

public class PlaceholderCtpExtractorTests
{
    [Theory]
    [InlineData("Hotel", "55.10", "Refer")]
    [InlineData("Professional Office", "69.20", "In")]
    public async Task Extracts_broker_and_occupation_from_generated_submission(string trade, string expectedCode, string expectedAppetite)
    {
        var scenario = new ScenarioGenerator(TimeProvider.System).Generate(new ScenarioRequest { Seed = 3, Trade = trade });
        var message = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System).Build(scenario);
        using var eml = new MemoryStream();
        await message.WriteToAsync(eml);
        eml.Position = 0;
        var payload = (await new EmlSubmissionParser(TimeProvider.System).ParseAsync(eml, "traded", ["agent"])).Payload;

        var ctp = PlaceholderCtpExtractor.Extract(payload);

        Assert.Equal(scenario.Broker.FirmName, ctp.BrokerName);
        Assert.Matches("^BRK-[0-9A-F]{6}$", ctp.BrokerId);
        Assert.Equal(expectedCode, ctp.OccupationCode);
        Assert.Equal(expectedAppetite, ctp.Appetite);
    }
}
