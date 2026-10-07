# SeedBomb tutorials and how-to guides

SeedBomb creates synthetic records in Microsoft Dataverse through a Windows desktop app.

## Start here

| Your goal | Start with |
| --- | --- |
| Learn to generate records in the app | [Generate your first test records](tutorials/first-generation.md) |
| Build and run the source | [Build and test SeedBomb locally](tutorials/build-and-test.md) |

The app tutorial assumes an existing non-production Dataverse environment and an identity permitted to read its metadata and create the chosen records. The developer tutorial uses Windows, the .NET 10 SDK, and PowerShell 7.

## App-user tasks

- [Connect to a Dataverse environment](how-to/app/connections.md)
- [Choose tables and record counts](how-to/app/tables-and-counts.md)
- [Customize field values and lookups](how-to/app/field-rules.md)
- [Save and reuse generation profiles](how-to/app/generation-profiles.md)
- [Review and run a generation](how-to/app/review-and-run.md)
- [Investigate failures and retry](how-to/app/failures-and-retry.md)
- [Review previous runs](how-to/app/history.md)

A **connection profile** stores environment and authentication details. A **generation profile** stores the table selection, counts, seed, and field rules used to prepare a run.

Records are written during generation. Cancelling a run leaves records already committed in Dataverse. Clearing local History does not delete those records.

## Developer tasks

- [Run focused tests](how-to/developer/focused-tests.md)
- [Add or change a field generator](how-to/developer/field-generator.md)
- [Diagnose local application failures](how-to/developer/diagnostics.md)
- [Publish the Windows executable](how-to/developer/publish.md)

## Documentation baseline

These guides describe v2., inspected on 2026-10-05. UI instructions are checked against XAML and application code. Source inspection and mocked tests do not establish your environment's permissions or guarantee that a Dataverse write will succeed.

See the [documentation verification record](VERIFICATION.md) for checks performed when these guides were written.

