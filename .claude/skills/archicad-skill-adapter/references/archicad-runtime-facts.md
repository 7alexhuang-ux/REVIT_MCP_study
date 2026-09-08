# Archicad MCP Runtime Facts

> **維護者補註（收編時新增，非貢獻者原文）**
>
> Every runtime observation in this file was gathered by external contributor @Archwiz-boss in
> their own environment and submitted via PR #123 (`REVIT_MCP_study` issue #98). This repository
> hosts no Archicad instance and has not reproduced or independently verified any of it. Where the
> text below says "this repository," it refers to the fork the contributor was writing from at the
> time (`Archwiz-boss/BIM_MCP_study`), not `REVIT_MCP_study`. See `log/2026-08.md` for the adoption
> record and verification scope.

## What this file is, and when to distrust it

This records what the Archicad MCP runtime actually did when driven from this repository, as
opposed to what its schemas and descriptions claim. Everything here has a shelf life: the
wrapper, the Tapir Add-On and Archicad itself ship independently, so a fact that held for one
combination can be wrong for the next.

| Verified against | Value |
|---|---|
| Wrapper | `tapir-archicad-mcp` `0.4.3` and `0.5.3` (both observed; differences called out) |
| Archicad | 28 |
| Tapir Add-On | 1.5.8 |
| Date | 2026-08-19 |
| Project | one solo project, metric calculation units |

