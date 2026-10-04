# Data Compare (XrmToolBox tool by Myscotek)

<img src="images/icon-80.png" alt="" width="80" align="right" />

Data Compare is an [XrmToolBox](https://www.xrmtoolbox.com) tool that checks a data migration. It reads
every record of a view in the environment the data came from (the **primary**, your XrmToolBox
connection) and in the environment it was migrated to (the **secondary**), matches the records on their
GUID, and shows each one coloured:

| Colour | Status | Meaning |
|---|---|---|
| Red | **Missing** | In the primary, not in the secondary at all. |
| Amber | **Different** | In both, but at least one compared column differs. |
| Green | **Matching** | In both, identical. |
| Blue | **Extra** | In the secondary's view, not in the primary at all. |

Select a record to see every column side by side, the differences highlighted. Data Compare is
read-only: it never writes to either environment. Dataverse online and Dynamics 365 on-premises 9.x.

## Installing

In XrmToolBox open the **Tool Library**, search for **Data Compare** and install it.

## Using the tool

1. Connect XrmToolBox to the **primary** environment (where the data was migrated from) and open Data
   Compare. Its entities are listed on the left (type in the filter box to find one by display or logical
   name). Without a connection, the buttons that need one open XrmToolBox's connection dialog.
2. Press **Select secondary environment...** in the toolbar and pick the environment the data was migrated
   to. XrmToolBox adds it as an additional connection: the tab keeps the primary as its own connection. The
   toolbar shows both names (`Primary: ...`, `Secondary: ...`). If you pick the same organisation twice the
   tool asks before comparing it with itself (not when the entity is mapped to another table of it - see
   [Entity mappings](#entity-mappings-data-migrated-into-another-table)).
3. Pick an entity, then one of its views below the entity list: the system views first, then your personal
   views (marked `(personal)`); `(All records)` when the entity has none. The view decides which records are
   compared and which columns the grid shows.
4. Press **Compare** (or double-click the view). Every page of the view is read from both environments
   (5000 records a page), then the records each view did not return are looked up by id in the other
   environment. The progress is shown next to the buttons and in the log; **Cancel** stops the run and shows
   what was already decided - the summary then says how many records were **Not checked**.
5. Read the result:
   - The **summary strip** counts the records each view returned and the rows of each status, on the
     status colours. `Secondary view failed` (hover it for the error) means the view's query failed in the
     secondary - for instance because it uses a column the secondary does not have: every primary record was
     then looked up by id instead, and extra records may be missed. It starts with `→ new_account` when the
     entity is mapped to another table, and `Prefixes: contoso_, new_` when a prefix filter is set.
   - The **Status** list and the **Filter rows...** box narrow the grid (the text is searched in every
     visible cell, not case-sensitive); `X of Y rows` says how many are shown. Click a column header to sort.
   - The **Differing column** list shows only the rows where one column differs - for instance `Status
     (statecode) · 12`: the 12 rows whose status is not the same in both environments. It lists every column
     that differs in at least one row (by display name, with its row count), is filled again when the compare
     options change, and works together with the Status list and the text box. Missing and Extra rows have no
     column comparison, so they are not shown while a column is chosen; `(any column)` shows every row again.
   - **Select a row** to see all its columns below the grid: the primary key first, then every column by
     display name, the primary's value next to the secondary's. A compared column that differs is **amber**;
     a column that is not compared is **grey** - the key, the ignored columns, columns outside the prefix
     filter, columns derived from another one, columns the primary's metadata does not have and, for a mapped
     entity, columns without a counterpart in the secondary table (hover the column name for its logical name
     and the reason). For a mapped entity a column read from a differently named secondary column shows both
     names (`Account Number → new_accountnumber`, `accountid → new_accountid` for the key). Tick
     **Differences only** to see just the columns whose values differ.
   - **Search columns** (under the header) shows only the columns whose logical or display name contains the text
     (not case-sensitive, together with Differences only); it is kept when you select another row, `X of Y columns`
     says how many are shown and Escape clears it.
   - The header above the columns names the record and its status, and says when the record was found by id
     outside one of the views (for instance a different status in the secondary: the view filtered it out).
6. The log can be copied, saved or cleared. Errors also go to XrmToolBox's own log.

## Compare options: ignored attributes and the prefix filter

Press **Compare options...** (next to Compare) to edit two lists. Both are saved, and when a result is shown it
is **re-evaluated at once** - nothing is read again, only the Matching/Different statuses, the summary and the
detail pane change. The columns they leave out are still shown in the detail pane, greyed with their values.

- **Ignored attributes.** Some columns always differ after a migration and are ignored by default: created
  on/by (and on behalf by), modified on/by (and on behalf by), overridden created on, owner, owning
  user/team/business unit, version number, import sequence number, the two time-zone bookkeeping columns and
  the record's **currency** (`transactioncurrencyid`: currency records are usually created afresh in each
  environment). One logical name a line (commas and spaces also separate names); **Restore defaults** brings
  that list back, and an empty list ignores nothing.
- **Only compare columns with these prefixes.** For instance `contoso_` and `new_` (one a line, or
  comma-separated) to check only your solution's columns: every other column is shown greyed (`Outside the
  prefix filter`). Blank - the default - compares the columns of every prefix. The prefixes are not
  case-sensitive, and the ignored list still applies to the columns they keep.

## Entity mappings (data migrated into another table)

When the data of a table was migrated into a **differently named table** of the secondary (for instance
`account` into `new_account`), map them: press **Entity mappings...**, **Add**, choose the **Primary entity**
(the tool's entities, or type a logical name) and the **Secondary entity** (the secondary's entities when it
is connected, or type a logical name), and press **OK**. The mappings are one list for every pair of
environments, saved with the settings, and apply from the next **Compare**: the summary strip shows
`→ new_account` while the selected entity is mapped.

- Records are still matched on their **GUID**: the secondary table's own primary key (e.g. `new_accountid`)
  is read from the secondary's metadata.
- Columns match by **identical logical name**. For the columns that were renamed, select the mapping and press
  **Columns...**: pick (or type) a primary column and its secondary column, **Add** (adding a primary column
  again replaces its pair), **Remove**, **OK**.
- A primary column whose counterpart - paired or same-named - the secondary table does not have is shown in
  the detail pane but **not compared** (`No counterpart in the secondary`); columns only the secondary table
  has are listed as `Only in the secondary`. When a column's type differs between the two tables (a lookup
  against a text column, say) it is still compared - the values usually differ then - and the detail pane
  notes `Type differs`; text against multi-line text, or the lookup kinds, do not count as different types.
- The **view is rewritten for the secondary table**: the entity name, and the main entity's column names in
  conditions, orders and links (with the column pairs; other names stay as they are). If that query fails -
  the view uses a column the secondary table does not have, for instance - the run goes on: every primary
  record is looked up by id (so Missing and Different are complete), but extra records cannot be found
  (`Secondary view failed`). An extra row shows the secondary record's values under the view's columns
  (blank for a column without a counterpart).
- Changing the mapping of the entity whose result is shown does not change that result (the records were read
  with the old mapping): the summary strip says `press Compare to apply` and the log says the same.

## How records are compared

- Records are matched on their primary key (the GUID), so the migration must have kept the GUIDs.
- **Every column of the entity** is compared, not just the view's columns, using the primary environment's
  metadata (except the ignored ones).
- A record the secondary's view does not return is looked up there by its id before it is called
  missing: it may exist but fall outside the view's filter (for instance a different status) - then it
  is compared like any other and usually shows as Different. Extra records are checked the same way in the
  primary, so blue really means "not in the primary at all".
- An empty value and a missing column are the same thing. Lookups are compared by the record they point
  at (not its name), option sets by value, dates as UTC, money by amount, text exactly (case and spaces
  count), multi-select option sets as sets, party lists (To, Cc...) by their parties. When two different
  values look the same on screen, the detail pane adds their raw values (`AB-1  ["AB-1 " (5 chars)]`).

## Limitations

- **Derived columns** - base-currency amounts (`*_base`), lookup names, image URLs and time stamps, rollup
  dates and states - are shown but never compared: the platform computes them from a column that is compared,
  and they differ between environments by design.
- **File columns** are compared by presence only (both have a file, or neither): their value is a file id
  that differs between environments by design. The file contents are not compared.
- **Columns only the secondary has** are shown in the detail pane but never compared: the primary's metadata
  decides what is compared. Without a mapping, columns the secondary lacks count as empty there (with a
  mapping they are not compared, see above).
- **Entity mappings** rename only the main entity's own columns in the view (and those of a link back to the
  same table); other link-entities are passed unchanged, so a linked table that was renamed too makes the
  secondary's view fail (the run then falls back to lookups by id). Same-named columns are compared even when a
  pair maps another primary column onto them. A mapping cannot be applied to a result already shown.
- **Distinct views** (`distinct="true"`) are read by page number without a paging cookie, because Dataverse's
  paging cookies are not reliable for them, and ordered by the primary key so the pages are stable. On a very
  large view that is slower, and records created or changed while the view is read can be missed or repeated.
  A view whose link-entity repeats a record (a 1:N link without distinct) shows each record once; the repeats
  are counted in the log.
- Linked-entity columns of the view (`Email (Primary Contact)`) are shown in the grid (from the primary's
  record; for an extra row, the secondary's) but never compared; compare the linked entity itself with one of
  its own views.
- Aggregate views cannot be compared.
- The records of both environments are held in memory with all their columns: views of a few hundred
  thousand records are fine, millions are not.

## Settings

The ignored attributes, the prefix filter, the entity mappings, Differences only and the entity selected last
(per primary organisation) are saved in `%AppData%\MscrmTools\XrmToolBox\Settings\MyscotekDataCompare.xml`. The page size (`PageSize`, 1-5000,
default 5000) can only be changed in that file - close XrmToolBox first, as the tool rewrites it on closing.

## Building from source

```
dotnet build MyscotekDataCompare.sln -c Release
dotnet test  MyscotekDataCompare.sln -c Release
.\build-package.ps1
```
Requires the .NET SDK (any recent one builds the net48 projects) on Windows. To try a build, close XrmToolBox
and copy `src\MyscotekDataCompare\bin\Release\MyscotekDataCompare.dll` (only that file) to
`%AppData%\MscrmTools\XrmToolBox\Plugins\`. See `SPEC.md` for the design.

## Licence

MIT - see [LICENSE](LICENSE). Copyright (c) 2026 Myscotek.
