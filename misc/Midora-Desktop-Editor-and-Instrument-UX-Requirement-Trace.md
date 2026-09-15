# Midora Desktop Editor and Event Instrument UX Requirement Trace

状态：历史增量实现记录。本文中涉及 Project Panel、Project tree、Event Instrument Folder、Unfiled、`LibraryFolderId`、Logical Track 直接绑定 Event Instrument，以及旧 Track order 的条款，均已由 SRS 第 24 章、INV-058～064/073～074、ADR-CORE-046 与 ADR-UI-041 取代，不得作为当前模型或 UI 要求。其余未冲突的点集编辑、颜色、对话框和交互记录仍可作为回归历史。

## Scope and authority

- Product-owner request dated 2026-08-17: point-set Logical Parameter editing, Event Instrument clipboard/duplicate support, per-instrument editor session state, color editing and timeline color projection, consistent selection/conductor interaction, custom dialogs, English UI, configuration fixes, and binding/playback reliability.
- SRS anchors: 17.2.3, 17.6, 18.1.3-18.1.4, 18.2.5, 18.3-18.4, 18.7-18.8, 20.3.7, 20.11, 20.13 and 20.15; cross-system invariants are governed by section 22.
- Explicit product-owner override: this increment adds copy/paste for Event Instruments even though SRS 20.6.5 excludes them from the ordinary object clipboard. SRS text is not changed. Paste remains an explicit project edit and creates fresh stable IDs.

## Inputs and formal outputs

- Inputs: Project Event Instruments, Logical Tracks, Segments, Logical Parameter lanes, Conductor events, current editor tool/modifiers, and session-only viewport state.
- Formal Project outputs: Event Instrument clones, binding/color/configuration edits, Logical Parameter points, and Conductor/selection edits produced through Application edit commands and Undo/Redo.
- Presentation outputs: point-set lane snapshots, fixed-size Conductor event glyphs, normalized Segment palette derived from the bound Event Instrument color, raw track-header color stripe, custom modal dialogs, and English UI text.

## Boundaries and ownership

- Project source owns instruments, colors, bindings, Segment data, points, and Conductor events. Stable IDs remain identity.
- Per-instrument zoom, scroll, selected lower editor and splitter heights are session UI state only; they are neither persisted in `.midora` nor compiled.
- Clipboard payloads are copy-time snapshots. Paste/duplicate remap every contained stable ID and preserve internal references only through that remapping.
- Instrument color projection changes presentation only. It must not affect compilation, playback, MIDI export, or audio rendering.
- Playback continues to consume only the current canonical compiled result; a stale background result must never be published for a newer Project revision.

## Edge and failure conditions

- Invalid loop or color input does not partially mutate the Project; loop fields commit only after both values parse and validate.
- Rebinding an already-bound track requires explicit confirmation after the drag operation has completed.
- Unmodified Select-mode blank clicks clear object selection; Ctrl/Alt marquee semantics remain unchanged.
- Parameter point edits clamp/validate tick and value through Application commands and commit as one undoable edit.
- Modal errors and confirmations use Midora-owned windows. Operating-system file/folder pickers remain native integration surfaces.

## Diagnostics and non-goals

- User-facing UI text and diagnostics remain English. Invalid operations surface their complete English message through custom dialogs.
- This increment does not change canonical Event Instrument semantics, audio scheduling, `.midora` persistence format, or the SRS itself.
- It does not persist session viewport state across application restarts.

## 2026-08-17 workspace, playback recovery, and resource increment

- Inputs: open Project/workspace state, explicit Compile, track-header context, held-preview failures, and the currently verified embedded SoundFont runtime resource.
- Formal outputs: no new canonical musical semantics. The embedded SoundFont extraction command copies the exact embedded resource bytes to an explicitly selected external path and does not mutate Project source data. Track-wide Segment selection and workspace focus are session-only UI state.
- Workspace boundary: Arrangement is the permanent index-zero workspace for every open Project. It cannot be closed or reordered; other workspaces remain session-only and reorder no earlier than index one.
- Focus boundary: workspace and Event Instrument section switches focus their non-editing tab host. They must not implicitly focus a search field, editor, toggle, or command button, and background compile/diagnostic refresh must not change the active workspace.
- Playback failure boundary: recovery from a prior Preview error occurs before the next application playback task is registered. Stop also consults the Playback Controller state so a coordinator bookkeeping mismatch cannot turn Stop into a no-op. Reset Playback Engine remains an explicit user command.
- Failure conditions: missing/mismatched embedded SoundFont runtime data rejects extraction; cancellation or copy failure never publishes a partial destination; an existing destination requires picker-authorized overwrite.
- Diagnostics: a clean explicit compile leaves the current workspace unchanged. A completed compile with warnings or errors may open Diagnostics, without focusing its search input.

