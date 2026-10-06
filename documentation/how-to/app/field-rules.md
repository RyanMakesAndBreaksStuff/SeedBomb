# Customize field values and lookups

Use field rules when automatic values do not suit your test: give every row a fixed value, generate recognizable names, constrain numbers, or link to existing records.

## Before you start

- Connect to the intended Dataverse environment.
- Select tables and set their counts in Generate, or select a saved generation profile in Profiles.
- For existing-record lookups, have permission to read the target records and permission to create records with the relationship.

## Add or change a rule

1. Choose **Edit rules** from Generate's Select tables step or from the selected profile in **Profiles**. On Generate's Volume & rules step, the rules link beside **Rows per table** opens the same editor.
2. Wait for table metadata to load. Select the table in the header switcher.
3. Find the column using **Filter columns** and the **Mapped**, **Required**, or **All** filters. Select a settable column; platform-owned columns cannot have rules.
4. Select an **Operation** and fill in its inputs. Only operations supported for that column appear.
5. Read validation messages and inspect **Preview**. Fix errors before saving. Warnings can describe adjusted values or values that require confirmation at Start run.
6. Choose **Save** in Preview to commit the selected column's rule without leaving the page. Do this before moving to another column.
7. Repeat for other columns. Choose **Save profile** to save the selected rule and return when the page was opened from Generate. For a new working set, this asks for a profile name; **Save profile as** also creates a named copy.

Use a lower-case name with hyphens, such as `account-demo`, when asked for a profile name.

Edits to an existing stored profile are written back when saved. A rule saved from an unnamed Generate working set updates that working set; save it under a name to retain it as a reusable profile.

## Choose values for your task

These examples assume the column's metadata permits the values and length shown.

| Goal | Operation and example |
| --- | --- |
| Give every row the same text | **constant** → Value: `Training account` |
| Alternate between two text values | **one-of** → Values: `North,South`; choose `Cycle` |
| Pick from a subset of choices | **one-of** → check at least two available options; choose `Random` or `Cycle` |
| Generate a bounded numeric value | **range** → Min: `10`; Max: `50` |
| Increment a whole-number value | **sequence** → Start: `100`; Step: `1` |
| Generate recognizable text | **pattern** → Template: `DEMO-{seq:0000}`; first values are `DEMO-0001`, `DEMO-0002`, `DEMO-0003` |
| Use a supported fake-data generator | **bogus** → choose an **API**, then an **Endpoint**, and fill any displayed bounds or length inputs |
| Leave an optional column unset | **null** → omit the value and let the platform default apply |

Use a decimal point for numeric input, for example `12.5`. One-of input for non-choice scalar columns is comma-separated; it is not a CSV editor for text containing commas.

Patterns support `{seq}`, zero-padded sequence tokens such as `{seq:0000}`, `{random:N}` such as `{random:6}`, and `{runId}`. Unknown tokens and patterns whose maximum expanded length exceeds the column limit are rejected. Numeric sequence validation uses the planned row count, so recheck it after increasing the volume.

Float columns do not offer sequence. Date and choice columns offer a smaller operation set than text and numeric columns. Null is invalid for required columns. The Bogus API and Endpoint lists are filtered for the selected column's value type.

**Reroll** changes the preview sample; it does not change the saved run seed. Preview helps inspect values and validation, but does not guarantee that Dataverse permissions, business rules, or plug-ins will accept a write.

## Link to specific existing records

1. Select a lookup column and choose **constant** for one existing record or **one-of** for a list of at least two.
2. Choose **Choose records…**.
3. Select **Target table** if the lookup allows multiple table types.
4. Search by a name prefix or a full record GUID, then choose **Search**.
5. Highlight a result and choose **Select highlighted record**. For one-of, repeat until at least two records appear under **Selected records**; use **Next page** if available.
6. Choose **Add** to bring the selection into the rule editor. **Cancel** leaves the editor's previous selection intact.
7. For one-of, choose `Random` or `Cycle`. Inspect the selected identities, then choose **Save**.

You can also select the target table, paste record GUIDs in the editor, and choose **Add GUIDs**. Use **Remove** for an individual identity or **Clear selected** to clear the list. Manual GUIDs are checked for format and target type, **not for existence**; confirm that they identify records in this environment.

## Pick randomly from existing records

1. Select the lookup column.
2. Choose **random (existing records)**.
3. Read the explanation and save the rule.
4. Choose **Start run** after completing Review. The app captures up to **1,000 existing records per allowed target table** and checks candidate availability before writes.

This operation prefers the captured existing records. If there are none, it can use records created earlier in the same run from selected target tables. If neither source is available, preparation fails before writes; if a planned earlier target subsequently creates no usable rows, generation cannot fill that lookup. Its preview resolves when the run starts. The same seed and the same candidate identities produce the same picks; changed existing data or newly created record identities can change the result.

## Remove a rule or table

- To return a mapped column to automatic handling, open the column's menu, choose **Delete rule**, and confirm. This differs from a null rule, which explicitly leaves the value unset.
- To remove a table and its rules from the profile, select it, choose **Remove table**, and confirm. The last table cannot be removed because a profile requires at least one table.

## Expected result and recovery

The saved column is shown as mapped, and Generate uses the committed rules in Review and the next run.

| Problem | Action |
| --- | --- |
| Save is disabled | Wait for metadata, select a settable column, choose an operation, and resolve its errors. |
| Metadata fails or the connection changes | Reconnect and choose **Retry** to reload metadata. |
| A profile write fails | Resolve the storage problem and choose **Retry save**; metadata Retry does not repeat a failed save. |
| Lookup search fails | Read the error, retry **Search**, or reconnect. Check target-table read access. |
| A value is rejected | Check the column type, length, bounds, required status, or available choice values; revise the rule. |

[Save and reuse generation profiles](generation-profiles.md) · [Back to documentation](../../index.md)
