# Generate your first test records

In this lesson, you will connect SeedBomb to a test environment, create ten records in one table, and find the run in History.

## Before you start

Have:

- The SeedBomb Windows app, or a local build from the [developer tutorial](build-and-test.md).
- The HTTPS URL of a non-production Dataverse environment.
- An account that can read metadata and create records in that environment.
- A simple existing custom table with a primary name column and no required lookups or other required columns needing special input.

For this lesson, call that table **Training item**. Use your table's actual display name when searching. If you do not have a suitable table, ask your environment administrator to provide one. Using a simple table keeps the exercise focused on SeedBomb's workflow.

Open the table in a model-driven app or another Dataverse record viewer before beginning. Note its existing records so you can compare the result afterward.

## 1. Add and connect a connection

1. Open SeedBomb. On first use, choose **Add connection**. If connections already exist, open **Connections**, then choose **Add connection**.
2. Enter a **Display name**, such as `Training environment`.
3. Enter the **Environment URL**, such as `https://contoso-dev.crm.dynamics.com`, replacing the example with your real environment URL.
4. Set **Auth type** to **OAuth**.
5. Keep the prefilled **Application (client) ID** for this exercise, or choose **Use default** to restore it. If your organization requires its own public client application, use the ID your administrator provides.
6. Leave **Tenant ID** blank for the common OAuth endpoint, or enter the tenant your administrator specifies.
7. Choose **Save**, then **Connect**.
8. Complete browser sign-in if prompted.

The connection should be marked **Active**. Saving stores the settings; connecting establishes the active session. A successful sign-in does not itself prove permission to create records.

If sign-in fails, use [Connect to a Dataverse environment](../how-to/app/connections.md) before continuing.

## 2. Select one table

1. Open **Generate**. If a previous selection or profile draft appears, choose **Reset** and confirm that you want to discard it before starting this exercise.
2. In **Select tables**, enter your table's name in **Search tables**.
3. Select its checkbox. Check the logical name shown beside the display name to avoid choosing a similarly named table.
4. Confirm the table appears in the **Selected** list.
5. Choose **Next**.

SeedBomb loads metadata for the selection. You should reach **Step 2 of 4 · Volume & rules**. If metadata cannot load, resolve the connection or access error before continuing.

## 3. Set a small volume

1. Under **Rows per table**, set your table's count to `10`.
2. Leave **Seed** at `42`.
3. Leave **Batch size** and **Parallelism** at their initial values.
4. Keep the default field behavior for this first run; do not add custom rules.
5. Choose **Next**.

The locale is displayed as `en`; the current app does not offer a locale picker. A seed helps repeat generation choices, but dates, record IDs, and environment-dependent values can vary.

## 4. Inspect the review

You should reach **Step 3 of 4 · Review**.

1. Read any review messages.
2. Inspect any sample values shown.
3. If an error blocks progress, choose **Back** and correct the affected selection or rule. For a table requiring special input, switch to the simple table described in the prerequisites.
4. When there are no blocking errors, choose **Next**.

An empty sample list does not mean that the selected table will receive no records; samples depend on the rules being reviewed. Review prepares the configuration without writing records. It cannot predict every server-side validation, plug-in, or permission failure.

## 5. Create the records

At **Step 4 of 4 · Run**:

1. Check **Ready to run** and the planned total. It should show ten rows for your one selected table.
2. Confirm the active connection points to your test environment.
3. Choose **Start run**.
4. Wait for generation to finish and inspect the run summary.

The expected outcome is ten created records and no rejected rows. If the result differs, inspect the actual created count and errors with [Investigate failures and retry](../how-to/app/failures-and-retry.md). Do not start another ten-row run as a recovery step without checking what the first run already created.

If you cancel, records already written remain in Dataverse.

## 6. Confirm your result

1. Refresh the table's record view in your Dataverse app.
2. Find the newly created rows. Compare their creation time and primary name values with your run.
3. Return to SeedBomb and open **History**.
4. Find the entry for this table and environment, then open its summary.
5. Compare the recorded outcome with the summary you saw after generation.

You have completed the app's four-step workflow and located its saved run record. Keep the test records for another exercise, or remove them through your normal Dataverse administration process after identifying them. History is a local record of activity, not a record-deletion tool.

## Continue learning

Use [Customize field values and lookups](../how-to/app/field-rules.md) to control generated values, then [Save and reuse generation profiles](../how-to/app/generation-profiles.md) to retain your configuration.

Implementation checked: [Generate page](../../src/SeedBomb.Wpf/Views/Pages/GeneratePage.xaml), [wizard behavior](../../src/SeedBomb.Wpf/ViewModels/GenerateViewModel.cs), and [connection editor](../../src/SeedBomb.Wpf/ViewModels/ConnectionManagerViewModel.cs).