## Standalone Event Instrument interchange decision

- Feasibility: an Event Instrument payload is mostly self-contained. Its direct structural dependency is Project TPQN, while effective sound can still differ with the target Project SoundFont and Project reset defaults. Standalone interchange is nevertheless not only a UI command: it creates a public persisted format and requires a versioned container, stable-ID remapping, single/batch conflict policy, TPQN conversion and rounding rules, overflow/collision validation, transactional import, and unknown-version handling.
- Current authority: SRS 7.2.1, 7.20, 7.21, 16.29, 20.5.7, and 21.2 explicitly exclude standalone/cross-Project Event Instrument import/export from the initial release. No extension, manifest, compatibility contract, or TPQN resampling rule is currently specified.
- Decision for this increment: do not introduce an ad-hoc format. Recommended follow-up is one versioned batch-capable package whose manifest records source TPQN and format version, with one Event Instrument protobuf payload per entry; a single export is the same package with one entry. Import must allocate fresh stable IDs and remap every internal reference atomically.
- Required product decision: approve the standalone format/extension and exact rational TPQN conversion policy before implementation, because these choices affect file compatibility and audible timing.

## 2026-08-17 rejected direct-edit rollback and transport icon increment

- Inputs: direct Project-backed text, choice and Boolean edits in Event Instrument Configurations, object-owned Properties editors and Project Settings. Text remains a local draft while the user is typing; a focus-loss or Enter commit, closed choice popup, or Boolean click is the submission boundary.
- Formal output: only a successfully validated Application edit command may change Project source or create Undo history. A rejected command produces no Project mutation and no Undo entry.
- Rejection presentation: after reporting the concrete failure, the submitting control is reprojected from the current Project model so it cannot continue to display a value that was never accepted. This includes Event Instrument configuration/lifecycle/overlap/isolation/loop/follow-velocity controls, object-owned Properties choices and Booleans, and Project Settings fields and choices.
- Boundary: modal creation/edit dialogs and the C# Mapping Function draft retain invalid local input for correction because those values are explicitly staged drafts rather than failed direct Project edits.
- Presentation-only output: Reset Playback Engine uses the filled Fluent System Icons `Flash 20` geometry stored in the shared icon resource dictionary. This changes neither playback behavior nor Project data.
- Playback/Preview edit-lock presentation: Event Instrument Project-backed text boxes, choices and check boxes are disabled whenever `CanEditProject` is false, including main playback, realtime Preview and foreground Project tasks. A focus-loss or routed-input race at the lock transition silently restores the formal Project value and must not report the expected edit-lock exception. Session-only scenario inputs and navigation remain outside this lock rule.

## 2026-08-17 transport, reorder, rename, and Project statistics increment

- The command bar exposes one primary transport button: Stopped shows the red Play presentation; active playback shows Stop with a gray fill and red border; an unavailable command is transparent with a disabled icon. Reset Playback Engine and Loop remain separate commands.
- Workspace tab focus must not draw a focus rectangle around the editor surface after the global Space shortcut. Workspace switching must not force keyboard focus onto the tab host.
- Project-tree Logical Track and Event Instrument rename commands use an explicit Midora modal text dialog. Instrument Folder retains its existing inline rename because it is outside this request.
- Reorderable UI containers use total pointer displacement with a minimum 10-DIP Euclidean threshold before starting drag-and-drop. This applies to workspace tabs, sortable Project-tree rows, and Arrangement track headers; it does not alter timeline note/event/Segment editing gestures.
- Project `Notes` is no longer free-text metadata. Project Settings reads `Notes` as the positive-velocity NoteOn count and `Events` as the total canonical MIDI channel-event count from the latest formal full-Project compilation. A non-consumable result exposes zero formal events. These statistics are derived in one allocation-free pass over the already-materialized canonical event array, are not persisted, and do not enter Undo / Redo. Project Settings also shows live total Project work time.
- This approved development-stage schema replacement removes `notes` from metadata schema v1 without changing the Project/file-format version. Files containing the removed strict-schema field are not a compatibility target for this increment.

