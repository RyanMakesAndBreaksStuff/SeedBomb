using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.ViewModels;
using Wpf.Ui;

namespace SeedBomb.Wpf.Tests;

/// <summary>Board and file-picker doubles for <see cref="ProfilesViewModel"/> tests (WR-001).</summary>
internal static class ProfilesHost
{
    public static Mock<IProfileBoard> Board(
        IReadOnlyDictionary<string, EntityMetadata>? metadata = null, string runId = "", bool dirty = false)
    {
        var board = new Mock<IProfileBoard>();
        board.SetupGet(b => b.EntityMetadataMap)
            .Returns(metadata ?? new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase));
        board.SetupGet(b => b.RunId).Returns(runId);
        board.Setup(b => b.IsBoardDirty()).Returns(dirty);
        return board;
    }

    // Parameter order mirrors ProfilesViewModel's optional tail so existing positional calls still bind.
    public static ProfilesViewModel Create(
        IProfileService profiles,
        RulesNavigationRequest? rulesRequest = null,
        IAppNavigator? navigator = null,
        IContentDialogService? dialogs = null,
        IProfileBoard? board = null) =>
        new(profiles, board ?? Board().Object, Mock.Of<IFileDialogService>(), rulesRequest, navigator, dialogs);
}
