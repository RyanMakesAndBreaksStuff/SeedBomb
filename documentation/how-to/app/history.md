# Review previous runs

Use **History** to find saved run outcomes, compare their totals, and export a local record of what was written to which environment.

## Find a run

1. Open **History** from the app navigation, or choose **Open in History** on **Run summary**.
2. Review runs grouped by day, with the newest days and runs first.
3. Enter a table name, environment, user, or generation-profile name in **Search runs**.
4. Select a run to open its summary.

Search matches any of those four fields without regard to letter case. It does not search timestamps, status, counts, or the saved activity log. Clear the search text to restore the full list.

Each row shows time, tables, environment, user, generation profile, records written, duration, and status. The profile column describes the generation configuration, not a saved connection profile.

| Status | Meaning |
| --- | --- |
| **Success** | The stored result completed without cancellation, a fatal error, or recorded errors. |
| **Rejected** | The stored run has rejected rows. A cancelled or stopped run with rejected rows also uses this status. |
| **Failed** | The stored run was unsuccessful and has no rejected-row count. |

History status is a compact summary; inspect the saved activity and actual Dataverse data when you need to distinguish a partial run from a completed run with rejections.

## Inspect the saved summary

An older run opens a historical summary with saved totals and activity. Historical entries do not preserve the full configuration, individual rejection groups, rejected-row indexes, or created record IDs. They cannot reproduce the original run or retry its rejected rows.

Selecting the current live run opens its live summary instead, while that run remains available in the app. See [Investigate failures and retry](failures-and-retry.md) for retry restrictions.

History is normally stored locally for the Windows user in `%LOCALAPPDATA%\SeedBomb\history.json`. If a migration warning says the app still uses its legacy DataGen folder, the history remains there until migration succeeds. It is not a list queried from Dataverse. A cancellation before any records were written may have no entry. If **Couldn't save run history** appears after generation, the history save failed independently of the generation outcome; inspect the run summary and the environment directly.

If the saved history file cannot be parsed, the app keeps the unreadable file aside, starts with an empty history, and reports the retained file's location. An empty list after that warning does not mean the prior Dataverse records were deleted.

## Export history

1. In **History**, choose **Export CSV**.
2. Read the **History exported** notification for the destination.
3. Open `%USERPROFILE%\Downloads\seedbomb-history-YYYYMMDD-HHMMSS.csv`.

The export contains all loaded history entries, even when **Search runs** filters the visible list. Columns are `Timestamp`, `Entities`, `TotalRecords`, `Duration`, `Status`, `Errors`, `Environment`, `User`, and `Profile`. Table names are joined with `|` in the `Entities` column. Activity logs and individual error details are not included.

If **Export failed** appears, check the displayed error. The History exporter writes to the Downloads folder under your user profile; that folder must exist and be writable.

## Clear local history

1. Export any history you want to retain.
2. Choose **Clear All**.
3. Read the **Clear history** confirmation and choose **Clear all** only if you want to remove every saved run entry.

Clear All removes the entire local history, including entries hidden by the current search. This cannot be undone through the app. It does not delete generated records, remove relationships, or undo any Dataverse writes. Remove unwanted Dataverse data separately using your environment's normal tools and procedures.

If clearing fails, the app displays **Couldn't clear history**; resolve the reported storage problem before trying again.