## 2026-08-17 transport glyph, compiled statistics, and diagnostic status increment

- The primary transport's local visibility styles remain based on the shared command-bar Fluent icon style; otherwise a local style replaces the implicit `FluentIcon` template and makes both Play and Stop geometries invisible.
- The bottom-left status dot reflects the current whole-Project compiler diagnostics used by the adjacent issue summary: green for no Warning or Error, orange for one or more Warnings and no Error, and red for one or more Errors. Error takes precedence when both severities are present.
- Project activation raises change notifications for both the primitive `CanPlayback` state and the bound derived transport properties (`CanTogglePlayback`, action and tooltip). The Play button must therefore become clickable immediately after New/Open completes whenever playback is available, without waiting for a playback-state transition.
- The status bar's fixed left sequence is `diagnostic dot + Errors/Warnings → Compile → SoundFont → Save → Playback`; the right side remains reserved for the transient message.
- Activating the Diagnostics Workspace focuses its non-editing root surface after the content template is loaded. WPF focus fallback must not place the caret in the first search field; an explicit user click can still focus and edit that field normally.
- The status bar's Errors/Warnings text is an explicit Diagnostics navigation target. It highlights on hover, uses the Hand cursor, and activates the existing Diagnostics Workspace on a left click without introducing a separate navigation path.

## 2026-08-17 explicit Follow Playback interaction increment

- Authority: the product owner's explicit UI interaction request refines the existing Follow Playback behavior in SRS 17.2.2 and 20.1.9. The existing Application Preference remains the only stored setting; no Project source, canonical result, audio semantics, or `.midora` data changes.
- The global command bar exposes a Follow Playback toggle immediately after Loop. It uses the filled Fluent System Icons right-arrow geometry and remains synchronized with `View > Follow Playback`.
- Follow applies only while playback/Preview is active and the active Arrangement or Segment timeline has a projected playback cursor. Ordinary follow retains the existing 10%–90% viewport band and places an out-of-band cursor at the 20% viewport anchor.
- A captured middle-button timeline pan or captured left-button bottom-overview drag is an explicit transient viewport interaction. While captured it suppresses automatic `StartTick` writes; release or lost capture immediately returns the viewport to the current playback cursor's 20% anchor and resumes follow.
- While those follow conditions hold, mouse-wheel input over the bottom overview is consumed and cannot scroll the horizontal viewport. Follow disabled, playback stopped, non-timeline workspaces, and Segment workspaces that do not contain the playback cursor retain their existing navigation behavior.
- Failure boundary: a missing projected cursor never moves the viewport. Follow calculations clamp at tick zero and use overflow-safe range arithmetic. The transient capture state is session-only and is neither persisted nor undoable.

## 2026-08-18 Logical Track clipboard and selected-object batch editing increment

