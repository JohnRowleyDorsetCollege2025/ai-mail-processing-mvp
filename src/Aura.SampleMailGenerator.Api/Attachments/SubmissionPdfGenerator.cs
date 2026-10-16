using System.Globalization;
using Aura.SampleMailGenerator.Api.Scenarios;

namespace Aura.SampleMailGenerator.Api.Attachments;

/// <summary>Broker's commercial combined submission / proposal document as a text PDF.</summary>
public sealed class SubmissionPdfGenerator : IAttachmentGenerator
{
    private static readonly CultureInfo IrishCulture = CultureInfo.GetCultureInfo("en-IE");

    public string Key => "submission-pdf";

    public GeneratedAttachment Generate(SubmissionScenario s)
    {
        var insured = s.Insured;
        var pdf = new SimplePdfDocument($"{insured.LegalName} - Commercial Combined Submission");

        pdf.Heading("Commercial Combined Insurance Submission", 17)
           .Paragraph($"{s.RequestType} - Inception {Date(s.InceptionDate)}", 11, bold: true)
           .Paragraph($"Prepared by {s.Broker.FirmName}, {s.Broker.Address}");

        pdf.Heading("1. Broker Details", 12)
           .Field("Broker", s.Broker.FirmName)
           .Field("Contact", s.Broker.ContactName)
           .Field("Email", s.Broker.Email)
           .Field("Telephone", s.Broker.Phone)
           .Field("Quotation required by", Date(s.QuoteRequiredBy));

        pdf.Heading("2. Proposer", 12)
           .Field("Legal name", insured.LegalName);
        if (insured.TradingName is not null)
        {
            pdf.Field("Trading as", insured.TradingName);
        }
        pdf.Field("CRO number", insured.CroNumber)
           .Field("Risk address", insured.Address)
           .Field("Trade", insured.Trade)
           .Field("Business description", insured.BusinessDescription)
           .Field("Years trading", insured.YearsTrading.ToString(CultureInfo.InvariantCulture))
           .Field("Number of employees", insured.Employees.ToString(CultureInfo.InvariantCulture))
           .Field("Annual turnover", Money(insured.Turnover))
           .Field("Annual wageroll", Money(insured.Wageroll));

        if (s.RequestType == "Renewal")
        {
            pdf.Heading("3. Current Insurance", 12)
               .Field("Current insurer", s.CurrentInsurer ?? "n/a")
               .Field("Expiring premium", s.ExpiringPremium is { } p ? Money(p) : "n/a")
               .Field("Renewal date", Date(s.InceptionDate));
        }

        pdf.Heading(s.RequestType == "Renewal" ? "4. Cover Required" : "3. Cover Required", 12)
           .Table(
               ["Section", "Basis", "Amount", "Excess"],
               s.Covers.Select(c => (IReadOnlyList<string>)[c.Section, c.Basis, Money(c.Amount), c.Excess is { } e ? Money(e) : "Nil"]),
               [0.32, 0.26, 0.24, 0.18]);

        pdf.Heading("Claims Experience (last 5 years)", 12);
        if (s.Claims.Count == 0)
        {
            pdf.Paragraph("No claims in the last 5 years.");
        }
        else
        {
            pdf.Table(
                ["Date", "Section", "Description", "Amount", "Status"],
                s.Claims.Select(c => (IReadOnlyList<string>)[Date(c.Date), c.Section, c.Description, Money(c.Amount), c.Status]),
                [0.15, 0.2, 0.37, 0.15, 0.13]);
        }

        pdf.Heading("Declaration", 12)
           .Paragraph("The proposer confirms that, to the best of their knowledge and belief, the information provided is true " +
                      "and complete and that all material facts have been disclosed. No premises are unoccupied and all " +
                      "fire, security and electrical installations are maintained in accordance with current regulations.");

        var fileName = $"{Safe(insured.TradingName ?? insured.LegalName)} - {s.RequestType} Submission {s.InceptionDate.Year}.pdf";
        return new GeneratedAttachment(fileName, "application/pdf", pdf.ToBytes());
    }

    private static string Money(decimal value) => value.ToString("C0", IrishCulture);

    private static string Date(DateOnly value) => value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Safe(string value) =>
        new string(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '-' : c).ToArray());
}
