# Contributing cleanup locations

SweepV's **Quick Clean** list lives in one file:

**[`src/SweepV.Core/Cleanup/cleanup-targets.json`](../src/SweepV.Core/Cleanup/cleanup-targets.json)**

You don't need to know C# to add a location — edit the JSON and open a pull request.
Editors like VS Code give autocomplete and validation through
[`cleanup-targets.schema.json`](../src/SweepV.Core/Cleanup/cleanup-targets.schema.json).

## Ground rules

SweepV is used by people who don't know what a folder is. A location may only be added if:

1. **Windows and apps keep working** after its contents are deleted.
2. The data is **regenerated, re-downloaded, or clearly disposable** (caches, temp files, logs, installers).
3. It's **worth it** — prefer locations that commonly reach hundreds of MB or more.
4. You can point to a **source** (official docs, the app's own "clear cache" feature, a well-known guide). Put it in the PR description.

If it can remove something a user might want (downloads, backups, offline data, rollback ability), set `"risk": "caution"` and say so in the description.

Things that are **not accepted** in the JSON:
- Running commands (DISM, powercfg, scripts…). Those are reviewed in code: `SystemActions.cs`.
- User documents, game saves, mailboxes (OST/PST), password stores, or app settings.
- Whole root folders (e.g. all of `%APPDATA%`).

## Entry format

```json
{
  "id": "telegram-cache",
  "name": "Telegram Desktop media cache",
  "description": "Photos and videos Telegram cached while you scrolled chats. They re-download when opened. Close Telegram first.",
  "category": "browsersAndApps",
  "recommended": true,
  "paths": ["%APPDATA%/Telegram Desktop/tdata/user_data"]
}
```

| Field | Required | Meaning |
|---|---|---|
| `id` | ✔ | Unique, `lowercase-kebab-case` |
| `name` | ✔ | Short title |
| `description` | ✔ | Plain language: what it is, what happens after cleaning, anything to do first ("close the app") |
| `category` | ✔ | `windows`, `upgradeLeftovers`, `browsersAndApps`, `gaming`, `developer`, `personal` |
| `paths` | ✔ | Folders whose **contents** are deleted (the folder itself stays) |
| `risk` | | `safe` (default) or `caution` |
| `recommended` | | Show the "recommended" badge and include in *Select recommended* |
| `requiresAdmin` | | Needs administrator rights (anything under `%WINDIR%`, `%PROGRAMDATA%` system folders, drive root) |
| `filePattern` | | Only delete matching files directly in the folder, e.g. `thumbcache_*.db` |
| `removeFolder` | | Take ownership and delete the folder itself (leftovers like `Windows.old`) |
| `kind` | | `folderContents` (default) or `recycleBin` |

### Path tokens

Every path must start with one of these, and use `/` or `\` as separators:

| Token | Typical value |
|---|---|
| `%WINDIR%` | `C:\Windows` |
| `%SYSTEMDRIVE%` | `C:\` |
| `%TEMP%` | `C:\Users\you\AppData\Local\Temp` |
| `%LOCALAPPDATA%` | `C:\Users\you\AppData\Local` |
| `%APPDATA%` | `C:\Users\you\AppData\Roaming` |
| `%PROGRAMDATA%` | `C:\ProgramData` |
| `%USERPROFILE%` | `C:\Users\you` |
| `%PROGRAMFILES%` / `%PROGRAMFILES(X86)%` | `C:\Program Files` / `C:\Program Files (x86)` |
| `%STEAM%` | Steam install folder (read from the registry) |

`*` matches folder names, which is handy for browser profiles or versioned folders:
`%LOCALAPPDATA%/JetBrains/*/caches`.

Locations that don't exist on a PC are hidden automatically, so app-specific entries are fine.

## Checking your change

```bash
dotnet test
```

The tests validate every entry (ids, categories, tokens, no root folders). An invalid entry is also skipped by the app at runtime.
