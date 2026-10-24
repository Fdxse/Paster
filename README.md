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

## Help

**Help > Quick guide** (F1) explains every part of Paster inside the app. **Help > About** shows the version and where files are stored.

## Modes

- **Setup**: the five editors are shown on the right, one tab each. The buttons update as you type, so they work as a live preview.
  A tab with problems turns red with the count, e.g. `Joiners (2)`. **Help** in a tab explains what that box accepts, and
  **Insert example** fills the box with example lines (only while it has nothing but `//` comments).
- **Action**: the editors are hidden; just pick a table and click buttons.

The app starts in the mode it was closed in.

Tick **Always on top** in the top bar to keep Paster above other windows. This is remembered too.

**View > Action buttons** makes the buttons easier to hit in Action mode: Normal, Large, Extra large, or
**Fill the window** (the buttons share the whole height). The choice is saved in the setup file, where you can
also type exact numbers:

```json
"actionButtonSize": "fixed",   // or "fill"
"actionButtonHeight": 60,      // pixels, used with "fixed"
"actionFontSize": 18
```

## Setup files

Each setup is **one JSON file** holding the table list, layers, variables, joiners and buttons, so you can copy it
anywhere (or to a colleague) and open it again. Use the **File** menu:

- **New** (Ctrl+N), **Open...** (Ctrl+O), **Open recent**, **Save** (Ctrl+S), **Save as...** (Ctrl+Shift+S).
- You can also drag a `.json` file onto the window, or onto `Paster.exe`.
- The title bar shows the current file, with `*` when there are unsaved changes. Paster asks before throwing changes away.
- On start Paster opens the file you used last. The very first time it creates `config.json` next to the EXE with an example setup.

Mode, "Always on top", "Keep buttons active", the counter and the recent files list are remembered per computer (in the registry under
`HKEY_CURRENT_USER\Software\Paster`), so they never end up in your setup files.

## Action log

In Action mode a log panel at the bottom records every copy, re-enable, reset and finished step with time and table (· layer).
The same lines are appended to a daily file, `logs\paster-YYYY-MM-DD.log` next to the EXE (tab-separated).
**Export...** saves the list to a tab-separated text file; **Clear** empties the list in the window only;
**Open log folder** shows the files.

## Buttons

- **Click**: copies the text and greys the button out.
- **Ctrl+click** a greyed-out button: enables it again so you can copy it once more.
- **Keep buttons active** (top bar): every click copies and the button stays clickable; **Ctrl+click** copies *and*
  toggles the grey (greyed out ↔ clickable). Off gives the behaviour above. Remembered per computer.
- **Count** (top bar): +1 for every copy. A plain click does nothing; **Ctrl+click** −1, **Alt+click** reset to 0,
  **Shift+click** type the number to continue from. Remembered between runs.
- **Hover** a button to see the full text it will copy.
- **Reset table** (**Reset step** with layers) enables every button in the selected row; **Reset all** does it everywhere.
- The list on the left shows `copied/total` per row and greys a row out with a ✓ when it is done; the progress bar above the buttons shows the total.
- In **Action mode**, copying the last button of a row jumps to the next row that still has buttons left.

## The five editors

Blank lines and lines starting with `//` are ignored everywhere.

| Editor | Line format | Example |
|---|---|---|
| Table list | `Name` or `Name; var=value; var=value` | `Customer; schema=stg; prefix=Fact` |
| Layers (optional) | `name` or `name; var=value; var=value` | `dwh; schema=dwh; prefix=Dim_` |
| Global variables | `name=value` | `schema=wrk` |
| Joiners | `name=template` | `fulltable={prefix}_{table}{suffix}` |
| Buttons | any text with `{tokens}`, optionally starting with `@layer,layer` | `TRUNCATE TABLE [{schema}].[{fulltable}];` |

### Layers

When every table goes through the same layers (stg, wrk, dwh...), list them in the **Layers** editor, in order.
Each table then becomes one step per layer, shown as `DBTable · wrk` on the left with its own buttons and progress.

- Variables after a `;` on a layer line only apply in that layer, so `{schema}` can be `stg` in one layer and `dwh` in the next.
- `{layer}` holds the layer name.
- `layers=stg,wrk` on a table line limits that table to those layers.
- A button line starting with `@stg` (or `@stg,wrk`) only shows in those layers. This works in front of a `<<<` block too.
- **Order** in the top bar lists the steps **By layer** (every table in stg, then every table in wrk...) or
  **By table** (DBTable through every layer, then the next table). Switching keeps what you already copied.
  The choice is saved in the setup file as `"walkOrder": "layer"` or `"table"`.

Leave the Layers editor empty to work with plain tables like before.

```
Layers:   stg; schema=stg
          wrk; schema=wrk
          dwh; schema=dwh; prefix=Dim_
Tables:   DBTable
          SalesOrder; layers=stg,wrk
Buttons:  @stg Import {table} with SSIS
          TRUNCATE TABLE [{schema}].[{prefix}{table}];
```

### Multi-line buttons

A button can copy a whole block. Start with `<<<` and a label, put the lines to copy under it, and end with `>>>`
on a line of its own. The button shows the label (tokens allowed); hover to see the full text. Lines inside the
block are copied exactly as typed.

```
<<< Reload {table}
TRUNCATE TABLE [{schema}].[{fulltable}];
INSERT INTO [{schema}].[{fulltable}]
SELECT * FROM [stg].[{table}];
>>>
```

### Tokens

- `{name}`: a variable or joiner. `{table}` is built in and holds the table name.
- `{name:upper}`: with a transform. Chain them: `{table:snake:upper}`.
  Transforms: `upper`, `lower`, `trim`, `pascal`, `camel`, `snake`, `kebab`, `title`.
- `{{` and `}}`: a literal `{` or `}`. Square brackets are plain text, so SQL like `[{schema}].[{table}]` just works.

A name is looked up in this order: the override on the table line, the layer's variables, `{table}` and `{layer}`, the global variables, then the joiners.
Joiners can use other joiners; a loop (`a` uses `b` uses `a`) is reported as an error.

### Soft errors

Problems never block you. The editor turns light red and the errors are listed under it;
table rows and buttons with problems are light red too (hover for details).

## Building locally

```
dotnet publish src/Paster/Paster.csproj -c Release -o publish
```

Requires the .NET 8 SDK. No NuGet packages are used.
