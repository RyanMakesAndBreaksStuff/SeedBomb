using DataGen.Core.Exceptions;
using DataGen.Core.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using Seedbomb.Services.Dataverse;
using Seedbomb.ViewModels.Controls;
using System.ServiceModel;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class LookupRecordPickerViewModelTests
{
    [Fact]
    public void Picker_selection_is_detached_from_the_authored_rule()
    {
        var original = new[] { new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111")) };
        using var vm = new LookupRecordPickerViewModel(Mock.Of<ILookupRecordSource>(),
            NullLogger<LookupRecordPickerViewModel>.Instance);
        vm.Initialize(new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] },
            original, single: true, CancellationToken.None);
        vm.Selected.Clear();
        Assert.Single(original);
        Assert.False(vm.CanAccept);
    }

    [Fact]
    public void Constant_requires_exactly_one_selected_record()
    {
        using var vm = Create(out _);
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);
        Assert.False(vm.CanAccept);
        vm.Selected.Add(new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111")));
        Assert.True(vm.CanAccept);
        vm.Selected.Add(new LookupRuleValue("account", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        Assert.False(vm.CanAccept);
    }

    [Fact]
    public void OneOf_requires_at_least_two_selected_records()
    {
        using var vm = Create(out _);
        vm.Initialize(Lookup(),
            [new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"))],
            single: false, CancellationToken.None);
        Assert.False(vm.CanAccept);
        vm.Selected.Add(new LookupRuleValue("account", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        Assert.True(vm.CanAccept);
    }

    [Fact]
    public async Task Typing_invalidates_results_but_does_not_search()
    {
        using var vm = Create(out var source);
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);
        var search = vm.SearchCommand.ExecuteAsync(null);
        Assert.Single(source.Requests);

        vm.SearchText = "Acme";

        Assert.Single(source.Requests);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.ErrorMessage);

        source.Completions[0].SetResult(Page([Row("account", Guid.NewGuid(), "Acme")]));
        await search;

        Assert.Empty(vm.Results);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.ErrorMessage);
    }

    [Fact]
    public async Task Late_success_of_superseded_search_does_not_mutate_state()
    {
        using var vm = Create(out var source);
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);
        var first = vm.SearchCommand.ExecuteAsync(null);
        var second = vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, source.Requests.Count);

        var newer = Page([Row("account", Guid.Parse("22222222-2222-2222-2222-222222222222"), "New")]);
        source.Completions[1].SetResult(newer);
        await second;

        Assert.Equal("New", Assert.Single(vm.Results).Value.Name);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.ErrorMessage);
        var results = vm.Results.ToArray();
        var error = vm.ErrorMessage;
        var busy = vm.IsBusy;

        source.Completions[0].SetResult(Page([Row("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Old")]));
        await first;

        Assert.Equal(results, vm.Results);
        Assert.Equal(error, vm.ErrorMessage);
        Assert.Equal(busy, vm.IsBusy);
        Assert.Equal("New", Assert.Single(vm.Results).Value.Name);
    }

    [Fact]
    public async Task Late_fault_of_superseded_search_does_not_mutate_state()
    {
        using var vm = Create(out var source);
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);
        var first = vm.SearchCommand.ExecuteAsync(null);
        var second = vm.SearchCommand.ExecuteAsync(null);

        source.Completions[1].SetResult(Page([Row("account", Guid.Parse("22222222-2222-2222-2222-222222222222"), "New")]));
        await second;

        var results = vm.Results.ToArray();
        var error = vm.ErrorMessage;
        var busy = vm.IsBusy;
        Assert.False(busy);
        Assert.Equal("", error);

        source.Completions[0].SetException(new InvalidOperationException("stale search exploded"));
        await first;

        Assert.Equal(results, vm.Results);
        Assert.Equal(error, vm.ErrorMessage);
        Assert.Equal(busy, vm.IsBusy);
        Assert.False(vm.HasError);
        Assert.Equal("New", Assert.Single(vm.Results).Value.Name);
    }

    [Fact]
    public async Task Target_change_ignores_late_success_and_fault()
    {
        using var vm = Create(out var source);
        vm.Initialize(Lookup("account", "contact"), [], single: true, CancellationToken.None);
        var first = vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("account", source.Requests[0].Target);

        vm.SelectedTarget = "contact";
        Assert.Empty(vm.Results);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.ErrorMessage);
        Assert.Single(source.Requests);

        var results = vm.Results.ToArray();
        var error = vm.ErrorMessage;
        var busy = vm.IsBusy;
        source.Completions[0].SetResult(Page([Row("account", Guid.NewGuid(), "Old")]));
        await first;
        Assert.Equal(results, vm.Results);
        Assert.Equal(error, vm.ErrorMessage);
        Assert.Equal(busy, vm.IsBusy);

        var second = vm.SearchCommand.ExecuteAsync(null);
        vm.SelectedTarget = "account";
        source.Completions[1].SetException(new InvalidOperationException("old target fault"));
        await second;
        Assert.Empty(vm.Results);
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
        Assert.Equal(2, source.Requests.Count);
    }

    [Fact]
    public async Task Text_change_ignores_late_success()
    {
        using var vm = Create(out var source);
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);
        var first = vm.SearchCommand.ExecuteAsync(null);
        vm.SearchText = "Acme";
        var results = vm.Results.ToArray();
        var error = vm.ErrorMessage;
        var busy = vm.IsBusy;
        source.Completions[0].SetResult(Page([Row("account", Guid.NewGuid(), "Old")]));
        await first;
        Assert.Equal(results, vm.Results);
        Assert.Equal(error, vm.ErrorMessage);
        Assert.Equal(busy, vm.IsBusy);
        Assert.Single(source.Requests);
    }

    [Fact]
    public async Task Dismissal_ignores_late_success_and_fault()
    {
        var source = new ScriptedLookupSource();
        var vm = Create(source);
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);
        var success = vm.SearchCommand.ExecuteAsync(null);
        var snapshotResults = vm.Results.ToArray();
        var snapshotError = vm.ErrorMessage;
        var snapshotBusy = vm.IsBusy;
        vm.Dispose();
        Assert.False(vm.CanAccept);

        source.Completions[0].SetResult(Page([Row("account", Guid.NewGuid(), "Late")]));
        await success;
        Assert.Equal(snapshotResults, vm.Results);
        Assert.Equal(snapshotError, vm.ErrorMessage);
        Assert.Equal(snapshotBusy, vm.IsBusy);

        var vmFault = Create(source);
        vmFault.Initialize(Lookup(), [], single: true, CancellationToken.None);
        var fault = vmFault.SearchCommand.ExecuteAsync(null);
        snapshotResults = vmFault.Results.ToArray();
        snapshotError = vmFault.ErrorMessage;
        snapshotBusy = vmFault.IsBusy;
        vmFault.Dispose();
        source.Completions[1].SetException(new InvalidOperationException("late fault"));
        await fault;
        Assert.Equal(snapshotResults, vmFault.Results);
        Assert.Equal(snapshotError, vmFault.ErrorMessage);
        Assert.Equal(snapshotBusy, vmFault.IsBusy);
        Assert.False(vmFault.HasError);
    }

    [Fact]
    public async Task Fake_ignoring_cancellation_still_drops_superseded_results()
    {
        using var vm = Create(out var source);
        source.IgnoreCancellation = true;
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);
        var first = vm.SearchCommand.ExecuteAsync(null);
        vm.SearchText = "next";
        var second = vm.SearchCommand.ExecuteAsync(null);
        Assert.False(source.Completions[0].Task.IsCompleted);

        source.Completions[1].SetResult(Page([Row("account", Guid.Parse("22222222-2222-2222-2222-222222222222"), "New")]));
        await second;
        var results = vm.Results.ToArray();
        var error = vm.ErrorMessage;
        var busy = vm.IsBusy;

        source.Completions[0].SetResult(Page([Row("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Old")]));
        await first;

        Assert.Equal(results, vm.Results);
        Assert.Equal(error, vm.ErrorMessage);
        Assert.Equal(busy, vm.IsBusy);
        Assert.Equal("New", Assert.Single(vm.Results).Value.Name);
    }

    [Fact]
    public async Task Multiple_open_close_cycles_do_not_leak_selection_or_results()
    {
        var source = new ScriptedLookupSource();
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        using (var vm = Create(source))
        {
            vm.Initialize(Lookup(), [new LookupRuleValue("account", firstId, "A")],
                single: true, CancellationToken.None);
            var search = vm.SearchCommand.ExecuteAsync(null);
            source.Completions[0].SetResult(Page([Row("account", firstId, "A")]));
            await search;
            Assert.Equal(firstId, Assert.Single(vm.Selected).Id);
            Assert.Equal("A", Assert.Single(vm.Results).Value.Name);
        }

        using (var vm = Create(source))
        {
            vm.Initialize(Lookup(), [], single: false, CancellationToken.None);
            Assert.Empty(vm.Selected);
            Assert.Empty(vm.Results);
            Assert.False(vm.CanAccept);
            var search = vm.SearchCommand.ExecuteAsync(null);
            source.Completions[1].SetResult(Page([Row("account", secondId, "B")]));
            await search;
            Assert.Equal("B", Assert.Single(vm.Results).Value.Name);
            Assert.Empty(vm.Selected);
        }
    }

    [Fact]
    public async Task Selection_survives_paging_and_is_not_accepted_by_add_highlighted()
    {
        using var vm = Create(out var source);
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        vm.Initialize(Lookup(), [], single: false, CancellationToken.None);
        var search = vm.SearchCommand.ExecuteAsync(null);
        var page1 = Row("account", id, "Acme");
        source.Completions[0].SetResult(Page([page1], more: true, cookie: "cookie-1"));
        await search;

        vm.SelectedResult = page1;
        vm.AddHighlightedCommand.Execute(null);
        Assert.Equal(id, Assert.Single(vm.Selected).Id);
        Assert.False(vm.CanAccept);
        Assert.NotSame(page1.Value, vm.Selected[0]);

        var next = vm.NextPageCommand.ExecuteAsync(null);
        source.Completions[1].SetResult(Page(
            [Row("account", Guid.Parse("22222222-2222-2222-2222-222222222222"), "Beta")],
            more: false, cookie: "cookie-2"));
        await next;

        Assert.Equal(id, Assert.Single(vm.Selected).Id);
        Assert.Equal("Beta", Assert.Single(vm.Results).Value.Name);
        Assert.Null(vm.SelectedResult);
        Assert.False(vm.CanAccept);
        Assert.Equal(2, source.Requests[1].PageNumber);
        Assert.Equal("cookie-1", source.Requests[1].PagingCookie);
    }

    [Fact]
    public async Task AddHighlighted_rejects_busy_error_empty_id_and_duplicates()
    {
        using var vm = Create(out var source);
        vm.Initialize(Lookup("account", "contact"), [], single: true, CancellationToken.None);
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var row = Row("account", id, "Acme");
        var search = vm.SearchCommand.ExecuteAsync(null);
        source.Completions[0].SetResult(Page([row]));
        await search;

        var busySearch = vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        vm.SelectedResult = row;
        vm.AddHighlightedCommand.Execute(null);
        Assert.Empty(vm.Selected);
        source.Completions[1].SetResult(Page([row]));
        await busySearch;

        vm.SelectedResult = row;
        vm.AddHighlightedCommand.Execute(null);
        Assert.Equal(id, Assert.Single(vm.Selected).Id);
        vm.AddHighlightedCommand.Execute(null);
        Assert.Single(vm.Selected);

        var empty = Row("account", Guid.Empty, "Nope");
        vm.Results.Add(empty);
        vm.SelectedResult = empty;
        vm.AddHighlightedCommand.Execute(null);
        Assert.Single(vm.Selected);

        var retry = vm.SearchCommand.ExecuteAsync(null);
        source.Completions[2].SetException(new InvalidOperationException("No connection profile configured."));
        await retry;
        Assert.True(vm.HasError);
        Assert.Contains("profile", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        vm.SelectedResult = row;
        var before = vm.Selected.Count;
        vm.AddHighlightedCommand.Execute(null);
        Assert.Equal(before, vm.Selected.Count);
    }

    [Fact]
    public void Reinitialize_throws_and_dispose_blocks_accept()
    {
        using var vm = Create(out _);
        vm.Initialize(Lookup(),
            [new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"))],
            single: true, CancellationToken.None);
        Assert.True(vm.CanAccept);
        Assert.Throws<InvalidOperationException>(() =>
            vm.Initialize(Lookup(), [], single: true, CancellationToken.None));
        vm.Dispose();
        Assert.False(vm.CanAccept);
        vm.Dispose();
    }

    [Fact]
    public async Task DescribeFailure_covers_schema_fault_and_retry_exhaustion()
    {
        using var vm = Create(out var source);
        vm.Initialize(Lookup(), [], single: true, CancellationToken.None);

        var schema = vm.SearchCommand.ExecuteAsync(null);
        source.Completions[0].SetException(new SchemaException("missing entity"));
        await schema;
        Assert.Equal("Lookup metadata could not be loaded. Reconnect and retry Search.", vm.ErrorMessage);

        var wrapped = vm.SearchCommand.ExecuteAsync(null);
        source.Completions[1].SetException(new DataGenerationException("retry",
            new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = -2147220969, Message = "Attribute validation error" },
                "Validation failed")));
        await wrapped;
        Assert.Equal("Dataverse error -2147220969: Attribute validation error", vm.ErrorMessage);

        var exhausted = vm.SearchCommand.ExecuteAsync(null);
        source.Completions[2].SetException(new DataGenerationException("retries exhausted"));
        await exhausted;
        Assert.Equal("The lookup read failed after retries. Retry Search or reconnect.", vm.ErrorMessage);
    }

    private static LookupRecordPickerViewModel Create(out ScriptedLookupSource source)
    {
        source = new ScriptedLookupSource();
        return Create(source);
    }

    private static LookupRecordPickerViewModel Create(ScriptedLookupSource source) =>
        new(source, NullLogger<LookupRecordPickerViewModel>.Instance);

    private static LookupAttributeMetadata Lookup(params string[] targets) =>
        new() { LogicalName = "parentaccountid", Targets = targets.Length == 0 ? ["account"] : targets };

    private static LookupRecord Row(string entity, Guid id, string? name = null) =>
        new(new LookupRuleValue(entity, id, name), "2020-01-01 00:00", "—");

    private static LookupRecordPage Page(IReadOnlyList<LookupRecord> records, bool more = false, string? cookie = null) =>
        new(records, more, cookie);

    private sealed class ScriptedLookupSource : ILookupRecordSource
    {
        public List<LookupSearchRequest> Requests { get; } = [];
        public List<TaskCompletionSource<LookupRecordPage>> Completions { get; } = [];
        public bool IgnoreCancellation { get; set; } = true;

        public Task<LookupRecordPage> ReadAsync(LookupSearchRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var tcs = new TaskCompletionSource<LookupRecordPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Completions.Add(tcs);
            if (!IgnoreCancellation)
                ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }
    }
}
