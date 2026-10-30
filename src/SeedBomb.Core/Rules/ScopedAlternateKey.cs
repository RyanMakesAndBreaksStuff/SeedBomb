using SeedBomb.Core.Metadata;
using Microsoft.Xrm.Sdk.Metadata;
using System.Buffers.Binary;

namespace SeedBomb.Core.Rules;

/// <summary>
/// CR-002: alternate-key values scoped to the run so re-runs never collide, and a function of the
/// row index so a retry regenerates the key a rejected row was first given. The column checks need
/// only metadata and the planned count, so Bulk runs them before the first write and Review runs
/// them before Start.
/// </summary>
public static class ScopedAlternateKey
{
    /// <summary>Eight scope hex characters, a separator and an eight-digit row index must survive truncation.</summary>
    public const int MinimumStringLength = 17;

    // Integer keys: width of one run's block, and how many blocks a run can pick from. Independent
    // of GenerationLimits.MaxRecordCount so raising that limit cannot move existing keys.
    private const long IntegerStride = 100_000;
    private const long IntegerRunOffsets = 21_000;

    /// <summary>The key value for row <paramref name="recordIndex"/> of a run.</summary>
    /// <param name="attr">Alternate-key column.</param>
    /// <param name="entityName">Entity logical name, the string key's prefix.</param>
    /// <param name="recordIndex">Row index within the table.</param>
    /// <param name="recordCount">Planned row count for the table.</param>
    /// <param name="scope">Hash of the run's key scope; at least four bytes.</param>
    public static object Generate(
        AttributeMetadata attr, string entityName, int recordIndex, int recordCount, byte[] scope)
    {
        if (attr is IntegerAttributeMetadata intAttr)
            return GenerateInteger(intAttr, recordIndex, recordCount, BinaryPrimitives.ReadUInt32BigEndian(scope));

        var key = $"{entityName}-{Convert.ToHexStringLower(scope.AsSpan(0, 4))}-{recordIndex:D8}";
        var max = (attr as StringAttributeMetadata)?.MaxLength ?? 100;
        return key.Length > max ? key[^max..] : key;
    }

    /// <summary>One message per alternate-key column on <paramref name="meta"/> that cannot hold scoped keys; empty when the table can be created.</summary>
    /// <param name="table">Entity logical name used in the messages.</param>
    /// <param name="meta">Live entity metadata.</param>
    /// <param name="recordCount">Planned row count for the table.</param>
    public static IReadOnlyList<string> FindUnsupported(string table, EntityMetadata meta, int recordCount)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var messages = new List<string>();
        foreach (var attr in KeyColumns(meta))
        {
            if (attr is StringAttributeMetadata s && (s.MaxLength ?? 100) < MinimumStringLength)
                messages.Add(
                    $"Entity '{table}': alternate-key column '{attr.LogicalName}' allows {s.MaxLength} characters, but generated keys need at least {MinimumStringLength} to keep the run scope and row index. Widen the column or seed this table another way.");
            else if (attr is IntegerAttributeMetadata i && !IntegerFits(i, recordCount))
                messages.Add(
                    $"Entity '{table}': alternate-key column '{attr.LogicalName}' allows {i.MinValue}..{i.MaxValue}, which cannot hold {recordCount} distinct values. Lower the record count or widen the column's range.");
        }

        return messages;
    }

    // Matches Bulk's alternate-key selection for the string and integer columns checked here: the
    // edge-case rules that precede the alternate-key rule never claim those types once
    // FieldFilter has passed them.
    private static IEnumerable<AttributeMetadata> KeyColumns(EntityMetadata meta) =>
        (meta.Attributes ?? []).Where(a =>
            a.LogicalName is not null
            && meta.Keys?.Any(k => k.KeyAttributes?.Contains(a.LogicalName) == true) == true
            && FieldFilter.ShouldGenerateField(a));

    // Rows of one run occupy a contiguous block inside the column's [MinValue, MaxValue]. A wide range
    // is cut into IntegerStride-sized blocks, up to IntegerRunOffsets of them, and the scope picks
    // one; the default int range therefore yields the same keys as before range support. A range
    // narrower than two blocks slides a recordCount-sized block to a scope-chosen start instead.
    // ponytail: two runs share a block with probability 1/blocks (1-in-21,000 on the default range);
    // narrow ranges collide more often. Use string keys if that bites.
    private static int GenerateInteger(IntegerAttributeMetadata attr, int recordIndex, int recordCount, uint scope)
    {
        var (start, span) = IntegerRange(attr);
        var blocks = Math.Min(span / IntegerStride, IntegerRunOffsets);
        var offset = blocks >= 2
            ? scope % blocks * IntegerStride
            : scope % (span - recordCount + 1);
        return (int)(start + offset + recordIndex);
    }

    // Starts at zero when the range allows it, so keys stay non-negative and match the pre-range scheme.
    private static (long Start, long Span) IntegerRange(IntegerAttributeMetadata attr)
    {
        long min = attr.MinValue ?? int.MinValue;
        long max = attr.MaxValue ?? int.MaxValue;
        var start = min <= 0 && max >= 0 ? 0 : min;
        return (start, max - start + 1);
    }

    // The block GenerateInteger picks must hold every row index.
    private static bool IntegerFits(IntegerAttributeMetadata attr, int recordCount)
    {
        var (_, span) = IntegerRange(attr);
        return span / IntegerStride >= 2 ? recordCount <= IntegerStride : recordCount <= span;
    }
}
