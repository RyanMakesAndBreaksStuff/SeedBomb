# Save and reuse generation profiles

A generation profile stores selected tables, per-table row counts, field rules, and an optional seed. Use it to repeat a test configuration or transfer that configuration to another machine or environment.

Generation profiles are separate from **connection profiles**: they contain no connection credentials. Batch size and parallelism are not part of a generation profile.

## Before you start

- Prepare the tables, counts, and rules you want to save in **Generate**.
- Connect to the intended environment before loading or importing a profile for use; the app checks its tables and rules against live metadata.
- For export, choose a folder you can write to. For import, use a SeedBomb profile JSON file.

## Save the current configuration

1. Set your selected tables and row counts in Generate. Save any rule edits in the Rules page first.
2. Open **Profiles** from the navigation or **Change profile** in Generate.
3. Choose **New profile**. This captures the current Generate configuration; prepare that configuration before choosing the button.
4. Enter a lower-case hyphenated name, such as `account-demo`, and confirm the name dialog. The name `draft` is reserved for autosave.
5. Check the new entry in the list. Select it and inspect **Rules**, **Tables & volume**, and the displayed seed.

You can also use **Save profile as** on the Rules page to save the current rule configuration under a name. **Save profile** on an unnamed working set routes to the same name prompt.

The Generate draft is kept automatically so you can leave and return. It is separate from named profiles and does not appear as a profile-library entry.

## Load a saved profile

1. Open **Profiles**, use **Search profiles** if needed, and select an entry.
2. Inspect its rules and tables, then choose **Load Profile**.
3. If the Generate board has unsaved changes, respond to the overwrite prompt. Keeping the board leaves its current configuration in place.
4. Read the validation summary. It reports applied tables and rules, warnings, and content that could not be imported into this environment.
5. Choose **Use in Generate** to apply the validated configuration and open Generate.
6. Check selected tables, counts, rules, and seed, then complete Review before starting.

If no profile table is available in the current environment, **Use in Generate** is disabled. Loading a profile during an active run queues the board change until that run finishes.

## Duplicate or change a profile name

To make a variant, select a profile, open its **More** menu, choose **Duplicate**, and enter a new valid name. Select the copy and choose **Edit rules** to change it.

There is no separate Rename command in this version. To retain the configuration under a different name:

1. Select the original and choose **Duplicate** from its More menu.
2. Supply the new name in the name dialog.
3. Verify the new entry in Profiles.
4. If you no longer need the old entry, select it, choose **Delete** from its More menu, and confirm.

Duplicate creates a copy; it does not remove the original. Deleting a generation profile removes its saved configuration, not records already created in Dataverse.

## Export a profile

1. Select the saved profile in **Profiles**.
2. Open its More menu and choose **Export…**.
3. Choose the destination file and save.
4. Check the export success message.

The exported JSON contains the generation configuration. Lookup rules can include record GUIDs and table identities; those identities must be valid in the environment where the profile is later used.

## Import a profile file

1. Connect to the environment where you intend to use the configuration.
2. Open **Profiles** and choose **Import…**.
3. Select the profile JSON file. If a profile with the same name exists, confirm overwrite only when you want to replace that saved configuration.
4. Read the import result. Import has two stages:

   - **File/schema validation:** rejects malformed JSON, unsupported versions or operations, unknown properties, invalid counts, and other structural errors. Files are limited to 1 MiB. Valid version 1 profiles are upgraded to version 2; Bogus rules require version 2.
   - **Live metadata validation:** checks available tables and columns, whether columns are settable, and whether rules fit their metadata. Valid rules are applied; range adjustments and other warnings are listed; unknown or invalid rules are listed under **Not imported**.

5. Resolve any warnings or omissions you care about. Choose **Use in Generate** to apply the validated result to the board, then inspect it in Generate and Rules.

A structurally valid import is saved in the profile library **before** live metadata validation. **Cancel** in the validation summary dismisses the pending board result; it does not delete the imported library file. If you kept a dirty board instead of replacing it, the imported file can still be selected from Profiles later.

Warnings do not all mean values were clamped: some rules remain as authored and need confirmation at Start. Rules listed as Not imported do not become explicit board rules; affected columns may fall back to automatic generation. Inspect them before running, particularly when the profile came from another environment.

Loading or importing does not establish that a manually entered lookup GUID exists in the target environment. Review existing-record rules when moving profiles across environments.

## Expected result and recovery

You have a named configuration in Profiles, or its metadata-validated configuration on the Generate board. Saving, loading, and importing a profile do not start record generation.

| Problem | Action |
| --- | --- |
| Name is rejected | Use lower-case letters/digits separated by hyphens, and avoid `draft`; choose a distinct name if another profile occupies the same file name. |
| Schema validation fails | Use a valid SeedBomb export from a compatible app version; read the displayed reason. The app has no JSON/schema editor. |
| Tables or rules are Not imported | Confirm the active environment, then edit the configuration for the tables and columns it actually contains. |
| A saved entry cannot load | Read the status message and check the file/storage problem; do not assume it reached Generate. |
| Export or save fails | Choose a writable destination or resolve the local storage problem, then retry. Rules-page save failures have a separate **Retry save** button. |

[Customize field values and lookups](field-rules.md) · [Back to documentation](../../index.md)
