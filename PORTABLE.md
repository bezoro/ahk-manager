# Portable application

Download `AHK-Manager-<version>-Portable-x64.zip` from the public release page. Extract the complete ZIP into a dedicated local folder. Open `Bezoro.AhkManager.exe` at its top level.

```text
AHK Manager/
  Bezoro.AhkManager.exe
  portable.json
  portable-package.json
  App/
  Data/
    Profile/
    Chromium/
    Updates/
```

`Data` is created on first launch. The root executable launches the application under `App`; shortcuts and Windows startup should target the root executable. Keep the package files together. Do not extract a new version over a running copy or mix application files from different versions.

## Windows integration

No application installation or uninstall registration is required. Opening the root launcher starts Manager and its notification-area icon. The root launcher requires administrator privileges before starting Manager. The native initializer also refuses an unelevated internal application before it prepares portable data. Windows may show UAC when you open Manager. Manager starts with Windows by default; this startup preference remains configurable. An enabled startup entry uses a current-user scheduled task with highest privileges. Older Manager administrator preferences are migrated to the required mode. Individual scripts keep their own administrator preferences and can still run with standard privileges.

When upgrading from version 1.6.2 or earlier, open that older Manager as administrator before applying the update. Its existing update helper must be elevated to reopen the new administrator-required application. Subsequent launches use the Windows elevation requirement automatically.

After obtaining writable ownership of its profile, Manager creates a current-user **AHK Manager Portable** shortcut in the Start Menu. It points to the root launcher, so Windows Search and PowerToys launchers that index Start Menu applications can discover it. Search results can take time to refresh, and the relevant PowerToys launcher must be enabled. Existing unrelated shortcuts are preserved.

After closing Manager normally, you can move the complete portable folder. Open the root launcher in its new location once: the application derives the relative `Data` paths again and repairs its owned Start Menu shortcut and configured Windows startup registration. An external absolute data location keeps its original meaning. External script paths are preserved; moving Manager does not move those scripts.

If a correct current startup task coexists with an older Manager entry in the Windows Run registry key, **Preferences > Startup** offers a review of the previous and current executable locations. Choose **Remove previous entry and apply saved startup preferences** only after checking the old location. This action is explicit consent; ordinary preference saves never silently adopt a conflicting registry entry. Save any preference edits before using the repair.

The repair requires a healthy current task with the Manager source marker, resolved account, exact launcher and arguments, interactive logon and expected privileges. It accepts only a standard previous Manager command for review. Its approval expires when the task, registry value or saved startup mode changes. Unknown programs, extra arguments and unproven tasks remain protected. The previous executable is never run.

Before removing the exact reviewed registry value, Manager saves a durable backup in `Data/Profile/startup-repairs`, or the selected profile location. It then verifies the state again and applies the saved startup preferences. A changed or unconfirmed registration is reported without overwriting a concurrent replacement. Backups are included in the complete diagnostic archive and are preserved automatically; new repairs stop if that folder has 32 entries. This validates configuration, not execution at a later Windows logon.

Owned tasks with missing or outdated working directories are repaired without treating them as foreign tasks. Equivalent path case and a trailing directory separator do not require rewriting the task. Its original working directory is retained for rollback if a registration change fails.

## Importing current settings

Before the first portable launch, close the current Manager with **Exit and keep scripts running**. Existing AHK processes can stay active. Manager imports a verified previous Electron profile from `%LOCALAPPDATA%/Bezoro/AhkManagerElectron` into `Data/Profile`. It preserves the source and a migration backup. Script files remain at their original paths.

Import requires exclusive ownership of Manager effects. Another active Manager, a corrupt source profile, or unresolved startup continuation prevents an unsafe import. An error never silently replaces the profile with empty settings. Explicit historical profile imports retain their separate preview and confirmation flow.

Only the public update channel preference is carried into the new update store. Credentials, cached packages and unfinished updates from the retired distribution are not imported. Portable version history remains in the selected data folder.

## Choosing a data folder

Open **Preferences > Data location** to see the active path. **Open folder** opens it in Explorer. **Change location** uses the Windows folder picker. Choose a new or empty local folder outside `App`.