- Authority: the product owner's explicit request adds Logical Track Cut/Copy/Paste/Duplicate, `A` as the focused timeline Snap toggle, Segment left-edge expansion, exact newcomer collision removal, selected-object transforms and formula-based batch editing. This explicitly overrides the Logical Track clipboard exclusion in SRS 20.6.5 and the older exact-conflict rejection behavior where the two conflict. The SRS source remains unchanged.
- Inputs: current Project and history state, focused Arrangement/Segment/SubVoice surface, stable-ID selection and Primary Selection, visible Segment content window, active Logical Parameter or MIDI Event target, and user-entered English dialog drafts. UI calculates no musical result outside the Application command.
- Formal outputs: one atomic `IProjectEditCommand` for each Track clipboard mutation, edge resize, flip, scale, transpose or batch apply. Full and incremental compilation continue to consume the resulting Project source through the canonical path. Clipboard copies and presets do not directly modify canonical data.
- Collision boundary: exact Logical Note `(Segment, start tick, key)`, Logical Parameter Point `(Lane, tick)`, Template Note `(SubVoice, tick, key)` and non-Note Template Event `(SubVoice, exact scalar target, tick)` use incumbent-wins/later-object-discard. Only explicitly touched owners are scanned. Different-tick Note duration overlap is unchanged. Point/Segment overlap caused by Flip, Scale or formula Batch Edit rejects the whole operation as explicitly requested.
- Segment exposure: Segment-level operations affect every Note intersecting and every parameter point contained by the exposed half-open content window; objects wholly outside remain untouched. Moving a batch result before the exposed left edge expands the Segment left while preserving all hidden content at its Project-absolute ticks. Crossing Project tick 0 deletes that calculated object; overlapping a neighboring Segment rejects the entire command.
- Scaling: the default span is `min(start)` through `max(start + length)` for length-bearing objects and `min(tick)` through `max(tick)` for points. Scaling is left-anchored, finite and positive; every final integral value uses `AwayFromZero`. Segment dialogs explicitly choose exposed-content-only or exposed-content-and-Segments.
- Transpose: the fixed English list covers `+12` through `-12`. Note keys outside `0..127` are deleted in the same Undo entry. Segment transpose affects exposed Notes only.
- Batch expressions: blank, direct number, `n%`, `*n`, `/n`, `+n`, `-n`, and restricted `=` numeric expressions are supported. Result-variable cycles are rejected statically. The restricted syntax prevents loops and arbitrary API calls; Roslyn only parses the expression tree and a Midora binder creates the `System.Linq.Expressions` delegate without Emit, ALC or TPA references. The whole object batch also has a 10-second runtime guard. Velocity/point values clamp to their target ranges, Gate clamps to at least one tick, key overflow deletes Notes, and Tick uses context-specific expansion/deletion rules.
- Runtime ownership: Selection state bookmarks, clipboard payloads, `A` shortcut routing, transient dialog validation, compiled Batch Edit delegates and viewport state are session-only. Note/Event presets are external local JSON resources under `<ProgramRoot>\Data\Presets`; they are deliberately not Project data or Application Preferences, and Midora does not probe or migrate the retired `%LOCALAPPDATA%\Midora\Presets` location.
- Failure and diagnostics: invalid formulas, dependency cycles, invalid factors, arithmetic overflow and transform overlap fail before visible mutation and do not create History. Validation and application errors use English Midora dialogs; a validation error leaves the editing dialog open. Corrupt preset files are isolated without modifying or deleting them.
- Project-tree hardening: a missing `LibraryFolderId` no longer drops an Event Instrument from the tree; it is presented as Unfiled. Logical Track reorder freezes exact before/after reference order and refuses Apply/Undo if membership/order changed unexpectedly, preventing a single reorder command from mutating unrelated Track order.
- Verification: the focused transform/collision suite passes 13/13; the complete core solution passes 949/949, Desktop Presentation passes 100/100, Desktop session/UI tests pass 56/56, all with zero skipped tests. The `Midora.Desktop` Release build completes with zero warnings and zero errors.
- Non-goals: no cross-Project/system clipboard serialization, no arbitrary C# execution, no preset sync, no change to canonical consumer semantics, no change to `.midora` schema, and no automatic repair of unrelated pre-existing invalid collisions.

## 2026-08-18 dense event-trace and selection-shortcut correction

- Authority: the product owner's explicit follow-up requires event/parameter drawing to emit every effective operation position, adds `Shift + Right Drag` horizontal lines, and registers `Ctrl+Q / Ctrl+T / Ctrl+E`. The shortcut registration overrides the conflicting initial shortcut exclusions in SRS 20.12.13; the SRS source remains unchanged.
- Inputs: the captured pointer polyline, frozen Pointer Down button/modifiers, focused TimelineSurface, effective Operation Subdivision, Snap state, Project Time Signature Map, active lane range, and current stable-ID selection.
- Formal output: MouseUp produces one ordered batch of Logical Parameter or SubVoice MIDI Event point upserts. Snap disabled enumerates every tick; Snap enabled enumerates every configured operation-grid position, including variable Bar boundaries. Later passes over a tick deterministically replace earlier transient samples before the single Project command is submitted.
- Interaction output: ordinary right drag linearly interpolates start-to-end values; Shift+right drag holds the start value across the horizontal range. Scale, Transpose and Batch Edit shortcuts invoke the same handlers, validation, dialogs, locking and Undo semantics as their context-menu entries.
- Focus/failure boundary: shortcuts only resolve from the currently focused supported timeline and never from text input, popup/menu state, stale context-menu targets or an empty/incompatible selection. Capture loss clears the transient trace without committing. Range end remains exclusive.
- Presentation fixes in the same correction: the Scale dialog binds its read-only current length OneWay, preventing WPF activation failure; Batch Edit formula boxes locally override the fixed global TextBox height so visually wrapped single-line expressions grow vertically inside the dialog ScrollViewer.
- Verification: the complete core solution passes 949/949; Desktop Presentation passes 108/108; Desktop session/UI tests pass 56/56; the `Midora.Desktop` Release build completes with zero warnings and zero errors.
- Non-goals: no persistent Curve model, no per-MouseMove Project mutation, no alternate compile path, no schema change, and no SRS source edit.

