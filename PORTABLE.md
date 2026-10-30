# Portable edition

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

No Setup or uninstall registration is required. Opening the root launcher starts Manager and its notification-area icon. A new profile starts Manager with Windows and requests administrator privileges by default; both preferences remain configurable. Windows may show UAC when elevation is needed.

After obtaining writable ownership of its profile, Manager creates a current-user **AHK Manager Portable** shortcut in the Start Menu. It points to the root launcher, so Windows Search and PowerToys launchers that index Start Menu applications can discover it. Search results can take time to refresh, and the relevant PowerToys launcher must be enabled. Existing unrelated shortcuts are preserved.

After closing Manager normally, you can move the complete portable folder. Open the root launcher in its new location once: the application derives the relative `Data` paths again and repairs its owned Start Menu shortcut and configured Windows startup registration. An external absolute data location keeps its original meaning. External script paths are preserved; moving Manager does not move those scripts.

## Importing current settings

Before the first portable launch, close the current Manager with **Exit and keep scripts running**. Existing AHK processes can stay active. The portable edition imports the current Electron profile from `%LOCALAPPDATA%/Bezoro/AhkManagerElectron` into `Data/Profile`. It preserves the source and a migration backup. Script files remain at their original paths.

Import requires exclusive ownership of Manager effects. Another active Manager, a corrupt source profile, or unresolved startup continuation prevents an unsafe import. An error never silently replaces the profile with empty settings. Archived WinUI profiles retain their separate preview and confirmation flow.

Only the public update channel preference is carried into the new update store. Installer credentials and unfinished installer operations are not imported. The installed edition and portable edition use different package histories.

## Choosing a data folder

Open **Preferences > Data location** to see the active path. **Open folder** opens it in Explorer. **Change location** uses the Windows folder picker. Choose a new or empty local folder outside `App`.

Choosing a location prepares the change. The Manager keeps using the current data until you select **Restart Manager**. Save other preference edits before changing the location. The restart closes the backend normally, transfers the profile and update history, verifies the copy, then switches the data pointer. Your independent scripts keep running. Chromium storage is recreated at the new location.

The old data folder remains as a backup. **Cancel move** cancels a prepared change without deleting the source or recovery copies. If a move fails before the data pointer changes, the Manager can keep using a verified original profile and show the error in Preferences. It does not choose an empty replacement folder.

**Use default** selects `Data` beside the launcher. A nonempty old backup is not merged or overwritten. Keep that backup elsewhere before choosing the same path again.

The selected path is stored in `portable.json` beside the launcher. Updates preserve this file. A folder inside the portable root is stored as a relative path; a location outside it is an absolute path. If you later move the application, an external absolute data path continues to refer to its original location.

## Updates and rollback

Use **Updates** and select **Stable** or **Test** as usual. The portable edition downloads portable packages and applies them without Setup registration. It verifies the package, waits for normal Manager shutdown, replaces the application files, and reopens the Manager. Your data path, scripts, and profile remain intact.

Before a confirmed update, the Manager preserves the current portable package when it is available in the public feed. Downloaded packages appear in **Version history** for rollback. Releases published before portable packages existed do not appear as portable rollback choices.

Do not delete `Updates/operations` while an update has an unresolved result. An unconfirmed operation blocks another application attempt rather than guessing whether it succeeded. See [UPDATES.md](UPDATES.md) for integrity and recovery details.

## Supported locations

Use a local NTFS volume for both the application and its data. ReFS, exFAT and FAT are not supported. An unsupported filesystem has a separate startup message; permission failures and incomplete packages do not appear as filesystem errors. On first launch, the Manager restricts access to its own dedicated portable folder when it can prove the package and current-user ownership. It never changes permissions on the containing repository or drive. A folder containing unrelated files is refused instead of having its permissions rewritten.

The application and data folders must remain private to the current user, Administrators, and SYSTEM. Network paths, junctions, symbolic links and unknown reparse tags are refused. Recognized Windows Cloud Files tags, used by OneDrive, are accepted without relaxing file identities, ownership, permissions or package integrity checks. Moving a private folder to another Windows account or computer may require a normal copy into a new user-owned folder because Windows account identities differ. Portable packaging does not bypass Windows UAC or make external script paths portable.

## Cloud backup

The storage policy checks the filesystem and Windows file metadata, not a provider's name. Use a local synchronized folder on NTFS, with all application and data files available offline. Keep `Data` beside the root launcher to include the profile and downloaded version history in that folder's backup. Wait for downloads to finish before opening Manager. Files that still require a cloud recall are refused with a message explaining how to make them available locally.

For OneDrive, choose **Always keep on this device** for the complete portable folder. Dropbox and other sync clients may offer an **Available offline** option. Google Drive's **Mirror files** mode provides a local folder; its streaming virtual drive is a different filesystem and is outside the NTFS storage requirement. Regular NTFS files and recognized Windows Cloud Files markers use the same validation regardless of provider. Provider-specific reparse tags, virtual drives and other filesystems require separate validation and are not enabled by a provider's name. The Windows test fixture exercises the Cloud Files API, not each provider's synchronization client.

Close Manager normally before moving its folder or making a consistent copy of its data. After moving it, launch the root `Bezoro.AhkManager.exe` and update shortcuts to that path. Owned Windows startup tasks can be repaired after relocation; account names and SIDs are compared by resolved Windows identity.

Scripts do not have to be in `Data` or in the portable folder. The catalog stores their absolute paths and identifies running scripts outside the installation, including on other local drives. Importing or moving the profile does not copy those scripts. Back up external script files separately if they are outside the synchronized folder.
