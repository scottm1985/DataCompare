# Data Compare — design specification

XrmToolBox plugin that compares the records of a view between a **PRIMARY** Dataverse / Dynamics 365
environment (the one the data was migrated FROM; cloud or on-premises 9.x) and a **SECONDARY** environment
(the one it was migrated TO), to find the rows a migration missed and the columns that do not match.
Records are matched on their primary-key GUID. The tool is **read-only**: it never writes to either environment.

This document is the contract for the code. Where it is silent, follow the conventions of the existing
code and of Data Copier (`C:\Claude\XRMDataCopy`, the template this tool was scaffolded from). Do not widen
the scope.

The tool is published as **Data Compare** by **Myscotek**: XrmToolBox Tool Library package
`Myscotek.DataCompare`, source https://github.com/scottm1985/DataCompare, MIT licence. The assembly
(`MyscotekDataCompare.dll`), its namespaces and its settings file keep the `MyscotekDataCompare` name.

Status: phase 1 (scaffold, Core engine, tests) and phase 2 (the UI of sections 6 and 7) done - version 1.2026.10.1,
not released. Phase 3 (2026-10-04, version 1.2026.10.2): entity and column mappings (5.9, 6.6) and the prefix
filter (5.5, 6.5). Where phase 2 had to settle what this document left open, see 6.4; phase 3, 6.7. Still in
1.2026.10.2: the detail pane's column search (6.8) and the results grid's differing-column filter (6.9).

## 1. Decisions taken by the owner — do not change

| Topic | Decision |
|---|---|
| What is compared to decide a row's status | **Every attribute of the entity** (per the PRIMARY metadata), not just the view's columns. The grid still shows the VIEW's columns. |
| How extra records are found | The **same view query is run against the secondary** too; its rows the primary does not have are extras. |
| Ignored attributes | Default list (editable, persisted by the UI): `createdon, createdby, createdonbehalfby, modifiedon, modifiedby, modifiedonbehalfby, overriddencreatedon, versionnumber, ownerid, owninguser, owningteam, owningbusinessunit, timezoneruleversionnumber, utcconversiontimezonecode, importsequencenumber, transactioncurrencyid` (`transactioncurrencyid` added by the owner on 2026-10-02: currency records are usually created afresh in each environment, so the lookup differs by design). Ignored attributes are still **shown** in the detail pane (greyed) with their values; a difference in them never makes a row Different. |
| Loading | **All pages** of the view on both sides (5000 records a page), with progress and cancellation. |
| Row colours | **RED** `#F8D7DA` = Missing: in the primary, not in the secondary at all. **AMBER** `#FFF3CD` = Different: in both, at least one compared column differs. **GREEN** `#D4EDDA` = Match: in both, identical. **BLUE** `#CCE5FF` = Extra: in the secondary's view, not in the primary at all. Dark (default) text on all four. |
| Entity mappings (2026-10-04) | For data migrated into a **differently named table**: ONE **global** list of mappings in the settings (not per environment pair), `EntityMapping { PrimaryEntity, SecondaryEntity, Columns: ColumnMapping { PrimaryColumn, SecondaryColumn } }`. A mapped entity is read in the secondary from `SecondaryEntity`; rows still match on the **GUID**, the secondary's primary id attribute coming from the **secondary metadata**. Columns match by **identical logical name**; the column pairs override for the columns that differ. A primary column whose counterpart (paired or same-named) the SECONDARY metadata does not have is shown in the detail pane, **not compared** ("No counterpart in the secondary"); secondary-only columns are listed, not compared. Types come from the PRIMARY metadata; a secondary type of another family is compared by the usual value rules and noted "Type differs". |
| Extras of a mapped entity | The view's FetchXML is **rewritten for the secondary** (entity name; the main entity's attribute names in conditions, orders and link `to`/`from` through the column pairs; unmapped names unchanged) and run there. If it fails: a WARNING, extras skipped (`SecondaryQueryFailed`), every primary row still checked by id so Missing/Different are complete. Extra rows show the secondary values under the view's primary columns (reverse mapping; blank without a counterpart). |
| Prefix filter (2026-10-04) | `CompareOptions.ComparedPrefixes`: when non-empty only the primary attributes whose logical name starts with one of them (case-insensitive) are compared; the others are shown greyed ("Outside the prefix filter"). **Empty = every column (the default).** The ignored list applies on top. |

