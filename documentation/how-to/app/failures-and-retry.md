# Investigate failures and retry

Use this guide when a run has rejected rows, stops with an error, or needs a retry after a transient failure.

## Identify what happened

1. Open **Run summary** and read the outcome message and **Written** and **Rejected** counts.
2. Inspect **Rejected rows**, which groups errors by table and cause.
3. Use the table filter or **Retryable only** to narrow the visible causes.
4. Choose **View full log** for the available activity log.

A failed or cancelled run may have written records already. Nothing is rolled back. If the outcome says the run stopped after writing rows and their lookups or links were not completed, check both the created rows and the incomplete relationships in Dataverse.

| Disposition | Action |
| --- | --- |
| **Retryable** | Consider retrying the selected rejected rows after a transient throttling or network problem. |
| **Needs a fix** | Investigate the rule, data, permissions, or server plug-in before starting another run. |
| Linking error without retryable rows | Inspect the incomplete relationships; the rejected-row retry cannot replay these links. |

The app recognizes transient errors and service-protection faults, including errors containing `429`. A cause is eligible for row retry only when the app also has the rejected generation-row indexes. Duplicate-key, validation, and plug-in errors ordinarily require a fix.

## Export the causes

1. In **Run summary**, choose **Export CSV**.
2. Read the **Rejections exported** notification for the destination.
3. Open `%USERPROFILE%\Downloads\seedbomb-rejected-YYYYMMDD-HHMMSS.csv`.

The CSV contains `Table`, `Cause`, `Rows`, `Disposition`, and `Retryable`. It exports all cause groups in the current summary, including groups hidden by the table or retryable filter. It contains grouped error information, not record payloads, Dataverse IDs, or a file that can be imported to retry a run.

If **Export failed** appears, read the error and check access to the destination before exporting again. For an unexpected generation error, the app points to the full diagnostic logs, normally under `%LOCALAPPDATA%\SeedBomb\logs`. Follow the displayed path if a migration warning says the app is still using its legacy DataGen folder.

## Retry eligible rows

Retry from the live run summary while its configuration is still held in the running app.

1. Keep, or return to, the same signed-in connection used for the original run.
2. Inspect every **Retryable** group. These groups are selected by default; clear the checkbox for any group you do not want to retry.
3. Check the selected-row total in the footer and the **Retry N selected** button.
4. Choose **Retry N selected** and monitor the new run.
5. Inspect the new summary and, when needed, confirm the resulting rows and relationships in Dataverse.

Filtering only changes visibility. A selected group hidden by a filter remains selected for retry; review the selection across all tables before clicking the button.

Retry regenerates the selected rejected generation-row indexes using the original table counts, rules, seed, and run configuration. It does not rewrite the successful rows or use edits you subsequently made to the Generate board. Lookups to parent tables that are not retried can draw from current environment records, so do not assume every relationship will exactly match the original attempt.

The button is unavailable while a run is active, when no eligible rows are selected, when the original configuration is unavailable, or when the active connection differs from the original one. The app checks the connection again before starting a retry and prevents connection changes from retargeting an in-flight run.

Older History summaries do not retain rejected-row indexes or the configuration needed for retry. Restarting the app also loses the live retry state. Use [History](history.md) for saved totals and activity, not as a queue of resumable runs.

## Resolve errors that need a fix

Read the cause and its hint, correct the affected rule or environment problem, then return to Generate and perform a new review before starting a new run. A new full run can create additional rows alongside rows already written; review the existing data and choose appropriate counts and unique values first.
