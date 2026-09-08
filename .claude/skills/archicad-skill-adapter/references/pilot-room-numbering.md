# Archicad Pilot: Room and Zone Numbering

## Scope

Translate the originating `room-numbering` method from Revit Rooms to Archicad Zones. Preserve target Story selection, top-to-bottom then left-to-right ordering, dry-run review, conflict checks, explicit write approval, and post-write verification.

This is a guarded write pilot. It does not claim that Archicad property writes are atomic or equivalent to one Revit transaction.

## Backend Route

- Revit selected: return to `.claude/skills/room-numbering/SKILL.md` and use `renumber_rooms_by_level` as documented.
- Archicad selected: continue below and use Zone GUIDs only.
- Target ambiguous: ask which application and project to use before reading room/Zone data.

## Archicad Workflow

1. Anchor one live project and port with `discovery_list_active_archicads`.
2. Discover a command that lists Zones for the requested Story. Confirm the actual Story name rather than mapping a Revit Level string by assumption.
3. Discover details, bounding boxes, or geometry sufficient to calculate a stable center point for each Zone. Confirm coordinate units and axes.
4. Discover the exact writable property that represents the project team's Zone number. Do not assume its display label or identifier.
5. Read all current candidate values and detect duplicates both inside and outside the target Story.
6. Build a dry-run table using the Domain ordering rule: descending Y groups, then ascending X within each group. Keep the requested prefix and increment only the trailing number.
7. Show Zone GUID, current number, proposed number, Story, center point, skipped Zones, and conflicts. Obtain explicit approval before writing.
8. **Confirm the target Zones are actually editable before writing.** Re-list the target set with the `IsEditable` element filter and intersect. Zones that drop out are locked and cannot be written; report them as skipped rather than attempting the write. See [Editability is a hard precondition](#editability-is-a-hard-precondition).
9. Discover and dispatch the property-write schema. If the runtime offers no atomic batch/rollback guarantee, state that limitation before the write and use the smallest verifiable batch. **Check `executionResults` element by element** — a tool call that returns without error can still report `success: false` for every element in it.
10. Re-read every affected Zone on the same port. Report successful, unchanged, failed, and conflicting GUIDs separately.

## Editability is a hard precondition

A write to a locked Zone does not raise an error anywhere the caller normally looks:

- the MCP tool call returns normally, with no `isError`;
- the response carries `executionResults`, one per element, each `{"success": false, "error": {"code": -2130312909, "message": "Failed to set property value for element"}}`;
- the model is unchanged, and a later read returns the original value.

Observed on 2026-09-08: writing 105 Zones on a locked Story reported five successful batches and changed nothing. The same command against an editable Zone succeeded, verified by read-back, and was restored the same way — so the failure is editability, not the write path.

`elements_get_elements_by_type` accepts `filters: ["IsEditable"]`. Note it is a *visibility/editability* filter, not a type filter; the element type is still carried by `elementType`. Combining `["OnActualFloor", "IsEditable"]` on the locked Story returned zero elements while `["OnActualFloor"]` alone returned 105 — that difference is the check.

## Observed Capability Hints

Use these only as discovery hints:

| Intent | Observed command hint |
|---|---|
| List Zones | `elements_get_elements_by_type` |
| Filter to writable Zones | `elements_get_elements_by_type` with `filters: ["IsEditable"]` |
| Zone center point | `elements_get2_d_bounding_boxes` (2D, metres) — `elements_get_details_of_elements` is currently broken |
| Infer Story membership | `elements_get3_d_bounding_boxes` `zMin` against `project_get_stories` |
| Read current number property | `properties_get_property_values_of_elements` |
| Write proposed number property | `properties_set_property_values_of_elements` |

## Units

Bounding boxes come from the geometry layer and are **metres**. The Domain's `yToleranceMm` default of `3000` is millimetres and must be divided by 1000 before it is compared with a bounding-box coordinate. Property values, including the Zone number, are display strings — see the unit contract in [revit-archicad-terminology.md](revit-archicad-terminology.md).

## Stop Conditions

- Story resolution is ambiguous.
- Zone center points, coordinate units, or the number property cannot be verified.
- Proposed values conflict with Zones outside the target Story.
- The selected Archicad instance changes between dry-run and write.
- The user has not approved the exact dry-run table.
- The target Zones are not editable, or editability was never checked.
- `executionResults` was not inspected element by element, or read-back was skipped.

## Live-Test Evidence

```text
backend: archicad
canonical_skill: room-numbering
domain_method: domain/room-numbering-workflow.md
adapter_reference: pilot-room-numbering.md
date: 2026-09-08
runtime: tapir-archicad-mcp 0.5.3 (started with `--with "mcp<2"`), Archicad 28, Tapir Add-On 1.5.8
project_port: 19723
project_type: solo
calculation_units: Meter / 2 decimals / DecimalDegree
target_story: 1FL (index 0, level 0.4 m, height 4.0 m)
story_membership: inferred from 3D bbox zMin vs project_get_stories
                  (elements_get_details_of_elements, which carries floor index, is broken)
number_property: 區域/區域號碼 — StaticBuiltIn, String, editable
                 (ID和類別/區域號碼 mirrors the same underlying value; 105/105 identical before,
                  42/42 identical after)
editability_check: project-wide editable Zones 480; on 1FL 42; none dropped
centre_points: elements_get2_d_bounding_boxes, 42 usable, 0 skipped, units metres
ordering: descending Y, banded at yTolerance 3000 mm = 3.0 m, ascending X within band
          -> 8 bands [1, 8, 6, 6, 7, 7, 1, 6]
dry_run_count: 42
start_number: 1F001 -> 1F042
conflicts: project zones 2723; distinct off-story values 1 ('<房間編號>'); collisions 0; duplicates 0
approval: pre-authorised by the file owner for this test template
write_guarantee: non-atomic (batched 25, executionResults checked per element)
write_result: executionResults ok=42 failed=0, 2.0 s
verification: success=42 failed=0 unchanged=0 other=0
```

A companion negative result from the same session is recorded above under
[Editability is a hard precondition](#editability-is-a-hard-precondition): the identical procedure
against the locked Story `7FL` produced 105 dry-run rows, five "successful" batches, and zero
changed Zones.

## Reference

- [Terminology and boundary map](revit-archicad-terminology.md)
- [Archicad MCP runtime facts](archicad-runtime-facts.md)
- `domain/room-numbering-workflow.md`
- `domain/session-context-guard.md`