Decisions taken in phase 1 (inside the owner's frame; flag to the owner, do not silently change):
* A secondary view row the primary's view did not return is looked up **by id in the primary** before it is
  called Extra (5.4 step 4), symmetric to the Missing check. Found there, it is compared (usually Different);
  only a record that does not exist in the primary at all is BLUE.
* **Derived attributes** (metadata `AttributeOf` set: `*_base` money, lookup `*name`/`*yominame`/`*type`
  columns, `entityimage_url`/`entityimage_timestamp`, rollup `*_date`/`*_state`) are shown but never compared:
  the platform computes them from an attribute that IS compared, and they differ between environments by design.
* **File columns** are compared by presence only (both have a file, or neither): the value is a file id
  that differs between environments by design.
* An empty string, an empty multi-select and an empty party list count as null.

## 2. Conventions

* Branding and plugin metadata: Data Copier's logo constants (`PluginImages`, copied unchanged) and
  `[ExportMetadata]` colours (`#2868B1`, `White`, `WhiteSmoke`).
* Connections: the primary is the tool's normal XrmToolBox connection; the secondary is a second connection
  requested with `RaiseRequestConnectionEvent` as XrmToolBox's additional organisation and caught in the
  `UpdateConnection` override (6.1) — exactly as Data Copier's destination.
* A colour-per-level RichTextBox log; settings persisted with `SettingsManager.Instance.TryLoad/Save`.
* csproj with the host DLLs excluded from the output (`ExcludeAssets="runtime"`); an xUnit net48 test project.
* **Core has NO WinForms / XrmToolBox references** (only the Dataverse SDK); the UI is a thin layer over it.

Known gotchas (also in CLAUDE.md):
* Pin `XrmToolBoxPackage 1.2025.10.74` **and** `MscrmTools.Xrm.Connection 1.2025.9.64` (else CS1705/MSB3277);
  `Microsoft.CrmSdk.CoreAssemblies 9.0.2.60`; all three `ExcludeAssets="runtime"`. Only `MyscotekDataCompare.dll` is deployed.
* All seven `ExportMetadata` keys are mandatory or MEF silently drops the plugin. `AssemblyCompany` is mandatory.
* `AssemblyInfo.cs` is hand-written and carries the release version (date style `1.YYYY.M.N`, N from 1) as
  `AssemblyVersion`, `AssemblyFileVersion` and `AssemblyInformationalVersion`, equal to the nuspec `<version>`.
* `MyscotekDataCompare.Core.CompareOptions` collides with `System.Globalization.CompareOptions`: a file that
  imports both namespaces needs an alias. `Microsoft.Xrm.Sdk.Label` collides with `System.Windows.Forms.Label`;
  `Microsoft.Xrm.Sdk.Metadata.ViewColumn` with `Core.Services.ViewColumn`.
* No .resx / designer files: build the UI in code. net48 has no `IsExternalInit`: no records or `init`.

## 3. Project layout

```
(repository root)
  MyscotekDataCompare.sln          classic .sln (SDK 10: dotnet new sln --format sln)
  .gitignore  LICENSE (MIT, Copyright (c) 2026 Myscotek)  README.md  CLAUDE.md  SPEC.md
  Myscotek.DataCompare.nuspec      Tool Library package: the DLL under lib\net48\Plugins, nothing else (iconUrl, never <icon>)
  build-package.ps1                build, test, version check, nuget pack into dist\, package content check
  images\                          icon-32.png, icon-80.png, icon-128.png (Data Copier's, = PluginImages)
  src\MyscotekDataCompare\
    MyscotekDataCompare.csproj     net48, UseWindowsForms, LangVersion 10.0, Deterministic, output bin\Release\
    Properties\AssemblyInfo.cs     Title/Product "Data Compare", Company "Myscotek", the release version
    MyscotekDataComparePlugin.cs   Export + PluginImages
    Core\                          (no WinForms / XrmToolBox references)
      CompareEngine.cs             5.4: the run (steps, paging, lookups by id, cancellation, progress, log)
      CompareOptions.cs  CompareProgress.cs (+ ComparePhase)  CompareSummary.cs  CompareResult.cs
      EntityMapping.cs             5.9: EntityMapping, ColumnMapping (settings) and EntityMap (resolved for a run)
      RowComparison.cs (+ RowStatus)  ValueComparer.cs (+ internal ComparePlan)  DetailBuilder.cs (+ ColumnComparison)
      ICompareLogger.cs  LogLevel.cs
      Schema\  EntitySchema, AttributeSchema, ISchemaProvider, DataverseSchemaProvider
      Services\ EntityCatalog, ViewService, LayoutParser, RecordPager, CellFormatter, FetchXmlHelper (+ CompareQuery,
                SecondaryQuery), ColumnHeaderResolver
    UI\
      DataCompareControl.cs        the tool (PluginControlBase, IGitHubPlugin, IHelpPlugin): connections, operations, result, filters, detail pane
      DataCompareControl.Layout.cs BuildUi() and FitLayout (code-built, every control Named)
      DataCompareSettings.cs       section 7 (+ LastEntity)
      CompareOptionsForm.cs        the Compare options... dialog (6.5: ignored attributes, prefix filter)
      EntityMappingsForm.cs        the Entity mappings... dialog (6.6) + NameChoice
      ColumnMappingsForm.cs        its Columns... dialog (6.6)
      UiLogger.cs                  Data Copier's, as an ICompareLogger
  tests\MyscotekDataCompare.Tests\ xUnit 2.9.2, net48, UseWindowsForms
```

Namespaces: `MyscotekDataCompare`, `MyscotekDataCompare.Core`, `MyscotekDataCompare.Core.Schema`,
`MyscotekDataCompare.Core.Services`, `MyscotekDataCompare.UI`.

## 4. Plugin entry

```csharp
[Export(typeof(IXrmToolBoxPlugin))]
[ExportMetadata("Name", "Data Compare")]
[ExportMetadata("Description", "Compares the records of a view between two Dataverse / Dynamics 365 environments after a migration, listing the records missing from the second environment, the extra ones and the ones whose columns differ.")]
[ExportMetadata("BackgroundColor", "#2868B1")]
[ExportMetadata("PrimaryFontColor", "White")]
[ExportMetadata("SecondaryFontColor", "WhiteSmoke")]
[ExportMetadata("SmallImageBase64", PluginImages.Small)]
[ExportMetadata("BigImageBase64", PluginImages.Big)]
public class MyscotekDataComparePlugin : PluginBase
{
    public override IXrmToolBoxPluginControl GetControl() => new UI.DataCompareControl();
}
```
`PluginImages` is Data Copier's (Myscotek logo, `Small` 32x32, `Big` 120x120), never edited by hand. The
description is one sentence (one full stop) and equals `AssemblyDescription`. The control implements
`IGitHubPlugin` (`UserName` "scottm1985", `RepositoryName` "DataCompare") and `IHelpPlugin`
(`HelpUrl` "https://github.com/scottm1985/DataCompare#readme"); constants `DialogTitle` "Data Compare",
`SecondaryActionName` "AdditionalOrganization", `SecondaryParameter` "secondary" (already in the placeholder).

## 5. Core (`MyscotekDataCompare.Core`)

### 5.1 Public API (exact)

```csharp
public enum LogLevel { Info, Success, Warning, Error }
public interface ICompareLogger { void Log(LogLevel level, string message); }   // called on the engine's thread

public sealed class CompareOptions
{
    public const int DefaultPageSize = 5000, MaxPageSize = 5000, DefaultLookupBatchSize = 500, MaxLookupBatchSize = 2000;
    public static readonly IReadOnlyList<string> DefaultIgnoredAttributes;   // section 1 (16 names, incl. transactioncurrencyid)
    public static string DefaultIgnoredAttributeList { get; }               // the same, comma-separated (settings default)
    public HashSet<string> IgnoredAttributes { get; }                        // OrdinalIgnoreCase; defaults to the list above
    public int PageSize { get; set; } = 5000;                                // 1..5000
    public int LookupBatchSize { get; set; } = 500;                          // 1..2000 (ids per In query)
    public List<string> ComparedPrefixes { get; }                            // 5.5 prefix filter; empty (default) = every column
    public List<EntityMapping> EntityMappings { get; }                       // 5.9; applied by the engine only
    public bool IsIgnored(string attribute);
    public bool HasPrefixFilter { get; }  public bool IsInPrefixFilter(string attribute);   // case-insensitive StartsWith
    public void SetComparedPrefixes(string list);   public static string FormatPrefixList(IEnumerable<string> prefixes);
    public EntityMapping FindMapping(string primaryEntity);                 // first COMPLETE mapping of the entity, else null
    public void SetIgnoredAttributes(string list);   // comma/semicolon/white-space list; NULL = defaults, blank = ignore nothing
    public static IList<string> ParseAttributeList(string list);           // trimmed, lower-case, distinct, first-seen order
    public static string FormatAttributeList(IEnumerable<string> names);   // "a,b,c" sorted, lower-case
    public CompareOptions Clone();                                          // deep: ignored list, prefixes, mappings
}

public enum ComparePhase { LoadingPrimary, LoadingSecondary, CheckingMissing, CheckingExtras, Comparing, Done }
public sealed class CompareProgress   // a new instance per report
{
    public ComparePhase Phase { get; set; }
    public int PageNumber { get; set; }     // loading: page just read; checking: batch just run; else 0
    public int RecordsSoFar { get; set; }   // loading: records read on that side; checking: ids looked up; comparing/done: rows classified
    public int? Total { get; set; }         // known for checking/comparing/done; null while loading
    public string Message { get; set; }     // e.g. "Primary: page 3, 15000 records", "Done: 120 rows - matching 100, ..."
}

public enum RowStatus { Match, Different, Missing, Extra }
public sealed class RowComparison
{
    public RowComparison(Guid id, RowStatus status, Entity primary, Entity secondary, IReadOnlyList<string> differingAttributes,
                         bool inPrimaryView, bool inSecondaryView);
    public Guid Id { get; }
    public RowStatus Status { get; }
    public Entity Primary { get; }                          // null for Extra; ALL attributes (+ the view's linked columns when InPrimaryView)
    public Entity Secondary { get; }                        // null for Missing
    public IReadOnlyList<string> DifferingAttributes { get; }   // compared attributes that differ, ordinal order; empty unless Different
    public bool InPrimaryView { get; }                      // false: found in the primary by id only (5.4 step 4)
    public bool InSecondaryView { get; }                    // false: found in the secondary by id (step 3), or Missing
    public Entity DisplayRecord { get; }                    // the record the grid shows: Primary if InPrimaryView, else Secondary
}

public sealed class CompareSummary
{
    public int PrimaryCount, SecondaryCount;              // distinct records each view returned
    public int Matching, Different, Missing, Extra;       // the rows by status
    public int Unchecked;                                 // rows a cancelled run could not decide (not in Rows); 0 when complete
    public int FoundByIdInSecondary, FoundByIdInPrimary;  // steps 3 and 4: found outside the other side's view
    public int DuplicateRowsIgnored;                      // rows repeating an id (1:N link-entity without distinct)
    public bool SecondaryQueryFailed; public string SecondaryQueryError;   // 5.4 step 2 failure (not fatal)
    public bool Cancelled; public TimeSpan Elapsed;
    public int Total { get; }                             // Matching + Different + Missing + Extra (= Rows.Count)
}   // (all are get/set properties)

public sealed class CompareResult
{
    public CompareResult(EntitySchema schema, CompareOptions options, List<RowComparison> rows, CompareSummary summary,
                         IReadOnlyDictionary<string, string> columnAliases = null, EntityMap mapping = null);   // public for UI tests; options cloned
    public string EntityLogicalName { get; }   public string PrimaryIdAttribute { get; }
    public EntitySchema Schema { get; }        // the PRIMARY metadata
    public CompareOptions Options { get; }     // a copy of the options the statuses were decided with
    public List<RowComparison> Rows { get; }   // primary view order, then rows only the secondary's view returned, in its order
    public CompareSummary Summary { get; }
    public IReadOnlyDictionary<string, string> ColumnAliases { get; }   // main-entity attribute aliases of the view
    public EntityMap Mapping { get; }                            // 5.9: the mapping the rows were read with; null = same entity
    public string SecondaryEntityLogicalName { get; }  public string SecondaryPrimaryIdAttribute { get; }
    public string CellText(RowComparison row, string column);   // grid cell: CellFormatter.Format(row.DisplayRecord, column)
                                                                 // (a mapped secondary record: under the column's counterpart)
    public IList<ColumnComparison> GetDetails(RowComparison row); // detail pane lines (5.8)
    public CompareResult Reevaluate(CompareOptions options);     // re-decide Match/Different in memory with other ignored
                                                                 // attributes / prefixes; keeps Mapping (mappings never re-apply)
    public IEnumerable<RowComparison> RowsWithStatus(RowStatus status);
}

public sealed class ColumnComparison
{
    public string LogicalName, DisplayName;       // display name from the primary metadata, else the logical name
    public string SecondaryLogicalName;           // the secondary column read: = LogicalName unless mapped (5.9); null = no counterpart
    public AttributeTypeCode? AttributeType;      // null when the primary metadata does not have the column
    public object PrimaryRaw, SecondaryRaw;       // raw values (null = none)
    public string PrimaryText, SecondaryText;     // CellFormatter.FormatDetail (+ raw values when they read the same, 5.8)
    public bool IsDifferent;                      // the values differ under 5.6 (whether or not compared)
    public bool IsIgnored;                        // in the ignored list (grey)
    public bool IsCompared;                       // takes part in the status (5.5)
    public bool IsPrimaryKey;
    public bool IsMismatch { get; }               // IsCompared && IsDifferent (amber)
    public string Note;                           // "Primary key" | "Ignored" | "Not in the primary metadata" | "Only in the secondary" |
                                                  // "Derived from {attr}" | "Outside the prefix filter" | "No counterpart in the secondary" |
                                                  // "Type differs: ..." (a compared line, 5.9) | null
}   // (all are get/set properties)
public static class DetailBuilder
{
    public const string PrimaryKeyNote = "Primary key", IgnoredNote = "Ignored", DerivedNotePrefix = "Derived from ",
                        NotInMetadataNote = "Not in the primary metadata", OutsidePrefixFilterNote = "Outside the prefix filter",
                        NoCounterpartNote = "No counterpart in the secondary", SecondaryOnlyNote = "Only in the secondary",
                        TypeDiffersNotePrefix = "Type differs: ";
    public static IList<ColumnComparison> Build(RowComparison row, EntitySchema schema, CompareOptions options);   // null options = defaults
    public static IList<ColumnComparison> Build(RowComparison row, EntitySchema schema, CompareOptions options, EntityMap map);
}

public static class ValueComparer   // 5.6
{
    public static bool AreEqual(object primary, object secondary);
    public static bool AreEqual(object primary, object secondary, AttributeSchema attribute);   // + file columns by presence
    public static bool IsEmpty(object value);
    public static string RawText(object value);   // invariant raw rendering, e.g. "account {guid}", "2024-03-01 14:30:05.0000000 UTC", "\"x \" (2 chars)"
}

public sealed class CompareEngine
{
    public const int ProgressEveryRows = 1000;
    public CompareEngine(IOrganizationService primary, IOrganizationService secondary, ISchemaProvider primarySchema,
                         CompareOptions options /* null = defaults; cloned per run */, ICompareLogger logger /* may be null */);
    public CompareEngine(IOrganizationService primary, IOrganizationService secondary, ISchemaProvider primarySchema,
                         ISchemaProvider secondarySchema /* 5.9, mapped entities only; null = DataverseSchemaProvider(secondary) */,
                         CompareOptions options, ICompareLogger logger);
    // Never throws for cancellation (returns a partial result, Summary.Cancelled = true). Throws, after logging
    // "Compare failed: ...", for: blank arguments (ArgumentException), out-of-range options (ArgumentOutOfRangeException),
    // the entity missing from the primary metadata or a mapped table missing from the secondary's (InvalidOperationException),
    // FetchXML that is invalid or for another
    // entity (ArgumentException), an aggregate view (NotSupportedException), a failing primary view query or by-id lookup.
    public CompareResult Compare(string entityLogicalName, string viewFetchXml, IProgress<CompareProgress> progress /* may be null */,
                                 CancellationToken cancellationToken);
}
```

`CompareEngine.Compare` is synchronous and long-running: the UI runs it inside `Task.Run`. One engine may run
several comparisons one after the other; it keeps no state between runs.

### 5.2 Schema and services (copied from Data Copier, trimmed)

* `EntitySchema { LogicalName, PrimaryIdAttribute, PrimaryNameAttribute, DisplayName, DisplayCollectionName, IsIntersect,
  IsPrivate, IsVirtual, IReadOnlyDictionary<string, AttributeSchema> Attributes (OrdinalIgnoreCase); AttributeSchema Attribute(name) }`.
  No relationships (Data Compare does not need them; the metadata request is lighter).
* `AttributeSchema { LogicalName, DisplayName, AttributeType, IsValidForRead (null metadata = true), IsValidForCreate,
  IsValidForUpdate, AttributeOf, SourceType, IsFile, IsMultiSelect, IsImage, LookupTargets }`.
* `ISchemaProvider.GetEntity(name)` (null when the entity does not exist); `DataverseSchemaProvider(service)`:
  `RetrieveEntityRequest(Entity | Attributes)`, thread-safe cache, caches "missing" too; `FromMetadata`, `IsVirtualTable`.
* `EntityCatalog.GetEntities(service)` → `IList<EntityInfo> { LogicalName, DisplayName, SchemaName, ObjectTypeCode,
  PrimaryIdAttribute, PrimaryNameAttribute, IsActivity, IsVirtual }`, intersect and private entities excluded, by display name.
* `ViewService.GetViews(service, entity, includePersonal, ICompareLogger logger)` → system views (`savedquery`,
  querytype 0, active, by name) then personal views (`userquery`, same filters); a failing query is a logged warning
  and never hides the other list. `ViewInfo { Id, Name, IsPersonal, FetchXml, LayoutXml, DisplayName ("Name (personal)") }`.
  `ViewService.CreateAllRecordsView(entity, primaryId, primaryName)` → the synthetic `(All records)` view.
* `LayoutParser.Parse(layoutXml)` → `IList<ViewColumn { Name, Width }>` (`alias.attribute` for linked columns).
* `ColumnHeaderResolver.Resolve(fetchXml, entity, columns, schemaProvider)` → grid headers ("Email (Primary Contact)").
  Uses metadata of linked entities too: call it off the UI thread with the PRIMARY `DataverseSchemaProvider`.
* `CellFormatter.Format(entity, column)` (grid text: FormattedValues first, then by type) and
  `CellFormatter.FormatDetail(entity, column)` (5.8).
* `FetchXmlHelper.ApplyPaging`, `EnsureAttribute` (as Data Copier), `PrepareCompareQuery` (5.3) and `MapToSecondary` (5.9);
  `RecordPager.Fetch(service, fetchXml, page, pageSize, cookie)` → `RecordPage { Entities, MoreRecords, PagingCookie }`.

### 5.3 The query (`FetchXmlHelper.PrepareCompareQuery(fetchXml, primaryIdAttribute)` → `CompareQuery`)

* The main `<entity>`'s `<attribute>` elements (and any `<all-attributes>`) are replaced by ONE `<all-attributes/>`
  (first child): the comparison covers every attribute, and the primary key is always returned. Aliases the view
  gave main-entity attributes are kept in `MainEntityAliases` (alias → attribute) so `CellText` can still show
  such a column.
* Filters, orders and link-entities — with their own attributes, which feed the grid's linked columns
  (`alias.attribute`, AliasedValues) — are kept unchanged.
* `count`, `page`, `paging-cookie`, `top` and `returntotalrecordcount` are removed from `<fetch>`; everything else
  (`version`, `mapping`, `no-lock`, `distinct`, `output-format`...) is kept. The engine pages the query itself.
* `distinct="true"` (or `1`): Dataverse paging cookies are not reliable with distinct queries, so such a view is
  paged **by page number without a paging cookie** (`IsDistinct`), and it gets an `<order attribute="{primary key}"/>`
  after its own orders (unless it already orders by the key) so the pages are stable. Non-distinct views are not
  given an order: the paging cookie already orders by the key.
* `aggregate="true"` → `NotSupportedException` (its rows are totals, not records). Invalid XML, a root that is not
  `<fetch>` or no `<entity name>` → `ArgumentException`.

### 5.4 The run (`CompareEngine.Compare`)

0. Options are cloned and validated (`PageSize` 1..5000, `LookupBatchSize` 1..2000). The PRIMARY metadata of the
   entity is read (`InvalidOperationException` when missing); the view is rewritten (5.3) and must be for that
   entity. A mapped entity (5.9) also reads the secondary table's metadata and rewrites the view for it. Log header:
   `Comparing {DisplayName} ({entity}) records matched on {primaryId}: {n} attributes compared, {m} ignored ({list})[, {p}
   outside the prefix filter ({prefixes})][, {c} without a counterpart in {table}], {d} derived attributes not compared.`
1. **Load the primary** (`LoadingPrimary`): page the query with `RecordPager.Fetch` (count = `PageSize`, the paging
   cookie of the previous page; none for a distinct view), until `MoreRecords` is false. Records are kept by id in
   read order. A row whose id was already read (a 1:N link-entity without distinct) is ignored and counted in
   `DuplicateRowsIgnored` (Warning); a row without an id is ignored (Warning). When the server returns no paging
   cookie the next pages are read by page number (Info, once); an empty page with `MoreRecords` stops the reading
   (Warning). Logs every page (`Primary: page 3: 5000 rows (15000 records so far).`) and the total with the time. A
   failure here is fatal (logged, thrown).
2. **Load the secondary** (`LoadingSecondary`): the SAME rewritten query, the same way. If it fails (e.g. the view
   uses a column or table the secondary lacks), the run goes on: `SecondaryQueryFailed` / `SecondaryQueryError`,
   Warning `Secondary: the view query failed (...)`; the rows read before the failure are kept, every other primary
   row goes through step 3, and extras the secondary's view would have returned may be missed (the result line is
   then a Warning, not a Success).
