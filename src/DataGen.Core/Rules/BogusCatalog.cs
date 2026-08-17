using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Bogus;
using DataGen.Core.Generators;

namespace DataGen.Core.Rules;

/// <summary>Dataverse column kinds a catalog endpoint may target.</summary>
public enum DataverseValueKind
{
    /// <summary>StringAttributeMetadata.</summary>
    String,

    /// <summary>MemoAttributeMetadata.</summary>
    Memo,

    /// <summary>BooleanAttributeMetadata.</summary>
    Boolean,

    /// <summary>IntegerAttributeMetadata.</summary>
    Integer,

    /// <summary>BigIntAttributeMetadata.</summary>
    BigInt,

    /// <summary>DecimalAttributeMetadata.</summary>
    Decimal,

    /// <summary>DoubleAttributeMetadata.</summary>
    Double,

    /// <summary>MoneyAttributeMetadata.</summary>
    Money,

    /// <summary>DateTimeAttributeMetadata.</summary>
    DateTime,
}

internal enum BogusRawKind { String, Bool, Int32, Double, Decimal, DateLike }

internal enum BogusRiskClass { None, RoutableContact, ExternalResource, FinancialIdentifier }

internal enum BogusLengthPolicy { Unknown, Fixed, ArgumentDerived }

internal readonly record struct BogusEndpointId(string Api, string Endpoint)
{
    public override string ToString() => $"{Api}.{Endpoint}";
}

internal abstract record BogusArgumentContract
{
    public static readonly BogusArgumentContract None = new NoArgumentContract();
}

internal sealed record NoArgumentContract : BogusArgumentContract;

internal sealed record NumericRangeContract(decimal DefaultMin, decimal DefaultMax, bool AcceptsAuthoredMinMax)
    : BogusArgumentContract;

internal sealed record LengthContract(int DefaultLength) : BogusArgumentContract;

internal sealed record DateRangeContract : BogusArgumentContract
{
    public static readonly DateRangeContract Required = new();
}

internal sealed record BogusOutputContract(
    BogusLengthPolicy Policy,
    int? Length = null,
    int Multiplier = 1,
    bool TransportCertified = false)
{
    public static readonly BogusOutputContract Unknown = new(BogusLengthPolicy.Unknown);
    public static BogusOutputContract FixedAscii(int length) =>
        new(BogusLengthPolicy.Fixed, length, 1, TransportCertified: true);
    public static BogusOutputContract DerivedAscii(int multiplier) =>
        new(BogusLengthPolicy.ArgumentDerived, Length: null, multiplier, TransportCertified: true);
}

internal abstract record NormalizedBogusArgs
{
    public static readonly NormalizedBogusArgs None = new NoNormalizedArgs();
}

internal sealed record NoNormalizedArgs : NormalizedBogusArgs;

internal sealed record NumericRangeArgs(decimal Min, decimal Max, int Scale = 0) : NormalizedBogusArgs;

internal sealed record LengthArgs(int Length) : NormalizedBogusArgs;

internal sealed record DateRangeArgs(DateOnly Min, DateOnly Max) : NormalizedBogusArgs;

internal sealed record BogusInvocationContext(
    Faker Faker,
    NormalizedBogusArgs Args,
    DateTime ReferenceUtc,
    bool UseDateOnly,
    int Scale = 2);

internal sealed record BogusEndpointDescriptor(
    BogusEndpointId Id,
    BogusRawKind RawKind,
    FrozenSet<DataverseValueKind> Targets,
    BogusArgumentContract Arguments,
    BogusOutputContract Output,
    BogusRiskClass Risk,
    Func<BogusInvocationContext, object> Invoke);

