namespace DataGen.Core.Tests;

public class ExceptionTests
{
    [Fact]
    public void DataGenerationException_StoresMessage()
    {
        var ex = new DataGenerationException("test error");
        Assert.Equal("test error", ex.Message);
    }

    [Fact]
    public void DataGenerationException_StoresInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new DataGenerationException("test error", inner);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void SchemaException_InheritsFromDataGenerationException()
    {
        var ex = new SchemaException("schema error");
        Assert.IsAssignableFrom<DataGenerationException>(ex);
    }

    [Fact]
    public void SchemaException_StoresInnerException()
    {
        var inner = new Exception("root cause");
        var ex = new SchemaException("schema error", inner);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void CyclicalDependencyException_StoresCycles()
    {
        var cycleList = new List<IReadOnlyList<string>> { new List<string> { "a", "b" }.AsReadOnly() };
        IReadOnlyList<IReadOnlyList<string>> cycles = cycleList.AsReadOnly();
        var ex = new CyclicalDependencyException("cycle detected", cycles);

        Assert.Single(ex.Cycles);
        Assert.Contains("a", ex.Cycles[0]);
        Assert.Contains("b", ex.Cycles[0]);
    }

    [Fact]
    public void UnbreakableCycleException_InheritsFromCyclicalDependencyException()
    {
        var cycleList = new List<IReadOnlyList<string>> { new List<string> { "a", "b" }.AsReadOnly() };
        IReadOnlyList<IReadOnlyList<string>> cycles = cycleList.AsReadOnly();
        var ex = new UnbreakableCycleException("unbreakable", cycles);
        Assert.IsAssignableFrom<CyclicalDependencyException>(ex);
    }

}
