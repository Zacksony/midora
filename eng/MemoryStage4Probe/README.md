# Stage 4 isolated persistence probe

Run from the repository root. All output belongs below `.tmp/memory-stage4/`; the
probe never opens or changes a user's project and never writes `dist/`.

The executable deliberately uses the persistence test friend assembly name. Build
the same source against separate frozen baseline/current directories containing
`Midora.Domain.dll`, `Midora.Persistence.dll`, and their dependencies:

```powershell
dotnet build eng/MemoryStage4Probe/MemoryStage4Probe.csproj -c Release -p:MemoryProbeReferenceDirectory=D:\Programing\midora\.tmp\memory-stage4\baseline -p:OutDir=D:\Programing\midora\.tmp\memory-stage4\probe-old\
dotnet .tmp/memory-stage4/probe-old/Midora.Persistence.Tests.dll logical 100000 .tmp/memory-stage4/runs/old-logical-100k
```

Use a new empty output directory for every run. Kinds: `logical`, `subvoice`,
`conductor`, `mixed`; counts: 1 through 1,000,000. Run the matrix serially at
100,000 and 1,000,000, in fresh processes. `mixed` contains each large sequence
and a 60,000-character instrument field. Clock, IDs and generated values are
fixed so all package entry hashes must match across builds. Save/reopen/resave
also checks each entry hash, not just object counts.

The baseline build must precede product edits. This stage freezes commit
`4a4beb63a11514e1f9042dc0dd739e143d120559`. Reflection selects new streaming
overloads only when present; older codecs use their real byte-buffer API.

Measurements distinguish elapsed operation time, cumulative allocation, forced-GC
live managed bytes, process private bytes, and peak working set. Forced collections
are diagnostic-only, outside measured phases, and are not product changes. This
is a synthesized persistence workload, not a WPF or total-application memory cap.
Logical/SubVoice construction intentionally uses ordinary per-object `Add`, not
the already optimized bulk adoption path. Existing unit gates separately exercise
adoption, snapshots, local edits, undo, cancellation and damaged-file isolation.

An optional fourth argument, `--warm`, first runs a separate 100k-or-smaller
fixture through the same codecs and package pipeline in the same process. That
fixture is disposed before measured phases. Use this mode only to investigate
cold/tiered-runtime preparation cost. Its process-lifetime peak still includes
warmup, so do not mix warm peaks with the ordinary cold-process memory matrix.
Keep first-use timings in the report even when warm timings improve.

The generated packages are persistence fixtures, not musical examples. Do not
open a large `mixed` fixture in an auto-compiling UI or use it for playback: its
large logical and template sequences intentionally create a very large expansion
product, which is a different workload reserved for stage 5.