A second run against `0.5.3` on **2026-09-08** (same Archicad 28 / Tapir 1.5.8, a different solo
project, Meter / 2 decimals) is recorded in [2026-09-08 re-run](#2026-09-08-re-run-on-053) rather
than merged into the observations above.

`docs/integrations/archicad-mcp.md` currently pins `0.4.3`. `0.5.3` was tested separately and is
**not** the pinned version; the sections below say which one they describe.

Treat every command name here as a search hint, never as a contract. Re-discover before dispatch,
exactly as `SKILL.md` requires. Nothing in this file removes that obligation.

## Discovery works differently on each version

**`0.5.x` — two-step, no search.**

| Tool | Behaviour |
|---|---|
| `archicad_list_commands` | Takes no arguments. Returns the full command inventory with one-line descriptions. |
| `archicad_get_command_schema` | Takes `command_name`. Returns that command's exact input schema. |
| `archicad_call_tool` | Dispatch. Its own description now requires `archicad_get_command_schema` first. |

Because there is no query, there are no false negatives. List everything, then read the schema of
the command you picked.

**`0.4.3` — one-step, and the search is not what it claims.**

`archicad_discover_tools` took a query string and its description advertised semantic search. It
behaved as a **case-insensitive literal substring match over command description text**:

- `get the currently selected elements` — the tool's own documented example — returned `[]`.
- `all elements` matched `CreateWalls` and `ModifyWalls`, because their descriptions contain
  `Wall elements`, i.e. the substring `all elements`.
- Broad single words appeared to cap at 10 results ordered by command name.

On that version, query short noun phrases likely to appear verbatim in a description
(`given type`, `property names`, `details of`), and never read an empty result as evidence that a
capability is absent.

`archicad_discover_tools` **does not exist on `0.5.3`**. `SKILL.md` step 4 and its tool table still
name it; that instruction cannot be followed on the newer wrapper.

## Discovery silently conflates two different failures

`discovery_list_active_archicads` returns a bare `{"result": []}` when the Tapir Add-On is not
loaded — the same response as when no Archicad is running at all. Observed with Archicad running
and port 19723 listening: an unloaded Add-On still produced an empty array with no diagnostic.

**Before concluding that no instance is available, confirm the Tapir Add-On is loaded.** Tracked
upstream as [SzamosiMate/tapir-archicad-MCP#11](https://github.com/SzamosiMate/tapir-archicad-MCP/issues/11).

## Known defect: `elements_get_details_of_elements` is unusable

A single Wall GUID fails validation on both versions tested:

| Wrapper | Tapir Add-On | Result |
|---|---|---|
| `0.4.3` | 1.5.8 | 431 validation errors |
| `0.5.3` | 1.5.8 | 522 validation errors |

The generated response models mark every per-type detail variant `additionalProperties: false`, so
fields the Add-On has since added are rejected rather than ignored. Because the result is a union,
each unmatched field is reported once per variant, so the error count tracks the number of
supported variants rather than how far behind the schema is — `0.5.3` reports *more* errors while
being *less* out of date.

`0.5.3` did narrow the gap: `flipped` and `floorPlanPolygons` are accepted where `0.4.3` rejected
them. Tapir 1.5.8 then added the next batch, currently rejected: `referenceLineLocation`,
`profileType`, `slantAlpha`, `slantBeta`, `topOffset`, `relativeTopStory`, `zoneRel`, `visibility`,
`isAutoOnStoryVisibility`, `referenceMaterial`, `oppositeMaterial`, `sideMaterial`, `cutFillPen`,
`cutFillBackgroundPen`.

Tracked upstream as
[SzamosiMate/tapir-archicad-MCP#24](https://github.com/SzamosiMate/tapir-archicad-MCP/issues/24).

**Consequence for planning.** Archicad does return `flipped` and `referenceLineLocation` — the
wrapper discards them. Any assessment that concluded "Archicad exposes no equivalent of Revit's
`Wall.Flipped`" was reading a wrapper defect as an API gap. The data exists; it is currently
unreachable through this command. Do not plan a wall-orientation workflow around
`elements_get_details_of_elements` until the upstream issue closes, and do not record the absence
of exterior-side evidence as a permanent Archicad limitation.

## Pagination

- Page size is 100. `next_page_token` is base64 of the integer offset (`MTAw` = 100).
- Absence of `next_page_token` is the only end-of-list signal, so a complete count is
  `offset_of_final_page + len(final page)`.
- On `0.4.3`, an unscoped `Wall` walk succeeded for 16 consecutive pages (1600 GUIDs) and then
  returned `Pagination session expired. Please start a new request.` Tokens also failed when
  reused a few minutes later in a separate turn with no intervening pages, which points at a
  **time-based session TTL rather than a page-count limit**.
- On `0.5.3`, a token survived a large intervening call. The walk was **not** run to completion, so
  whether the TTL still exists is unresolved.
- **Resolved on 2026-09-08**: an unscoped `Wall` walk did run to completion on `0.5.3`. See below.

**Working rule regardless of version:** bound the scope with a server-side filter first, then
exhaust pagination inside that scope, and report the scope alongside the count. A count without its
scope is not evidence.

## Command inventory

`archicad_list_commands` on `0.5.3` returned roughly 250 commands. Do not copy that list into a
Skill — run the command. The families below exist so an application-neutral intent can be pointed
at the right area quickly.

| Family | Covers |
|---|---|
| `app_*` | Active window, Add-On version, special folders, alerts |
| `attributes_*` | Layer, Line, Fill, Composite, Surface, Profile, Pen Table, Zone Category, MEP System, Building Material; folders; physical properties |
| `classifications_*` | Systems, items, per-element classification, availability |
| `components_*` | Element components and their property values |
| `design_options_*` | Design options, sets, combinations (Archicad 29+) |
| `elements_*` | Query, create, modify, delete, relations, collisions, bounding boxes, GDL parameters, highlight, selection |
| `favorites_*` | Create, apply, rename, import/export Favorites |
| `grouping_*` | Groups and Suspend Groups mode |
| `ifc_*` | IFC ids, types, properties, file operations |
| `issues_*` | Issues and BCF import/export |
| `keynotes_*` | Keynote tree, items, folders, labels (Archicad 28+) |
| `layout_*`, `navigator_*` | Layout Book, views, drawings, sections, publisher sets |
| `library_*` | Loaded libraries, available library parts, embedded library |
| `mep_*` | MEP elements, routing, ports, distribution systems, preference tables (Archicad 28+) |
| `project_*` | Stories, calculation units, geo location, project info, open/save/close |
| `properties_*` | Property definitions, groups, ids, values, availability |
| `revisions_*` | Document revisions, changes, issues |
| `solid_ops_*` | Solid element operation links |
| `teamwork_*` | Reserve, release, send, receive |

### Commands that change existing assessments

These exist on `0.5.3` and did not on `0.4.3`, or were not found by its discovery. Each one
invalidates an earlier "capability gap" conclusion, so check here before recording a new one.

| Command | Why it matters |
|---|---|
| `elements_get_zone_boundaries` | Zone boundaries including connected elements and neighbour zones — the evidence a Zone-based takeoff needs |
| `elements_get_relations_of_elements` | Wall/beam endpoint and reference-line connections, zone boundaries, the zones either side of a window or door, roofs and shells |
| `elements_get_elements_related_to_zones` | Elements grouped by type per Zone |
| `elements_get_subelements_of_hierarchical_elements` | Decomposes Stair and Railing into their subelements |
| `properties_get_all_properties` | All user-defined and built-in properties in one call |
| `properties_get_all_property_ids_of_elements` | Which properties are actually available on given elements |
| `properties_get_property_definition_availability` | Which classification items a property is available for |
| `classifications_get_classification_item_availability` | Which properties a classification item exposes |
| `elements_get_gdl_parameters_of_elements` | Library Part parameters, the nearest thing to Revit type/instance parameters on objects |
| `mep_get_mep_preference_tables` | Circular cross-section preference tables for Piping and Ventilation — the closest Archicad analogue to a Revit pipe segment size catalog |
| `elements_filter_elements`, `elements_get_collisions` | Both referenced by the Wave 2 plan; confirmed present |

## Verified response shapes

Dispatched on `0.5.3` against the test project, so these are observed rather than inferred from
schemas. Re-verify before relying on them; the schema is the contract, this is only what one
project actually returned.

### `elements_get_relations_of_elements` on Zones

Returns `zoneRelations` per input element:

```text
elementsGroupedByType : [{elementType, elements[]}]   Wall, Beam, Column, Door, Window observed
wallParts             : [{elementId, roomEdgeIndex, begDistance, endDistance}]
beamParts             : [{elementId, begDistance, endDistance}]
curtainWallSegmentParts : []
```

`wallParts` is the important one. It states which segment of which wall forms which edge of the
Zone polygon, which is the Archicad counterpart of a Revit room boundary segment. Two consequences:

- A room-side test does not need ray casting. "Does this wall segment have a Zone on one side" is
  answerable directly, and it does not depend on `elements_get_details_of_elements`, which is
  currently broken.
- A perimeter-based takeoff can source its evidence chain here: which walls bound a room, and over
  what length of each.

Distances are geometry-layer values, so metres, and `begDistance` can be negative where the wall
runs past the start of the Zone edge.

**Not every Zone returns relations.** Of seven Zones on the active story, six returned full
relations and one returned an empty structure; `elements_get_zone_boundaries` was also empty for
that Zone. An empty result must be reported as distinct from a Zone that genuinely has no
boundaries, not silently dropped.

### `project_get_hotlinks`

Returns only `[{location}]` — a file path per hotlink node. No module name, and **no GUID
ownership**, so it cannot answer which elements came from which hotlink. Any workflow that needs to
separate host content from hotlinked content cannot get that from this command.

Worth knowing when reading counts from any Archicad project: the test project used here reports 13
hotlink nodes, all pointing at its own file. Element counts therefore include hotlinked content
unless something else excludes it, and an unscoped `Wall` enumeration in this project showed a
visible change of GUID format partway through, consistent with crossing from host elements into
module elements. **State whether a count includes hotlinked content.**

## Element types decompose further than Revit categories

`elements_get_elements_by_type` takes an `ElementType` enum with roughly seventy values. The trap
is not the size of the list, it is that several Revit "single elements" are several Archicad
elements:

- A stair is `Stair` plus `Riser`, `Tread` and `StairStructure`.
- A railing is `Railing` plus a dozen part types (`RailingPost`, `RailingBaluster`, `RailingPanel`,
  `RailingSegment`, and so on).
- A curtain wall is `CurtainWall` plus `CurtainWallSegment`, `Frame`, `Panel`, `Junction`,
  `Accessory`.
- Beams and columns also expose `BeamSegment` and `ColumnSegment`.

Before any count or quantity, decide and state which granularity is being measured, and use
`elements_get_subelements_of_hierarchical_elements` rather than guessing at the parent/child split.
Counting parents and subelements together double-counts; counting only parents loses the parts a
takeoff usually needs.

`ElementFilter` (`IsEditable`, `OnActualFloor`, `IsVisibleByLayer`, `IsVisibleIn3D`, ...) filters by
visibility and editability state. It is **not** a type filter — type selection comes only from the
separate `elementType` argument.

## 2026-09-08 re-run on `0.5.3`

Same wrapper (`0.5.3`), Archicad 28 and Tapir Add-On 1.5.8 as the 2026-08-19 run, but a different
solo project (Meter / 2 decimals / DecimalDegree, 97 stories, ~7.4k walls). Driven through the
`archicad-mcp` stdio server only; no direct call to the JSON API port. Earlier observations are
left as they were.

### Startup: `0.5.3` no longer resolves a working dependency set on its own

`uvx --from tapir-archicad-mcp==0.5.3 archicad-server` now fails before serving anything:

```
ModuleNotFoundError: No module named 'mcp.server.fastmcp'. This is mcp 2.x, where FastMCP was
renamed to MCPServer ... or pin 'mcp<2' to keep running v1 code.
```

A fresh resolve picks up `mcp` 2.x while `0.5.3` still imports the v1 path. Adding
`--with "mcp<2"` starts the server normally. An environment resolved before `mcp` 2.x shipped keeps
working, so a stored config can look correct right up until it is re-resolved.

### `archicad_call_tool` needs `discovery_list_active_archicads` in the same session

Calling a command with a correct `port` but without a prior discovery call in that MCP session
returns `Port 19723 is not an active Archicad connection.` Discovery is not only how the port is
found; it registers the connection for the session. The same call succeeds once discovery has run.

### Argument shape

`archicad_call_tool` takes `{name, arguments}` where `arguments` is
`{port, params: {...}, page_token?}`. Command parameters go inside `params`. Placing them at the
top level makes pydantic reject **`port`** as `extra_forbidden`, because the whole `arguments`
object is then validated against the command's parameter model — the error names `port`, which
reads as a port problem and is not one.

### Pagination on `0.5.3` runs to completion

An unscoped `Wall` walk returned **7376 GUIDs over 74 pages in 0.6 s** with no
`Pagination session expired`. The `0.4.3` ceiling (~1600 GUIDs) did not reproduce. Scoped walks on
the active story completed as well (Zone 105 / 2 pages, Wall 280 / 3 pages).

**But the page-size trap is real and easy to miss.** `properties_get_all_properties` returns 100
definitions on its first page; taking that as the whole set produced a false "this project has no
area property" conclusion during this run. Walked to the end it returns **1766 definitions over 18
pages**. Any "capability absent" claim must first show pagination was exhausted.

### `elements_get_details_of_elements`: still broken, with the exact reject list

Unchanged verdict, now with field-level detail: **522 validation errors** on a single wall — the
same count as 2026-08-19. The rejected `WallDetails` fields are:

```
cutFillBackgroundPen, cutFillPen, isAutoOnStoryVisibility, oppositeMaterial, profileType,
referenceLineLocation, referenceMaterial, relativeTopStory, sideMaterial, slantAlpha, slantBeta,
topOffset, visibility, zoneRel
```

`flipped` is **not** in that list — `0.5.3` accepts it. `zoneRel` was not called out previously.
The rejected values are visible inside the error text itself (`referenceLineLocation='Center'`,
`slantAlpha=1.570796327`), which is the clearest available evidence that Archicad returns the data
and the wrapper's response model discards it.

### Zone relations at story scale

`elements_get_relations_of_elements` batched 50 Zones per call. On the active story, **all 105
Zones returned `zoneRelations` with a populated `wallParts`; none returned an empty structure**
(2026-08-19 saw 1 empty out of 7, so empty results still have to be handled).

Building a wall-to-Zone reverse index from `wallParts` over that story's 280 walls:

| Bucket | Count |
|---|---|
| bounded by >= 2 Zones (interior candidate) | 196 |
| bounded by exactly 1 Zone (exterior candidate) | 69 |
| bounded by no Zone (no verdict) | 15 |
| coverage | 265 / 280 = 94.6% |
| `wallParts` referencing a wall not on this story | 0 |

This is the evidence chain behind treating `wall-orientation-check` as a pilot candidate rather
than a capability gap.

### `elements_get_zone_boundaries`: better semantics, unusable at scale

It answers the interior/exterior question more directly than `wallParts` does. One call returns,
per boundary: `connectedElementId`, **`isExternal`**, **`neighbouringZoneElementId`** (all-zero GUID
when there is no neighbour), `area` as a raw SI float, and a 3D `polygonOutline`.

It is nevertheless not usable as the main route here: it takes **one Zone per call**, and in this
project only the **first** call succeeded. Every later call — including repeats of the *same* GUID
that had just succeeded — timed out past 60 s, across three separate attempts and two server
restarts. Walking the story's 105 Zones never completed.

### Property layer: the declared type is not the returned type

Property definitions declare `propertyValueType: "Real"`. The values come back as strings:

| Property | measure | declared | returned | Python type |
|---|---|---|---|---|
| 計算面積 | Area | `Real` | `'3.46'` | `str` |
| 被測量的面積 | Area | `Real` | `'8.15'` | `str` |
| 牆表面面積 | Area | `Real` | `'23.31'` | `str` |
| 牆週長 | Length | `Real` | `'7.91'` | `str` |
| 門寬度 | Length | `Real` | `'0.80'` | `str` |

All two-decimal, because the project's calculation units are Meter / 2 decimals — the precision
comes from a project setting, not from the value. Note also that one Zone carries both
`計算面積 '3.46'` and `被測量的面積 '8.15'`: different area definitions, not a discrepancy.

`propertyValuesForElements` is still positional and echoes back neither element nor property GUID.

### Property availability, and picking the right command

- A property that does not apply to the element returns `value: None`, with nothing in the response
  distinguishing "no value" from "not available for this classification". Reading window/door area
  properties on a Zone produced `None` for every one of them.
- **A single command returning an error is not evidence the property is absent.**
  `properties_get_details_of_properties` answered `6702 Property definition not supported` for
  property ids that `properties_get_all_properties` resolves without trouble. The Zone tested owned
  312 property ids and **all 312** resolved against the 1766-definition table.
- Working order: ask the element what it has (`properties_get_all_property_ids_of_elements`), then
  join against the definition table. Do not pick names out of the global definition list.

### A Zone has several "areas" and several "perimeters"

Reading one of them without recording which one was read is enough to make a takeoff disagree with
the model. On a single Zone:

| Property | Value |
|---|---|
| `區域/計算面積` | `'49.95'` |
| `區域/測得淨面積` | `'49.95'` |
| `區域/縮小面積` | `'49.95'` |
| `區域/被測量的面積` | `'53.34'` |
| `常規參數/面積` | `'53.34'` |
| `區域/牆週長` | `'44.91'` |
| `區域/區域淨週長` | `'44.91'` |
| `區域/區域總週長` | `'44.60'` |

Across 42 Zones on one story, calculated area and measured area differed on **36**. Resolve which
definition governs, say so in the output, and warn on the rows where they disagree.

### `elements_get_elements_related_to_zones` returns a flat, repeating list

It answers "which elements relate to this Zone" but the payload is a single `elements` array per
Zone with no type grouping and with GUIDs repeated (one GUID appeared three times in the first
result). `elements_get_relations_of_elements` answers the same question with
`elementsGroupedByType` *and* the per-segment `wallParts` evidence, so prefer it. Observed grouping
over 42 Zones: Wall 233, Riser 168, Tread 160, Object 98, Beam 73, Door 60, StairStructure 60,
Column 47, Window 24, Stair 8 — note that stair sub-elements dominate the count, so any "related
element" tally has to say which types it includes.

### Writes report success at the wrong level

`properties_set_property_values_of_elements` returns a per-element `executionResults` array. A call
whose elements *all* failed still comes back as a normal tool result — no `isError`, nothing that a
caller checking only for a tool-level error would notice:

```json
{"executionResults": [
  {"success": false, "error": {"code": -2130312909, "message": "Failed to set property value for element"}},
  {"success": false, "error": {"code": -2130312909, "message": "Failed to set property value for element"}}
]}
```

Observed end to end: renumbering 105 Zones on a locked story reported five successful batches in
1.1 s and changed nothing; the read-back returned the original value for all 105. **Check
`executionResults` element by element, and still read back.** This is the concrete case the
terminology map's "mutate then trust success -> re-read affected GUIDs" rule exists for.

### Editability is a precondition, and it is not visible in the property definition

The failure above is not a permissions model on built-in properties. On one locked Zone, writes to
`StaticBuiltIn`, `Custom` and `DynamicBuiltIn` properties all failed — every one of them with
`propertyIsEditable: true` in its definition. The same command against an editable Zone succeeded,
verified by read-back, and was restored the same way.

`propertyIsEditable` describes the property, not whether *this element* can currently be written.
The element-level check is the `IsEditable` element filter:

| Query | Result |
|---|---|
| `Zone` + `filters: ["OnActualFloor"]` | 105 |
| `Zone` + `filters: ["OnActualFloor", "IsEditable"]` | **0** |
| `Zone` + `filters: ["IsVisibleByLayer", "OnActualFloor"]` | 105 |
| `Zone` + `filters: ["IsEditable"]`, project-wide | 480 (of 2723) |

So the story was locked while remaining fully visible and readable. Filters combine as AND, and
`IsEditable` is the one that predicts whether a write will land. Run it as a precondition and report
the elements that drop out as skipped.

### Verified write path

With editability confirmed, the write path itself is sound. Renumbering 42 Zones on story `1FL`:
`executionResults` ok=42 / failed=0 in 2.0 s across two batches of 25, and read-back returned
`success=42 / unchanged=0 / other=0`. A single-element write followed by a restore to the original
value also succeeded both ways, so the operation is reversible.

Two properties named `區域號碼` exist in different groups (`區域` and `ID和類別`). They mirror one
underlying value: identical on all 105 Zones before the write, and identical on all 42 after it.
Writing either appears to be equivalent, but confirm the mirror by reading both rather than assuming.

### Story membership currently has to be inferred

`elements_get_details_of_elements` carries the floor index and is broken, so there is no direct read
of which story a Zone belongs to. The working substitute is `elements_get3_d_bounding_boxes` and
comparing `zMin` against the `project_get_stories` level table. This is an inference: record it as
one, and prefer `OnActualFloor` when the target story is the active one.

### The Add-On stops responding under sustained load

After roughly 7.4k GUID enumerations, 1766 property definitions and 105 Zone relation queries in
quick succession, `discovery_list_active_archicads` began returning a bare `[]` while port 19723
was still listening and `Archicad.exe` was still alive (its memory had grown from ~0.8 GB to
~2.3 GB). That empty array is the same response as "Add-On not loaded" and as "no Archicad running".

It recovered on its own: killing the leftover `archicad-server` processes, releasing their
`ESTABLISHED` connections to the port, and waiting a few minutes brought discovery back in 0.8 s.

**Working rule:** when discovery returns `[]`, stop and report that instance state cannot be
confirmed. Do not retry in a tight loop, and do not record it as a capability gap for whatever
command happened to be next.

## Re-verification triggers

Re-run the checks in this file when any of these change: the pinned wrapper version, the Tapir
Add-On version, the Archicad major version, or the project's calculation units. Record the new
combination in the table at the top rather than editing the old observations away.

## Reference

- [Terminology and boundary map](revit-archicad-terminology.md)
- [Element query pilot](pilot-element-query.md)
- `domain/tool-capability-boundary.md`
