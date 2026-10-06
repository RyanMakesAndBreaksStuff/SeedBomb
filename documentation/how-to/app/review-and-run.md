# Review and run a generation

Use this guide when you have selected tables and configured their row counts and rules, and want to check the configuration before writing records.

## Before you start

- Connect to the intended Dataverse environment with an account allowed to create the selected records and populate their relationships.
- Use an environment where you can safely create synthetic data.
- Check the table selection, row counts, seed, locale, batch size, and parallelism in **Generate**.

Review and preview do not write records. **Start run** starts a real generation; it is not a dry run.

## Review the rules

1. In **Generate**, complete **Select tables** and **Volume & rules**, then choose **Next** to reach **Review**.
2. Read the validation messages and sample values. The preview samples the first five values for configured rules; it does not preview every field of every complete record.
3. Resolve errors before proceeding. Use **Back** to adjust the configuration, or return to the Rules page to edit the affected column, then review again.
4. For a missing required lookup, supply a suitable lookup rule or select its target table with a positive row count when that is appropriate for your scenario.
5. Choose **Next** to reach **Ready to run**.

An error blocks progression. If a rule changes after review, the app requires a current review before starting. A valid preview still cannot guarantee successful Dataverse writes: server permissions, duplicate keys, plug-ins, and current environment data can affect the result.

The engine performs further validation after starting and before its first write, including run limits, configured rules, required lookups, and some alternate-key constraints. Follow any resulting error message rather than assuming that passing Review completes every check.

## Start and monitor the run

1. On **Ready to run**, check the confirmation and configuration values against your intended run.
2. Choose **Start run**.
3. If **Allow risky generated values** appears, read it before choosing **Allow** or **Cancel**. Some Bogus endpoints can produce routable, financial, or external values. Cancelling this prompt prevents the run from starting.
4. Watch the run sheet for rows written, per-table progress, elapsed time, remaining time, throughput, and recent activity.
5. Choose **Show all** to open the available activity log if you need more context.
6. Wait for the final outcome. **Populating Relationships** can continue after record creation reaches its planned count: deferred lookup updates and links are still being written.

The run sheet blocks interaction with the underlying Generate page while the run proceeds. Treat progress percentages as progress indicators; the final written count comes from the completed result.

## Cancel a run

1. Choose **Cancel run** on the run sheet.
2. Read the confirmation, then choose **Cancel run** to stop further writes or **Keep running** to continue.
3. Wait for cancellation to settle and inspect the outcome.

Cancellation does not roll back rows already committed. Those rows remain in Dataverse, and relationship work may be incomplete. Inspect the environment before starting another full run; that would create another set of records rather than resume the cancelled run.

## Check the result

When the run finishes, the app normally opens **Run summary**. If **Settings → Keep the run sheet open** is enabled, choose **Close** to dismiss the completed sheet, then open **History** and select the latest run to inspect its summary.

Check the **Written**, **Rejected**, **Tables**, and **Throughput** statistics, the outcome message, and any rejected-row causes. Confirm the created records in Dataverse when you need to verify their actual values and relationships.

For rejected rows or a stopped run, follow [Investigate failures and retry](failures-and-retry.md). To find the saved run later, follow [Review previous runs](history.md).
