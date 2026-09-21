using DataGen.Bulk;
using DataGen.Core.Exceptions;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Seedbomb.Services.Dataverse;
using System.ServiceModel;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class LookupRecordSourceTests
{
    [Fact]
    public async Task Search_uses_metadata_primary_id_and_explicit_paging()
    {
        var lookup = new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] };
        var meta = new EntityMetadata { LogicalName = "account" };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(meta, "accountid");
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryNameAttribute))!.SetValue(meta, "name");
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.Attributes))!.SetValue(meta,
            new AttributeMetadata[] { Readable(new StringAttributeMetadata { LogicalName = "name" }) });
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(m => m.GetEntityAsync("account", It.IsAny<CancellationToken>())).ReturnsAsync(meta);
        QueryExpression? captured = null;
        var service = new Mock<IOrganizationServiceAsync2>();
        service.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => captured = Assert.IsType<QueryExpression>(q))
            .ReturnsAsync(new EntityCollection());
        var connection = new Mock<IDataverseConnectionService>();
        connection.Setup(c => c.GetOrganizationServiceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(service.Object);
        var source = new LookupRecordSource(connection.Object, metadata.Object,
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance));
        await source.ReadAsync(new LookupSearchRequest(lookup, "account", "Acme"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Null(captured.TopCount);
        Assert.False(captured.ColumnSet.AllColumns);
        Assert.Equal(100, captured.PageInfo.Count);
        Assert.Equal(1, captured.PageInfo.PageNumber);
        Assert.Equal("accountid", Assert.Single(captured.Orders).AttributeName);
        Assert.Contains("accountid", captured.ColumnSet.Columns);
        Assert.DoesNotContain("createdby", captured.ColumnSet.Columns);
        Assert.Equal(ConditionOperator.Like, Assert.Single(captured.Criteria.Conditions).Operator);
        Assert.Equal("Acme%", captured.Criteria.Conditions[0].Values[0]);
    }

    [Fact]
    public async Task Search_uses_guid_equality_not_name_like()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var (source, service, _) = CreateSource();
        QueryExpression? captured = null;
        service.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => captured = Assert.IsType<QueryExpression>(q))
            .ReturnsAsync(new EntityCollection());

        await source.ReadAsync(new LookupSearchRequest(Lookup(), "account", id.ToString("D")),
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured.ColumnSet.AllColumns);
        Assert.Null(captured.TopCount);
        var condition = Assert.Single(captured.Criteria.Conditions);
        Assert.Equal("accountid", condition.AttributeName);
        Assert.Equal(ConditionOperator.Equal, condition.Operator);
        Assert.Equal(id, Assert.Single(condition.Values));
    }

    [Fact]
    public async Task Search_escapes_like_wildcards_as_literals()
    {
        var (source, service, _) = CreateSource();
        QueryExpression? captured = null;
        service.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => captured = Assert.IsType<QueryExpression>(q))
            .ReturnsAsync(new EntityCollection());

        await source.ReadAsync(new LookupSearchRequest(Lookup(), "account", @"A%c_me[x"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured.ColumnSet.AllColumns);
        var condition = Assert.Single(captured.Criteria.Conditions);
        Assert.Equal("name", condition.AttributeName);
        Assert.Equal(ConditionOperator.Like, condition.Operator);
        Assert.Equal(@"A[%]c[_]me[[]x%", Assert.Single(condition.Values));
    }

    [Fact]
    public async Task Search_preserves_page_two_cookie()
    {
        var (source, service, _) = CreateSource();
        QueryExpression? captured = null;
        service.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => captured = Assert.IsType<QueryExpression>(q))
            .ReturnsAsync(new EntityCollection());

        await source.ReadAsync(new LookupSearchRequest(Lookup(), "account", "Acme", 2, "cookie-from-page-1"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured.ColumnSet.AllColumns);
        Assert.Null(captured.TopCount);
        Assert.Equal(100, captured.PageInfo.Count);
        Assert.Equal(2, captured.PageInfo.PageNumber);
        Assert.Equal("cookie-from-page-1", captured.PageInfo.PagingCookie);
    }

    [Fact]
    public async Task Search_rejects_missing_primary_name_metadata()
    {
        var (source, service, metadata) = CreateSource(AccountMetadata(primaryName: null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", "Acme"),
                TestContext.Current.CancellationToken));

        Assert.Contains("primary name", ex.Message, StringComparison.OrdinalIgnoreCase);
        service.Verify(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
        metadata.Verify(m => m.GetEntityAsync("account", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Search_projects_null_optional_values_as_em_dash()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var page = new EntityCollection([new Entity("account", id)])
        {
            MoreRecords = true,
            PagingCookie = "next-page",
        };
        var (source, service, _) = CreateSource(AccountMetadata(includeCreated: true), page);
        QueryExpression? captured = null;
        service.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => captured = Assert.IsType<QueryExpression>(q))
            .ReturnsAsync(page);

        var result = await source.ReadAsync(new LookupSearchRequest(Lookup(), "account", ""),
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured.ColumnSet.AllColumns);
        Assert.Contains("createdon", captured.ColumnSet.Columns);
        Assert.Contains("createdby", captured.ColumnSet.Columns);
        Assert.Empty(captured.Criteria.Conditions);
        var row = Assert.Single(result.Records);
        Assert.Equal("account", row.Value.Entity);
        Assert.Equal(id, row.Value.Id);
        Assert.Null(row.Value.Name);
        Assert.Equal("—", row.CreatedOn);
        Assert.Equal("—", row.CreatedBy);
        Assert.Equal($"account · {id:D}", row.Label);
        Assert.True(result.MoreRecords);
        Assert.Equal("next-page", result.PagingCookie);
    }

    [Fact]
    public async Task Search_rejects_wrong_logical_name()
    {
        var page = new EntityCollection([new Entity("contact", Guid.NewGuid())]);
        var (source, _, _) = CreateSource(page: page);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", ""),
                TestContext.Current.CancellationToken));

        Assert.Contains("outside the requested target", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_rejects_empty_id()
    {
        var page = new EntityCollection([new Entity("account")]);
        var (source, _, _) = CreateSource(page: page);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", ""),
                TestContext.Current.CancellationToken));

        Assert.Contains("outside the requested target", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_rejects_empty_guid_text()
    {
        var (source, service, _) = CreateSource();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", Guid.Empty.ToString("D")),
                TestContext.Current.CancellationToken));

        Assert.Contains("non-empty record GUID", ex.Message, StringComparison.Ordinal);
        service.Verify(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_propagates_no_profile_invalid_operation()
    {
        var metadata = new Mock<IMetadataProvider>();
        var service = new Mock<IOrganizationServiceAsync2>();
        var connection = new Mock<IDataverseConnectionService>();
        connection.Setup(c => c.GetOrganizationServiceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No connection profile configured."));
        var source = new LookupRecordSource(connection.Object, metadata.Object,
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", "Acme"),
                TestContext.Current.CancellationToken));

        Assert.Contains("profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        metadata.Verify(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_wraps_dataverse_fault()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147220969, Message = "Attribute validation error" },
            "Validation failed");
        var (source, _, _) = CreateSource(retrieveError: fault);

        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", "Acme"),
                TestContext.Current.CancellationToken));

        Assert.Contains("account", ex.Message, StringComparison.Ordinal);
        Assert.IsType<FaultException<OrganizationServiceFault>>(ex.InnerException);
    }

    [Fact]
    public async Task Search_propagates_cancellation()
    {
        var (source, service, metadata) = CreateSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", "Acme"), cts.Token));

        metadata.Verify(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_rejects_missing_cookie_on_forward_page()
    {
        var (source, service, metadata) = CreateSource();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup(), "account", "Acme", 2),
                TestContext.Current.CancellationToken));

        Assert.Contains("paging cookie", ex.Message, StringComparison.OrdinalIgnoreCase);
        metadata.Verify(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.ExecuteAsync(It.IsAny<Microsoft.Xrm.Sdk.OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_makes_zero_sdk_calls_when_target_is_not_allowed()
    {
        var metadata = new Mock<IMetadataProvider>();
        var service = new Mock<IOrganizationServiceAsync2>();
        var connection = new Mock<IDataverseConnectionService>();
        var source = new LookupRecordSource(connection.Object, metadata.Object,
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ReadAsync(new LookupSearchRequest(Lookup("account"), "contact", "Acme"),
                TestContext.Current.CancellationToken));

        Assert.Contains("allowed target", ex.Message, StringComparison.OrdinalIgnoreCase);
        connection.Verify(c => c.GetOrganizationServiceAsync(It.IsAny<CancellationToken>()), Times.Never);
        metadata.Verify(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static LookupAttributeMetadata Lookup(params string[] targets) =>
        new() { LogicalName = "parentaccountid", Targets = targets.Length == 0 ? ["account"] : targets };

    private static T Readable<T>(T attribute) where T : AttributeMetadata
    {
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.IsValidForRead))!.SetValue(attribute, true);
        return attribute;
    }

    private static EntityMetadata AccountMetadata(string? primaryName = "name", bool includeCreated = false)
    {
        var meta = new EntityMetadata { LogicalName = "account" };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(meta, "accountid");
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryNameAttribute))!.SetValue(meta, primaryName);
        var attributes = new List<AttributeMetadata>();
        if (primaryName is not null)
            attributes.Add(Readable(new StringAttributeMetadata { LogicalName = primaryName }));
        if (includeCreated)
        {
            attributes.Add(Readable(new DateTimeAttributeMetadata { LogicalName = "createdon" }));
            attributes.Add(Readable(new LookupAttributeMetadata { LogicalName = "createdby" }));
        }
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.Attributes))!
            .SetValue(meta, attributes.ToArray());
        return meta;
    }

    private static (LookupRecordSource Source, Mock<IOrganizationServiceAsync2> Service, Mock<IMetadataProvider> Metadata)
        CreateSource(EntityMetadata? meta = null, EntityCollection? page = null, Exception? retrieveError = null)
    {
        meta ??= AccountMetadata();
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(m => m.GetEntityAsync(meta.LogicalName, It.IsAny<CancellationToken>())).ReturnsAsync(meta);
        var service = new Mock<IOrganizationServiceAsync2>();
        var retrieve = service.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()));
        if (retrieveError is not null)
            retrieve.ThrowsAsync(retrieveError);
        else
            retrieve.ReturnsAsync(page ?? new EntityCollection());
        var connection = new Mock<IDataverseConnectionService>();
        connection.Setup(c => c.GetOrganizationServiceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(service.Object);
        var source = new LookupRecordSource(connection.Object, metadata.Object,
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance));
        return (source, service, metadata);
    }
}
