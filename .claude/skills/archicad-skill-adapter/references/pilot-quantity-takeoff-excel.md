# Archicad Pilot: Quantity Takeoff Excel

## Scope

Reuse the evidence and workbook method from `quantity-takeoff-excel` with current-turn Archicad data. The first pilot covers Zone inventory and evidence-backed boundary quantities. Partition, finish-height, opening deduction, and scaffold results remain partial until every required Archicad relationship is verified.

## Backend Route

- Revit selected: return to `.claude/skills/quantity-takeoff-excel/SKILL.md` and use its existing Revit workflow.
- Archicad selected: continue below and replace Revit-specific record fields with the mapping in this document.
- Target ambiguous: ask which application and project to use.

## Evidence Contract

| Method field | Archicad pilot field | Guardrail |
|---|---|---|
| `RoomId` | `ZoneGuid` | Never label a GUID as ElementId. |
| `Number`, `Name` | Verified Zone number/name property | Discover exact property identifiers. |
| `Level` | `Story` | Confirm actual Story membership and elevation behavior. |
| `BoundaryElementIds` | `BoundaryElementGuids` | Accept only relationships returned for the current Zone. |
| `PerimeterM` | `BoundaryPerimeterM` | Compute only from verified geometry and units. |
| `Openings` | Related Door/Window GUIDs with evidence | Do not deduct by proximity. |
| `HeightM`, `HeightSource` | Verified Zone/element height plus source | Mark unresolved when ceiling/slab evidence is missing. |

## Archicad Workflow

1. Anchor one live project and port.
2. Discover and list all target Zones, including those with missing finish codes or zero calculated quantity.
3. Discover Zone detail/property reads and retain exact property identifiers, units, GUIDs, and warnings in an intermediate record.
4. Discover Zone boundary and related-element commands. Preserve every returned relationship; do not silently replace it with nearest-element inference.
5. Apply only formulas whose inputs are supported by current evidence. Mark each unsupported deduction or height source as unresolved rather than substituting Revit API behavior.
6. Build the workbook with source GUIDs, formulas, warning columns, fallback counts, and a selected-project/port provenance sheet.
7. Reconcile detail rows against totals and scan formulas for spreadsheet errors before delivery.

## There is more than one area, and more than one perimeter

A Zone carries several quantities whose names all read like "the area". They are not equal, and
picking one without saying so is how a takeoff silently disagrees with the model. Observed on one
Zone:

| Property | Value |
|---|---|
| `區域/計算面積` | `'49.95'` |
| `區域/被測量的面積` | `'53.34'` |
| `區域/測得淨面積` | `'49.95'` |
| `區域/縮小面積` | `'49.95'` |
| `常規參數/面積` | `'53.34'` |
| `區域/牆週長` | `'44.91'` |
| `區域/區域淨週長` | `'44.91'` |
| `區域/區域總週長` | `'44.60'` |

Across the 42 Zones tested, calculated area and measured area differed on **36 of them**. Resolve
which definition the project team means, record the choice in the workbook, and emit a warning row
where the two disagree. Do not average them and do not silently prefer one.

All of these are property-layer values: display strings formatted to the project's calculation
units. See the unit contract in [revit-archicad-terminology.md](revit-archicad-terminology.md).

## Observed Capability Hints

Use these only as discovery hints:

| Intent | Observed command hint |
|---|---|
| List Zones or related element types | `elements_get_elements_by_type` |
| Read Zone details | ~~`elements_get_details_of_elements`~~ — **broken**, see [archicad-runtime-facts.md](archicad-runtime-facts.md) |
| Read Zone properties | `properties_get_property_values_of_elements` |
| Read boundary walls with per-segment evidence | `elements_get_relations_of_elements` — `zoneRelations.wallParts` gives `{elementId, roomEdgeIndex, begDistance, endDistance}`, and `elementsGroupedByType` separates Wall / Door / Window / Column / Beam |
| Read connected boundary elements | `elements_get_zone_boundaries` — returns `isExternal` and a per-boundary SI `area`, but is one Zone per call and timed out on repeat calls; **not used in this pilot** |
| Read elements related to Zones | `elements_get_elements_related_to_zones` — returns a **flat array with repeated GUIDs** and no type grouping; prefer the relations command above |
| Infer Story membership | `elements_get3_d_bounding_boxes` `zMin` against `project_get_stories` |

