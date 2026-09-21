using Bogus;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Maps Dataverse attribute metadata types to their corresponding field generators
/// and dispatches generation requests.
/// </summary>
public class GeneratorFactory
{
    private readonly ILogger<GeneratorFactory> _logger;
    private readonly StringFieldGenerator _string = new();
    private readonly MoneyFieldGenerator _money = new();
    private readonly PicklistFieldGenerator _picklist = new();
    private readonly LookupFieldGenerator _lookup = new();
    private readonly DateTimeFieldGenerator _dateTime = new();
    private readonly MultiSelectPicklistFieldGenerator _multiSelectPicklist = new();
    private readonly BooleanFieldGenerator _boolean = new();
    private readonly IntegerFieldGenerator _integer = new();
    private readonly DecimalFieldGenerator _decimal = new();
    private readonly DoubleFieldGenerator _double = new();
    private readonly MemoFieldGenerator _memo = new();
    private readonly UniqueIdentifierFieldGenerator _uniqueIdentifier = new();
    private readonly BigIntFieldGenerator _bigInt = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GeneratorFactory"/> class
    /// with the default set of field generators.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public GeneratorFactory(ILogger<GeneratorFactory> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates a fake value for the given attribute using the appropriate generator.
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <param name="faker">The seeded Faker instance.</param>
    /// <param name="pool">The record pool for lookup resolution.</param>
    /// <returns>The generated value, or null if no generator is registered.</returns>
    public object? Generate(AttributeMetadata attr, Faker faker, DataverseRecordPool pool)
    {
        ArgumentNullException.ThrowIfNull(attr);
        ArgumentNullException.ThrowIfNull(faker);
        ArgumentNullException.ThrowIfNull(pool);

        // Subtypes (MemoAttributeMetadata : StringAttributeMetadata,
        // MultiSelectPicklistAttributeMetadata : PicklistAttributeMetadata) are matched
        // before their base types — pattern order below is significant.
        IFieldGenerator? generator = attr switch
        {
            MemoAttributeMetadata => _memo,
            StringAttributeMetadata => _string,
            MoneyAttributeMetadata => _money,
            MultiSelectPicklistAttributeMetadata => _multiSelectPicklist,
            PicklistAttributeMetadata => _picklist,
            LookupAttributeMetadata => _lookup,
            DateTimeAttributeMetadata => _dateTime,
            BooleanAttributeMetadata => _boolean,
            IntegerAttributeMetadata => _integer,
            DecimalAttributeMetadata => _decimal,
            DoubleAttributeMetadata => _double,
            UniqueIdentifierAttributeMetadata => _uniqueIdentifier,
            BigIntAttributeMetadata => _bigInt,
            _ => null
        };

        if (generator is null)
        {
            _logger.LogDebug("No generator registered for {AttributeType}, skipping {FieldName}",
                attr.GetType().Name, attr.LogicalName);
            return null;
        }

        _logger.LogDebug("Generating value for {FieldName} using {GeneratorType}",
            attr.LogicalName, generator.GetType().Name);
        return generator.Generate(attr, faker, pool);
    }
}
