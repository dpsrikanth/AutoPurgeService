# Auto Purge Service

A .NET 10 Windows Service that deletes old log files on the web server once a day. It purges IIS logs, application log folders, payment-service log folders and old temp files according to the rules in `appsettings.json`, and logs everything it does to the Windows Event Log.

| | |
|---|---|
| Install folder | `E:\Services\AutoPurgeService` |
| Service name | `AutoPurgeService` (display name "Auto Purge Service") |
| Schedule | Daily at 02:00 server time |
| Configuration | `E:\Services\AutoPurgeService\appsettings.json` |
| Logs | Event Viewer → Windows Logs → Application, source `AutoPurgeService` |
| Last-run record | `E:\Services\AutoPurgeService\last-run.txt` |

---

## What Gets Purged

The shipped `appsettings.json` contains these rules. Age is measured from each file's last-modified time.

| Folder | Files | Kept for | Subfolders |
|---|---|---|---|
| `C:\inetpub\logs\LogFiles` (IIS logs for every site using the default log folder, including the FTP sites) | `.log` | 7 days | Yes |
| `C:\Program Files (x86)\Plesk\admin\logs\W3SVC11` (IIS logs for site `pleskcontrolpanel`, ID 11) | `.log` | 7 days | Yes |
| `E:\CLOUDCAMPUSLIVE\CampusWebSolutions\CampusRFIdAttendance\Log Files` (IIS logs for site `siliconrfidsol.unicampus.in`, ID 72) | `.log` | 7 days | Yes |
| `C:\Windows\Temp` | `.tmp`, `.bak`, `.log` | 7 days | No |
| 17 application log folders (listed below) | All | 7 days | Yes |
| 3 payment log folders (listed below) | All | 30 days | Yes |

Application log folders, under `E:\CLOUDCAMPUSLIVE\NEWSERVERCODE\`:

- `PRODUCTIONNEW\CLOUDCOLLEGECAMPUS\Log Files`
- `PRODUCTIONNEW\DEEKSHALEARNING\Log Files`
- `PRODUCTIONNEW\CampusWeb\Log Files`
- `PRODUCTIONNEW\CLOUDCAMPUSNEWBUILD\Log Files`
- `CLOUDCAMPUS\Log Files`
- `PRODUCTION\MobileServices2023\Student\Log Files`
- `PRODUCTION\MobileServices2023\Campus\Log Files`
- `PRODUCTION\MobileServices2023\StudentService\Log Files`
- `PRODUCTION\MobileServices2023\CampusApi\Log Files`
- `PRODUCTION\MobileServices2023\Staff\Log Files`
- `PRODUCTION\MobileServices2023\CampusService\Log Files`
- `PRODUCTION\MobileServices2023\StaffService\Log Files`
- `PRODUCTION\MobileServices2021\Log Files`
- `LOGS`
- `UNITRACK\UniTrack\Logs`
- `UNITRACK\LOGS`
- `CLOUDCONTROLPANEL\Log Files`

Payment log folders (kept 30 days), under `E:\CLOUDCAMPUSLIVE\NEWSERVERCODE\PRODUCTION\`:

- `PGPay\Log Files`
- `PGPay_01Nov2023\Log Files`
- `PAYTMAPI\Log Files`

Subfolders themselves are never removed, only the files in them. Read-only files are skipped.

---

## Installation

Run every command on the server in **PowerShell as an Administrator**.

### Prerequisites

- The .NET 10 SDK on the machine you build on. The server itself needs no .NET runtime: the publish command below bundles it.
- If you build on a different machine, publish to a local folder there and copy the whole output folder to `E:\Services\AutoPurgeService` on the server.

### Step 1: Publish

From the project folder (the one containing `AutoPurgeService.csproj`):

```powershell
dotnet publish AutoPurgeService.csproj -c Release -r win-x64 --self-contained true -o E:\Services\AutoPurgeService
```

Name `AutoPurgeService.csproj` explicitly. The folder also contains the solution file, which includes the test project, and publishing that would copy test DLLs into the service folder.

### Step 2: Review the Configuration

Open `E:\Services\AutoPurgeService\appsettings.json` and check the schedule and rules match [What Gets Purged](#what-gets-purged). See the [Configuration Reference](#configuration-reference) for every setting.

### Step 3: Preview What Will Be Deleted

Before the service deletes anything, run a single purge in WhatIf mode. It lists the files that would be deleted, deletes nothing, and exits:

```powershell
E:\Services\AutoPurgeService\AutoPurgeService.exe --once --PurgeSettings:WhatIf=true
```

Check the `WhatIf: would delete` lines and the `Finished rule` summary for each folder. Also look for warnings about skipped rules or unrecognized settings. A folder that doesn't exist on the server is reported and skipped.

### Step 4: Register the Service

```powershell
New-Service -Name "AutoPurgeService" `
            -BinaryPathName "E:\Services\AutoPurgeService\AutoPurgeService.exe" `
            -DisplayName "Auto Purge Service" `
            -Description "Deletes old log files daily based on appsettings.json." `
            -StartupType Automatic
