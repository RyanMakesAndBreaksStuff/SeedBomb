using Bogus;
using SeedBomb.Core.Generators;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SeedBomb.Core.Rules;

/// <summary>One Faker per generation or preparation worker. Never shared across concurrent workers.</summary>
public sealed class BogusEvaluatorSession : IDisposable
{
    private readonly Faker _faker;

    /// <summary>Creates a worker-scoped session for the given Bogus locale.</summary>
    /// <param name="locale">v1 accepts only <c>en</c>.</param>
    public BogusEvaluatorSession(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (!string.Equals(locale, DeterministicFaker.DefaultLocale, StringComparison.Ordinal))
            throw new ArgumentException("v1 accepts only locale 'en'.", nameof(locale));
        _faker = new Faker(locale);
    }

    /// <summary>Evaluates one prepared rule for one row into an SDK payload.</summary>
    /// <param name="rule">Compiled rule from <see cref="BogusRulePreparer.CompileRule"/>.</param>
    /// <param name="attr">Live attribute metadata.</param>
    /// <param name="context">Evaluation context. Table participates in seed framing.</param>
    /// <param name="rowIndex">Zero-based row. Negative values are rejected.</param>
    public object Evaluate(PreparedBogusRule rule, AttributeMetadata attr,
                           RuleEvaluationContext context, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(attr);
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);
        rule.EnsureMatches(context);

        _faker.Random = new Randomizer(DeriveSeed(context.Seed, rule, context.Table, rowIndex));
        return Coerce(rule.Descriptor.Invoke(rule.CreateInvocationContext(_faker)), attr, rule);
    }

    /// <summary>Engine v1: HMAC key is the 4-byte BE seed; message is length-prefixed and unambiguous.</summary>
    private static int DeriveSeed(int seed, PreparedBogusRule rule, string table, int rowIndex)
    {
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, seed);

        var message = new ArrayBufferWriter<byte>();
        WriteLengthPrefixed(message, "SeedBomb.Bogus/v1");
        WriteLengthPrefixed(message, table);
        WriteLengthPrefixed(message, rule.CanonicalColumn);
        WriteLengthPrefixed(message, rule.Descriptor.Id.Api);
        WriteLengthPrefixed(message, rule.Descriptor.Id.Endpoint);
        WriteInt32BE(message, rowIndex);

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(key, message.WrittenSpan, hash);
        return BinaryPrimitives.ReadInt32BigEndian(hash);
    }

    private static void WriteLengthPrefixed(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var span = writer.GetSpan(4 + byteCount);
        BinaryPrimitives.WriteInt32BigEndian(span, byteCount);
        Encoding.UTF8.GetBytes(value, span[4..]);
        writer.Advance(4 + byteCount);
    }

    private static void WriteInt32BE(ArrayBufferWriter<byte> writer, int value)
    {
        var span = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        writer.Advance(4);
    }

    internal static object Coerce(object raw, AttributeMetadata attr, PreparedBogusRule rule)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw switch
        {
            string text => CoerceText(text, attr),
            bool flag => flag,
            DateOnly dateOnly => ApplyDateBehavior(
                dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), attr),
            DateTime dateTime => ApplyDateBehavior(dateTime, attr),
            _ => CoerceNumber(ToDecimal(raw), attr, rule),
        };
    }

    private static string CoerceText(string text, AttributeMetadata attr)
    {
        EnsureTransportSafe(text);
        var maxLen = attr switch
        {
            StringAttributeMetadata s => s.MaxLength,
            MemoAttributeMetadata m => m.MaxLength,
            _ => null,
        };
        if (maxLen is { } n && text.Length > n)
            throw new InvalidOperationException($"Generated text exceeds MaxLength {n} for '{attr.LogicalName}'.");
        return text;
    }

    private static object CoerceNumber(decimal n, AttributeMetadata attr, PreparedBogusRule rule)
    {
        _ = rule;
        return attr switch
        {
            MoneyAttributeMetadata m => new Money(Math.Round(n, m.Precision ?? 2)),
            IntegerAttributeMetadata => checked((int)decimal.Round(n, 0, MidpointRounding.AwayFromZero)),
            BigIntAttributeMetadata => checked((long)decimal.Round(n, 0, MidpointRounding.AwayFromZero)),
            DoubleAttributeMetadata => (double)n,
            DecimalAttributeMetadata d => Math.Round(n, d.Precision ?? 2),
            _ => n,
        };
    }

    private static decimal ToDecimal(object raw) => raw switch
    {
        decimal d => d,
        byte b => b,
        sbyte sb => sb,
        short s => s,
        ushort us => us,
        int i => i,
        uint ui => ui,
        long l => l,
        ulong ul => ul,
        float f => (decimal)f,
        double d => (decimal)d,
        _ => throw new InvalidOperationException($"Unexpected Bogus result type '{raw.GetType()}'."),
    };

    private static DateTime ApplyDateBehavior(DateTime value, AttributeMetadata attr)
    {
        if (value > DateTimeAttributeMetadata.MaxSupportedValue)
            value = DateTimeAttributeMetadata.MaxSupportedValue;
        if (value < DateTimeAttributeMetadata.MinSupportedValue)
            value = DateTimeAttributeMetadata.MinSupportedValue;

        var behavior = (attr as DateTimeAttributeMetadata)?.DateTimeBehavior?.Value;
        if (behavior is "DateOnly" or "TimeZoneIndependent")
        {
            if (behavior == "DateOnly")
                value = value.Date;
            return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>Well-formed UTF-16 and XML 1.0 character check used by text coercion.</summary>
    internal static bool IsTransportSafe(string value)
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

            if (c is '\0' or < '\t' || (c > '\r' && c < ' ' && c != '\n') || c >= '\uFFFE')
                return false;
            if (c is '\v' or '\f')
                return false;
        }

        return true;
    }

    internal static void EnsureTransportSafe(string value)
    {
        if (!IsTransportSafe(value))
            throw new InvalidOperationException(
                "Generated text is not well-formed UTF-16 or contains XML-invalid characters.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
