using SeedBomb.Core.Rules;
using System.Globalization;

namespace SeedBomb.Core.Tests.Rules;

public class BogusCatalogTests
{
    [Fact]
    public void Manifest_MatchesGoldenPairListExactly()
    {
        // Transcribed independently from BogusReviewedPlan.md "Catalog manifest",
        // not read back from BogusCatalog.All.
        string[] golden =
        [
            "ADDRESS.zipCode", "ADDRESS.city", "ADDRESS.streetAddress", "ADDRESS.cityPrefix",
            "ADDRESS.citySuffix", "ADDRESS.streetName", "ADDRESS.buildingNumber", "ADDRESS.streetSuffix",
            "ADDRESS.secondaryAddress", "ADDRESS.county", "ADDRESS.country", "ADDRESS.fullAddress",
            "ADDRESS.countryCode", "ADDRESS.state", "ADDRESS.stateAbbr", "ADDRESS.latitude",
            "ADDRESS.longitude", "ADDRESS.direction", "ADDRESS.cardinalDirection", "ADDRESS.ordinalDirection",
            "COMMERCE.department", "COMMERCE.price", "COMMERCE.productName", "COMMERCE.color",
            "COMMERCE.product", "COMMERCE.productAdjective", "COMMERCE.productMaterial",
            "COMMERCE.ean8", "COMMERCE.ean13",
            "COMPANY.companySuffix", "COMPANY.companyName", "COMPANY.catchPhrase", "COMPANY.bs",
            "DATABASE.column", "DATABASE.type", "DATABASE.collation", "DATABASE.engine",
            "DATE.past", "DATE.soon", "DATE.future", "DATE.recent", "DATE.between", "DATE.month", "DATE.weekday",
            "FINANCE.account", "FINANCE.accountName", "FINANCE.amount", "FINANCE.transactionType",
            "FINANCE.creditCardNumber", "FINANCE.creditCardCvv", "FINANCE.bitcoinAddress",
            "FINANCE.ethereumAddress", "FINANCE.routingNumber", "FINANCE.bic", "FINANCE.iban",
            "HACKER.abbreviation", "HACKER.adjective", "HACKER.noun", "HACKER.verb", "HACKER.ingVerb", "HACKER.phrase",
            "IMAGE.dataUri", "IMAGE.picsumUrl", "IMAGE.placeholderUrl", "IMAGE.loremFlickrUrl",
            "INTERNET.avatar", "INTERNET.email", "INTERNET.exampleEmail", "INTERNET.userName",
            "INTERNET.userNameUnicode", "INTERNET.domainName", "INTERNET.domainWord", "INTERNET.domainSuffix",
            "INTERNET.ip", "INTERNET.port", "INTERNET.ipv6", "INTERNET.userAgent", "INTERNET.mac",
            "INTERNET.password", "INTERNET.color", "INTERNET.protocol", "INTERNET.url",
            "INTERNET.urlWithPath", "INTERNET.urlRootedPath",
            "LOREM.word", "LOREM.letter", "LOREM.sentence", "LOREM.sentences", "LOREM.paragraph",
            "LOREM.paragraphs", "LOREM.text", "LOREM.lines", "LOREM.slug",
            "NAME.firstName", "NAME.lastName", "NAME.fullName", "NAME.prefix", "NAME.suffix",
            "NAME.findName", "NAME.jobTitle", "NAME.jobDescriptor", "NAME.jobArea", "NAME.jobType",
            "PHONE.phoneNumber", "PHONE.phoneNumberFormat",
            "RANT.review",
            "VEHICLE.vin", "VEHICLE.manufacturer", "VEHICLE.model", "VEHICLE.type", "VEHICLE.fuel",
            "MUSIC.genre",
            "RANDOM.number", "RANDOM.even", "RANDOM.odd", "RANDOM.int", "RANDOM.byte", "RANDOM.sByte",
            "RANDOM.short", "RANDOM.uShort", "RANDOM.long", "RANDOM.uInt", "RANDOM.uLong",
            "RANDOM.double", "RANDOM.float", "RANDOM.decimal", "RANDOM.digits", "RANDOM.bytes", "RANDOM.bool",
        ];

        var actual = BogusCatalog.All.Select(d => d.Id.ToString()).ToArray();

        Assert.Equal(129, golden.Length);
        Assert.Equal(golden, actual);
        Assert.Equal(golden.Length, golden.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Exhaustive_target_matrix_matches_oracle()
    {
        var expected = new Dictionary<string, DataverseValueKind[]>(StringComparer.Ordinal);
        foreach (var id in DefaultTextIds())
            expected[id] = [DataverseValueKind.String, DataverseValueKind.Memo];

        expected["ADDRESS.latitude"] = [DataverseValueKind.Double, DataverseValueKind.Decimal];
        expected["ADDRESS.longitude"] = [DataverseValueKind.Double, DataverseValueKind.Decimal];
        expected["FINANCE.amount"] = [DataverseValueKind.Decimal, DataverseValueKind.Money];
        expected["INTERNET.port"] = [DataverseValueKind.Integer, DataverseValueKind.BigInt, DataverseValueKind.Decimal, DataverseValueKind.Double];
        expected["RANDOM.bool"] = [DataverseValueKind.Boolean];
        foreach (var id in new[] { "RANDOM.number", "RANDOM.even", "RANDOM.odd", "RANDOM.int", "RANDOM.byte", "RANDOM.sByte", "RANDOM.short", "RANDOM.uShort" })
            expected[id] = [DataverseValueKind.Integer, DataverseValueKind.BigInt, DataverseValueKind.Decimal, DataverseValueKind.Money, DataverseValueKind.Double];
        expected["RANDOM.long"] = [DataverseValueKind.BigInt, DataverseValueKind.Decimal, DataverseValueKind.Money];
        expected["RANDOM.uInt"] = [DataverseValueKind.BigInt, DataverseValueKind.Decimal, DataverseValueKind.Money];
        expected["RANDOM.uLong"] = [DataverseValueKind.Decimal, DataverseValueKind.Money];
        expected["RANDOM.double"] = [DataverseValueKind.Double, DataverseValueKind.Decimal, DataverseValueKind.Money];
        expected["RANDOM.float"] = [DataverseValueKind.Double, DataverseValueKind.Decimal, DataverseValueKind.Money];
        expected["RANDOM.decimal"] = [DataverseValueKind.Decimal, DataverseValueKind.Money];
        foreach (var id in new[] { "DATE.past", "DATE.soon", "DATE.future", "DATE.recent", "DATE.between" })
            expected[id] = [DataverseValueKind.DateTime];

        Assert.Equal(129, expected.Count);
        foreach (var d in BogusCatalog.All)
        {
            Assert.True(expected.TryGetValue(d.Id.ToString(), out var targets), d.Id.ToString());
            foreach (var kind in Enum.GetValues<DataverseValueKind>())
                Assert.Equal(targets.Contains(kind), BogusCatalog.Fits(d, kind));
        }
    }

    [Fact]
    public void Risk_snapshot_matches_oracle()
    {
        var expected = new Dictionary<string, BogusRiskClass>(StringComparer.Ordinal)
        {
            ["INTERNET.email"] = BogusRiskClass.RoutableContact,
            ["INTERNET.domainName"] = BogusRiskClass.RoutableContact,
            ["INTERNET.ip"] = BogusRiskClass.RoutableContact,
            ["INTERNET.ipv6"] = BogusRiskClass.RoutableContact,
            ["PHONE.phoneNumber"] = BogusRiskClass.RoutableContact,
            ["PHONE.phoneNumberFormat"] = BogusRiskClass.RoutableContact,
            ["IMAGE.picsumUrl"] = BogusRiskClass.ExternalResource,
            ["IMAGE.placeholderUrl"] = BogusRiskClass.ExternalResource,
            ["IMAGE.loremFlickrUrl"] = BogusRiskClass.ExternalResource,
            ["INTERNET.avatar"] = BogusRiskClass.ExternalResource,
            ["INTERNET.url"] = BogusRiskClass.ExternalResource,
            ["INTERNET.urlWithPath"] = BogusRiskClass.ExternalResource,
            ["FINANCE.account"] = BogusRiskClass.FinancialIdentifier,
            ["FINANCE.creditCardNumber"] = BogusRiskClass.FinancialIdentifier,
            ["FINANCE.creditCardCvv"] = BogusRiskClass.FinancialIdentifier,
            ["FINANCE.bitcoinAddress"] = BogusRiskClass.FinancialIdentifier,
            ["FINANCE.ethereumAddress"] = BogusRiskClass.FinancialIdentifier,
            ["FINANCE.routingNumber"] = BogusRiskClass.FinancialIdentifier,
            ["FINANCE.bic"] = BogusRiskClass.FinancialIdentifier,
            ["FINANCE.iban"] = BogusRiskClass.FinancialIdentifier,
        };

        foreach (var d in BogusCatalog.All)
        {
            var risk = expected.GetValueOrDefault(d.Id.ToString(), BogusRiskClass.None);
            Assert.Equal(risk, d.Risk);
        }
    }

    [Fact]
    public void Output_policy_snapshot_matches_oracle()
    {
        Assert.Equal(BogusLengthPolicy.Fixed, Desc("COMMERCE.ean8").Output.Policy);
        Assert.Equal(8, Desc("COMMERCE.ean8").Output.Length);
        Assert.True(Desc("COMMERCE.ean8").Output.TransportCertified);
        Assert.Equal(13, Desc("COMMERCE.ean13").Output.Length);
        Assert.Equal(17, Desc("VEHICLE.vin").Output.Length);
        Assert.Equal(BogusLengthPolicy.ArgumentDerived, Desc("RANDOM.digits").Output.Policy);
        Assert.Equal(1, Desc("RANDOM.digits").Output.Multiplier);
        Assert.Equal(2, Desc("RANDOM.bytes").Output.Multiplier);
        Assert.Equal(BogusLengthPolicy.Unknown, Desc("NAME.firstName").Output.Policy);
        Assert.Equal(BogusLengthPolicy.Unknown, Desc("COMMERCE.price").Output.Policy);
    }

    [Fact]
    public void Default_and_authored_invocations_match_argument_contracts()
    {
        var number = Desc("RANDOM.number").Invoke(Ctx());
        Assert.IsType<int>(number);
        Assert.InRange((int)number, 0, int.MaxValue);

        var authored = Desc("RANDOM.number").Invoke(Ctx(new NumericRangeArgs(3, 3)));
        Assert.Equal(3, authored);

        var digits = Assert.IsType<string>(Desc("RANDOM.digits").Invoke(Ctx()));
        Assert.Equal(8, digits.Length);
        Assert.All(digits, ch => Assert.True(char.IsAsciiDigit(ch)));

        var longDigits = Assert.IsType<string>(Desc("RANDOM.digits").Invoke(Ctx(new LengthArgs(12))));
        Assert.Equal(12, longDigits.Length);

        var bytes = Assert.IsType<string>(Desc("RANDOM.bytes").Invoke(Ctx()));
        Assert.Equal(16, bytes.Length);
        Assert.Matches("^[0-9a-f]+$", bytes);

        var longBytes = Assert.IsType<string>(Desc("RANDOM.bytes").Invoke(Ctx(new LengthArgs(4))));
        Assert.Equal(8, longBytes.Length);

        var lat = Assert.IsType<double>(Desc("ADDRESS.latitude").Invoke(Ctx()));
        Assert.InRange(lat, -90, 90);
        var lon = Assert.IsType<double>(Desc("ADDRESS.longitude").Invoke(Ctx()));
        Assert.InRange(lon, -180, 180);

        var amount = Assert.IsType<decimal>(Desc("FINANCE.amount").Invoke(Ctx()));
        Assert.InRange(amount, 0m, 1000m);

        var port = Assert.IsType<int>(Desc("INTERNET.port").Invoke(Ctx()));
        Assert.InRange(port, 1, 65535);

        Assert.IsType<bool>(Desc("RANDOM.bool").Invoke(Ctx()));
        Assert.IsType<string>(Desc("NAME.firstName").Invoke(Ctx()));
    }

    [Fact]
    public void Image_invocations_use_640_by_480()
    {
        foreach (var id in new[] { "IMAGE.dataUri", "IMAGE.picsumUrl", "IMAGE.placeholderUrl", "IMAGE.loremFlickrUrl" })
        {
            var value = Assert.IsType<string>(Desc(id).Invoke(Ctx()));
            Assert.Contains("640", value, StringComparison.Ordinal);
            Assert.Contains("480", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Commerce_price_is_numeric_text_without_currency_symbol()
    {
        var price = Assert.IsType<string>(Desc("COMMERCE.price").Invoke(Ctx()));
        Assert.False(price.StartsWith('$'));
        Assert.True(decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out _));
    }

    [Fact]
    public void DateLike_branches_on_prepared_target_mode()
    {
        var pastDt = Desc("DATE.past").Invoke(Ctx(dateOnly: false));
        Assert.IsType<DateTime>(pastDt);
        var pastDo = Desc("DATE.past").Invoke(Ctx(dateOnly: true));
        Assert.IsType<DateOnly>(pastDo);

        var min = new DateOnly(2020, 1, 1);
        var max = new DateOnly(2020, 12, 31);
        var betweenDt = Desc("DATE.between").Invoke(Ctx(new DateRangeArgs(min, max), dateOnly: false));
        var dt = Assert.IsType<DateTime>(betweenDt);
        Assert.InRange(dt, min.ToDateTime(TimeOnly.MinValue), max.ToDateTime(TimeOnly.MaxValue));

        var betweenDo = Desc("DATE.between").Invoke(Ctx(new DateRangeArgs(min, max), dateOnly: true));
        var only = Assert.IsType<DateOnly>(betweenDo);
        Assert.InRange(only, min, max);
    }

    [Fact]
    public void Every_text_endpoint_emits_well_formed_utf16_and_xml_characters()
    {
        foreach (var d in BogusCatalog.All.Where(x => x.RawKind == BogusRawKind.String))
        {
            var args = d.Arguments is DateRangeContract
                ? (NormalizedBogusArgs)new DateRangeArgs(new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 2))
                : NormalizedBogusArgs.None;
            var value = Assert.IsType<string>(d.Invoke(Ctx(args)));
            Assert.True(IsXmlSafe(value), d.Id.ToString());
        }
    }

    [Fact]
    public void Query_surface_hides_descriptors_and_follows_manifest_order()
    {
        Assert.True(BogusCatalogQuery.HasAny(DataverseValueKind.String));
        Assert.True(BogusCatalogQuery.HasAny(DataverseValueKind.DateTime));
        var apis = BogusCatalogQuery.ApisFor(DataverseValueKind.String);
        Assert.Equal("ADDRESS", apis[0]);
        Assert.Contains("RANDOM", apis);
        var endpoints = BogusCatalogQuery.EndpointsFor("NAME", DataverseValueKind.String);
        Assert.Equal("NAME.firstName", endpoints[0].Id);
        Assert.DoesNotContain(BogusCatalogQuery.EndpointsFor("RANDOM", DataverseValueKind.String),
            o => o.Id == "RANDOM.number");
    }

    private static BogusEndpointDescriptor Desc(string id)
    {
        var dot = id.IndexOf('.');
        Assert.True(BogusCatalog.TryGet(new BogusEndpointId(id[..dot], id[(dot + 1)..]), out var d));
        return d;
    }

    private static BogusInvocationContext Ctx(
        NormalizedBogusArgs? args = null, bool dateOnly = false, int seed = 42) =>
        new(DeterministicFaker.Create(seed, 0, "en"), args ?? NormalizedBogusArgs.None,
            DeterministicFaker.ReferenceDate, dateOnly);

    private static IEnumerable<string> DefaultTextIds()
    {
        string[] exceptions =
        [
            "ADDRESS.latitude", "ADDRESS.longitude",
            "DATE.past", "DATE.soon", "DATE.future", "DATE.recent", "DATE.between",
            "FINANCE.amount", "INTERNET.port",
            "RANDOM.number", "RANDOM.even", "RANDOM.odd", "RANDOM.int", "RANDOM.byte", "RANDOM.sByte",
            "RANDOM.short", "RANDOM.uShort", "RANDOM.long", "RANDOM.uInt", "RANDOM.uLong",
            "RANDOM.double", "RANDOM.float", "RANDOM.decimal", "RANDOM.bool",
        ];
        return GoldenIds().Except(exceptions, StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> GoldenIds() =>
    [
        "ADDRESS.zipCode", "ADDRESS.city", "ADDRESS.streetAddress", "ADDRESS.cityPrefix",
        "ADDRESS.citySuffix", "ADDRESS.streetName", "ADDRESS.buildingNumber", "ADDRESS.streetSuffix",
        "ADDRESS.secondaryAddress", "ADDRESS.county", "ADDRESS.country", "ADDRESS.fullAddress",
        "ADDRESS.countryCode", "ADDRESS.state", "ADDRESS.stateAbbr", "ADDRESS.latitude",
        "ADDRESS.longitude", "ADDRESS.direction", "ADDRESS.cardinalDirection", "ADDRESS.ordinalDirection",
        "COMMERCE.department", "COMMERCE.price", "COMMERCE.productName", "COMMERCE.color",
        "COMMERCE.product", "COMMERCE.productAdjective", "COMMERCE.productMaterial",
        "COMMERCE.ean8", "COMMERCE.ean13",
        "COMPANY.companySuffix", "COMPANY.companyName", "COMPANY.catchPhrase", "COMPANY.bs",
        "DATABASE.column", "DATABASE.type", "DATABASE.collation", "DATABASE.engine",
        "DATE.past", "DATE.soon", "DATE.future", "DATE.recent", "DATE.between", "DATE.month", "DATE.weekday",
        "FINANCE.account", "FINANCE.accountName", "FINANCE.amount", "FINANCE.transactionType",
        "FINANCE.creditCardNumber", "FINANCE.creditCardCvv", "FINANCE.bitcoinAddress",
        "FINANCE.ethereumAddress", "FINANCE.routingNumber", "FINANCE.bic", "FINANCE.iban",
        "HACKER.abbreviation", "HACKER.adjective", "HACKER.noun", "HACKER.verb", "HACKER.ingVerb", "HACKER.phrase",
        "IMAGE.dataUri", "IMAGE.picsumUrl", "IMAGE.placeholderUrl", "IMAGE.loremFlickrUrl",
        "INTERNET.avatar", "INTERNET.email", "INTERNET.exampleEmail", "INTERNET.userName",
        "INTERNET.userNameUnicode", "INTERNET.domainName", "INTERNET.domainWord", "INTERNET.domainSuffix",
        "INTERNET.ip", "INTERNET.port", "INTERNET.ipv6", "INTERNET.userAgent", "INTERNET.mac",
        "INTERNET.password", "INTERNET.color", "INTERNET.protocol", "INTERNET.url",
        "INTERNET.urlWithPath", "INTERNET.urlRootedPath",
        "LOREM.word", "LOREM.letter", "LOREM.sentence", "LOREM.sentences", "LOREM.paragraph",
        "LOREM.paragraphs", "LOREM.text", "LOREM.lines", "LOREM.slug",
        "NAME.firstName", "NAME.lastName", "NAME.fullName", "NAME.prefix", "NAME.suffix",
        "NAME.findName", "NAME.jobTitle", "NAME.jobDescriptor", "NAME.jobArea", "NAME.jobType",
        "PHONE.phoneNumber", "PHONE.phoneNumberFormat",
        "RANT.review",
        "VEHICLE.vin", "VEHICLE.manufacturer", "VEHICLE.model", "VEHICLE.type", "VEHICLE.fuel",
        "MUSIC.genre",
        "RANDOM.number", "RANDOM.even", "RANDOM.odd", "RANDOM.int", "RANDOM.byte", "RANDOM.sByte",
        "RANDOM.short", "RANDOM.uShort", "RANDOM.long", "RANDOM.uInt", "RANDOM.uLong",
        "RANDOM.double", "RANDOM.float", "RANDOM.decimal", "RANDOM.digits", "RANDOM.bytes", "RANDOM.bool",
    ];

    private static bool IsXmlSafe(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                    continue;
                }

                return false;
            }

            if (c is '\0' or < '\t' || (c > '\r' && c < ' ' && c != '\n') || (c >= '\uFFFE'))
                return false;
            if (c is '\v' or '\f')
                return false;
        }

        return true;
    }
}