```

### Step 5: Register the Event Log Source

The service can create its Event Log source itself only when it runs as an administrator account such as `LocalSystem`. Under any other account, Event Log output is silently disabled if the source doesn't exist, so register it now:

```powershell
if (-not [System.Diagnostics.EventLog]::SourceExists("AutoPurgeService")) { [System.Diagnostics.EventLog]::CreateEventSource("AutoPurgeService", "Application") }
```

### Step 6: (Optional) Restart Automatically After a Crash

Restart after 1 minute, resetting the failure count daily:

```powershell
sc.exe failure AutoPurgeService reset= 86400 actions= restart/60000/restart/60000/restart/60000
```

### Step 7: Service Account and Permissions

The service runs as `LocalSystem` by default, which can reach every folder above; nothing else is needed.

To run it under a dedicated account instead:

1. Open `services.msc`, right-click **Auto Purge Service**, choose **Properties** → **Log On** → **This account**, and enter the account's credentials.
2. Give that account **Modify** permission on every purged folder.
3. Give it **Write** permission on `E:\Services\AutoPurgeService`, so it can update `last-run.txt`. Without it, a warning is logged and every restart triggers an extra purge.

### Step 8: Start and Verify

```powershell
Start-Service -Name "AutoPurgeService"
```

On its first start the service purges immediately, because it has no record of a previous run. After that it runs daily at 02:00.

Check it's running:

```powershell
Get-Service -Name "AutoPurgeService"
```

And check what it did:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'AutoPurgeService' } -MaxEvents 30 | Format-Table TimeCreated, LevelDisplayName, Message -Wrap
```

You should see a `Finished rule` summary per folder and a `Next run at <date> 02:00` line. If the service won't start, see [Troubleshooting](#troubleshooting).

---

## Day-to-Day Operation

### When It Runs

The service purges once a day at `DailyRunTime` (02:00). It records each completed purge in `last-run.txt`, so:

- Restarting the service doesn't cause an extra purge.
- If the server was down at 02:00, the missed purge runs as soon as the service starts again.
- If daylight saving skips 02:00, the purge runs an hour later that day; if it repeats the hour, the purge runs once.

Stopping the service interrupts a purge in progress between files.

### Running a Purge Now

To purge immediately, whatever the schedule:

```powershell
E:\Services\AutoPurgeService\AutoPurgeService.exe --once
```

Add `--PurgeSettings:WhatIf=true` to only list what would be deleted. The output goes to the console and the Event Log. The exit code is `0` if the purge ran and `1` if it couldn't, e.g. because `appsettings.json` has an invalid value.

A `--once` run isn't recorded in `last-run.txt`, so it doesn't change when the service next runs, and it's safe while the service is running. It runs as your account rather than the service account, so file permissions can differ.

### Changing the Configuration

Edit `E:\Services\AutoPurgeService\appsettings.json` and save. There is no need to restart the service:

- Rule changes apply from the next purge.
- A `DailyRunTime` change reschedules the next purge immediately.