3. **Check the missing** (`CheckingMissing`): the primary ids the secondary's view did not return are retrieved from
   the SECONDARY by primary key — `QueryExpression(entity) { ColumnSet(true) }` with ONE condition
   `{primaryId} In (ids)`, `LookupBatchSize` (500) ids a query (a mapped entity: the secondary table and its own key). Found → compared in step 5 (`InSecondaryView` false;
   usually Different, since the view's filter excluded it, but Match when only linked data kept it out). Not found →
   **Missing**. A failure here is fatal (a secondary without the entity, for instance).
4. **Check the extras** (`CheckingExtras`): symmetric — the secondary ids the primary's view did not return are
   retrieved from the PRIMARY by id. Found → compared (`InPrimaryView` false). Not found → **Extra**.
5. **Compare** (`Comparing`): every pair (5.5, 5.6) → Match or Different with `DifferingAttributes`. Rows are built in a
   deterministic order: the primary view's rows in its order (Match / Different / Missing), then the rows only the
   secondary's view returned, in its order (Different / Match / Extra). Progress every `ProgressEveryRows` (1000) rows.
6. Summary log: `Result: {n} rows - matching a, different b, missing c, extra d[, not checked e].` (Success; Warning when
   cancelled or the secondary query failed), counts of both views and of the lookups, `Most frequent differences:
   name (12), telephone1 (3) ...` (up to 10), `Elapsed: 4.2 s.`; then `Done` progress.

**Cancellation** is checked before every page, before every lookup batch and every 1000 compared rows. A cancelled
run never throws: it returns the rows it could decide — pairs already matched (they are still compared, it is quick),
Missing/Extra rows whose lookup ran — with `Cancelled = true` and the rest counted in `Unchecked` (Warning
`Cancelled by user: partial result, N record(s) not checked.`). An `OperationCanceledException` thrown by a server call
while the token is cancelled counts as a cancellation too. Cancelling during step 5 stops it at the next 1000 rows.

**Invariants** of a complete run: `Matching + Different + Missing = PrimaryCount + FoundByIdInPrimary`;
`Matching + Different + Extra = SecondaryCount + FoundByIdInSecondary`; `Total = Rows.Count`; `Unchecked = 0`.

Nothing is ever written: only `RetrieveMultiple` (FetchExpression for the views, QueryExpression for the lookups),
plus the metadata request of the schema provider.

### 5.5 Which attributes are compared

Every attribute of the PRIMARY entity metadata that is valid for read, is not derived (`AttributeOf == null`), is not
the primary key, is not in the ignored list, passes the **prefix filter** (`ComparedPrefixes` empty, or the logical name
starts with one of them, case-insensitive) and - for a mapped entity (5.9) - has a counterpart in the secondary table's
metadata. Not compared: attributes the primary metadata does not have
(e.g. a column only the secondary has — shown in the detail pane), linked-entity values (keys with a `.`, or
`AliasedValue`s), derived and unreadable attributes. Calculated and rollup columns ARE compared. The same rule
(internal `ComparePlan`) serves the engine, `Reevaluate` and the detail pane, so a row is Different exactly when its
detail pane shows a mismatch.

### 5.6 Value rules (`ValueComparer.AreEqual`)

* **Null rule**: an absent attribute, a null value, an empty string, an empty `OptionSetValueCollection`, an empty
  `EntityCollection` and an empty `byte[]` are all equal. So an attribute present in primary metadata but never
  returned by the secondary (e.g. the column does not exist there) is a difference only when the primary value is
  non-null (and the other way round).
* `EntityReference`: same logical name (case-insensitive) and id; the name is ignored.
* `OptionSetValue`: same value. `OptionSetValueCollection`: same SET of values (order and repeats ignored).
* `Money`: same decimal value (`1.5 = 1.50`).
* `DateTime`: compared as UTC — a `Local` value is converted, `Utc` and `Unspecified` keep their ticks.
* `string`: ordinal, case-sensitive, no trimming. `byte[]`: byte by byte.
* `int`, `long`, `decimal`, `double`, `bool`, `Guid`: `Equals` of the same type (values of different types differ:
  that is a schema difference).
* `EntityCollection` (party lists): the same multiset of parties — a party is its `partyid` (logical name + id),
  else its unresolved `addressused` (ordinal), else the row itself; `activityparty` row ids and participation masks
  are ignored, the order too.
* An `AliasedValue` on either side is never compared (equal). Anything else: `Equals` of the same type.
* File columns (`AttributeSchema.IsFile`): presence only.

### 5.7 Result model

See 5.1. `CompareResult.CellText(row, column)` is the grid text of a view column: `CellFormatter.Format` of the row's
`DisplayRecord` (the primary record, or the secondary one for a row only the secondary's view returned — those records
carry the view's linked columns), FormattedValues first; a column named after a main-entity alias shows that attribute.
`Reevaluate(options)` returns a NEW result whose Match/Different rows are re-decided with other options (nothing is
read again; Missing/Extra rows, the order and the other counts are kept): the UI calls it when the user edits the
ignored attributes or the prefix filter after a run. The options' entity mappings are not applied: the new result keeps
`Mapping`, the one the rows were read with. Records are kept in memory with all their attributes (two per row): about 100 000
rows is fine, millions are not (out of scope).

### 5.8 Detail pane model (`DetailBuilder.Build` / `CompareResult.GetDetails`)

* Lines: the primary key FIRST (`IsPrimaryKey`, Note "Primary key", never different), then every other line ordered
  by display name (current culture, case-insensitive), then logical name. Listed: every attribute of the primary
  metadata valid for read (also when both sides are blank); a derived attribute only when either record has a value
  (Note `Derived from {attribute}`); any other attribute either record holds that the metadata does not know (Note
  "Not in the primary metadata", `AttributeType` null). Never linked-entity values. Unreadable attributes never. A
  mapped entity (5.9): each line reads the secondary under `SecondaryLogicalName` (the key line: the secondary's key),
  and a secondary attribute that is nobody's counterpart gets a line of its own (Note "Only in the secondary").
* Flags: `IsDifferent` (5.6, for every line), `IsIgnored`, `IsCompared` (5.5), `IsMismatch = IsCompared && IsDifferent`.
  Notes in priority order: "Primary key", "Ignored", "Not in the primary metadata" / "Only in the secondary",
  `Derived from {attribute}`, "Outside the prefix filter", "No counterpart in the secondary"; null for a compared line,
  except "Type differs: {primary type} in the primary, {secondary type} in the secondary" on a compared line of a mapped
  entity whose two types are of different families (5.9).
* Texts (`CellFormatter.FormatDetail`): a lookup is `Name (guid)` (the formatted name first; just the guid without a
  name) because lookups are compared by id; an option set `Label (value)` (just the value without a label); a
  multi-select `Label; Label (1, 3)`; a party list `Name (guid); address@x.com`; a byte array `(image, N bytes)`;
  everything else as the grid shows it (FormattedValues first: money with its currency, dates in the user's format,
  Yes/No). Null or absent: "". A Missing row has every secondary text "" (and an Extra row every primary text "").
* When two different values read the same (equal texts once white space is removed), both texts get the raw values
  appended: `01/03/2024 14:30 [2024-03-01 14:30:05.0000000 UTC]`, `AB-1  ["AB-1 " (5 chars)]`.

### 5.9 Entity mappings (phase 3)

```csharp
public sealed class ColumnMapping { public string PrimaryColumn, SecondaryColumn; Clone(); }          // get/set properties
public sealed class EntityMapping                                                                      // XmlSerializer-friendly
{
    public string PrimaryEntity, SecondaryEntity;  public List<ColumnMapping> Columns;
    public bool IsComplete();                 // both entity names set
    public bool AppliesTo(string primaryEntity);   // complete and for that entity (trimmed, case-insensitive)
    public IList<ColumnMapping> UsableColumns();   // both names set, trimmed + lower case, the first pair of each primary column
    public EntityMapping Clone(); public EntityMapping Normalized();   // Normalized: null when incomplete
    public static bool SameMapping(EntityMapping a, EntityMapping b); public static string Normalize(string name);
}
public sealed class EntityMap   // the mapping resolved for one run: CompareResult.Mapping
{
    public EntityMap(EntityMapping mapping, EntitySchema primarySchema, EntitySchema secondarySchema);
    public EntityMapping Mapping { get; }  public EntitySchema PrimarySchema, SecondarySchema { get; }
    public string PrimaryEntity, SecondaryEntity, PrimaryIdAttribute, SecondaryIdAttribute { get; }
    public IReadOnlyDictionary<string, string> ColumnPairs { get; }   public IReadOnlyCollection<string> LinkAliases { get; }
    public string SecondaryColumn(string primaryColumn);   // counterpart (key -> secondary key; pair; same name) or null when
                                                          // the SECONDARY metadata lacks it
    public string QueryColumn(string primaryColumn);       // the name in the secondary's query: key / pair / unchanged
    public AttributeSchema SecondaryAttribute(string primaryColumn);
    public static bool TypesDiffer(AttributeSchema primary, AttributeSchema secondary);
}
public static SecondaryQuery FetchXmlHelper.MapToSecondary(string fetchXml, string primaryEntity, string secondaryEntity,
                                                           Func<string, string> mapColumn);   // { FetchXml, LinkAliases }
```

* `CompareOptions.FindMapping(entity)` - the first complete mapping of the entity - turns the run into a mapped run.
  The engine reads the secondary table's metadata through its secondary `ISchemaProvider` (the UI passes a
  `DataverseSchemaProvider` over the secondary connection, cached per secondary; without one the engine creates it).
  A table the secondary does not have, or one without a primary id attribute, fails the run (`InvalidOperationException`).
  An unmapped run never reads the secondary metadata.
* **Primary key**: rows match on the GUID. Secondary records are identified by `Entity.Id`, else the secondary table's
  primary id attribute; the lookups by id (5.4 step 3) query the secondary table on its own key.
* **Columns**: each primary attribute is compared with `SecondaryColumn(name)`: the explicit pair's column when there is
  one, else the same logical name - in both cases only when the SECONDARY metadata has it; otherwise it has no
  counterpart and is not compared (5.5). Same-named columns are compared even when a pair also maps another primary
  column onto them (kept simple). Pairs whose secondary column the secondary lacks, or whose primary column the primary
  lacks, are logged as warnings. The log also lists the attributes without a counterpart.
* **Types**: the comparison uses the PRIMARY attribute's metadata and the value rules (5.6) as ever, so values of other
  .NET types simply differ. When the two attributes' type families differ - families: text (String, Memo), references
  (Lookup, Customer, Owner), option sets (Picklist, State, Status), multi-select, image, file, otherwise the type itself -
  the detail line notes `Type differs: {primary} in the primary, {secondary} in the secondary`.
* **The secondary's view query** (`MapToSecondary` over the prepared query, 5.3): the main `<entity name>` becomes the
  secondary table. In the main entity's scope - the `<entity>` and any `<link-entity>` on the primary entity itself (a
  parent of the same table, renamed too, its `from` mapped; their aliases are `LinkAliases`) - every attribute reference
  goes through `QueryColumn`: `<attribute name>`, `<condition attribute>` and `valueof` (a condition with `entityname`
  belongs to that alias's scope), `<order attribute>`, and the `to` of the scope's child link-entities. Unmapped names,
  other link-entities (`from`, attributes, filters), values and the fetch attributes pass through unchanged. The rewritten
  query is paged like the primary's. If it fails, step 2's existing fallback applies: Warning `Secondary: the view query
  rewritten for {table} failed (...)`, `SecondaryQueryFailed`, every primary row looked up by id, extras skipped.
* **Grid cells**: a row whose `DisplayRecord` is the secondary record (Extra, or found by id in the primary) shows each
  view column under its counterpart (`SecondaryColumn`; a main-entity alias first resolves to its attribute; a linked
  column of a `LinkAliases` link under `alias.QueryColumn(attribute)`; other linked columns unchanged); blank without one.
* **Re-evaluation** never changes the mapping (5.7): the UI says a new Compare is needed when the mapping of the entity
  whose result is shown changes (6.6).

## 6. UI (phase 2: `MyscotekDataCompare.UI.DataCompareControl : PluginControlBase, IGitHubPlugin, IHelpPlugin`)

Built in code (`BuildUi()` in a partial class `DataCompareControl.Layout.cs`), `Font = Segoe UI 9`, log in `Consolas 9`.
Follow Data Copier's control (`C:\Claude\XRMDataCopy\src\MyscotekDataCopier\UI`) for every mechanism not
re-specified here: `UiLogger`, settings load/save seams, `ExecuteMethod`-based connection requests, one cancellable
`Task.Run` operation at a time, `FitLayout`, the internal test constructor `(loadSettings, saveSettings, mirrorToXrmToolBoxLog)`.

### 6.1 Connections
* **Primary** = the normal XrmToolBox connection (`Service` / `ConnectionDetail`).
* **Secondary** = XrmToolBox's additional organisation, exactly as Data Copier's destination:
  `RaiseRequestConnectionEvent(new RequestConnectionEventArgs { ActionName = SecondaryActionName ("AdditionalOrganization"),
  Parameter = SecondaryParameter ("secondary"), Control = this })`; in the `UpdateConnection` override, when
  `actionName == SecondaryActionName` store `_secondaryService` / `_secondaryDetail`, refresh labels, log it and
  **return without calling base** (base would replace `Service`). Every other `UpdateConnection` calls base FIRST, then
  resets the tool (entities, views, results, the primary schema cache) and loads the entity list — unless the action
  was `RefreshEntities`, which just did.
* Same-org guard: when primary and secondary resolve to the same organisation (same organisation name, or the same
  web/service URL, or the same service object), warn with a Yes/No dialog when Compare is pressed.
* Actions that need the primary go through `ExecuteMethod` (parameterless instance methods with unique names:
  `RefreshEntities`, `LoadSelectedEntityViews`, `CompareSelectedView`); without a connection XrmToolBox opens its dialog.

### 6.2 Layout (top to bottom)
1. **ToolStrip** (GripStyle Hidden): `Select secondary environment...` | sep | `Refresh entities` | sep | `Close`
   (right-aligned, `CloseTool()`), plus right-aligned labels `Secondary: (none)` and `Primary: (none)`.
2. **SplitContainer (vertical)**:
   * **Left** (about 300 px, narrower down to 200 px when the right side would get less than 560 px):
     label `Entities`; a filter `TextBox` (display or logical name, as you type, cue `Filter entities...`); a `ListView`
     (Details, FullRowSelect, HideSelection = false, columns *Display name* / *Logical name*, by display name). Below it
     (a horizontal split inside the left panel, or a fixed area): label `Views`, a `ListBox` (or `ListView`) of the
     selected entity's views: the system views by name, then the personal views by name suffixed ` (personal)`
     (`ViewInfo.DisplayName`); `(All records)` when the entity has none (`ViewService.CreateAllRecordsView`).
     Selecting an entity loads its views (`ViewService.GetViews(Service, entity, true, logger)`); double-clicking a view
     or pressing Compare starts the comparison of the selected view.
   * **Right**: a `SplitContainer (horizontal)`:
     * **Top**: an action row — `Button` **`Compare`** (bold; enabled when a primary, a secondary and a view are set and
       nothing runs), `Button` `Cancel` (enabled only during a run), a progress `Label` filling the rest
       (`CompareProgress.Message`, AutoEllipsis). A **summary strip**: `Primary: N  Secondary: N  |  Missing: a
       Different: b  Extra: c  Matching: d` (each count on the row colour of its status; `Not checked: e` when
       cancelled; `Secondary view failed` in red with the error as tooltip when `SecondaryQueryFailed`). A filter row:
       `Status:` `ComboBox` (DropDownList: `All`, `Missing`, `Different`, `Extra`, `Matching`), `Differing column:`
       `ComboBox` (`differingColumnFilter`, DropDownList, 6.9) and a `TextBox`
       `Filter rows...` (client-side, over every visible cell). The **results grid** (`DataGridView`, ReadOnly,
       AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode FullRowSelect, MultiSelect = false,
       AutoSizeColumnsMode None): first column `Status` (text: Missing / Different / Extra / Matching), then the VIEW's
       columns (`LayoutParser.Parse(view.LayoutXml)`, widths from the layout, headers from `ColumnHeaderResolver`
       with the primary schema provider, the raw column name as the header tooltip), cell text from
       `CompareResult.CellText(row, column)`; a hidden id column. Row back colour by status — RED `#F8D7DA`
       (Missing), AMBER `#FFF3CD` (Different), GREEN `#D4EDDA` (Match), BLUE `#CCE5FF` (Extra) — with dark
       (default) text; the selection colour stays readable (e.g. a darker shade of the row colour or the default
       highlight). Bind through a `DataTable` + `BindingSource` (its `Filter` implements the status and text filters;
       escape `'`, `[`, `%`, `*` as Data Copier does). Sorting by column header is allowed.
     * **Bottom**: a second horizontal `SplitContainer`: the **detail pane** above the **log**.
       * Detail pane: a header label (`{DisplayName} {id} - {status}`, plus `found by id outside the secondary's
         view` / `... the primary's view` when `InSecondaryView` / `InPrimaryView` is false), a `CheckBox`
         `Differences only` (persisted; shows the lines with `IsDifferent`), a column search row (6.8: `TextBox`
         `detailFilterBox`, cue `Search columns (name or display name)`, and `X of Y columns`), and a `DataGridView` (ReadOnly) with
         columns `Column` (DisplayName, tooltip = logical name + Note), `Primary`, `Secondary` from
         `CompareResult.GetDetails(row)`. Line colours: `IsMismatch` → AMBER `#FFF3CD`; `!IsCompared` → grey text
         (`SystemColors.GrayText`) on the window background (ignored, derived, primary key, not in metadata);
         otherwise default. Empty when no row is selected. Recomputed when the selection changes (cheap).
       * Log: label `Log`, `RichTextBox` (ReadOnly, WordWrap off, DetectUrls false) with `Copy log`, `Save log...`,
         `Clear`; colours Info = WindowText, Success = ForestGreen, Warning = DarkGoldenrod, Error = Firebrick;
         `[HH:mm:ss] ` prefix; batched `BeginInvoke`; about 20 000 lines cap (Data Copier's `UiLogger`).
3. A `Compare options...` button (phase 3; it was `Ignored attributes...`) opens a small dialog (6.5) with the ignored
   list - a multi-line `TextBox` (one per line or comma-separated), `Restore defaults` - and the prefix filter, `OK` /
   `Cancel`. OK saves the settings and, when a result is shown, applies them at once with `CompareResult.Reevaluate` (no
   reload) and logs the new counts. An `Entity mappings...` button opens the mappings dialog (6.6).

### 6.3 Behaviour
* **Compare**: validate (secondary set; a view selected; same-org confirmation), build `CompareOptions` from the
  settings (`SetIgnoredAttributes(settings.IgnoredAttributes)`, `PageSize` 5000), then on `Task.Run`:
  `new CompareEngine(Service, _secondaryService, _primarySchema, options, _logger).Compare(entity, view.FetchXml,
  progress, token)` where `progress` marshals to the UI (a `Progress<CompareProgress>` created on the UI thread is
  fine) and `_primarySchema` is the cached `DataverseSchemaProvider` of the primary connection (phase 3: the engine also
  gets the cached `DataverseSchemaProvider` of the secondary connection, read only for a mapped entity, and the options
  carry the prefix filter and the entity mappings). The headers are
  resolved inside the same `Task.Run` before the engine runs. Log a header first:
  `Comparing view "{view}" of {entity} between {primary} (primary) and {secondary} (secondary)`, then the engine logs.
  While it runs: disable the entity list, the view list, Compare, Refresh, Compare options..., Entity mappings...;
  enable Cancel. The same-organisation question is not asked for an entity mapped to another table (a log line instead).
* **Cancel** cancels the token; the partial result is shown like a complete one (summary shows `Not checked`).
* **Errors** (an exception from `Compare`): Error log line (the engine already logged `Compare failed: ...`) and
  `MessageBox.Show(this, ex.Message, "Data Compare", OK, Error)`; the previous result is cleared.
* Changing entity or view clears the grid, the summary and the detail pane (a result belongs to one view).
* Every handler is `async void` with try/catch; nothing blocks the UI thread; one operation at a time (Data Copier's
  `RunGuarded` / `BeginOperation` / `EndOperation`).
* Error-level log lines are mirrored to XrmToolBox's `LogError`, the header and result lines to `LogInfo`.

### 6.4 Settled in phase 2 (within the frame above; Data Copier's conventions where this section was silent)
* **Ignored attributes...** is a button of the action row (Compare | Cancel | Ignored attributes... | progress), not a
  toolbar item: with it the toolbar overflowed at 800 px with real connection names. It needs no connection. (Phase 3:
  Compare options... and Entity mappings... are both in the action row, see 6.7.)
* The filter row ends with `X of Y rows` (rows the filters show / rows of the result). The summary strip shows
  `No comparison yet: ...` while there is no result, and `Not checked: e (cancelled)` for a cancelled run.
* Row colours are applied in `CellFormatting` from the bound row's status (rows stay shared: 20 000 rows bind and
  paint in about 0.2 s); a selected row takes a darker shade of its colour (`#F1AEB5`, `#FFE69C`, `#A3CFBB`,
  `#9EC5FE`), black text on all of them. The table is built on the compare's worker thread (`Shown.Build`).
* After a run the progress label reads `Done: N rows - missing a, different b, extra c, matching d` (`Cancelled
  (partial result): ...`, `, not checked e`, ` - the secondary view failed`); a failure leaves `Compare failed`.
* The detail caption puts the found-by-id note in parentheses: `Account {id} - Different (found by id outside the
  secondary's view)`. Its selected line is neutral grey (`#E2E3E5`); amber lines select darker amber.
* Re-evaluating after the ignored list changes updates the status cells in place (the DataTable's order is the
  result's), the summary, the differing-column filter's list (6.9), the filters and the detail pane, and logs `Re-evaluated with the new compare options
  (nothing read again) - Result: ...` (phase 3 wording; it was "ignored attributes").
* Kept from Data Copier: a `====` line before the run header, a same-organisation heads-up log line when the
  secondary is chosen, Refresh entities also forgets the primary metadata cache.

### 6.5 Compare options dialog (phase 3, `CompareOptionsForm`, was `IgnoredAttributesForm`)
* Two sections: **Ignored attributes** (the multi-line box `ignoredAttributesBox`, sorted, `Restore defaults`, a count)
  and **"Only compare columns with these prefixes (one per line or comma-separated, blank = all)"** (`prefixesBox`, in
  the order given, a line saying which prefixes apply or "Every column is compared"); `OK` / `Cancel`, no AcceptButton
  (Enter is a new line). Needs no connection.
* OK saves `IgnoredAttributes` and `ComparedPrefixes`, logs `Ignored attributes: ...` and `Prefix filter: only the
  columns starting with contoso_, new_ are compared.` (or `... none ...`), updates the summary strip and, with a result
  shown, re-evaluates it (6.4). Seam: `ShowCompareOptionsDialog`.
* The summary strip shows `Prefixes: contoso_, new_` (`prefixLabel`, second label of the strip) while a prefix filter is set,
  with or without a result.

### 6.6 Entity mappings dialog (phase 3, `EntityMappingsForm` + `ColumnMappingsForm`)
* `Entity mappings...` (action row) first lists the secondary's entities with `EntityCatalog` - once per secondary
  connection, as an operation off the UI thread; a failure is a warning and the names are typed - then shows the
  dialog (seam `ShowEntityMappingsDialog`). Needs no connection: without one every name is typed.
* The dialog: a list (`mappingList`: Primary entity | Secondary entity | Columns "Matched by name" / "N column pairs"),
  `Add` (starts with the tool's current entity when it is not mapped yet) / `Remove`, and for the selected mapping a
  **Primary entity** combo (the primary's loaded entities, "Display name (logical name)") and a **Secondary entity** combo
  (the secondary's entities), both taking a typed logical name, and `Columns...`. OK validates - both entities set,
  plausible logical names (`[a-z0-9_]+`), one mapping per primary entity; blank mappings are dropped - and confirms the
  normalised list; Cancel discards every change.
* `Columns...` reads both entities' columns (readable, not derived) off the UI thread through the cached schema
  providers (typed names, with a note, when a side is not connected or cannot be read), then shows the column pairs
  dialog (seam `EntityMappingsForm.ShowColumnsDialog`): a list (Primary column | Secondary column), two combos, `Add`
  (replaces the pair of a primary column already listed), `Remove`; OK adds a pair still in the combos, then confirms.
* OK saves `EntityMappings` and logs `Entity mappings: account -> new_account (accountnumber -> new_accountnumber).` When
  the mapping of the entity whose result is shown changed, nothing is re-evaluated (it cannot be): a Warning says to
  press Compare, and the indicator says so too.
* **Indicator** (`mappingLabel`, first label of the summary strip, bold): `→ new_account` while the selected entity is
  mapped (with or without a result); `→ new_account (press Compare to apply)` or `Mapping removed (press Compare to
  apply)` in dark goldenrod while the result shown was read with another mapping. Tooltip: what is compared with what.
* The detail pane's Column cell is `{display name} → {secondary column}` when a line reads a differently named secondary
  column (the key line shows `accountid → new_accountid`); the tooltip adds the note.

### 6.7 Settled in phase 3
* The action row is a wrapping flow (Compare | Cancel | Compare options... | Entity mappings... | progress): the progress
  text fills the rest of the buttons' line while that leaves it `ProgressMinWidth` (160 px), else it takes a line of its
  own (an 800 px tool); the buttons wrap only below any real tab width (640 x 400). Sized by `FitActionRow` in `FitLayout`.
* Log texts use `->` for mappings (ASCII); the UI labels use `→`.
* A mapping to a table of the same organisation skips the same-organisation question (it is a real comparison).

### 6.8 Column search in the detail pane (owner request 2026-10-04)
* A free-text `TextBox` (`detailFilterBox`, cue `Search columns (name or display name)`) on a row of its own
  (`detailFilterRow`) between the caption row (caption | `Differences only`) and the detail grid, with `detailCountLabel`
  at its right: `X of Y columns` (lines shown / lines of the selected row; `0 of 0 columns` without a row). Not on the
  caption's row: at 800 px the caption (entity, GUID, status, found-by-id note) would be cut short.
* A line is shown when the trimmed text is blank or a case-insensitive substring of `ColumnComparison.LogicalName` OR
  `DisplayName` (`MatchesColumnSearch`) AND, with `Differences only`, it `IsDifferent`. The primary key line obeys the
  search like any other. Applied as the user types (TextChanged): the selected row's lines are kept
  (`_detailLines`) and only filtered again, nothing is recomputed.
* The text stays when another row is selected (and when the result changes), so one column can be followed from row to
  row; only the user clears it - Escape in the box clears it (taken as the box's own key while there is text).
  Not persisted in the settings.
* `DetailPanelMinHeight` went from 90 to 120 px so the grid keeps a few lines under the two header rows.

### 6.9 Differing-column filter (owner request 2026-10-05)
* "Filter by all rows with a specific column that is different": a `ComboBox` (`differingColumnFilter`, DropDownList,
  220 px, its list as wide as its longest item) after the status filter, labelled `Differing column:`
  (`differingColumnLabel`). First item `(any column)` = no filter (`AnyColumn`); then one item per attribute of the
  UNION of the rows' `DifferingAttributes` (`DifferingColumnChoices`): `Display Name (logicalname) · N` - N = the rows
  where it differs, the display name from the result's (primary) metadata, just `logicalname · N` when it has no
  label (as `NameChoice`) - sorted by display name (current culture, ignoring case), then logical name. Names are
  the PRIMARY logical names, also for a mapped entity (5.9).
* Filled when a result is shown (`ShowResult`, before binding) and after every re-evaluation (6.4): the attribute
  selected stays selected while it is still listed, else the filter falls back to `(any column)`. Only `(any
  column)` while no result is shown - also from the moment a new Compare starts (`ClearResult`) and after another
  view or entity is chosen. Not saved in the settings.
* Filtering: a hidden DataTable column `__differing` (`DifferingColumn`, never a grid column, never searched by the
  text filter) holds `|name1|name2|`; choosing an attribute adds `[__differing] LIKE '%|name|%'` to the
  BindingSource filter, AND-ed with the status and text filters (a row must pass all three); `X of Y rows` as usual.
  Only Different rows can pass: Missing and Extra rows have no column comparison (empty `DifferingAttributes`) and
  drop out while an attribute is chosen, and so do Matching rows. Re-evaluation rewrites `__differing` in place with
  `__status` and applies the filters again (`ApplyRowFilter(force)`: the filter text may be unchanged while the cells
  changed). Measured on 20 000 rows: choosing an item about 0.1 s.
* The detail pane is left alone: choosing a column does not touch the column search (6.8) or Differences only.
* Layout (`FitFilterRow`, in `FitLayout`): the filter row is a wrapping flow (`filterRow`: `Status:` | status combo |
  `Differing column:` | its combo | `Filter rows...` | `X of Y rows`) whose text box takes the rest of the line while
  that leaves it `FilterBoxMinWidth` (160 px); else the two combos keep the first line and the text box (as wide as the
  line allows) and the count take the next (an 800 px tool); on a tool too narrow for both combos (640 x 400) each
  label stays with its combo (flow breaks) on a line of its own. `X of Y rows` always ends its line. At 800 x 500 with a
  two-line summary strip the results area therefore scrolls by (at most) the filter row's second line; 800 x 600
  does not scroll.

## 7. Settings (`UI\DataCompareSettings`, XmlSerializer via SettingsManager, file `MyscotekDataCompare.xml`)

* `IgnoredAttributes` (string, comma-separated; default `CompareOptions.DefaultIgnoredAttributeList`; blank = ignore
  nothing; a missing element = the default).
* `DifferencesOnly` (bool, default false): the detail pane's check box.
* `LastEntities` (`List<LastEntity { Organization, Entity }>`): the entity selected last, per PRIMARY organisation
  (key as Data Copier's `OrganizationKey`: the web application URL lower-cased without trailing slash, else the
  organisation service URL, else the organisation name); re-selected after the entity list loads if it exists.
* `PageSize` (int, default 5000, only in the file; clamped to 1..5000).
* `ComparedPrefixes` (string, comma-separated, default "" = every column; a missing element = the default) - phase 3.
* `EntityMappings` (`List<EntityMapping>`, 5.9; default empty; a missing element = none; incomplete mappings are kept in
  the file but never applied: `BuildOptions` copies the complete ones) - phase 3, global (not per environment pair).
* Loaded in the constructor (`SettingsManager.Instance.TryLoad`), saved in `ClosingPlugin`, when the check box changes,
  when the compare options or the entity mappings are confirmed and when an entity is selected. Unknown elements are
  skipped when read.

## 8. Build, test, package, release

```
dotnet build MyscotekDataCompare.sln -c Release      (0 warnings, 0 errors)
dotnet test  MyscotekDataCompare.sln -c Release
.\build-package.ps1                                  (dist\Myscotek.DataCompare.<version>.nupkg)
copy src\MyscotekDataCompare\bin\Release\MyscotekDataCompare.dll %AppData%\MscrmTools\XrmToolBox\Plugins\
```
`build-package.ps1` builds, tests, refuses a DLL whose AssemblyVersion/FileVersion differs from the nuspec version,
packs with `tools\nuget.exe` (downloaded when missing; gitignored) into `dist\` and fails unless the package holds only
`lib/net48/Plugins/MyscotekDataCompare.dll` (identical to the build output) and NuGet's own parts. **Never a packed
`<icon>`** in the nuspec: `<iconUrl>` only, pointing at `images/icon-128.png` on GitHub (Tool Library icon rule;
NU5048 expected). Release: bump `1.YYYY.M.N` in `AssemblyInfo.cs` (three attributes) AND the nuspec (+ release notes);
`PluginMetadataTests` and the script fail on a mismatch; push to nuget.org, the Tool Library and GitHub by hand.

## 9. Tests (`tests\MyscotekDataCompare.Tests`, xUnit 2.9.2, net48)

* **Fakes**: `FakeOrganizationService` — in-memory store; QueryExpression evaluation (AND-ed Equal / **In** / Null /
  NotNull, orders, TopCount, PageInfo + `<cookie page="N" />`) and a minimal **FetchXML interpreter** (`FakeFetchXml`:
  all-attributes / attributes (+ alias), nested and/or filters with eq, ne, null, not-null, in, not-in, like,
  not-like, gt/ge/lt/le and `entityname`, main-entity orders, inner/outer link-entities of any cardinality with
  `alias.attribute` AliasedValues and link filters, distinct, count/page paging with MoreRecords and a cookie;
  anything else throws `NotSupportedException`); seeded FormattedValues are returned; `FailFetchPages` +
  `FetchFailure`, `FailRetrieveMultiple[entity]`, `ReturnPagingCookies`, `BeforeExecute`, `ExecuteHandler`,
  `RetrieveMultipleHandler`; `Executed`, `Fetches`, `Queries`, `Writes`. `FakeSchemaProvider` fluent builder (incl.
  `Derived`, `Unreadable`, `File`, `Image`, `MultiSelect`). `TestSupport`: `ListLogger`, `SyncProgress`, `TestData`
  (records, `Account`, parties, `Changed`, `Formatted`, `StandardSchema`, `AccountView`), `Harness` (engine wired to
  the fakes, `OnProgress`), `Reflect.With`. `UiTestHost` + `ToastNotificationsStub` (for the phase-2 UI tests: run UI
  test bodies inside `Application.Run` on an STA thread; never deploy the real toast assembly next to the tests).
* **Covered in phase 1**: query rewrite (5.3); every value rule (5.6) unit-level and through the engine; the four
  statuses, order and summary invariants; null/absent; ignored, custom ignored, derived, unreadable, unknown and
  linked attributes; found by id outside the view (both directions, incl. a Match due to linked data); 500-id batching
  (1201 ids) and a custom batch size; multi-page paging with cookies, distinct paging without cookie, no cookie from the
  server, duplicates from a 1:N link; failing secondary view (page 1 and page 2), failing primary view, missing entity,
  wrong-entity and aggregate views, option validation; cancellation while loading either side, between lookup batches,
  while comparing, and a cancelled server call; progress phases in order and per page/batch/1000 rows; the log lines;
  `Reevaluate`; `CellText` (incl. aliases); the detail pane (order, listing rules, flags, notes, texts, raw values,
  Missing/Extra rows, consistency with `DifferingAttributes`); `CellFormatter.FormatDetail`; the fakes themselves;
  the copied services (catalog, views, layout, headers, paging, schema provider); plugin metadata, versions, nuspec.
* **Phase 2 added** (done; the UI test classes share the xUnit collection `UiTestCollection` so they run one at a
  time - like XrmToolBox's single UI thread; on parallel STA threads the WinForms layout came out stale about one
  run in five): UI smoke (control builds, initial states, settings round-trip incl. old files), the connection
  flows (Data Copier's UiFlowTests patterns: `OnRequestConnection` recorded, `UpdateConnection` simulated; the
  secondary request is an additional organisation and never replaces `Service`), an end-to-end compare through the
  control against the fakes (grid rows, colours, status filter, text filter, summary strip, detail pane with
  differences only, cancel), `Reevaluate` after editing the ignored list, and layout at 800 x 500 / 1600 x 900; also
  the ignored-attributes dialog, a failing secondary view and a failing run, the same-organisation question, another
  view/entity clearing the result, a linked column's header, layout at 640 x 400 (scrolls), and a 20 000-row result
  (build, bind, filters, selection, re-evaluation each well under 5 s; rows stay shared).
* **Phase 3 added** (68 tests; 318 in all): `MappingEngineTests` (the mapped table read in the secondary, each side's
  lookups and key, the secondary key from the secondary metadata, same-named and paired columns, no counterpart, pairs
  to missing columns, type differs, the detail's secondary-only lines, Extra / found-by-id cells, linked columns of a
  self-link, the rewritten view, the failing rewritten query, a missing mapped table, the provider fallback, unmapped
  runs never reading the secondary metadata, case-insensitive mappings, the invariants, `Reevaluate`),
  `SecondaryQueryTests` (entity, conditions incl. nested and `valueof`, orders, link `to`/`from`, self-links at any
  depth, `entityname` scopes, pass-through, refusals, the distinct key order), `EntityMappingTests` (model, counterparts,
  type families, options' `FindMapping`/`Clone`), `PrefixFilterTests` (include, case, ignored on top, empty = all,
  detail notes, header, `Reevaluate`, parsing) and `UiMappingTests` (a mapped compare end to end, the mappings dialog -
  add with a column pair, cancel, remove, validation, typed names without connections - the column pairs dialog, the
  changed-mapping indicator, the secondary entity list once per connection, a mapped table in the same organisation,
  the prefix filter round trip with re-evaluation, settings round trip and old files, captions and name parsing); the
  layout at 800 x 600 / 1920 x 1080 / 640 x 400 with the new buttons and labels, and the three dialogs at their default
  and minimum sizes.
* **Column search added** (6.8; 2 tests, 320 in all): `UiFlowTests` - by a part of the logical name only and of the
  display name only, case-insensitive, the key line filtered too, `X of Y columns`, combined with Differences only, kept
  across row selection (and an empty pane without a row), Escape clears (input key while there is text), blank and white
  space restore every line, never saved; `MatchesColumnSearch` unit-level. The layout tests check the search row (stacked
  between the caption row and the grid, nothing clipped or overlapping, box at least 100 px, count at the right) at
  800 x 500, 1600 x 900, 800 x 600, 1920 x 1080 and 640 x 400.
* **Differing-column filter added** (6.9; 3 tests, 323 in all): `UiFlowTests` - `(any column)` alone and selected
  without a result; after Compare the differing attributes with their counts, by display name (logical name alone
  without a label); a column keeps only its rows, Missing/Extra drop out, combined with the status and the text
  filters (the hidden names are not searched), `X of Y rows`, the detail pane's column search untouched; after a
  re-evaluation an attribute that stops differing leaves the list and the filter falls back, one still differing stays
  selected and is filtered again, a newly differing one joins; cleared while a new Compare runs and by another view;
  `DifferingColumnChoices` / `DifferingText` / `DifferingColumnFilter` unit-level (union, counts, order, label
  fallback, whole names). `UiPerformanceTests` - the list filled from 20 000 rows, choosing a column, with the status
  filter, back to `(any column)` (each under 2 s; measured about 0.1 s), the list after re-evaluation. `UiLayoutTests`
  - each label on its combo's line, the text box on the count's line and the count ending it, at every size; one line
  at 1600 x 900 and 1920 x 1080, two at 800 x 600 (combos, then the text box), three at 640 x 400; a chosen column at
  640 x 400 and 1600 x 900; 800 x 500 now scrolls by at most the filter row's second line.