## Stop Conditions

- Boundary geometry or units are not explicit enough to compute a perimeter.
- Door/window ownership is inferred rather than returned as a verified relationship.
- Net height requires a ceiling/slab match that the discovered commands cannot establish.
- Workbook rows cannot retain source GUIDs and warnings.
- Two area or perimeter definitions disagree and no one has decided which one governs.

## Live-Test Evidence

```text
backend: archicad
canonical_skill: quantity-takeoff-excel
domain_method: domain/quantity-takeoff-excel.md
adapter_reference: pilot-quantity-takeoff-excel.md
date: 2026-09-08
status: approximate mapping (NOT verified — see unresolved_inputs)
runtime: tapir-archicad-mcp 0.5.3 (started with `--with "mcp<2"`), Archicad 28, Tapir Add-On 1.5.8
project_port: 19723
project_type: solo
calculation_units: Meter / SquareMeter / 2 decimals
target_story: 1FL (membership inferred from 3D bbox zMin vs project_get_stories)
zones_read: 42
property_identifiers_resolved: 10/10
  Number        <- 區域/區域號碼                [Default/String]
  Name          <- ID和類別/名稱                [Default/String]
  AreaCalc      <- 區域/計算面積                 [Area/Real]
  AreaMeasured  <- 區域/被測量的面積              [Area/Real]
  PerimNet      <- 區域/區域淨週長                [Length/Real]
  PerimTotal    <- 區域/區域總週長                [Length/Real]
  HeightZone    <- 區域/區域高度(Archicad 20)     [Length/Real]
  WallSurface   <- 區域/牆表面面積                [Area/Real]
  WindowSurface <- 區域/窗表面面積                [Area/Real]
  DoorSurface   <- 區域/門表面面積                [Area/Real]
relationships: elements_get_relations_of_elements over 42 Zones ->
  Wall 233, Riser 168, Tread 160, Object 98, Beam 73, Door 60,
  StairStructure 60, Column 47, Window 24, Stair 8
  wallParts segments retained as evidence rows: 233
supported_formulas:
  OpeningAreaM2  = SUM(DoorSurfaceM2, WindowSurfaceM2)
  NetWallAreaM2  = BoundaryPerimeterM * HeightM - OpeningAreaM2
  AreaDeltaM2    = AreaMeasuredM2 - AreaCalcM2
unresolved_inputs: 5
  1. finish-face boundary: Revit GetBoundarySegments(Finish) has no Archicad equivalent;
     perimeter falls back to the 區域淨週長 property
  2. per-opening dimensions: deducted from Zone-level door/window surface totals,
     not from individual Door/Window width x height
  3. net height: taken from the Zone height property, not matched against ceiling/slab geometry
  4. area definition: calculated vs measured disagree on 36 of 42 Zones; not adjudicated
  5. elements_get_zone_boundaries (isExternal, SI area) unusable at scale, so external/internal
     boundary split is absent from the workbook
workbook: 3 sheets — Zone 明細 (42 rows, formulas retained + SUM totals row),
  邊界證據 (233 wallPart rows with per-segment length formula),
  來源與限制 (project/port/units/commands/property map/unresolved list)
verification: SUM(perimeter)=1142.92 m; SUM(door+window surface)=412.66 m2;
  SUM(perimeter*height-openings)=4610.22 m2; formula error scan 0 suspicious cells;
  warning-carrying rows 42/42 (every row carries at least the finish-face caveat)
```

**Why this is `approximate mapping` and not `verified`:** the workbook is reproducible and every
number traces to a named property on a named GUID, but three of the method's core inputs — finish-face
boundary, per-opening deduction, and geometric net height — are substitutes rather than equivalents,
and the area definition is undecided. The pilot proves the pipeline runs; it does not yet prove the
numbers mean what the Revit-side method means.

## Reference

- [Terminology and boundary map](revit-archicad-terminology.md)
- [Archicad MCP runtime facts](archicad-runtime-facts.md)
- `domain/quantity-takeoff-excel.md`
- `domain/tool-capability-boundary.md`