Preview a change with `--once --PurgeSettings:WhatIf=true` first. Mistakes are reported in the Event Log rather than silently ignored:

- A rule with a value that can't be read (e.g. `"AgeType": "Modified"`) doesn't run, and the service logs an error naming it.
- A misspelled setting name (e.g. `"Recursve"`) is logged as a warning and otherwise ignored.
- If the file stops being valid JSON, the service falls back to no rules and logs "No purge rules configured".

Also make the same change in the repository's `appsettings.json`, or it will be overwritten by the next update.

### Adding Folders

To add a folder, add a rule to `Rules` (see the [Configuration Reference](#configuration-reference)).

When new IIS sites are added, check where they write their logs:

```powershell
Get-Website | Select-Object Name, Id, @{ n = 'LogFolder'; e = { $_.logFile.directory } }
```

Sites showing `%SystemDrive%\inetpub\logs\LogFiles` are already covered. A site logging anywhere else needs its own rule. IIS writes each site's files to a `W3SVC<Id>` subfolder of that folder, so target that subfolder if the folder is shared with other files, as the Plesk rule does.

### Reviewing Logs

1. Open **Event Viewer** (`eventvwr.msc`).
2. Go to **Windows Logs** → **Application**.
3. Filter by source **`AutoPurgeService`**.

Each run logs:

- when it started
- each file it deleted
- a summary per folder: files inspected, deleted, read-only files skipped, and failures
- any files that couldn't be deleted, e.g. because another process has them open

To keep a large purge from flooding the Event Log, only the first 100 deleted files per folder per run are listed individually; the summary always gives the total. To list every file, add `"AutoPurgeService": "Debug"` under `Logging` → `EventLog` → `LogLevel` in `appsettings.json`.

---

## Updating to a New Version

Publishing overwrites the files in `E:\Services\AutoPurgeService`, including `appsettings.json`, and fails while the service is running. So:

1. Stop the service:

   ```powershell
   Stop-Service -Name "AutoPurgeService"
   ```

2. If `appsettings.json` was edited on the server and the repository copy wasn't updated, back it up.
3. Publish as in [Step 1](#step-1-publish).
4. Restore the backed-up `appsettings.json`, if you made one.
5. Start the service:

   ```powershell
   Start-Service -Name "AutoPurgeService"
   ```

`last-run.txt` isn't touched by publishing, so the schedule carries on.

---

## Uninstalling

```powershell
Stop-Service -Name "AutoPurgeService"
```

```powershell
sc.exe delete AutoPurgeService
```

Then delete `E:\Services\AutoPurgeService`. Optionally, remove the Event Log source:

```powershell
[System.Diagnostics.EventLog]::DeleteEventSource("AutoPurgeService")
```

---

## Troubleshooting

### The Service Won't Start

`Start-Service` only reports "Cannot start service". To find the cause:

**1. Check the registered exe path.** It must be where the service is actually installed:

```powershell
sc.exe qc AutoPurgeService
```

If `BINARY_PATH_NAME` is wrong (e.g. a `C:\Services\...` path), correct it, then start the service again:

```powershell
sc.exe config AutoPurgeService binPath= "E:\Services\AutoPurgeService\AutoPurgeService.exe"
```

The space after `binPath=` is required.

**2. Get the real error code:**

```powershell
sc.exe start AutoPurgeService
```

| Code | Meaning |
|---|---|
| 2 | The exe isn't at the registered path. Fix it as above. |
| 5 | Access denied: the service account can't read `E:\Services\AutoPurgeService`. |
| 1053 | The service didn't respond in time. Check the files were copied completely. |
| 1067 | The program started and then crashed. Go to step 3. |
| 1069 | Logon failure: wrong password, or the account lacks the "Log on as a service" right. |

**3. Run it directly to see the error:**

```powershell
E:\Services\AutoPurgeService\AutoPurgeService.exe --once --PurgeSettings:WhatIf=true
```

The usual causes are an `appsettings.json` edit that broke the JSON, or a copy that missed some published files. The error says which. Crash details also appear in the Application log:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime', 'Application Error' } -MaxEvents 5 | Format-List TimeCreated, Message
```

### Files Aren't Being Deleted

Check the `Finished rule` summaries and warnings in the Event Log:

| What you see | Cause |
|---|---|
| "WhatIf mode is on" | `WhatIf` is `true` in `appsettings.json`. |
| "Directory ... does not exist" | The folder path is wrong or the drive isn't there. |
| "is not allowed" | The folder is a drive root, a relative path, or a core system folder. See [Safety Checks](#safety-checks). |
| "has no Extensions" | The rule's `Extensions` list is empty; use `["*"]` for all files. |
| "could not be read and will not run" | The rule has an invalid value, named in the message. |
| "skipped N read-only files" | Those files have the read-only attribute. Set `DeleteReadOnly` on the rule to delete them. |
| "Could not read folder" | The service account lacks permission on that folder or subfolder. |
| "Failed to process or delete file" | The file is open in another process or permission was denied. It's retried at the next run. |
| No entries at all | The Event Log source isn't registered ([Step 5](#step-5-register-the-event-log-source)), or the service isn't running. |

---

## Configuration Reference

```json
{
  "PurgeSettings": {
    "DailyRunTime": "02:00",
    "WhatIf": false,
    "Rules": [
      { "FolderPath": "C:\\inetpub\\logs\\LogFiles", "Extensions": [ ".log" ], "AgeInDays": 7, "Recursive": true }
    ]
  }
}
```

Use double backslashes (`\\`) in paths.

### Schedule and Mode

| Setting | Default | Meaning |
|---|---|---|
| `DailyRunTime` | not set | Time of day to purge, once daily, in server local time (24-hour `"HH:mm"`, e.g. `"02:00"`). When set, `IntervalMinutes` is ignored. |
| `IntervalMinutes` | `60` | Used only when `DailyRunTime` isn't set: minutes between purges, from the end of one to the start of the next. Must be greater than 0 and at most `71582` (about 49.7 days). |
| `WhatIf` | `false` | When `true`, matching files are logged as "would delete" but nothing is deleted. |

### Rule Settings

| Setting | Default | Meaning |
|---|---|---|
| `FolderPath` | (required) | Absolute path of the folder to purge. |
| `Extensions` | (required) | Extensions to match, e.g. `[".log", ".txt"]`, or `["*"]` for all files. A rule with an empty list is skipped. |
| `AgeInDays` | `7` | Files older than this are deleted. Decimals allowed (`0.5` = 12 hours). Must be 0 or more. |
| `AgeType` | `LastWriteTime` | Which timestamp to compare: `LastWriteTime` (modified), `CreationTime`, or `LastAccessTime`. `LastAccessTime` is only reliable if NTFS last-access updates are enabled (`fsutil behavior query disablelastaccess`); the service warns if they aren't. |
| `Recursive` | `false` | `true` to include subfolders. Junctions and symbolic links to folders are never followed, so a purge can't be led outside `FolderPath`. |
| `DeleteReadOnly` | `false` | `true` to delete files with the read-only attribute; otherwise they're skipped and counted. |
| `DeleteEmptyFolders` | `false` | With `Recursive`, also remove subfolders that are empty after the purge and were created at least `AgeInDays` ago. `FolderPath` itself is never removed. |

### Safety Checks

A rule is skipped, with a warning in the Event Log, if:

- `FolderPath` is relative, a drive root (`C:\`), or a share root (`\\server\share`)
- `FolderPath` is, or contains, a core system folder: Windows, System32, SysWOW64, Program Files, Program Files (x86), ProgramData, the Users folder, or the service account's profile. Subfolders of these, such as `C:\Windows\Temp`, are allowed.
- `Extensions` is empty
- `AgeInDays` is negative

---

## Development

- `dotnet run` uses the `Development` environment, where `appsettings.Development.json` turns on WhatIf mode, so running locally deletes nothing.
- `dotnet run -- --once` runs a single purge and exits.
- The local `last-run.txt` goes in the build output folder.

Run the tests (they create and purge files in temporary folders only):

```powershell
dotnet test AutoPurgeService.Tests
```
