# Paster
Small WPF-tool to paste things! :)

Paste a list of table names, describe the text you need for each one, and get a column of
buttons that copy the finished text to the clipboard.

## Download

Every merge to `main` builds `Paster.exe` (one self-contained file, no .NET install needed) and
publishes it as a zip under **Releases**. Unzip and run it. On first start a `config.json` with
example values is created next to the EXE.

Windows SmartScreen may warn about an unknown publisher the first time, because the EXE is not code-signed.
Click "More info" and then "Run anyway" (or right-click the zip, Properties, tick "Unblock" before unzipping).

## Modes

- **Setup**: the four editors are shown on the right. The buttons update as you type, so they work as a live preview.
- **Action**: the editors are hidden; just pick a table and click buttons.

The app starts in the mode it was closed in.

Tick **Always on top** in the top bar to keep Paster above other windows. This is remembered too.

## Setup files

Each setup is **one JSON file** holding the table list, variables, joiners and buttons, so you can copy it
anywhere (or to a colleague) and open it again. Use the **File** menu:

- **New** (Ctrl+N), **Open...** (Ctrl+O), **Open recent**, **Save** (Ctrl+S), **Save as...** (Ctrl+Shift+S).
- You can also drag a `.json` file onto the window, or onto `Paster.exe`.
- The title bar shows the current file, with `*` when there are unsaved changes. Paster asks before throwing changes away.
- On start Paster opens the file you used last. The very first time it creates `config.json` next to the EXE with an example setup.

Mode, "Always on top" and the recent files list are remembered per computer (in the registry under
`HKEY_CURRENT_USER\Software\Paster`), so they never end up in your setup files.

## Action log

In Action mode a log panel at the bottom records every copy, re-enable and reset with time and table.
The same lines are appended to a daily file, `logs\paster-YYYY-MM-DD.log` next to the EXE (tab-separated).
**Clear** empties the list in the window only; **Open log folder** shows the files.

## Buttons

- **Click**: copies the text and greys the button out.
- **Ctrl+click** a greyed-out button: enables it again so you can copy it once more.
- **Reset buttons**: enables every button for the selected table.
- The table list shows `copied/total` per table.

## The four editors

Blank lines and lines starting with `//` are ignored everywhere.

| Editor | Line format | Example |
|---|---|---|
| Table list | `Name` or `Name; var=value; var=value` | `Customer; schema=stg; prefix=Fact` |
| Global variables | `name=value` | `schema=wrk` |
| Joiners | `name=template` | `fulltable={prefix}_{table}{suffix}` |
| Buttons | any text with `{tokens}` | `TRUNCATE TABLE [{schema}].[{fulltable}];` |

### Tokens

- `{name}`: a variable or joiner. `{table}` is built in and holds the table name.
- `{name:upper}`: with a transform. Chain them: `{table:snake:upper}`.
  Transforms: `upper`, `lower`, `trim`, `pascal`, `camel`, `snake`, `kebab`, `title`.
- `{{` and `}}`: a literal `{` or `}`. Square brackets are plain text, so SQL like `[{schema}].[{table}]` just works.

A name is looked up in this order: the override on the table line, `{table}`, the global variables, then the joiners.
Joiners can use other joiners; a loop (`a` uses `b` uses `a`) is reported as an error.

### Soft errors

Problems never block you. The editor turns light red and the errors are listed under it;
table rows and buttons with problems are light red too (hover for details).

## Building locally

```
dotnet publish src/Paster/Paster.csproj -c Release -o publish
```

Requires the .NET 8 SDK. No NuGet packages are used.
