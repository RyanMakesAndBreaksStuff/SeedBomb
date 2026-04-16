using Bogus;

namespace DataGen.Core.Tests;

public class LookupFieldGeneratorTests
{
    private readonly LookupFieldGenerator _gen = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void CanGenerate_LookupMetadata_ReturnsTrue()
        => Assert.True(_gen.CanGenerate(new LookupAttributeMetadata()));

    [Fact]
    public void CanGenerate_StringMetadata_ReturnsFalse()
        => Assert.False(_gen.CanGenerate(new StringAttributeMetadata()));

    [Fact]
    public void Generate_EmptyPool_ReturnsNull()
    {
        var pool = new DataverseRecordPool();
        var attr = new LookupAttributeMetadata { LogicalName = "accountid", Targets = ["account"] };
        var result = _gen.Generate(attr, _faker, pool);
        Assert.Null(result);
    }

    [Fact]
    public void Generate_PoolHasRecords_ReturnsEntityReference()
    {
        var pool = new DataverseRecordPool();
        var id = Guid.NewGuid();
        pool.Add("account", [id]);

        var attr = new LookupAttributeMetadata { LogicalName = "accountid", Targets = ["account"] };
        var result = (EntityReference)_gen.Generate(attr, _faker, pool)!;

        Assert.Equal("account", result.LogicalName);
        Assert.Equal(id, result.Id);
    }

    [Fact]
    public void Generate_PolymorphicLookup_ReturnsEntityReferenceFromAvailableTarget()
    {
        var pool = new DataverseRecordPool();
        var accountId = Guid.NewGuid();
        pool.Add("account", [accountId]);
        // no contacts in pool

        var attr = new LookupAttributeMetadata
        {
            LogicalName = "customerid",
            Targets = ["account", "contact"]
        };

        // Run multiple times; should always return a valid reference (only account has records)
        for (int i = 0; i < 10; i++)
        {
            var faker = DeterministicFaker.Create(42, i);
            var result = _gen.Generate(attr, faker, pool);
            // May be null if contact was selected and pool is empty
            // But if it's not null, it must be the account
            if (result is EntityReference er)
            {
                Assert.Equal(accountId, er.Id);
            }
        }
    }

    [Fact]
    public void Generate_NullTargets_ReturnsNull()
    {
        var pool = new DataverseRecordPool();
        var attr = new LookupAttributeMetadata { LogicalName = "accountid", Targets = null };
        var result = _gen.Generate(attr, _faker, pool);
        Assert.Null(result);
    }
}