Choosing a location prepares the change. The Manager keeps using the current data until you select **Restart Manager**. Save other preference edits before changing the location. The restart closes the backend normally, transfers the profile and update history, verifies the copy, then switches the data pointer. Your independent scripts keep running. Chromium storage is recreated at the new location.

The old data folder remains as a backup. **Cancel move** cancels a prepared change without deleting the source or recovery copies. If a move fails before the data pointer changes, the Manager can keep using a verified original profile and show the error in Preferences. It does not choose an empty replacement folder.

**Use default** selects `Data` beside the launcher. A nonempty old backup is not merged or overwritten. Keep that backup elsewhere before choosing the same path again.

The selected path is stored in `portable.json` beside the launcher. Updates preserve this file. A folder inside the portable root is stored as a relative path; a location outside it is an absolute path. If you later move the application, an external absolute data path continues to refer to its original location.

## Updates and rollback

Use **Updates** and select **Stable** or **Test** as usual. Manager downloads portable packages and replaces its application files directly. It verifies the package, waits for normal Manager shutdown, replaces the application files, and reopens the Manager. Your data path, scripts, and profile remain intact.

Before a confirmed update, the Manager preserves the current portable package when it is available in the public feed. Downloaded packages appear in **Version history** for rollback. Releases published before portable packages existed do not appear as portable rollback choices.

Do not delete `Updates/operations` while an update has an unresolved result. An unconfirmed operation blocks another application attempt rather than guessing whether it succeeded. See [UPDATES.md](UPDATES.md) for integrity and recovery details.

## Diagnostic logs

Command logging is enabled automatically. Open **Help > Open logs folder** to inspect `Data/Logs`, including when the backend is unavailable. A custom data location keeps its logs in that location's `Logs` directory. The existing **Activity** page continues to show script and settings results.

Failures before portable initialization are recorded separately in `%LOCALAPPDATA%/Bezoro/AhkManagerElectronDiagnostics/Startup`, outside the blocked app or Data folder. Up to ten reports of 64 KiB are retained. Reports distinguish failure to launch the helper from a native startup refusal and retain the phase, check, resource category, Windows error, HRESULT and available token identity. They do not contain raw helper stderr, authentication tokens or script contents. The startup error dialog offers **Copy diagnostic details** and, when saved, **Open diagnostics folder**. Background login failures remain silent and still save a report. A later complete diagnostic archive includes these reports.

The JSON Lines files record command starts and completions, UTC timestamps, durations, result and reason codes, and selected preference flags. Session and request IDs relate an interface command to its backend calls. Cancelled, refused, partial and unknown results remain distinct from success. Startup, tray actions, background update checks and shutdown are included after the data folder is prepared. A start without a completion can indicate that the process exited before reporting its result.

Logs rotate automatically, retaining up to five verified files of 2 MiB each. Unknown files in `Logs` are preserved. Logging errors never repeat or block an application command; an unavailable log folder is reported inline when opened from Help. Logs do not contain raw request bodies, authentication tokens, clipboard text, script contents or exception messages. Keep the corresponding Activity history and `Updates/operations` evidence when investigating a script or update failure.

In **Help**, choose **Save complete diagnostic archive…** to save a ZIP ready to attach to a support chat. It includes all available retained command records, Activity files and rotations, the recovery journal, startup-continuation evidence and update-operation diagnostics such as `plan.json`, `outcome.json` and `helper.log`. These evidence files have no 200-record excerpt limit. Useful local paths, account names and error details are included. Recognized credentials and script-content fields are redacted. Scripts, executable packages, browser storage and credential stores are excluded.

The archive contains `manifest.json` with file sizes, SHA-256 hashes, redaction counts and every collection issue. Unreadable, changed, unsafe or oversized files and collection limits are explicit. A partial archive produces a warning in Help. Valid JSONL records preceding an interrupted write are preserved, with the invalid record reported. Collection is read-only and bounded to 32 MiB of source evidence. Retention still applies; records already rotated out or deleted cannot be recovered. The archive does not collect live Task Scheduler XML or prove that a Windows logon ran successfully.

**Copy diagnostic summary** copies app information, safe Activity codes and recent command logs for pasting directly into a chat. **Copy error summary** selects failures, refusals and uncertain results with their command context. Ordinary cancellations are excluded. These summaries omit file paths, names, raw error messages, tokens and script contents, and include up to 200 command records within a 64 KiB clipboard limit. Omitted records and unreadable files are indicated. All diagnostic actions work when the backend is unavailable, with inline feedback and no additional confirmation prompt. **Open logs folder** remains available for inspecting the original files.

