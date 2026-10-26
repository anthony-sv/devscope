# Devscope

A Windows desktop tool (WPF on .NET 10) for three jobs:

1. **EAR diff**: compare two EARs (or exploded deployments) file by file, including everything nested in WARs and JARs.
2. **DB diff**: compare two SQL Server databases: which objects exist on one side only, how tables differ down to each column, key and index, and which rows are missing, extra or changed.
3. **Logs**: tail, filter and colour unstructured logs across environments and cluster nodes, a bit like Seq but for plain-text logs.

Everything runs locally. Files are read in place from local disks or `\\server\share` paths. The app never uploads anything, makes no HTTP calls, sends no telemetry and opens no listening ports. The DB diff connects only to the SQL Servers you configure, and only reads from them. It needs no admin rights.

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
- **Identical text is kept out of the way**: a file whose bytes differ only in line endings, a byte order mark or compiler output (e.g. a class recompiled after a comment moved) is still Modified, but hidden from the list by default. A hint says how many there are, and one click (or the ⋯ menu) lists them with a "same text" tag. Their diff says what differs.
- **Single click previews, double click opens**: the preview sits next to the list, and double-click (or Enter) opens the file in a separate diff window with editor-style tabs (Ctrl+W closes one). The previewed row stays clearly marked (fill, bar and an eye icon) even while you read the diff.
- **Zoom** the diff text with Ctrl+mouse wheel or Ctrl+plus/minus (Ctrl+0 resets); it applies to the preview and every tab, and is remembered.
- Filter by status chips (Modified, Added, Removed, Unchanged) and by path, sort by any column, export the differences to CSV. You can drag and drop files onto the path boxes.

### DB diff (SQL Server)
- **Saved connections**: server, database and login per environment (`Billing PROD`, `Billing TEST`, ...), stored in `%APPDATA%\Devscope\connections.json`. Windows authentication or SQL logins. A saved password is encrypted with DPAPI for your Windows account. If you don't save it, you're asked once per session. **Duplicate** makes adding environments quick, and **Test connection** checks a connection.
- **Paste instead of typing**: an ADO.NET connection string from a config file, or the **JDBC URL of a Java data source** (`jdbc:sqlserver://...` or jTDS `jdbc:jtds:sqlserver://...`, as in a WebSphere connection pool), becomes a connection.
- **Read-only by design**: catalog views for the schema and `SELECT` for rows. A login with `db_datareader` and `VIEW DEFINITION` is enough. Connections use `ApplicationIntent=ReadOnly` by default, so an availability group sends them to a readable secondary.
- **Objects on one side only**: tables, views, procedures, functions, triggers, user-defined and table types, sequences and synonyms. Status chips show *Different*, *Only in left*, *Only in right* and *Identical*. You can filter by object type and name, and sort by any column.
- **Differences in words**: for tables, e.g. `21 columns → 20 columns`, `Column [Email] only in left: nvarchar(200) NULL`, `Column [Status]: varchar(20) → varchar(30); NULL → NOT NULL`, primary key, unique constraint, index, foreign key and check constraint changes (a renamed constraint is reported as a rename). For code, `Definition differs: +12 −3 lines`. For sequences, the property that changed (`INCREMENT BY 1 → INCREMENT BY 10`).
- **Script diff**: each object's `CREATE` script side by side, with git-style colours, F8 change navigation and zoom, exactly as the comparison saw it. Scripts list one column or constraint per line in a stable order, so the diff pinpoints what changed.
- **Noise you can ignore**, re-applied instantly without reading the databases again:
  - **Generated constraint names**, on by default. `DF__Orders__Statu__3B75D760` differs in every database, even when the constraint is the same.
  - **Whitespace in code**, on by default. Indentation, blank lines and line endings.
  - **Column order** and **collations**, off by default.
- **Database-level differences** (default collation, compatibility level, SQL Server version) are shown above the list.
- **Row counts from metadata** for every table, free to read. The count is highlighted when the two sides differ, which is often the quickest hint that rows are missing.
- **Data diff, per table or for every listed table**: rows only in left (missing on the right), only in right, and changed (same key, different values). Changes are counted per column, and up to 1,000 example rows per kind are listed. Filter rows with a `WHERE` condition (e.g. one tenant or the last month), leave out columns such as audit timestamps (`rowversion` columns always are), or choose the key columns.
  - Tables with a primary key or unique index are **streamed in key order and merged**, so a table of any size needs almost no memory. Text keys are sorted under a binary collation on the server and the same way locally, so case-insensitive, accented and CP1252 `varchar` keys line up.
  - When that cannot work (keys of different types on each side, or a key that isn't unique), the comparison starts over in a mode that does, and explains why. Tables without any key are compared as whole rows.
- **Sync script**: copy the `INSERT`/`UPDATE`/`DELETE` statements that make the right table match the left (or the other way round) for the rows found, with `IDENTITY_INSERT` handled and computed columns left out. It is meant for you to review; Devscope never runs it.
- Export the listed objects, with their differences and data results, to CSV.

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
src/Devscope.Core              UI-agnostic engine (net10.0): Ear/, Database/, Logs/, Workspaces/. No network code. Fully unit-tested.
src/Devscope.SqlServer         SQL Server access (Microsoft.Data.SqlClient): catalog reader and row streaming. Only reads.
src/Devscope.App               WPF shell (net10.0-windows): MVVM, views, services.
tests/Devscope.Core.Tests      xUnit v3 tests for the engine.
tests/Devscope.SqlServer.Tests Connection string tests, and integration tests against a real SQL Server (see below).
```

The database comparison is split so it can be tested without a server: `Core/Database` holds the schema model, the `CREATE` scripter, the schema comparer and the row comparer, all working on plain data. `Devscope.SqlServer` turns catalog views and table rows into that data. Another database product would be another `IDatabaseProvider`.

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

The SQL Server integration tests are skipped unless `DEVSCOPE_TEST_SQLSERVER` holds a connection string whose login may create databases. They create two scratch databases with known differences and drop them afterwards. A throwaway server in Docker works:

```powershell
docker run -d -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Devscope!Pass123" -p 14330:1433 mcr.microsoft.com/mssql/server:2022-latest
$env:DEVSCOPE_TEST_SQLSERVER = "Server=localhost,14330;User ID=sa;Password=Devscope!Pass123;TrustServerCertificate=True"
dotnet test --solution Devscope.slnx
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

- DB diff: a schema sync script (`ALTER TABLE ... ADD`, changed procedures) next to the data one, permissions, users and roles, extended properties, and other database products (Oracle, PostgreSQL) through another `IDatabaseProvider`.
- DB diff: saved comparisons with per-object ignore rules, and a matrix of environments (dev, test, prod) compared in one go.

- A query language (`level >= warn and env = prod-*`), saved filters, and a histogram over time.
- Import and export of workspaces for teams.
