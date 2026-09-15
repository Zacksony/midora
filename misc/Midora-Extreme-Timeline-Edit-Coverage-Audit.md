# Midora Extreme Timeline Edit Coverage Audit

Status: implementation-time correctness and scalability gate for the extreme timeline refactor.

This audit covers Project-backed editing commands only. WPF hit testing, drag-preview rendering,
and selection-adorner performance have separate Presentation/Desktop gates. Every row below preserves
the existing user-visible behavior; the refactor is not allowed to redefine collision, selection,
window, or undo semantics.

## Shared invariants

- Apply → Undo → Redo → Undo must restore the exact source objects/IDs, collection order, local range
  fingerprint, and imported duplicate content not touched by the edit.
- Logical, Template/SubVoice, and Direct MIDI Note collisions use incumbent-wins at exact
  `(start tick, key)`; edited Event/Parameter points use newcomer-wins at the exact formal lane key.
- Collision work is restricted to edited keys. Existing unrelated duplicates are retained.
- A bulk edit publishes one owner-collection revision per phase. It must not perform one full-owner
  lookup per selected ID or one revision publication per selected value.
- Full and incremental compilation equivalence remains enforced by command test fixtures that call
  `AssertCurrentCompilationMatchesFull`; edit-only scalability tests deliberately execute prepared
  commands directly so compilation scheduling cannot hide collection costs.

## Coverage matrix

Legend: **E** = existing exact behavior test; **N** = new cross-model oracle/performance gate;
**T** = targeted-collision/paged locality test; **—** = not applicable.

| Object / operation | Create / draw | Delete | Move | Copy-drag | Clipboard | Resize | Properties / value | Flip H/V | Scale | Transpose | Batch expression | Split / window | Undo/Redo + local fingerprint | Evidence |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Logical Note | E | E | E,N,T | E | E | E,N | E,N | E,N | E,N | E,N | E | — | E,N,T | `ProjectBatchTimelineEditCommandsTests`, `ProjectSelectionTransformEditCommandsTests`, `ExactTimelineCollisionPolicyTests`, `ExtremeTimelineEditCoverageTests` |
| SubVoice / Template Note | E | E | E,N,T | E | E | E,N | E,N | E,N | E,N | E,N | E | — | E,N,T | same classes plus `ProjectCreationEditCommandsTests`, `ProjectTemplateEventEditCommandsTests` |
| Direct MIDI Note | E | E | E,N,T | E | E | E,N | E,N | E,N | E,N | E,N | E | — | E,N,T | `ProjectPureMidiTimelineEditCommandsTests`, `ProjectObjectClipboardPureMidiTests`, `ProjectSelectionTransformEditCommandsTests`, `BulkTimelineEditScalabilityTests`, `ExtremeTimelineEditCoverageTests` |
| Logical Parameter point | E,T | E | E,T | E | E | — | E | E | E | — | E | — | E,T | `ProjectLogicalParameterPointEditCommandsTests`, `ProjectBatchTimelineEditCommandsTests`, `ProjectSelectionTransformEditCommandsTests`, `ExtremeTimelineEditCoverageTests` |
| SubVoice MIDI Event point | E,T | E | E,T | E | E | — | E | E | E | — | E | — | E,N,T | `ProjectTemplateEventEditCommandsTests`, `ProjectSubVoiceEventLaneEditCommandsTests`, `ProjectSelectionTransformEditCommandsTests`, `ExtremeTimelineEditCoverageTests` |
| Direct MIDI Channel Event | E,T | E | E,T | E | E | — | E | E | E | — | E | — | E,N,T | `ProjectObjectClipboardPureMidiTests`, `ProjectSelectionTransformEditCommandsTests`, `BulkTimelineEditScalabilityTests`, `ExtremeTimelineEditCoverageTests` |
| Direct MIDI Opaque / imported Meta-SysEx | import/paste E | E | E | E | E | — | tick/opaque payload E | — | — | — | — | MIDI split E | E,T | `ProjectObjectClipboardPureMidiTests`, `ProjectCreationEditCommandsTests`, `BulkTimelineEditScalabilityTests` |
| Logical Segment | E | E | E | E | E | E | E | E | E | E | E | E | E | `ProjectBatchTimelineEditCommandsTests`, `ProjectSelectionTransformEditCommandsTests` |
| MIDI Segment | E | E | E | E | E | E | E | E | E | E | E | E | E,T | `ProjectCreationEditCommandsTests`, `ProjectSelectionTransformEditCommandsTests` |
| Mixed Logical + MIDI Segment selection | — | E | E | — | — | E | E | E | E | E | E | window E | E | `ProjectMixedArrangementSegmentEditCommandsTests` |

## New gates added by this audit

`ExtremeTimelineEditCoverageTests` adds:

1. A deterministic randomized scalar oracle over all three Piano Roll Note models for move, saturated
   edge resize, velocity paint, horizontal/vertical flip, scale, and transpose. Every operation runs
   Apply → Undo → Redo → Undo and verifies exact values plus restored local fingerprints.
2. A 60,000-Note cold first-phase gate for Logical, SubVoice, and Direct MIDI Notes. Apply, first Undo,
   Redo, and final Undo are timed independently; every individual phase has a five-second upper bound.
3. A 60,000-point gate for Logical Parameter and SubVoice Event move/value edits with the same cycle
   and local-fingerprint restoration.
4. A 60,000-point Direct MIDI Event transform gate requiring one generation publication per phase.
5. A paged Direct MIDI exposed-Segment transform gate proving that hidden pages are not decoded or
   transformed.

## Command-path defects corrected during the audit

- Direct MIDI Event flip/scale now wraps all setters in one `BeginBatchChange` scope for Apply and Undo.
- Template Note velocity painting no longer performs `FindTemplateEvent` once per selected ID; it uses
  the collection ID index and validates the resolved batch.
- Logical Parameter and Value Curve single-point ordered insertion no longer scans the entire point
  collection for identity and insertion position; identity uses the ID index and formal order uses
  binary search over paged random access.
- SubVoice event-line upsert resolves all edited ticks from one frozen exact-tick query and performs
  replacement/creation/removal under one collection batch instead of querying/publishing per point.
- Selected Logical Note, Logical Parameter, SubVoice Event, and Value Curve clipboard capture uses
  directed ID resolution instead of full-owner enumeration.
- Segment-exposed Logical/Direct Note, Parameter, Channel Event, and Opaque Event transforms and batch
  edits query only the exposed tick window and then resolve formal mutable objects by stable ID.

## Deliberate remaining limits / follow-up gates

- Whole-Segment duplicate, copy, split, and left-edge content shift are semantically whole-owner
  operations. They remain O(content) unless the persistent sequence later gains subtree sharing; they
  must still be batched and may not regress to O(N²).
- Deleting an entire SubVoice event lane intentionally enumerates the lane to determine affected
  targets. This is proportional to content being deleted, not to unrelated Project content.
- Event Instrument/SubVoice definition duplication intentionally normalizes all collisions in the new
  owner. That is a whole-definition operation, not an ordinary local point edit.
- UI selection identity/visual persistence and drag-preview parity are not representable in Application
  command tests; Desktop/Presentation tests must verify them separately.
- Full-vs-incremental equivalence is extensively sampled by existing command suites, but not every
  60,000-item timing case invokes compilation; doing so would measure compiler scheduling rather than
  source-edit complexity.
