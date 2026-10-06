# Add or change a field generator

Use this guide when changing the default value produced for a Dataverse attribute. For a reusable configuration of an existing field, first consider an app field rule; a generator change affects default generation across configurations.

Start with a working local build and basic familiarity with C# and Dataverse attribute metadata. Run commands from the repository root.

## 1. Locate the existing behavior

Open [GeneratorFactory](../../../src/SeedBomb.Core/Generators/GeneratorFactory.cs) and find the metadata type in its `Generate` switch. Follow the matching generator under `src/SeedBomb.Core/Generators` and its tests under `src/SeedBomb.Core.Tests`.

For example, [StringFieldGenerator](../../../src/SeedBomb.Core/Generators/StringFieldGenerator.cs) uses string format metadata, then field-name heuristics, and truncates the result to `MaxLength`. Its [tests](../../../src/SeedBomb.Core.Tests/StringFieldGeneratorTests.cs) check formats, value types, and maximum length. Change that implementation for a new string heuristic rather than adding another dispatch mechanism.

## 2. Define the behavior with a test

Add a test for the requested behavior before changing production code. Construct the actual SDK metadata subtype, including relevant bounds, formats, options, or lookup targets. Assert a useful property of the value, such as its SDK value type, permitted range, or metadata limit.

For seeded behavior, create separate Faker instances with [DeterministicFaker.Create](../../../src/SeedBomb.Core/Generators/DeterministicFaker.cs) when comparing the same generation sequence. Reusing one instance consumes its random sequence. Avoid exact-name or exact-sentence assertions unless that exact text is the requirement.

Run the selected test as described in [Run focused tests](focused-tests.md) and confirm it reproduces the missing behavior.

## 3. Implement the generator contract

Existing generators implement [IFieldGenerator](../../../src/SeedBomb.Core/Generators/IFieldGenerator.cs):

```csharp
object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool);
```

Use the supplied `faker` for random values and `pool` when resolving generated record references. Return a value appropriate for the SDK attribute, or `null` when a value cannot be produced. Respect the relevant metadata restrictions and match the surrounding code's style.

If adding support for a previously unsupported metadata type, add an implementation following an existing generator, add its field to `GeneratorFactory`, and add a case to the factory's switch. Generators are instantiated directly in this factory; there is no generator plug-in discovery or DI registration list.

Keep derived metadata cases before their base cases. The current switch deliberately matches `MemoAttributeMetadata` before `StringAttributeMetadata`, and `MultiSelectPicklistAttributeMetadata` before `PicklistAttributeMetadata`.

Add a [GeneratorFactoryTests](../../../src/SeedBomb.Core.Tests/GeneratorFactoryTests.cs) test when introducing or changing dispatch. Calling the generator directly does not prove the factory selects it.

### Worked example: a default tracking code

Suppose your requirement is a 12-character uppercase alphanumeric default for the string attribute `new_trackingcode`, shortened when its metadata allows fewer characters. The smallest change is normally another heuristic in `StringFieldGenerator`. The following example instead shows all the wiring for a separate generator, useful when that behavior warrants its own implementation. It is an example change to make in your checkout, not functionality already shipped.

Create `src/SeedBomb.Core/Generators/TrackingCodeFieldGenerator.cs`:

```csharp
using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

internal sealed class TrackingCodeFieldGenerator : IFieldGenerator
{
    /// <inheritdoc />
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var stringMetadata = (StringAttributeMetadata)metadata;
        var length = Math.Min(12, stringMetadata.MaxLength ?? 100);
        return faker.Random.AlphaNumeric(length).ToUpperInvariant();
    }
}
```

Add this field alongside the other generator fields in `GeneratorFactory`:

```csharp
private readonly TrackingCodeFieldGenerator _trackingCode = new();
```

In the existing metadata switch, replace the first two cases with these three. Keep every other case:

```csharp
MemoAttributeMetadata => _memo,
StringAttributeMetadata { LogicalName: "new_trackingcode" } => _trackingCode,
StringAttributeMetadata => _string,
```

The attribute-specific case must precede the general string case. This implementation assumes valid string metadata, as the existing string generator does.

Create `src/SeedBomb.Core.Tests/TrackingCodeFieldGeneratorTests.cs`. Testing through the factory verifies the registration as well as the value:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Metadata;
using SeedBomb.Core.Generators;
using Xunit;

namespace SeedBomb.Core.Tests;

public class TrackingCodeFieldGeneratorTests
{
    [Theory]
    [InlineData(100, 12)]
    [InlineData(5, 5)]
    public void Factory_TrackingCode_RespectsFormatAndLength(int maxLength, int expectedLength)
    {
        var factory = new GeneratorFactory(NullLogger<GeneratorFactory>.Instance);
        var metadata = new StringAttributeMetadata
        {
            LogicalName = "new_trackingcode",
            MaxLength = maxLength,
        };

        var value = Assert.IsType<string>(factory.Generate(
            metadata, DeterministicFaker.Create(42, 0), new DataverseRecordPool()));

        Assert.Equal(expectedLength, value.Length);
        Assert.Matches("^[A-Z0-9]+$", value);
    }
}
```

Build Core tests and run the new class:

```powershell
dotnet build src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-restore -m:1
dotnet run --project src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-build --no-restore -- -class SeedBomb.Core.Tests.TrackingCodeFieldGeneratorTests
```

Expect two passing cases. To verify that this test guards the wiring, temporarily remove the special string switch case and confirm the test fails, then restore it and rerun. Continue with the broader checks below. The example snippets are source-grounded instructions; this documentation change does not install or compile the example generator.

## 4. Verify the callers

```powershell
dotnet build src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-restore -m:1
dotnet run --project src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-build --no-restore
dotnet build src/SeedBomb.Bulk.Tests/SeedBomb.Bulk.Tests.csproj --no-restore -m:1
dotnet run --project src/SeedBomb.Bulk.Tests/SeedBomb.Bulk.Tests.csproj --no-build --no-restore
dotnet build tests/SeedBomb.Integration.Tests/SeedBomb.Integration.Tests.csproj --no-restore -m:1
dotnet run --project tests/SeedBomb.Integration.Tests/SeedBomb.Integration.Tests.csproj --no-build --no-restore
```

Restore the affected projects first if their assets are missing. Check the generated payload through a behavior-focused pipeline test when the change affects SDK value types or write behavior; [BulkCreatorPipelineTests](../../../tests/SeedBomb.Integration.Tests/BulkCreatorPipelineTests.cs) shows how existing tests capture requests.

For a change visible in the app, build and test WPF as well, then inspect the relevant preview and perform a small run in a non-production environment. Record this separately from automated test results: mocked requests cannot prove that the Dataverse server accepts the new values.
