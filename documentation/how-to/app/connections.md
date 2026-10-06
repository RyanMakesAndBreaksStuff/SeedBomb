# Connect to a Dataverse environment

Use this guide to add, test, activate, edit, or remove a saved connection.

## Before you start

Have an HTTPS environment URL and an identity permitted to use that environment. For app-only authentication, your administrator must already have prepared the Entra application and its Dataverse application user and access. This guide covers entering those details in SeedBomb.

Finish or cancel an active run before changing its connection. Save, connect, switch, and delete actions are restricted while the run owns the session.

## Add an interactive OAuth connection

1. Open **Connections** and choose **Add connection**.
2. Enter a **Display name** and **Environment URL**.
3. Select **OAuth** under **Auth type**.
4. Keep the prefilled **Application (client) ID**, or select **Use default**. A supplied custom public client ID must be a GUID.
5. Leave **Tenant ID** blank to use the common endpoint, or use your organization's tenant.
6. Optionally choose **Test connection** and complete sign-in if prompted.
7. Choose **Save**.
8. Choose **Connect**, then complete any browser sign-in.
9. Check that the saved connection is marked **Active** before opening **Generate**.

**Test connection** tests authentication using the editor values without replacing the active session. **Save** persists the configuration. **Connect** activates the saved configuration. Neither successful authentication nor a connection test proves table-level create access.

Keep an actual client ID in the field: although the page's helper text mentions a blank default, the current save validator requires a GUID. **Use default** fills the required value.

## Add a client-secret connection

1. Choose **Add connection** and fill in **Display name** and **Environment URL**.
2. Select **ClientSecret** under **Auth type**.
3. Replace the default public client ID with your own app's **Application (client) ID**.
4. Enter its **Tenant ID** as a GUID.
5. Enter the secret's value in **Client secret**, rather than its identifier.
6. Choose **Test connection** if you want to check authentication before saving.
7. Choose **Save**, then **Connect**.
8. Check the **Active** marker.

When editing a saved client-secret connection, leave **Client secret** blank to retain the stored secret. Enter a new value to replace it. A blank field is not a request to erase a saved secret.

## Add a certificate connection

1. Install the application's certificate with its usable private key in the personal certificate store for the Windows user running SeedBomb: `CurrentUser\\My`.
2. Choose **Add connection** and enter a **Display name** and **Environment URL**.
3. Select **Certificate** under **Auth type**.
4. Enter your app's **Application (client) ID** and its **Tenant ID** as GUIDs.
5. Paste the **Certificate thumbprint**. Spacing and case are ignored.
6. Choose **Test connection**, then **Save** and **Connect**.
7. Check the **Active** marker.

SeedBomb looks in the current user's personal store, not the local machine store. The thumbprint identifies a locally installed certificate; it does not import one.

## Edit or switch a connection

1. Select the connection in the list to open its editor.
2. Make the changes you need.
3. Choose **Save**.
4. Choose **Connect** to activate the saved settings.
5. Confirm the **Active** marker and environment before preparing another run.

Selecting a list item opens the editor; use **Connect** to activate it. Choose **Cancel** to discard unsaved editor changes. Switching connections refreshes the app's metadata-dependent state, so review your configuration again in the new environment.

## Delete a connection

1. Select a saved connection.
2. Choose **Delete connection**.
3. Read the confirmation and choose **Delete** only if it is the connection you intend to remove.

The app removes saved connection credentials and clears its cached authentication for that connection. Deleting the active connection drops its session. Deleting the last saved connection returns the app to its first-use connection flow. This action does not delete Dataverse records or generation profiles.

## Resolve common problems

| Symptom | Action |
| --- | --- |
| **Save** is disabled | Enter a name, valid HTTPS URL, and GUID client ID; app-only methods also require a GUID tenant and a secret or thumbprint. Wait for an active run to finish. |
| **Connect** is absent | Save the new or edited configuration first. |
| Authentication succeeds but metadata or writes fail | Check the identity's permissions in the target environment and inspect the actual operation error. |
| Saved secret cannot be decrypted | Re-enter it while running as the Windows user who should own this connection, then save and connect. |
| Certificate not found | Install it in that user's personal store and confirm the thumbprint. |
| Certificate has no usable private key | Install or grant access to the matching private key, then test again. |
| Connection list is unexpectedly empty with a warning | Read the warning for the store recovery location; see [diagnostics](../developer/diagnostics.md) before replacing files. |

## Where settings are stored

Connections live in `%LOCALAPPDATA%\\SeedBomb\\connections.json`. Saved client secrets are protected with Windows DPAPI for the current user. Copying this file is not a portable credential migration method; recreate authentication details for the destination user or machine.

See [Generate your first test records](../../tutorials/first-generation.md) for a complete run after connecting.

Implementation checked: [Connections page](../../../src/SeedBomb.Wpf/Views/Pages/ConnectionsPage.xaml), [connection commands](../../../src/SeedBomb.Wpf/ViewModels/ConnectionManagerViewModel.cs), [authentication](../../../src/SeedBomb.Wpf/Services/Auth/ProfileAuthService.cs), and [certificate loading](../../../src/SeedBomb.Wpf/Services/Auth/CertificateLoader.cs).