## 2026-08-18 dense event-point raster performance increment

- Authority: the product owner's explicit performance request requires Segment Logical Parameter and SubVoice MIDI Event points to follow the established Velocity raster-cache direction, while keeping point size invariant under horizontal and vertical view zoom. SRS 18.2.5, 18.4, and 20.10 continue to require self-rendered lanes and stable-ID editing rather than per-event WPF controls.
- Inputs: immutable active-lane `TimelineRenderSnapshot`, immutable `TimelineSelectionSnapshot`, exact device pixels per tick, exact device pixels per normalized value, current value viewport, DPI, palette, and visible tile coordinates.
- Presentation output: committed points and their Selection/Primary rings are generated as DPI-aware `256 × 256` two-dimensional raster tiles with fixed `4 DIP` point radius and `6 DIP` selection radius. Visible tiles are composed over the existing value grid; hover, current drag/create preview, trace, marquee, and cursors remain transient vector overlays.
- Performance boundary: `TimelineSurface.OnRender` no longer enumerates or issues a WPF primitive for every visible event point. Candidate query, deterministic ordering, and pixel generation execute on the existing bounded raster workers; only visible tiles plus one prefetch ring are requested. Dense offscreen populations are culled by the immutable interval index before rasterization.
- Identity/edit boundary: bitmaps never participate in hit testing or Project mutation. Hit, marquee, copy/move/delete, Undo/Redo, compilation, and consumers continue to use original stable IDs and formal point values. Selection revision enters the tile generation so a large selection cannot reintroduce a per-point foreground path.
- Runtime ownership and failure: the new tiles share the process/session-only `256 MiB` LRU, bounded in-flight requests, frozen bitmap publication, and stale/failure behavior established by ADR-UI-018 through ADR-UI-022. Nothing is persisted, compiled, exported, or made audible by this increment.
- Verification: the Presentation suite passes 113/113, including fixed-size zoom, placement, gutter continuity, cached Selection/Primary, and a 100,000-point offscreen-culling case; Desktop session/UI tests pass 56/56. The `Midora.Desktop` Release build completes with zero warnings and zero errors.
- Non-goals: no Skia/D3D backend, no Curve model, no Project/schema/canonical change, no alternate event semantics, and no computer-use validation.

## 2026-08-23 explicit Properties and editor interaction correction

- Authority: the product owner's explicit follow-up requires modal, explicit Properties submission; complete creation drafts for Logical Parameters, Mapping Steps and Envelope Presets; reliable Arrangement secondary links and Draw-mode multi-Selection; common dark dismissable menus; outer-form wheel routing; and a bidirectionally resizable Conductor lower editor.
- Inputs: current Project revision, stable-ID selection, explicitly selected Mapping Chain owner, local modal draft values, pointer button/modifiers and whether a Draw gesture crossed into an actual move/resize operation.
- Formal outputs: Logical Parameter creation, Mapping Step creation or owner relocation, and Envelope Preset creation each publish one validated Application edit command only after the dialog OK action. Mapping Step relocation preserves the Step stable ID and restores its exact former owner, index and values on Undo. Cancel and rejected validation publish no Project mutation and no History entry.
- Presentation outputs: SubVoice Properties excludes Initial State values because their dedicated editor remains authoritative; `Ctrl+P` invokes the active supported Properties target; Event Instrument section headers no longer duplicate Properties buttons; closed input controls route wheel gestures to the nearest scrollable outer form; a ComboBox popup retains its own independent scrolling; Piano Roll key rulers suppress context-menu creation entirely.
- Arrangement interaction: Logical Track instrument labels are explicit Event Instrument navigation links; Pure MIDI Track route summaries open MIDI Route Settings. A plain Draw-mode Segment click replaces Selection only on MouseUp when no move/resize occurred; an actual drag keeps the existing multi-Selection, and right-click never applies that replacement rule.
- Runtime ownership: menu popup state, hover outlines, deferred click state, Conductor splitter height and outer-scroll routing are session-only UI state. They are not Project source, are not persisted and do not affect compilation, playback, export or audio rendering.
- Failure boundaries: a context menu remains enabled as a popup even when all of its commands are disabled, so it keeps the shared style and normal dismissal lifecycle. Menu scrollbars use `Auto` and appear only when content exceeds the available popup height. Invalid modal drafts remain open with their error and do not partially apply.
- Non-goals: this correction does not merge Mapping Chain and Mapping Step domain concepts, does not change mapping evaluation semantics, does not change `.midora` data, and does not introduce a new canonical consumer path.