Close Manager normally before copying or moving its data. Logs move with the selected data folder; the previous folder remains a backup after changing the data location.

## Supported locations

Use a local NTFS volume for both the application and its data. ReFS, exFAT and FAT are not supported. An unsupported filesystem has a separate startup message; permission failures and incomplete packages do not appear as filesystem errors. On first launch, the Manager restricts access to its own dedicated portable folder when it can prove the package and current-user ownership. It never changes permissions on the containing repository or drive. A folder containing unrelated files is refused instead of having its permissions rewritten.

The application and data folders must remain private to the current user, Administrators, and SYSTEM. Network paths, junctions, symbolic links and unknown reparse tags are refused. Recognized Windows Cloud Files tags, used by OneDrive, are accepted without relaxing file identities, ownership, permissions or package integrity checks. Moving a private folder to another Windows account or computer may require a normal copy into a new user-owned folder because Windows account identities differ. Portable packaging does not bypass Windows UAC or make external script paths portable.

The portable root keeps a protected private DACL. App folders and startup files may inherit an equally private effective DACL from that root; startup verifies their trusted owners and access rules without rewriting valid inherited permissions. When a verified owned package needs protection, Manager changes only the DACL and preserves its already trusted owner. It does not request ownership changes as a side effect of preparing access. Foreign owners or writers remain a refusal.

### Recovering an older blocked version

An older version may refuse a private package whose `App` folder inherits its permissions. If the root already has protected private permissions, `tools/Repair-PortableInheritedAccess.ps1` can freeze that existing inheritance so the older Manager can open and update normally. This is a narrow compatibility repair, not a general permission reset.

Close Manager normally first. Independent AutoHotkey scripts can keep running. Preview the repair with Windows PowerShell 5.1 or PowerShell 7:

```powershell
& .\Repair-PortableInheritedAccess.ps1 -PortableRoot 'C:\Your portable folder'
```

Review the displayed paths, then run the same command with `-Apply`. The script verifies the full application inventory, trusted owners and existing private permissions before making changes. It only protects the existing DACL on the root, `App` and six startup files when needed. It does not add permissions, change owners, touch `Data`, or change the containing OneDrive or other parent folders. Foreign access, incomplete packages and redirected paths are refused.

Before changing permissions, the script saves the original descriptors, identities and package hashes under `%LOCALAPPDATA%\Bezoro\AhkManagerElectronDiagnostics\PermissionRepairs`. Keep this evidence if a repair fails. A failure after a completed change reports the affected paths and preserves the backup; it does not overwrite concurrent permission changes with an automatic rollback. Open the root launcher after a successful repair, then use **Updates** to obtain the corrected version.

## Cloud backup

The storage policy checks the filesystem and Windows file metadata, not a provider's name. Use a local synchronized folder on NTFS, with all application and data files available offline. Keep `Data` beside the root launcher to include the profile and downloaded version history in that folder's backup. Wait for downloads to finish before opening Manager. Files that still require a cloud recall are refused with a message explaining how to make them available locally.

For OneDrive, choose **Always keep on this device** for the complete portable folder. Dropbox and other sync clients may offer an **Available offline** option. Google Drive's **Mirror files** mode provides a local folder; its streaming virtual drive is a different filesystem and is outside the NTFS storage requirement. Regular NTFS files and recognized Windows Cloud Files markers use the same validation regardless of provider. Provider-specific reparse tags, virtual drives and other filesystems require separate validation and are not enabled by a provider's name. The Windows test fixture exercises the Cloud Files API, not each provider's synchronization client.

Close Manager normally before moving its folder or making a consistent copy of its data. After moving it, launch the root `Bezoro.AhkManager.exe` and update shortcuts to that path. Owned Windows startup tasks can be repaired after relocation; account names and SIDs are compared by resolved Windows identity.

Scripts do not have to be in `Data` or in the portable folder. The catalog stores their absolute paths and identifies running scripts outside the installation, including on other local drives. Importing or moving the profile does not copy those scripts. Back up external script files separately if they are outside the synchronized folder.
