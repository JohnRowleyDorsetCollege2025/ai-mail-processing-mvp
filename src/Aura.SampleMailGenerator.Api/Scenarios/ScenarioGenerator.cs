namespace Aura.SampleMailGenerator.Api.Scenarios;

/// <summary>Optional overrides for a generated scenario. Anything left null is chosen randomly from the seed.</summary>
public sealed record ScenarioRequest
{
    /// <summary>Same seed (on the same day) produces the same scenario. Omit for a random one.</summary>
    public int? Seed { get; init; }
    public string? RequestType { get; init; }
    public string? Trade { get; init; }
    public string? InsuredName { get; init; }
    public string? BrokerFirm { get; init; }
    public int? ClaimCount { get; init; }
}

/// <summary>
/// Produces plausible Irish commercial combined submissions. Catalogues are deliberately small for now; extend them
/// (or add new trades) to widen the variety of test data.
/// </summary>
public sealed class ScenarioGenerator
{
    private readonly TimeProvider _timeProvider;

    public ScenarioGenerator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public static IReadOnlyList<string> Trades => TradeProfiles.Select(t => t.Trade).ToList();

    public SubmissionScenario Generate(ScenarioRequest? request = null)
    {
        request ??= new ScenarioRequest();
        var seed = request.Seed ?? Random.Shared.Next();
        var rng = new Random(seed);
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        var profile = request.Trade is null
            ? Pick(rng, TradeProfiles)
            : TradeProfiles.FirstOrDefault(t => t.Trade.Equals(request.Trade, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"Unknown trade '{request.Trade}'. Valid trades: {string.Join(", ", Trades)}");

        var brokerFirm = request.BrokerFirm is null
            ? Pick(rng, BrokerFirms)
            : new BrokerFirm(request.BrokerFirm, Slug(request.BrokerFirm) + ".example", "Dublin 2");
        var (first, last) = (Pick(rng, FirstNames), Pick(rng, LastNames));
        var broker = new Broker(
            brokerFirm.Name,
            $"{first} {last}",
            $"{first}.{last}@{brokerFirm.Domain}".ToLowerInvariant().Replace("'", ""),
            $"+353 1 {rng.Next(200, 999)} {rng.Next(1000, 9999)}",
            $"{rng.Next(1, 90)} {Pick(rng, Streets)}, {brokerFirm.Area}");

        var town = Pick(rng, Towns);
        var legalName = request.InsuredName ?? $"{Pick(rng, CompanyStems)} {Pick(rng, CompanySuffixes)}";
        var employees = rng.Next(profile.MinEmployees, profile.MaxEmployees + 1);
        var turnover = RoundTo(rng.Next(profile.MinTurnoverK, profile.MaxTurnoverK + 1) * 1000m, 5000);
        var insured = new Insured
        {
            LegalName = legalName,
            TradingName = rng.NextDouble() < 0.5 ? Pick(rng, profile.TradingNames).Replace("{town}", town.Name) : null,
            CroNumber = rng.Next(100000, 799999).ToString(),
            Address = $"Unit {rng.Next(1, 40)}, {Pick(rng, Streets)}, {town.Name}, Co. {town.County}",
            Trade = profile.Trade,
            BusinessDescription = profile.Description,
            YearsTrading = rng.Next(2, 35),
            Employees = employees,
            Turnover = turnover,
            Wageroll = RoundTo(employees * rng.Next(28, 46) * 1000m, 5000),
        };

        var requestType = request.RequestType ?? (rng.NextDouble() < 0.6 ? "Renewal" : "New Business");
        var inception = today.AddDays(rng.Next(21, 60));
        var covers = BuildCovers(rng, profile, turnover);
        var claimCount = request.ClaimCount ?? rng.Next(0, 4);

        return new SubmissionScenario
        {
            Seed = seed,
            Broker = broker,
            Insured = insured,
            RequestType = requestType,
            InceptionDate = inception,
            QuoteRequiredBy = PreviousWorkingDay(inception.AddDays(-rng.Next(7, 14))),
            CurrentInsurer = requestType == "Renewal" ? Pick(rng, Insurers) : null,
            ExpiringPremium = requestType == "Renewal" ? RoundTo(turnover * (decimal)(0.004 + rng.NextDouble() * 0.008), 50) : null,
            Covers = covers,
            Claims = Enumerable.Range(0, claimCount).Select(_ => BuildClaim(rng, today, covers)).OrderByDescending(c => c.Date).ToList(),
            RecipientName = "Commercial Quotes",
            RecipientAddress = "commercial.quotes@example-insurer.ie",
        };
    }

    private static List<CoverRequested> BuildCovers(Random rng, TradeProfile profile, decimal turnover)
    {
        var covers = new List<CoverRequested>
        {
            new("Employers Liability", "Limit of Indemnity", 13_000_000m, null),
            new("Public Liability", "Limit of Indemnity", Pick(rng, [6_500_000m, 6_500_000m, 10_000_000m]), Pick(rng, [500m, 1_000m, 2_500m])),
        };

        if (profile.ProductsLiability)
        {
            covers.Add(new("Products Liability", "Limit of Indemnity", 6_500_000m, Pick(rng, [500m, 1_000m])));
        }

        var buildings = RoundTo(rng.Next(profile.MinBuildingsK, profile.MaxBuildingsK + 1) * 1000m, 10_000);
        if (buildings > 0)
        {
            covers.Add(new("Buildings", "Sum Insured", buildings, 1_000m));
        }
        covers.Add(new("Contents", "Sum Insured", RoundTo(buildings * 0.15m + rng.Next(50, 400) * 1000m, 5_000), 500m));
        covers.Add(new("Stock", "Sum Insured", RoundTo(turnover * (decimal)(0.03 + rng.NextDouble() * 0.1), 5_000), 500m));
        covers.Add(new("Business Interruption", "Sum Insured", RoundTo(turnover * (decimal)(0.25 + rng.NextDouble() * 0.2), 10_000), null));

        return covers;
    }

    private static ClaimRecord BuildClaim(Random rng, DateOnly today, IReadOnlyList<CoverRequested> covers)
    {
        var (section, description) = Pick(rng, ClaimTypes);
        if (!covers.Any(c => c.Section == section))
        {
            (section, description) = ClaimTypes[0];
        }

        var date = today.AddDays(-rng.Next(60, 5 * 365));
        var settled = date < today.AddDays(-365) || rng.NextDouble() < 0.5;
        return new ClaimRecord(date, section, description, RoundTo(rng.Next(1, 60) * 1000m, 250), settled ? "Settled" : "Open");
    }

    private static DateOnly PreviousWorkingDay(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(-2),
        _ => date,
    };

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    private static decimal RoundTo(decimal value, decimal step) => Math.Round(value / step) * step;

    private static string Slug(string value) =>
        new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private sealed record BrokerFirm(string Name, string Domain, string Area);

    private sealed record Town(string Name, string County);

    private sealed record TradeProfile(
        string Trade,
        string Description,
        string[] TradingNames,
        int MinEmployees, int MaxEmployees,
        int MinTurnoverK, int MaxTurnoverK,
        int MinBuildingsK, int MaxBuildingsK,
        bool ProductsLiability);

    // Fictitious names only: generated mail must never be mistaken for a real broker's or client's submission.
    private static readonly BrokerFirm[] BrokerFirms =
    [
        new("Harbourview Insurance Brokers", "harbourview-brokers.example", "Dublin 2"),
        new("Corrib Risk Partners", "corribrisk.example", "Galway"),
        new("Lee Valley Insurances", "leevalley-ins.example", "Cork"),
        new("Shannon Commercial Brokers", "shannoncb.example", "Limerick"),
        new("Midland Cover Ltd", "midlandcover.example", "Athlone"),
    ];

    private static readonly string[] FirstNames = ["Aoife", "Ciara", "Sinead", "Niamh", "Conor", "Darragh", "Eoin", "Laura", "Mark", "Orla", "Padraig", "Siobhan"];
    private static readonly string[] LastNames = ["Byrne", "Kelly", "Murphy", "O'Brien", "Walsh", "Ryan", "Doyle", "Lennon", "Kavanagh", "Brennan", "Nolan", "Quinn"];
    private static readonly string[] Streets = ["Main Street", "Mill Road", "Church Lane", "Industrial Estate", "Business Park", "Dock Road", "Bridge Street"];
    private static readonly string[] CompanyStems = ["Ardmore", "Kilcarra", "Brookfield", "Rathbeg", "Glenmore", "Lisduff", "Ballyvale", "Clonmore", "Dunbrook"];
    private static readonly string[] CompanySuffixes = ["Ltd", "Holdings Ltd", "Enterprises Ltd", "Trading Ltd", "Group DAC"];
    private static readonly string[] Insurers = ["Insurer A", "Insurer B", "Insurer C", "Insurer D"];

    private static readonly Town[] Towns =
    [
        new("Naas", "Kildare"), new("Mullingar", "Westmeath"), new("Ennis", "Clare"), new("Tralee", "Kerry"),
        new("Drogheda", "Louth"), new("Carlow", "Carlow"), new("Sligo", "Sligo"), new("Wexford", "Wexford"),
    ];

    private static readonly (string Section, string Description)[] ClaimTypes =
    [
        ("Public Liability", "Customer slip and fall on premises"),
        ("Employers Liability", "Employee injury - manual handling"),
        ("Buildings", "Escape of water damage"),
        ("Contents", "Theft following forcible entry"),
        ("Stock", "Stock spoilage following power outage"),
        ("Buildings", "Storm damage to roof"),
    ];

    private static readonly TradeProfile[] TradeProfiles =
    [
        new("Restaurant / Takeaway", "Operation of fast food restaurants and takeaway outlets with on-premises cooking and deep fat frying.",
            ["Burger Hub {town}", "Pizza Corner {town}", "{town} Chip Inn"], 8, 60, 400, 3_500, 0, 900, true),
        new("Hotel", "Operation of a hotel with bar, restaurant and function rooms.",
            ["The Grand Hotel {town}", "{town} Riverside Hotel", "Park Lodge {town}"], 25, 120, 1_500, 9_000, 2_000, 12_000, true),
        new("Retail Shop", "Retail sale of convenience goods, newspapers and off-licence.",
            ["Daybreak {town}", "{town} Corner Stores", "Value Mart {town}"], 4, 25, 300, 2_500, 0, 1_200, true),
        new("Light Manufacturing", "Manufacture and assembly of metal shelving and storage products.",
            ["Steelform", "RackRight Storage", "{town} FabWorks"], 10, 80, 800, 7_000, 800, 5_000, true),
        new("Warehouse & Logistics", "Warehousing, storage and distribution of non-hazardous goods for third parties.",
            ["{town} Express Logistics", "Hub Distribution", "Storeline"], 12, 90, 900, 8_000, 1_500, 9_000, false),
        new("Professional Office", "Accountancy and business advisory services from office premises.",
            ["{town} Accountancy Partners", "Byrne & Associates", "{town} Business Advisory"], 5, 40, 500, 4_000, 0, 1_500, false),
    ];
}
