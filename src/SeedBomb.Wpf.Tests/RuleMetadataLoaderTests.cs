using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using SeedBomb.Core.Exceptions;
using SeedBomb.ViewModels;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class RuleMetadataLoaderTests
{
    [Fact]
    public void DescribeFailure_UnwrapsTheDataverseFaultInsideASchemaException()
    {
        // WR-010: DataverseMetadataProvider wraps every failure in a SchemaException, so the
        // FaultException<OrganizationServiceFault> branch could never match `ex` itself — only
        // ex.InnerException, which the switch never inspected.
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147220969, Message = "Attribute validation error" },
            "Validation failed");
        var schema = new SchemaException("Failed to retrieve metadata for entity 'account'.", fault);

        Assert.Equal(
            "Dataverse error -2147220969: Attribute validation error",
            RuleMetadataLoader.DescribeFailure(schema));
    }

    [Fact]
    public void DescribeFailure_FallsBackToTheSchemaMessage_WhenThereIsNoFault()
    {
        var schema = new SchemaException(
            "Failed to retrieve metadata for entity 'account': Network unreachable.",
            new TimeoutException("Network unreachable."));

        Assert.Equal(
            "Failed to retrieve metadata for entity 'account': Network unreachable.",
            RuleMetadataLoader.DescribeFailure(schema));
    }
}