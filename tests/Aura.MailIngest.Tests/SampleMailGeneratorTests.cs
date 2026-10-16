using System.Text;
using System.Text.RegularExpressions;
using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;

namespace Aura.MailIngest.Tests;

public class SampleMailGeneratorTests
{
    private readonly ScenarioGenerator _scenarios = new(TimeProvider.System);

    [Fact]
    public void Same_seed_produces_same_scenario()
    {
        var a = _scenarios.Generate(new ScenarioRequest { Seed = 123 });
        var b = _scenarios.Generate(new ScenarioRequest { Seed = 123 });

        Assert.Equal(a.Insured, b.Insured);
        Assert.Equal(a.Broker, b.Broker);
        Assert.Equal(a.Covers, b.Covers);
        Assert.Equal(a.Claims, b.Claims);
    }

    [Fact]
    public void Quote_deadline_is_a_weekday_before_inception()
    {
        foreach (var seed in Enumerable.Range(0, 200))
        {
            var scenario = _scenarios.Generate(new ScenarioRequest { Seed = seed });
            Assert.DoesNotContain(scenario.QuoteRequiredBy.DayOfWeek, new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });
            Assert.True(scenario.QuoteRequiredBy < scenario.InceptionDate);
        }
    }

    [Fact]
    public void Overrides_are_respected()
    {
        var scenario = _scenarios.Generate(new ScenarioRequest { Trade = "Hotel", RequestType = "New Business", ClaimCount = 2, InsuredName = "Test Co Ltd" });

        Assert.Equal("Hotel", scenario.Insured.Trade);
        Assert.Equal("New Business", scenario.RequestType);
        Assert.Null(scenario.CurrentInsurer);
        Assert.Equal(2, scenario.Claims.Count);
        Assert.Equal("Test Co Ltd", scenario.Insured.LegalName);
    }

    [Fact]
    public void Unknown_trade_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => _scenarios.Generate(new ScenarioRequest { Trade = "Nuclear" }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(2026)]
    public void Pdf_is_structurally_valid(int seed)
    {
        var scenario = _scenarios.Generate(new ScenarioRequest { Seed = seed, ClaimCount = 3 });
        var bytes = new SubmissionPdfGenerator().Generate(scenario).Content;
        var text = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);

        // Every xref entry must point at the start of its object, otherwise readers fall back to repair mode.
        var startXref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[startXref..]);
        var offsets = Regex.Matches(text[startXref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        for (var i = 0; i < offsets.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..]);
        }

        // Stream lengths must match their content.
        foreach (Match m in Regex.Matches(text, @"<< /Length (\d+) >>\nstream\n"))
        {
            var length = int.Parse(m.Groups[1].Value);
            Assert.StartsWith("\nendstream", text[(m.Index + m.Length + length)..]);
        }

        Assert.Contains("Commercial Combined Insurance Submission", text);
    }

    [Fact]
    public void Euro_amounts_are_encoded_as_winansi()
    {
        var pdf = new SimplePdfDocument("t").Paragraph("Turnover €1,250,000").ToBytes();
        Assert.Contains(@"(Turnover \2001,250,000)", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void Email_contains_body_and_pdf_attachment()
    {
        var scenario = _scenarios.Generate(new ScenarioRequest { Seed = 5 });
        var message = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System).Build(scenario);

        Assert.Equal($"Submission - {scenario.Insured.DisplayName} - {scenario.RequestType}", message.Subject);
        Assert.Contains(scenario.Insured.DisplayName, message.TextBody);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        Assert.Equal("5", message.Headers["X-Aura-Sample-Seed"]);
    }

    [Fact]
    public void Unknown_attachment_key_is_rejected()
    {
        var builder = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System);
        Assert.Throws<ArgumentException>(() => builder.Build(_scenarios.Generate(), ["word-proposal"]));
    }
}