/// <summary>Frozen typed catalog of retained Bogus endpoints. Single owner of every endpoint fact.</summary>
internal static class BogusCatalog
{
    private static readonly FrozenSet<DataverseValueKind> TextTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.String, DataverseValueKind.Memo]);
    private static readonly FrozenSet<DataverseValueKind> DateTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.DateTime]);
    private static readonly FrozenSet<DataverseValueKind> BoolTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.Boolean]);
    private static readonly FrozenSet<DataverseValueKind> LatLongTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.Double, DataverseValueKind.Decimal]);
    private static readonly FrozenSet<DataverseValueKind> AmountTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.Decimal, DataverseValueKind.Money]);
    private static readonly FrozenSet<DataverseValueKind> PortTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.Integer, DataverseValueKind.BigInt, DataverseValueKind.Decimal, DataverseValueKind.Double]);
    private static readonly FrozenSet<DataverseValueKind> RandIntTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.Integer, DataverseValueKind.BigInt, DataverseValueKind.Decimal, DataverseValueKind.Money, DataverseValueKind.Double]);
    private static readonly FrozenSet<DataverseValueKind> RandLongTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.BigInt, DataverseValueKind.Decimal, DataverseValueKind.Money]);
    private static readonly FrozenSet<DataverseValueKind> RandULongTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.Decimal, DataverseValueKind.Money]);
    private static readonly FrozenSet<DataverseValueKind> RandFloatTargets =
        FrozenSet.ToFrozenSet([DataverseValueKind.Double, DataverseValueKind.Decimal, DataverseValueKind.Money]);

    /// <summary>UI-ordered manifest. Order is part of the contract and is asserted by golden tests.</summary>
    public static readonly ImmutableArray<BogusEndpointDescriptor> All = BuildManifest();

    private static readonly FrozenDictionary<BogusEndpointId, BogusEndpointDescriptor> ById =
        All.ToFrozenDictionary(d => d.Id);

    /// <summary>Ordinal, case-sensitive O(1) lookup. Evaluator never scans.</summary>
    public static bool TryGet(BogusEndpointId id, out BogusEndpointDescriptor descriptor) =>
        ById.TryGetValue(id, out descriptor!);

    /// <summary>True when the descriptor can produce the target kind. Domain proof is validation's job.</summary>
    public static bool Fits(BogusEndpointDescriptor d, DataverseValueKind target) => d.Targets.Contains(target);

    private static ImmutableArray<BogusEndpointDescriptor> BuildManifest() =>
    [
        T("ADDRESS", "zipCode", f => f.Address.ZipCode()),
        T("ADDRESS", "city", f => f.Address.City()),
        T("ADDRESS", "streetAddress", f => f.Address.StreetAddress()),
        T("ADDRESS", "cityPrefix", f => f.Address.CityPrefix()),
        T("ADDRESS", "citySuffix", f => f.Address.CitySuffix()),
        T("ADDRESS", "streetName", f => f.Address.StreetName()),
        T("ADDRESS", "buildingNumber", f => f.Address.BuildingNumber()),
        T("ADDRESS", "streetSuffix", f => f.Address.StreetSuffix()),
        T("ADDRESS", "secondaryAddress", f => f.Address.SecondaryAddress()),
        T("ADDRESS", "county", f => f.Address.County()),
        T("ADDRESS", "country", f => f.Address.Country()),
        T("ADDRESS", "fullAddress", f => f.Address.FullAddress()),
        T("ADDRESS", "countryCode", f => f.Address.CountryCode()),
        T("ADDRESS", "state", f => f.Address.State()),
        T("ADDRESS", "stateAbbr", f => f.Address.StateAbbr()),
        Num("ADDRESS", "latitude", BogusRawKind.Double, LatLongTargets, new NumericRangeContract(-90, 90, false),
            ctx => ctx.Faker.Address.Latitude((double)NumMin(ctx, -90), (double)NumMax(ctx, 90))),
        Num("ADDRESS", "longitude", BogusRawKind.Double, LatLongTargets, new NumericRangeContract(-180, 180, false),
            ctx => ctx.Faker.Address.Longitude((double)NumMin(ctx, -180), (double)NumMax(ctx, 180))),
        T("ADDRESS", "direction", f => f.Address.Direction()),
        T("ADDRESS", "cardinalDirection", f => f.Address.CardinalDirection()),
        T("ADDRESS", "ordinalDirection", f => f.Address.OrdinalDirection()),

        T("COMMERCE", "department", f => f.Commerce.Department()),
        T("COMMERCE", "price", f => f.Commerce.Price()),
        T("COMMERCE", "productName", f => f.Commerce.ProductName()),
        T("COMMERCE", "color", f => f.Commerce.Color()),
        T("COMMERCE", "product", f => f.Commerce.Product()),
        T("COMMERCE", "productAdjective", f => f.Commerce.ProductAdjective()),
        T("COMMERCE", "productMaterial", f => f.Commerce.ProductMaterial()),
        T("COMMERCE", "ean8", f => f.Commerce.Ean8(), output: BogusOutputContract.FixedAscii(8)),
        T("COMMERCE", "ean13", f => f.Commerce.Ean13(), output: BogusOutputContract.FixedAscii(13)),

        T("COMPANY", "companySuffix", f => f.Company.CompanySuffix()),
        T("COMPANY", "companyName", f => f.Company.CompanyName()),
        T("COMPANY", "catchPhrase", f => f.Company.CatchPhrase()),
        T("COMPANY", "bs", f => f.Company.Bs()),

        T("DATABASE", "column", f => f.Database.Column()),
        T("DATABASE", "type", f => f.Database.Type()),
        T("DATABASE", "collation", f => f.Database.Collation()),
        T("DATABASE", "engine", f => f.Database.Engine()),

        Date("DATE", "past",
            ctx => ctx.Faker.Date.Past(1, ctx.ReferenceUtc),
            ctx => ctx.Faker.Date.PastDateOnly(1, DateOnly.FromDateTime(ctx.ReferenceUtc))),
        Date("DATE", "soon",
            ctx => ctx.Faker.Date.Soon(1, ctx.ReferenceUtc),
            ctx => ctx.Faker.Date.SoonDateOnly(1, DateOnly.FromDateTime(ctx.ReferenceUtc))),
        Date("DATE", "future",
            ctx => ctx.Faker.Date.Future(1, ctx.ReferenceUtc),
            ctx => ctx.Faker.Date.FutureDateOnly(1, DateOnly.FromDateTime(ctx.ReferenceUtc))),
        Date("DATE", "recent",
            ctx => ctx.Faker.Date.Recent(1, ctx.ReferenceUtc),
            ctx => ctx.Faker.Date.RecentDateOnly(1, DateOnly.FromDateTime(ctx.ReferenceUtc))),
        new BogusEndpointDescriptor(
            new("DATE", "between"),
            BogusRawKind.DateLike,
            DateTargets,
            DateRangeContract.Required,
            BogusOutputContract.Unknown,
            BogusRiskClass.None,
            InvokeBetween),
        T("DATE", "month", f => f.Date.Month()),
        T("DATE", "weekday", f => f.Date.Weekday()),

        T("FINANCE", "account", f => f.Finance.Account(), risk: BogusRiskClass.FinancialIdentifier),
        T("FINANCE", "accountName", f => f.Finance.AccountName()),
        Num("FINANCE", "amount", BogusRawKind.Decimal, AmountTargets, new NumericRangeContract(0, 1000, false),
            ctx => ctx.Faker.Finance.Amount(NumMin(ctx, 0), NumMax(ctx, 1000), ctx.Scale)),
        T("FINANCE", "transactionType", f => f.Finance.TransactionType()),
        T("FINANCE", "creditCardNumber", f => f.Finance.CreditCardNumber(), risk: BogusRiskClass.FinancialIdentifier),
        T("FINANCE", "creditCardCvv", f => f.Finance.CreditCardCvv(), risk: BogusRiskClass.FinancialIdentifier),
        T("FINANCE", "bitcoinAddress", f => f.Finance.BitcoinAddress(), risk: BogusRiskClass.FinancialIdentifier),
        T("FINANCE", "ethereumAddress", f => f.Finance.EthereumAddress(), risk: BogusRiskClass.FinancialIdentifier),
        T("FINANCE", "routingNumber", f => f.Finance.RoutingNumber(), risk: BogusRiskClass.FinancialIdentifier),
        T("FINANCE", "bic", f => f.Finance.Bic(), risk: BogusRiskClass.FinancialIdentifier),
        T("FINANCE", "iban", f => f.Finance.Iban(), risk: BogusRiskClass.FinancialIdentifier),

        T("HACKER", "abbreviation", f => f.Hacker.Abbreviation()),
        T("HACKER", "adjective", f => f.Hacker.Adjective()),
        T("HACKER", "noun", f => f.Hacker.Noun()),
        T("HACKER", "verb", f => f.Hacker.Verb()),
        T("HACKER", "ingVerb", f => f.Hacker.IngVerb()),
        T("HACKER", "phrase", f => f.Hacker.Phrase()),

        T("IMAGE", "dataUri", f => f.Image.DataUri(640, 480)),
        T("IMAGE", "picsumUrl", f => f.Image.PicsumUrl(640, 480), risk: BogusRiskClass.ExternalResource),
        T("IMAGE", "placeholderUrl", f => f.Image.PlaceholderUrl(640, 480), risk: BogusRiskClass.ExternalResource),
        T("IMAGE", "loremFlickrUrl", f => f.Image.LoremFlickrUrl(640, 480), risk: BogusRiskClass.ExternalResource),

        T("INTERNET", "avatar", f => f.Internet.Avatar(), risk: BogusRiskClass.ExternalResource),
        T("INTERNET", "email", f => f.Internet.Email(), risk: BogusRiskClass.RoutableContact),
        T("INTERNET", "exampleEmail", f => f.Internet.ExampleEmail()),
        T("INTERNET", "userName", f => f.Internet.UserName()),
        T("INTERNET", "userNameUnicode", f => f.Internet.UserNameUnicode()),
        T("INTERNET", "domainName", f => f.Internet.DomainName(), risk: BogusRiskClass.RoutableContact),
        T("INTERNET", "domainWord", f => f.Internet.DomainWord()),
        T("INTERNET", "domainSuffix", f => f.Internet.DomainSuffix()),
        T("INTERNET", "ip", f => f.Internet.Ip(), risk: BogusRiskClass.RoutableContact),
        Num("INTERNET", "port", BogusRawKind.Int32, PortTargets, new NumericRangeContract(1, 65535, false),
            ctx => ctx.Faker.Internet.Port()),
        T("INTERNET", "ipv6", f => f.Internet.Ipv6(), risk: BogusRiskClass.RoutableContact),
        T("INTERNET", "userAgent", f => f.Internet.UserAgent()),
        T("INTERNET", "mac", f => f.Internet.Mac()),
        T("INTERNET", "password", f => f.Internet.Password()),
        T("INTERNET", "color", f => f.Internet.Color()),
        T("INTERNET", "protocol", f => f.Internet.Protocol()),
        T("INTERNET", "url", f => f.Internet.Url(), risk: BogusRiskClass.ExternalResource),
        T("INTERNET", "urlWithPath", f => f.Internet.UrlWithPath(), risk: BogusRiskClass.ExternalResource),
        T("INTERNET", "urlRootedPath", f => f.Internet.UrlRootedPath()),

        T("LOREM", "word", f => f.Lorem.Word()),
        T("LOREM", "letter", f => f.Lorem.Letter()),
        T("LOREM", "sentence", f => f.Lorem.Sentence()),
        T("LOREM", "sentences", f => f.Lorem.Sentences()),
        T("LOREM", "paragraph", f => f.Lorem.Paragraph()),
        T("LOREM", "paragraphs", f => f.Lorem.Paragraphs()),
        T("LOREM", "text", f => f.Lorem.Text()),
        T("LOREM", "lines", f => f.Lorem.Lines()),
        T("LOREM", "slug", f => f.Lorem.Slug()),

        T("NAME", "firstName", f => f.Name.FirstName()),
        T("NAME", "lastName", f => f.Name.LastName()),
        T("NAME", "fullName", f => f.Name.FullName()),
        T("NAME", "prefix", f => f.Name.Prefix()),
        T("NAME", "suffix", f => f.Name.Suffix()),
        T("NAME", "findName", f => f.Name.FindName()),
        T("NAME", "jobTitle", f => f.Name.JobTitle()),
        T("NAME", "jobDescriptor", f => f.Name.JobDescriptor()),
        T("NAME", "jobArea", f => f.Name.JobArea()),
        T("NAME", "jobType", f => f.Name.JobType()),

        T("PHONE", "phoneNumber", f => f.Phone.PhoneNumber(), risk: BogusRiskClass.RoutableContact),
        T("PHONE", "phoneNumberFormat", f => f.Phone.PhoneNumberFormat(0), risk: BogusRiskClass.RoutableContact),

        T("RANT", "review", f => f.Rant.Review()),

        T("VEHICLE", "vin", f => f.Vehicle.Vin(), output: BogusOutputContract.FixedAscii(17)),
        T("VEHICLE", "manufacturer", f => f.Vehicle.Manufacturer()),
        T("VEHICLE", "model", f => f.Vehicle.Model()),
        T("VEHICLE", "type", f => f.Vehicle.Type()),
        T("VEHICLE", "fuel", f => f.Vehicle.Fuel()),

        T("MUSIC", "genre", f => f.Music.Genre()),

        RandInt("number", 0, int.MaxValue, ctx => ctx.Faker.Random.Number((int)NumMin(ctx, 0), (int)NumMax(ctx, int.MaxValue))),
        RandInt("even", 0, int.MaxValue, ctx => ctx.Faker.Random.Even((int)NumMin(ctx, 0), (int)NumMax(ctx, int.MaxValue))),
        RandInt("odd", 0, int.MaxValue, ctx => ctx.Faker.Random.Odd((int)NumMin(ctx, 0), (int)NumMax(ctx, int.MaxValue))),
        RandInt("int", int.MinValue, int.MaxValue, ctx => ctx.Faker.Random.Int((int)NumMin(ctx, int.MinValue), (int)NumMax(ctx, int.MaxValue))),
        RandInt("byte", 0, 255, ctx => ctx.Faker.Random.Byte((byte)NumMin(ctx, 0), (byte)NumMax(ctx, 255))),
        RandInt("sByte", sbyte.MinValue, sbyte.MaxValue, ctx => ctx.Faker.Random.SByte((sbyte)NumMin(ctx, sbyte.MinValue), (sbyte)NumMax(ctx, sbyte.MaxValue))),
        RandInt("short", short.MinValue, short.MaxValue, ctx => ctx.Faker.Random.Short((short)NumMin(ctx, short.MinValue), (short)NumMax(ctx, short.MaxValue))),
        RandInt("uShort", 0, ushort.MaxValue, ctx => ctx.Faker.Random.UShort((ushort)NumMin(ctx, 0), (ushort)NumMax(ctx, ushort.MaxValue))),
        Num("RANDOM", "long", BogusRawKind.Decimal, RandLongTargets, new NumericRangeContract(long.MinValue, long.MaxValue, true),
            ctx => ctx.Faker.Random.Long((long)NumMin(ctx, long.MinValue), (long)NumMax(ctx, long.MaxValue))),
        Num("RANDOM", "uInt", BogusRawKind.Decimal, RandLongTargets, new NumericRangeContract(0, uint.MaxValue, true),
            ctx => ctx.Faker.Random.UInt((uint)NumMin(ctx, 0), (uint)NumMax(ctx, uint.MaxValue))),
        Num("RANDOM", "uLong", BogusRawKind.Decimal, RandULongTargets, new NumericRangeContract(0, ulong.MaxValue, true),
            ctx => ctx.Faker.Random.ULong(ToULong(NumMin(ctx, 0)), ToULong(NumMax(ctx, ulong.MaxValue)))),
        Num("RANDOM", "double", BogusRawKind.Double, RandFloatTargets, new NumericRangeContract(0, 1, true),
            ctx => ctx.Faker.Random.Double((double)NumMin(ctx, 0), (double)NumMax(ctx, 1))),
        Num("RANDOM", "float", BogusRawKind.Double, RandFloatTargets, new NumericRangeContract(0, 1, true),
            ctx => ctx.Faker.Random.Float((float)NumMin(ctx, 0), (float)NumMax(ctx, 1))),
        Num("RANDOM", "decimal", BogusRawKind.Decimal, RandULongTargets, new NumericRangeContract(0, 1, true),
            ctx => ctx.Faker.Random.Decimal(NumMin(ctx, 0), NumMax(ctx, 1))),
        new BogusEndpointDescriptor(
            new("RANDOM", "digits"),
            BogusRawKind.String,
            TextTargets,
            new LengthContract(8),
            BogusOutputContract.DerivedAscii(1),
            BogusRiskClass.None,
            InvokeDigits),
        new BogusEndpointDescriptor(
            new("RANDOM", "bytes"),
            BogusRawKind.String,
            TextTargets,
            new LengthContract(8),
            BogusOutputContract.DerivedAscii(2),
            BogusRiskClass.None,
            InvokeBytes),
        Num("RANDOM", "bool", BogusRawKind.Bool, BoolTargets, BogusArgumentContract.None,
            ctx => ctx.Faker.Random.Bool()),
    ];

    private static BogusEndpointDescriptor T(
        string api, string endpoint, Func<Faker, object> invoke,
        BogusRiskClass risk = BogusRiskClass.None, BogusOutputContract? output = null) =>
        new(new(api, endpoint), BogusRawKind.String, TextTargets, BogusArgumentContract.None,
            output ?? BogusOutputContract.Unknown, risk, ctx => invoke(ctx.Faker));

    private static BogusEndpointDescriptor Num(
        string api, string endpoint, BogusRawKind raw, FrozenSet<DataverseValueKind> targets,
        BogusArgumentContract args, Func<BogusInvocationContext, object> invoke) =>
        new(new(api, endpoint), raw, targets, args, BogusOutputContract.Unknown, BogusRiskClass.None, invoke);

    private static BogusEndpointDescriptor Date(
        string api, string endpoint,
        Func<BogusInvocationContext, DateTime> dateTime,
        Func<BogusInvocationContext, DateOnly> dateOnly) =>
        new(new(api, endpoint), BogusRawKind.DateLike, DateTargets, BogusArgumentContract.None,
            BogusOutputContract.Unknown, BogusRiskClass.None,
            ctx => ctx.UseDateOnly ? dateOnly(ctx) : dateTime(ctx));

    private static BogusEndpointDescriptor RandInt(
        string endpoint, decimal dMin, decimal dMax, Func<BogusInvocationContext, object> invoke) =>
        Num("RANDOM", endpoint, BogusRawKind.Int32, RandIntTargets, new NumericRangeContract(dMin, dMax, true), invoke);

    private static decimal NumMin(BogusInvocationContext ctx, decimal fallback) =>
        ctx.Args is NumericRangeArgs n ? n.Min : fallback;

    private static decimal NumMax(BogusInvocationContext ctx, decimal fallback) =>
        ctx.Args is NumericRangeArgs n ? n.Max : fallback;

    private static int LengthOrDefault(BogusInvocationContext ctx, int fallback) =>
        ctx.Args is LengthArgs l ? l.Length : fallback;

    private static ulong ToULong(decimal value) => (ulong)value;

    private static object InvokeBetween(BogusInvocationContext ctx)
    {
        var range = ctx.Args as DateRangeArgs
            ?? throw new ArgumentException("DATE.between requires min/max.", nameof(ctx));
        if (ctx.UseDateOnly)
            return ctx.Faker.Date.BetweenDateOnly(range.Min, range.Max);

        var start = DateTime.SpecifyKind(range.Min.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var endExclusiveCap = DateTime.SpecifyKind(range.Max.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            .AddDays(1).AddTicks(-1);
        return ctx.Faker.Date.Between(start, endExclusiveCap);
    }

    private static object InvokeDigits(BogusInvocationContext ctx)
    {
        var length = LengthOrDefault(ctx, 8);
        var digits = ctx.Faker.Random.Digits(length, 0, 9);
        return string.Concat(digits.Select(d => d.ToString(CultureInfo.InvariantCulture)));
    }

    private static object InvokeBytes(BogusInvocationContext ctx)
    {
        var length = LengthOrDefault(ctx, 8);
        return Convert.ToHexString(ctx.Faker.Random.Bytes(length)).ToLowerInvariant();
    }
}

/// <summary>Public, narrow surface for WPF. Exposes no descriptors or invocation delegates.</summary>
public static class BogusCatalogQuery
{
    /// <summary>Ordered distinct API IDs that have at least one endpoint compatible with <paramref name="target"/>.</summary>
    public static IReadOnlyList<string> ApisFor(DataverseValueKind target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var apis = new List<string>();
        foreach (var d in BogusCatalog.All)
        {
            if (!BogusCatalog.Fits(d, target) || !seen.Add(d.Id.Api))
                continue;
            apis.Add(d.Id.Api);
        }

        return apis;
    }

    /// <summary>UI-ordered endpoints of <paramref name="api"/> that can produce <paramref name="target"/>.</summary>
    public static IReadOnlyList<BogusEndpointOption> EndpointsFor(string api, DataverseValueKind target)
    {
        var options = new List<BogusEndpointOption>();
        foreach (var d in BogusCatalog.All)
        {
            if (!string.Equals(d.Id.Api, api, StringComparison.Ordinal) || !BogusCatalog.Fits(d, target))
                continue;
            options.Add(new BogusEndpointOption(d.Id.ToString(), d.Id.Endpoint));
        }

        return options;
    }

    /// <summary>True when any retained endpoint can produce <paramref name="target"/>.</summary>
    public static bool HasAny(DataverseValueKind target)
    {
        foreach (var d in BogusCatalog.All)
        {
            if (BogusCatalog.Fits(d, target))
                return true;
        }

        return false;
    }
}

/// <summary>UI-facing endpoint choice. Id is the ordinal catalog ID; DisplayName is presentation only.</summary>
/// <param name="Id">Ordinal catalog ID, for example <c>NAME.firstName</c>.</param>
/// <param name="DisplayName">Presentation label.</param>
public sealed record BogusEndpointOption(string Id, string DisplayName);
