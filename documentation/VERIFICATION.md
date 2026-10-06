# Documentation verification record

Baseline: local `master`, commit `b2ac13be1cc170d099e857c2a3876dddf6fd058a`, inspected on 2026-10-05.

## Evidence used

| Documentation area | Local evidence |
| --- | --- |
| Connections and first sign-in | [Connections page](../src/SeedBomb.Wpf/Views/Pages/ConnectionsPage.xaml), [connection commands](../src/SeedBomb.Wpf/ViewModels/ConnectionManagerViewModel.cs), [connection validation](../src/SeedBomb.Wpf/Services/Connections/ConnectionProfile.cs), [authentication](../src/SeedBomb.Wpf/Services/Auth/ProfileAuthService.cs), [certificate loading](../src/SeedBomb.Wpf/Services/Auth/CertificateLoader.cs) |
| Generation wizard and review | [Generate page](../src/SeedBomb.Wpf/Views/Pages/GeneratePage.xaml), [wizard behavior](../src/SeedBomb.Wpf/ViewModels/GenerateViewModel.cs), [wizard tests](../src/SeedBomb.Wpf.Tests/GenerateViewModelStepTests.cs) |
| Run results, cancellation, and retry | [run behavior](../src/SeedBomb.Wpf/ViewModels/RunViewModel.cs), [run tests](../src/SeedBomb.Wpf.Tests/RunViewModelTests.cs), [run sheet](../src/SeedBomb.Wpf/Views/Controls/RunSheet.xaml) |
| History and CSV export | [History behavior](../src/SeedBomb.Wpf/ViewModels/HistoryViewModel.cs), [stored run shape](../src/SeedBomb.Wpf/Services/History/RunRecord.cs), [History tests](../src/SeedBomb.Wpf.Tests/HistoryViewModelTests.cs) |
| Profile and rule editing | [profile commands](../src/SeedBomb.Wpf/ViewModels/ProfilesViewModel.cs), [rule editor](../src/SeedBomb.Wpf/ViewModels/RuleEditorViewModel.cs), [profile persistence contract](../src/SeedBomb.Wpf/Services/Profiles/IProfileService.cs) |
| Build prerequisites | [shared build properties](../Directory.Build.props), [WPF project](../src/SeedBomb.Wpf/SeedBomb.Wpf.csproj), executable test project configuration |

## Checks performed

- Confirmed HEAD matches local `master`; the working tree had only the documentation outline from the preceding approval step.
- Confirmed local SDK `10.0.401` and PowerShell `7.6.6` using `dotnet --version` and `pwsh --version`.
- Checked UI labels, action availability, persistence, exports, and recovery instructions against the current source and XAML.
- Executed the documented Core class filter: `StringFieldGeneratorTests`, 18 passing tests.
- Executed the documented Core method filter: `Generate_RespectsMaxLength`, one passing test.
- Inspected executable runner help to confirm `-class` and `-method` syntax; the help-only invocation returned its expected nonzero status.
- Executed `pwsh -NoProfile -File tools/Generate-ThirdPartyNotices.ps1 -ValidateResources`: 47 notice components validated.
- Checked all 16 documentation Markdown files: 90 local links resolved, every file had a title, and code fences were balanced.
- Independent source review found no actionable factual issues in the tutorials and guides, including generator wiring and retry restrictions.
- Ran `git diff --check` successfully. Git reported only the repository's usual README LF-to-CRLF conversion warning.

## Limits of verification

The app tutorials and recipes have not been exercised against a live Dataverse environment for this documentation change. Browser sign-in, tenant-specific permissions, record creation, server plug-ins, and the visual interaction sequence remain environment-dependent. Test-source inspection is evidence of intended contracts; it is not a claim that those tests were executed during this change.

The full solution restore/build, complete test suites, application launch, Release publish, and release workflow were not run for this documentation change. Their instructions were checked against current configuration, scripts, and source. The worked tracking-code generator is example code; it was not installed or compiled in this checkout.

The existing README contains older test counts and a reference to an absent `global.json`. The new guides use current project configuration and actual runner results rather than carrying those statements forward. The README change is limited to adding the documentation entry point.
