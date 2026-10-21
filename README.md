# Devscope

A Windows desktop tool (WPF on .NET 10) for two jobs:

1. **EAR diff**: compare two EARs (or exploded deployments) file by file, including everything nested in WARs and JARs.
2. **Logs**: tail, filter and colour unstructured logs across environments and cluster nodes, a bit like Seq but for plain-text logs.

Everything runs locally. Files are read in place from local disks or `\\server\share` paths. The app never uploads anything, makes no HTTP calls, sends no telemetry and opens no listening ports. It needs no admin rights.

## Features

### EAR diff
- Each side can be an `.ear/.war/.jar` **or an exploded folder**. You can also compare a packaged EAR with what is deployed.
- It recurses into nested archives (`billing.war!/WEB-INF/lib/core.jar!/com/acme/Foo.class`).
- Files are compared by CRC-32 + size, the values zip already stores, so most entries never need to be decompressed.
- A re-packaged jar with identical content is reported as unchanged: an archive's status comes from its children.
- **Side-by-side text diff** for xml/properties/jsp/MANIFEST and similar files, with next/previous-change navigation.
- **Changed `.class` files are decompiled** with [Vineflower](https://github.com/Vineflower/vineflower) (Apache-2.0) and diffed as Java source. Inner classes are included, and a Java target change (e.g. Java 17 → 21) is called out. Decompiling runs as a local `java` process on a private temp copy of the class bytes, which is deleted afterwards. If Java or the jar is missing, you get a size/Java-version summary instead.
- **Background decompiling**: after a comparison, every changed class is decompiled in the background, batched per JAR so one `java` process handles many classes. Most rows are ready before you click them, and the row you click is never queued behind the batch.
- **Caches**: nested WARs/JARs stay unpacked in memory (bounded, 256 MB by default), so selecting rows doesn't re-inflate them. Decompiled sources are kept on disk under `%LOCALAPPDATA%\Devscope\cache`, keyed by the class bytes, so an unchanged class is never decompiled twice, even after a restart.
- **Auto-refresh**: rescans when either side changes on disk, for example after a redeploy. It waits until the files have stopped changing, keeps your selection, and keeps the open diff when that file didn't change. Network shares are polled, because file change events aren't reliable there.
- Filter by path, show or hide unchanged files, export the differences to CSV. You can drag and drop files onto the path boxes.

### Logs
- **Workspaces**: one per app, with environments such as `local`, `test`, `beta-1`, `beta-2`, `prod-1` and `prod-2`. Each environment has one or more paths (a file, or a folder searched recursively with patterns like `*.log;*.txt`). "Duplicate" makes adding cluster nodes quick, and "Test paths" checks that every share is reachable.
- **Quick open**: any file or folder, without saving a workspace.
- All files are **merged by timestamp**. Multi-line events such as stack traces are grouped into one row.
- Formats detected automatically: ISO-8601/log4j/logback/Serilog, Tomcat, WebSphere `SystemOut.log`, `level=` / JSON `"level"`, and time-only formats.
- **Live tail** (like `tail -F`): handles rotation and truncation. Files are opened only during each poll, with full sharing, so the writing app is never blocked. Follow and pause are supported.
- Filters: text or regex search (regex runs on the non-backtracking engine, so it's safe), exclude, severity chips and environment chips, each with counts.
- **Colours**: tinted rows by severity, search hits highlighted, and per-workspace highlight rules (text or regex → colour).
- On start it reads only the last 4 MB of each file and the newest 50 files per path. Both limits can be changed in **Settings**.

## Architecture

```
src/Devscope.Core        UI-agnostic engine (net10.0): Ear/, Logs/, Workspaces/. Fully unit-tested.
src/Devscope.App         WPF shell (net10.0-windows): MVVM, views, services.
tests/Devscope.Core.Tests xUnit v3 tests for the engine.
```

- **Generic Host** (`Microsoft.Extensions.Hosting`) for DI, configuration and logging.
- **MVVM** with `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`, cancellable async commands).
- **Fluent theme** (`ThemeMode="System"`): follows Windows light/dark and the accent colour.
- View models never touch WPF windows. Dialogs, the clipboard and Explorer go through `IDialogService` / `IShellService`.
- Large lists are virtualized, and filtering runs off the UI thread with debouncing.
- Central package management (`Directory.Packages.props`), nullable, analyzers at `latest-recommended`, and warnings as errors.
- Crash handlers write to `%LOCALAPPDATA%\Devscope\crash.log`. The app's own log goes to `%LOCALAPPDATA%\Devscope\logs` (kept 14 days).
- Workspaces are stored in `%APPDATA%\Devscope\workspaces.json`, written atomically.

## Build, test, run

Requires the .NET 10 SDK.

```powershell
dotnet build Devscope.slnx
dotnet test --solution Devscope.slnx
dotnet run --project src/Devscope.App
```

## Java decompiler setup

Needs **Java 17+** and the Vineflower jar:

```powershell
./tools/Get-Vineflower.ps1   # downloads vineflower-1.12.0.jar from Maven Central, verifies SHA-1
```

The build copies the jar to `tools\` next to `Devscope.exe`, where it is found automatically. Java is found through `JAVA_HOME` or `PATH`. Either one can be overridden in **Settings** (the gear at the bottom of the navigation rail, or `Ctrl+,`). Settings also cover background decompiling, cache sizes and the log read limits. Everything is stored in `%APPDATA%\Devscope\settings.json`.

## Download

Grab the latest build from [Releases](https://github.com/anthony-sv/devscope/releases): unzip and run `Devscope.exe`. No .NET install is needed.

To cut a release, push a version tag. GitHub Actions tests, publishes and attaches the zip:

`powershell
git tag v0.2.0
git push origin v0.2.0
`

## Distribute

```powershell
dotnet publish src/Devscope.App -c Release -o artifacts/publish
```

This produces a **single self-contained `Devscope.exe`** (about 70 MB, with no .NET install needed on target machines) plus `toolsineflower-*.jar` if you fetched it. Copy it to a team share and people can run it from there or copy it locally. Each user's workspaces live in their own `%APPDATA%`. To share a workspace, copy `workspaces.json`.

If you later want auto-updates, either MSIX with an App Installer file on the share, or Velopack, works without any server.

## Possible next steps

- A query language (`level >= warn and env = prod-*`), saved filters, and a histogram over time.
- Import and export of workspaces for teams.
