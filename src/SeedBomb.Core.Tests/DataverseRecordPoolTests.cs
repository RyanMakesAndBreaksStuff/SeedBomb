namespace SeedBomb.Core.Tests;

public class DataverseRecordPoolTests
{
    [Fact]
    public void Add_ThenGet_ReturnsAddedIds()
    {
        var pool = new DataverseRecordPool();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        pool.Add("account", ids);

        var result = pool.Get("account");
        Assert.Equal(2, result.Count);
        Assert.Contains(ids[0], result);
        Assert.Contains(ids[1], result);
    }

    [Fact]
    public void Get_UnknownEntity_ReturnsEmpty()
    {
        var pool = new DataverseRecordPool();
        var result = pool.Get("nonexistent");
        Assert.Empty(result);
    }

    [Fact]
    public void GetRandom_EmptyPool_ReturnsNull()
    {
        var pool = new DataverseRecordPool();
        var faker = DeterministicFaker.Create(42, 0);

        var result = pool.GetRandom("account", faker);
        Assert.Null(result);
    }

    [Fact]
    public void GetRandom_WithRecords_ReturnsValidId()
    {
        var pool = new DataverseRecordPool();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        pool.Add("account", [id1, id2]);
        var faker = DeterministicFaker.Create(42, 0);

        var result = pool.GetRandom("account", faker);

        Assert.NotNull(result);
        Assert.True(result == id1 || result == id2);
    }

    [Fact]
    public void Add_MultipleCallsSameEntity_AccumulatesIds()
    {
        var pool = new DataverseRecordPool();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        pool.Add("account", [id1]);
        pool.Add("account", [id2]);

        var result = pool.Get("account");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Add_ThrowsOnNullEntity()
    {
        var pool = new DataverseRecordPool();
        Assert.Throws<ArgumentNullException>(() => pool.Add(null!, [Guid.NewGuid()]));
    }

    [Fact]
    public void Get_ThrowsOnNullEntity()
    {
        var pool = new DataverseRecordPool();
        Assert.Throws<ArgumentNullException>(() => pool.Get(null!));
    }

    [Fact]
    public void GetRandom_ThrowsOnNullEntity()
    {
        var pool = new DataverseRecordPool();
        var faker = DeterministicFaker.Create(42, 0);
        Assert.Throws<ArgumentNullException>(() => pool.GetRandom(null!, faker));
    }

    [Fact]
    public void GetRandom_ThrowsOnNullFaker()
    {
        var pool = new DataverseRecordPool();
        Assert.Throws<ArgumentNullException>(() => pool.GetRandom("account", null!));
    }
}