## 2026-08-23 interaction regression hardening

- Inputs: Arrangement header hover coordinates, a Draw-mode pointer gesture over an already selected Segment, WPF `GridSplitter` star/pixel height writes, `Ctrl+P`, and modal OK/Cancel input.
- Presentation outputs: only the hit Logical/Pure MIDI Track secondary link receives hover emphasis; Conductor starts with equal star allocation and accepts both star and pixel resize writes; an unmoved Draw click replaces Segment selection only at Pointer Up, while a move/resize keeps the selection; every confirmation dialog consumes the shared red Primary/Cancel button contract with a `112 DIP` action width, Enter default and Escape cancel.
- Failure boundaries: Properties commands no longer call a routed-event handler through a synthetic `RoutedEventArgs`; command routing invokes a non-event core method, so no code writes `Handled` on an argument without a `RoutedEvent`. Invalid dialog drafts continue to keep the dialog open and publish no Project mutation.
- Ownership: hover, deferred-click and splitter height remain Project-session UI state. This correction changes no Project source, persistence format, compilation, playback, MIDI export or audio-render semantics.

## 2026-08-23 secondary-link and dialog-cancel correction

- Inputs: the Arrangement secondary-label hover target; Escape from a custom modal Dialog while focus is in a TextBox, List, Button or ordinary dialog surface; and title-bar Close.
- Presentation outputs: a secondary navigation label keeps one inset low-emphasis border and changes that existing border to red on hover, rather than drawing an expanded overlay that is clipped by the lane-header content clip. Every XAML Dialog registers exactly one Escape cancel target through its bottom action; title-bar Close explicitly executes the same cancel result without registering a second `IsCancel` access key.
- Failure boundary: an open ComboBox/Popup may consume the first Escape according to the common innermost-state rule. Otherwise Escape closes the cancellable Dialog regardless of focused control and does not merely move focus to a caption button.
- Ownership and non-goals: hover and keyboard focus are session-only presentation state. This correction changes no Project source, Undo command, persistence, compilation, playback, export or audio-render behavior.

## 2026-08-23 lower-editor collapse and continuous-value background correction

- Inputs: the Segment `Lanes` toggle, the current session-only lower-editor height, WPF row minimum/height bindings, and the rendered size of Velocity, MIDI Event and Logical Parameter value surfaces.
- Presentation outputs: hiding a Segment lower editor publishes both zero row height and zero minimum height in the same property-change turn, so the first toggle collapses it without requiring workspace template reconstruction. Restoring it reuses the prior session height. Continuous value surfaces use one uniform value-domain background plus their normalized value grid; they are not projected as an arbitrary stack of `LaneHeight` rows and therefore do not acquire height-dependent alternating bands or row boundaries.
- Ownership and failure boundaries: visibility, remembered splitter height and viewport dimensions remain Project-session UI state only. This correction changes no points, Notes, Project source, Undo history, persistence, compilation, playback, export, cache or hit-testing semantics.
- Verification: view-model notification coverage checks row height and minimum height together; WPF rendering coverage checks both Velocity and Event Lane surfaces at pixels that previously landed in different synthetic rows.

## 2026-08-23 MIDI import report/progress and Arrangement preview prewarm

