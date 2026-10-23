# Paster
Small WPF-tool to paste things! :)

Paste a list of table names, describe the text you need for each one, and get a column of
buttons that copy the finished text to the clipboard.

## Download

Every merge to `main` builds `Paster.exe` (one self-contained file, no .NET install needed) and
publishes it as a zip under **Releases**. Unzip and run it. On first start a `config.json` with
example values is created next to the EXE; everything you type is saved there automatically.

## Modes

- **Setup**: the four editors are shown on the right. The buttons update as you type, so they work as a live preview.
- **Action**: the editors are hidden; just pick a table and click buttons.

The app starts in the mode it was closed in.

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