- Inputs: an SMF source stream and its two-pass import metrics; the complete ordered compatibility Info/Warning report; the active Arrangement snapshot, Segment content fingerprints and presentation colors.
- Formal outputs: MIDI import still produces the same detached candidate Project and compatibility findings. The first pass reports parsed source bytes while discovering events; after the total event count is known, the second pass reports processed events/total events, followed by bounded validation/finalization progress. Progress is presentation-only and cannot alter import order, diagnostics or atomic adoption.
- Presentation outputs: the status bar keeps a short import summary plus the exact complete report used by the initial dialog. `View` reopens that complete report until Dismiss or a later status replaces it. Arrangement Segment previews use a maximum fixed `96 pixels / quarter note` reference scale, 64-pixel height and 256-pixel tiles, plus fixed half-octave (`1 / 2^(n/2)`) lower LODs; viewport zoom selects the first fixed target level that does not downsample a source pixel rather than generating an arbitrary exact-scale identity. All tiles share one device-pixel-snapped full-Segment transform, so pan only translates their sampling phase. Snapshot publication queues one complete, at-most-four-tile LOD per Segment for background prewarm with at most two raster workers. Visible Segments atomically publish a same-version, viewport-independent fallback at twice that horizontal resolution, bounded to eight tiles; zooming farther out scales the retained fallback instead of clearing it. Only after every visible fallback is ready may new current-LOD refinement begin. A finished target tile exclusively owns its horizontal range, while the retained fallback is drawn only in remaining gaps.
- Ownership and performance boundary: import progress, retained status detail and preview work queues are session-only state. Preview tiles share the existing bounded process LRU and are content/color keyed; they are never persisted and never become hit-test, compile, playback or export input. Background preview work must not acquire the Project edit lock or run on the UI/audio producer threads.
- Failure boundaries: import failure remains structural and explicit; progress cannot convert failure into success. A failed/stale preview task leaves a missing visual tile and may be retried by a later snapshot/draw, but cannot block editing or audio. A new snapshot invalidates the old warmup queue generation without requiring eviction of still-valid content-keyed tiles.
- Verification: streaming import tests assert precise second-pass event completion and monotonic finalization progress; Desktop session tests assert full report retention and clearing on Dismiss; rendering tests exercise source-backed note/event tiles, bounded zoom-out LOD/warmup tile counts, and the complete asynchronous cache-to-`TimelineSurface` composition path at normal and extreme zoom.

## 2026-08-23 extreme paged-MIDI edit latency correction

- Input: Direct MIDI stable-ID Selection in a paged MIDI Segment, its active timeline projection, one atomic move/resize/delete/copy/properties command, and exact touched collision keys.
- Formal output: unchanged Project edit semantics and one Undo entry. Note exact-start collisions remain incumbent-wins; Event exact-target collisions remain newcomer-wins. Imported duplicates outside touched keys remain intact.
- Runtime implementation boundary: UI resolves selected IDs in one source batch, freezes Selection bounds/earliest audition item once per Selection revision, and reuses those metrics throughout a drag. Application resolves IDs and exact collision targets in batches; Domain publishes one generation change per multi-field batch.
- Failure/performance boundary: ordinary edits must not enumerate a multi-million-note Segment merely to find selected IDs. Collision checking may scan only pages that can contain the touched key set. A missing drag-preview tile remains an asynchronous presentation miss and may not trigger per-frame whole-selection vector rebuilding on the UI thread.
- Persistence/consumer boundary: no `.midora`, canonical, compiler, playback, MIDI export or audio-render semantic change. New lookup/materialization caches are runtime-only and bounded by objects actually queried or edited.
- Verification: an automated fake source advertises 6.7 million Notes and rejects both full `GetNote` enumeration and per-ID `FindNoteIndex`; Move/Undo passes through batch lookup. The opt-in `Krash Noets 6.7 million.mid` gate imports the real sample and applies/undoes a 4,096-Note paged edit successfully.

## 2026-08-23 Direct MIDI copy selection and Arrangement label correction

- Inputs: a Direct MIDI Note copy-drag committed from either `Ctrl` or `Ctrl+Alt`, exact-collision removal performed by the Application command, Arrangement Track share/detach drag projection, and Application Preferences audio-cache root selection.
- Formal selection result: after a Direct MIDI copy-drag, the active Segment selection is replaced with only the newly allocated copied Note IDs that still exist after incumbent-wins exact collision resolution. Source Notes and discarded copies are not selected. Logical Segment and SubVoice behavior is unchanged.
- Arrangement presentation: Conductor has no secondary label. Shared and independent Logical Tracks display only the Event Instrument name; Shared and independent Pure MIDI Tracks display only the same concise route text. Every valid secondary navigation label uses the same low-emphasis rounded outline.
- Cache failure correction: timeline formatted-text cache identity includes the foreground brush. Pointer hit testing therefore cannot seed a transparent measurement object that is later reused for visible Arrangement text after a share/detach drag.
- Preference boundary: Audio Cache Local Cache Root is a browse-only path. Its TextBox remains enabled for selection/copy but is read-only; the folder picker remains the only UI mutation entry.
- Persistence and consumer boundary: this increment changes no Project source schema, canonical compilation, playback, export or audio-render semantics. Arrangement hover/text cache and post-command selection remain session-only state; the selected cache root continues to be stored in Application Preferences through its existing contract.

## 2026-08-23 Logical parameter overview, import focus and MIDI audio audit

- Inputs: Logical Segment Note/Logical Parameter source data, the application-level Audio Cache Local Cache Root preference, a successfully committed Open MIDI as New Project task, and mouse-wheel input over an open ComboBox popup.
- Arrangement output: Logical Parameter points are projected into the existing independent Segment event-preview layer above the Note layer. Values use the parameter definition's display range, lines retain the existing event color/50% opacity/minimum one-device-pixel contract, and cache identity includes the normalized point source. Logical and Pure MIDI Segments share the same fixed-LOD tile/cache/warm-up path; preview data remains presentation-only.
- Focus output: after a MIDI import report has closed, focus returns on the input dispatcher priority to the active timeline surface, or to the workspace tab host when no timeline is active. Space and single-key editing shortcuts therefore do not remain captured by the completed task/report surface.
- ComboBox output: a wheel notch over an open ComboBox popup advances exactly one logical scroll line. Closed ComboBoxes remain protected from wheel-driven selection changes, and the popup's own ScrollViewer remains the only scroll target.
- Verified range-start behavior: both Direct MIDI held channel state and Logical Parameter-mapped non-Note state are already restored from the latest value before a non-zero compilation/playback range. Earlier NoteOn events are not retriggered. No consumer or audible-semantics change is made for this verified behavior.
- Verified SysEx boundary: imported opaque SysEx/Meta is preserved in Project/canonical SMF projection and MIDI export, but the current canonical audio projection, fixed-size scheduled-message plan and worker IPC carry only short channel messages. Consequently opaque SysEx is not submitted to BASSMIDI. This matches SRS 15.6.4 and 23.13.2 and is not changed in this increment: arbitrary pass-through would require a formal routed SysEx execution contract, variable-payload IPC/cache fingerprints, range-start reconstruction, monitoring rules and explicit treatment of global versus channel-addressed vendor messages. Meta events must never be sent to the synthesizer.
- Failure/persistence boundary: cache rendering, focus restoration and popup scrolling do not modify Project data or Undo/Redo. The Local Cache Root continues to be persisted only in Application Preferences. The SysEx audit does not modify SRS audio semantics or project persistence.

## 2026-08-25 Arrangement and Piano Roll pointer coordinate readouts

- Inputs: the pointer position within the shared `TimelineSurface`, its current viewport, Operation Grid/Snap settings, Arrangement absolute tick domain, and Piano Roll local tick/MIDI lane mapping.
- Presentation outputs: Arrangement displays `(absolute tick)` and Logical Segment, Pure MIDI Segment, and SubVoice Piano Rolls display `(local tick, Key Number)` at the left edge of their upper-right tool groups, followed by a separator before the remaining tools. Readouts explicitly use the Primary text foreground shared with Event/Parameter Lane coordinates. The entire readout group is hidden outside the corresponding timeline content; Piano Roll blank space outside the 128-key domain is also excluded.
- Coordinate and ownership boundary: readouts reuse the same viewport and snapping transforms as editing. They are transient session UI state only and do not move the Edit Cursor, mutate Project data, enter Undo/Redo, affect compilation/canonical consumers, or persist.
- Failure boundary and verification: unsupported surface modes expose no readout, while Event/Parameter Lane `(tick, value)` behavior remains unchanged. Automated WPF coverage checks Arrangement absolute tick, Piano Roll local tick/key conversion, and content-boundary clearing.

## 2026-08-29 Follow Playback disabled default

- Input and persistence owner: a missing Application Preferences file, a `desktopUi` object without `followPlayback`, or an explicit UI-preference reset. An explicitly persisted `true` or `false` remains authoritative.
- Runtime output: Follow Playback initializes disabled for the default cases above; the View menu and command-bar toggle continue to mirror the stored preference. Enabling it still immediately follows the active playback cursor through the existing interaction contract.
- Boundary: this changes only the program-level preference default. It does not modify Project source, `.midora`, Undo/Redo, playback state, canonical compilation, MIDI export, audio rendering, or the follow algorithm.
- Verification: Application Preferences tests cover a missing file, a legacy/missing property, and round-trip preservation of an explicit enabled value.
