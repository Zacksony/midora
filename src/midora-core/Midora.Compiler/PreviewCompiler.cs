using Midora.Domain;

namespace Midora.Compiler;

public sealed record EventInstrumentPreviewRequest(
    MidoraId EventInstrumentId,
    MidoraId? SubVoiceId = null,
    int? Pitch = null,
    int Velocity = 100,
    long? GateLengthTicks = null,
    decimal? Tempo = null,
    long CursorTick = 0)
{
    public bool DirectSubVoicePitchPreview { get; init; }

    public IReadOnlyCollection<MidoraId>? MutedSubVoiceIds { get; init; }

    public IReadOnlyCollection<MidoraId>? SoloSubVoiceIds { get; init; }
}

public sealed record SegmentNotePreviewRequest(
    MidoraId TrackId,
    MidoraId SegmentId,
    long StartTick,
    int Pitch,
    int Velocity = 100);

public sealed class PreviewCompiler
{
    public CanonicalCompiledResult CompileHeldSegmentNoteGateOpen(
        MidoraProject source,
        SegmentNotePreviewRequest request,
        long windowLengthTicks)
    {
        if (windowLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowLengthTicks));
        }
        return CompileHeldSegmentNoteCore(
            source,
            request,
            noteGateLengthTicks: long.MaxValue,
            previewLengthTicks: windowLengthTicks,
            heldGateOpen: true,
            finalMappingGateLength: null);
    }

    public CanonicalCompiledResult CompileHeldSegmentNoteGateEnd(
        MidoraProject source,
        SegmentNotePreviewRequest request,
        long finalGateLengthTicks,
        long effectiveGateEndTick)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (finalGateLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGateLengthTicks));
        }
        if (effectiveGateEndTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveGateEndTick));
        }
        (_, _, EventInstrument instrument) = RequireSegmentNoteContext(source, request);
        long maximumRelease = instrument.Envelopes.Count == 0
            ? 0
            : instrument.Envelopes.Max(value => value.ReleaseTicks);
        long previewLength = AddPreviewDurationClamped(
            AddPreviewDurationClamped(
                AddPreviewDurationClamped(
                    effectiveGateEndTick,
                    Math.Max(instrument.TemplateLengthTicks, 0)),
                Math.Max(maximumRelease, 0)),
            1);
        return CompileHeldSegmentNoteCore(
            source,
            request,
            effectiveGateEndTick,
            previewLength,
            heldGateOpen: false,
            finalMappingGateLength: finalGateLengthTicks);
    }

    public CanonicalCompiledResult CompileHeldEventInstrumentGateOpen(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        long windowEndTick)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateLengthTicks.HasValue)
        {
            throw new ArgumentException(
                "A held-preview Gate Start cannot carry a final Gate Length.",
                nameof(request));
        }
        if (windowEndTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowEndTick));
        }

        return CompileEventInstrumentCore(
            source,
            request,
            long.MaxValue,
            windowEndTick,
            heldGateOpen: true,
            finalMappingGateLength: null);
    }

    public CanonicalCompiledResult CompileHeldEventInstrumentGateEnd(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        long finalGateLengthTicks,
        long effectiveGateEndTick)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateLengthTicks.HasValue)
        {
            throw new ArgumentException(
                "A held-preview request must not carry a second final Gate Length.",
                nameof(request));
        }
        if (finalGateLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGateLengthTicks));
        }
        if (effectiveGateEndTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveGateEndTick));
        }

        EventInstrument instrument = RequireInstrument(source, request, out _);
        long maximumRelease = instrument.Envelopes.Count == 0
            ? 0
            : instrument.Envelopes.Max(value => value.ReleaseTicks);
        long previewLength = AddPreviewDurationClamped(
            AddPreviewDurationClamped(
                AddPreviewDurationClamped(
                    effectiveGateEndTick,
                    Math.Max(instrument.TemplateLengthTicks, 0)),
                Math.Max(maximumRelease, 0)),
            1);
        return CompileEventInstrumentCore(
            source,
            request,
            effectiveGateEndTick,
            previewLength,
            heldGateOpen: false,
            finalMappingGateLength: finalGateLengthTicks);
    }

    public CanonicalCompiledResult CompileEventInstrument(
        MidoraProject source,
        EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        EventInstrument instrument = RequireInstrument(source, request, out _);
        long gateLength = request.GateLengthTicks ?? instrument.TemplateLengthTicks;
        long maximumRelease = instrument.Envelopes.Count == 0
            ? 0
            : instrument.Envelopes.Max(value => value.ReleaseTicks);
        long previewLength = AddPreviewDurationClamped(
            AddPreviewDurationClamped(
                AddPreviewDurationClamped(gateLength, Math.Max(instrument.TemplateLengthTicks, 0)),
                Math.Max(maximumRelease, 0)),
            1);
        return CompileEventInstrumentCore(
            source,
            request,
            gateLength,
            previewLength,
            heldGateOpen: false,
            finalMappingGateLength: null);
    }

    private CanonicalCompiledResult CompileEventInstrumentCore(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        long gateLength,
        long previewLength,
        bool heldGateOpen,
        long? finalMappingGateLength)
    {
        EventInstrument instrument = RequireInstrument(source, request, out SubVoice? selectedVoice);

        HashSet<MidoraId>? includedSubVoiceIds = ResolvePreviewSubVoices(instrument, request);

        if (request.DirectSubVoicePitchPreview && selectedVoice is null)
        {
            throw new ArgumentException(
                "Direct SubVoice pitch Preview requires a selected SubVoice.",
                nameof(request));
        }
        int pitch = request.Pitch ?? selectedVoice?.RootNoteOverride ?? instrument.RootNote;
        decimal tempo = request.Tempo ?? GetTempoAt(source.Conductor, request.CursorTick);
        if (pitch is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview pitch must be in 0..127.");
        }
        if (request.Velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview velocity must be in 1..127.");
        }
        if (gateLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview Gate Length must be positive.");
        }
        if (tempo <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview Tempo must be positive.");
        }

        MidoraProject context = CreateContextShell(source);
        context.Conductor.Tempos.Clear();
        context.Conductor.Tempos.Add(new TempoChange(context, 0, tempo));
        context.Conductor.TimeSignatures.Add(new TimeSignatureChange(context, 0, 4, 4));
        EventInstrument compileInstrument = request.DirectSubVoicePitchPreview
            ? CreateDirectPitchPreviewInstrument(
                context,
                instrument,
                selectedVoice!,
                pitch,
                request.Velocity,
                gateLength)
            : CreateTimelineIndependentPreviewInstrument(context, instrument);
        AddInstrumentContext(context, compileInstrument);

        LogicalTrack track = new(context) { Name = "Event Instrument Preview" };
        Segment segment = new(context) { ProjectStartTick = 0, ContentOffsetTick = 0, LengthTicks = previewLength };
        segment.Notes.Add(new LogicalNote(context)
        {
            StartTick = 0,
            LengthTicks = gateLength,
            Note = request.DirectSubVoicePitchPreview ? compileInstrument.RootNote : pitch,
            Velocity = request.Velocity
        });
        track.Segments.Add(segment);
        AddPreviewLogicalTrack(context, track, instrument.Id);

        using MidoraCompiler compiler = new();
        return compiler.CompileFull(context, new CompilationRequest
        {
            Purpose = CompilationPurpose.EventInstrumentPreview,
            StartTick = 0,
            EndTick = previewLength,
            HeldPreviewGateOpen = heldGateOpen,
            HeldPreviewFinalGateLengthTicks = finalMappingGateLength,
            IncludedSubVoiceIds = selectedVoice is null
                ? includedSubVoiceIds
                : new HashSet<MidoraId> { selectedVoice.Id }
        });
    }

    private static EventInstrument CreateDirectPitchPreviewInstrument(
        MidoraProject context,
        EventInstrument sourceInstrument,
        SubVoice sourceVoice,
        int pitch,
        int velocity,
        long gateLength)
    {
        EventInstrument instrument = new(context)
        {
            Id = sourceInstrument.Id,
            Name = sourceInstrument.Name,
            Description = sourceInstrument.Description,
            Color = sourceInstrument.Color,
            RootNote = sourceInstrument.RootNote,
            TemplateLengthTicks = gateLength == long.MaxValue ? long.MaxValue : Math.Max(1, gateLength),
            RequiresChannelIsolation = sourceInstrument.RequiresChannelIsolation,
            OverlapPolicy = sourceInstrument.OverlapPolicy,
            OverlapScope = sourceInstrument.OverlapScope,
            ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff,
            LongLifecycle = LongNoteLifecycle.HoldLastState
        };
        CopyState(sourceInstrument.InitialState, instrument.InitialState);
        SubVoice voice = new(context)
        {
            Id = sourceVoice.Id,
            Name = sourceVoice.Name,
            RootNoteOverride = sourceVoice.RootNoteOverride
        };
        CopyState(sourceVoice.InitialState, voice.InitialState);
        TemplateEvent note = TemplateEvent.Note(context, 0, gateLength, pitch, velocity);
        note.FollowPitchDelta = false;
        voice.Events.Add(note);
        instrument.SubVoices.Add(voice);
        return instrument;
    }

    private static EventInstrument RequireInstrument(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        out SubVoice? selectedVoice)
    {
        EventInstrument instrument = source.EventInstruments.FirstOrDefault(
            value => value.Id == request.EventInstrumentId)
            ?? throw new ArgumentException(
                "The Event Instrument does not belong to the Project.",
                nameof(request));
        selectedVoice = request.SubVoiceId.HasValue
            ? instrument.SubVoices.FirstOrDefault(value => value.Id == request.SubVoiceId.Value)
            : null;
        if (request.SubVoiceId.HasValue && selectedVoice is null)
        {
            throw new ArgumentException(
                "The SubVoice does not belong to the Event Instrument.",
                nameof(request));
        }
        return instrument;
    }

    private static (LogicalTrack Track, Segment Segment, EventInstrument Instrument)
        RequireSegmentNoteContext(
            MidoraProject source,
            SegmentNotePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        LogicalTrack track = source.Tracks.FirstOrDefault(value => value.Id == request.TrackId)
            ?? throw new ArgumentException(
                "The Logical Track does not belong to the Project.",
                nameof(request));
        Segment segment = track.Segments.FirstOrDefault(value => value.Id == request.SegmentId)
            ?? throw new ArgumentException(
                "The Segment does not belong to the Logical Track.",
                nameof(request));
        MidoraId? instrumentId = source.ResolveEventInstrumentDefinitionId(track);
        if (!instrumentId.HasValue)
        {
            throw new InvalidOperationException(
                "Segment Note preview requires a bound Event Instrument.");
        }
        EventInstrument instrument = source.EventInstruments.FirstOrDefault(
            value => value.Id == instrumentId.Value)
            ?? throw new InvalidOperationException(
                "The Segment Note preview Event Instrument binding is missing or damaged.");
        if (request.StartTick < segment.ContentOffsetTick
            || request.StartTick >= segment.ContentEndTick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The draft Note start must be inside the Segment content range.");
        }
        if (request.Pitch is < 0 or > 127 || request.Velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Draft Note pitch must be 0..127 and velocity must be 1..127.");
        }
        return (track, segment, instrument);
    }

    private CanonicalCompiledResult CompileHeldSegmentNoteCore(
        MidoraProject source,
        SegmentNotePreviewRequest request,
        long noteGateLengthTicks,
        long previewLengthTicks,
        bool heldGateOpen,
        long? finalMappingGateLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (noteGateLengthTicks <= 0 || previewLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(noteGateLengthTicks));
        }
        (LogicalTrack sourceTrack, Segment sourceSegment, EventInstrument instrument) =
            RequireSegmentNoteContext(source, request);
        long projectStartTick = checked(
            sourceSegment.ProjectStartTick
            + (request.StartTick - sourceSegment.ContentOffsetTick));
        long projectEndTick = projectStartTick >= long.MaxValue - previewLengthTicks
            ? long.MaxValue
            : projectStartTick + previewLengthTicks;
        long availableLength = projectEndTick - projectStartTick;
        if (availableLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previewLengthTicks));
        }

        MidoraProject context = CreateContextShell(source);
        context.Conductor.Tempos.Clear();
        context.Conductor.Tempos.Add(new TempoChange(
            context,
            0,
            GetTempoAt(source.Conductor, projectStartTick)));
        context.Conductor.TimeSignatures.Add(new TimeSignatureChange(context, 0, 4, 4));
        EventInstrument previewInstrument = CreateTimelineIndependentPreviewInstrument(context, instrument);
        AddInstrumentContext(context, previewInstrument);
        LogicalTrack track = new(context)
        {
            Id = sourceTrack.Id,
            Name = sourceTrack.Name,
            LastBoundEventInstrumentName = sourceTrack.LastBoundEventInstrumentName
        };
        Segment segment = new(context)
        {
            Id = sourceSegment.Id,
            ProjectStartTick = projectStartTick,
            ContentOffsetTick = request.StartTick,
            LengthTicks = availableLength
        };
        segment.ParameterLanes.AddRange(sourceSegment.ParameterLanes);
        segment.Notes.Add(new LogicalNote(context)
        {
            StartTick = request.StartTick,
            LengthTicks = heldGateOpen ? availableLength : noteGateLengthTicks,
            Note = request.Pitch,
            Velocity = request.Velocity
        });
        track.Segments.Add(segment);
        AddPreviewLogicalTrack(context, track, instrument.Id);

        using MidoraCompiler compiler = new();
        return compiler.CompileFull(context, new CompilationRequest
        {
            Purpose = CompilationPurpose.EventInstrumentPreview,
            StartTick = projectStartTick,
            EndTick = projectEndTick,
            HeldPreviewGateOpen = heldGateOpen,
            HeldPreviewFinalGateLengthTicks = finalMappingGateLength,
            IncludedTrackIds = [track.Id]
        });
    }

    private static HashSet<MidoraId>? ResolvePreviewSubVoices(
        EventInstrument instrument,
        EventInstrumentPreviewRequest request)
    {
        if (request.SubVoiceId.HasValue
            && (request.MutedSubVoiceIds is not null || request.SoloSubVoiceIds is not null))
        {
            throw new ArgumentException(
                "Dedicated SubVoice preview cannot also carry Event Instrument preview Mute/Solo state.",
                nameof(request));
        }
        if (request.SubVoiceId.HasValue
            || request.MutedSubVoiceIds is null && request.SoloSubVoiceIds is null)
        {
            return null;
        }

        HashSet<MidoraId> knownIds = instrument.SubVoices.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> muted = ValidatePreviewStateIds(
            request.MutedSubVoiceIds,
            knownIds,
            "Mute",
            request);
        HashSet<MidoraId> soloed = ValidatePreviewStateIds(
            request.SoloSubVoiceIds,
            knownIds,
            "Solo",
            request);
        bool hasSolo = soloed.Count > 0;
        return instrument.SubVoices
            .Where(value => !muted.Contains(value.Id) && (!hasSolo || soloed.Contains(value.Id)))
            .Select(value => value.Id)
            .ToHashSet();
    }

    private static HashSet<MidoraId> ValidatePreviewStateIds(
        IReadOnlyCollection<MidoraId>? ids,
        HashSet<MidoraId> knownIds,
        string stateName,
        EventInstrumentPreviewRequest request)
    {
        if (ids is null)
        {
            return [];
        }

        MidoraId[] snapshot = ids.ToArray();
        HashSet<MidoraId> result = new(snapshot);
        if (result.Count != snapshot.Length)
        {
            throw new ArgumentException(
                $"The SubVoice {stateName} state contains duplicate IDs.",
                nameof(request));
        }
        if (result.Any(value => !knownIds.Contains(value)))
        {
            throw new ArgumentException(
                $"The SubVoice {stateName} state references another Event Instrument.",
                nameof(request));
        }
        return result;
    }

    public CanonicalCompiledResult CompileSegment(
        MidoraProject source,
        MidoraId trackId,
        MidoraId segmentId)
    {
        ArgumentNullException.ThrowIfNull(source);
        LogicalTrack sourceTrack = source.Tracks.FirstOrDefault(value => value.Id == trackId)
            ?? throw new ArgumentException("The Logical Track does not belong to the Project.", nameof(trackId));
        Segment sourceSegment = sourceTrack.Segments.FirstOrDefault(value => value.Id == segmentId)
            ?? throw new ArgumentException("The Segment does not belong to the Logical Track.", nameof(segmentId));

        MidoraProject context = CreateContextShell(source);
        CopyConductor(source.Conductor, context);
        MidoraId? sourceInstrumentId = source.ResolveEventInstrumentDefinitionId(sourceTrack);
        if (sourceInstrumentId.HasValue)
        {
            EventInstrument? instrument = source.EventInstruments.FirstOrDefault(
                value => value.Id == sourceInstrumentId.Value);
            if (instrument is not null)
            {
                AddInstrumentContext(context, instrument);
            }
            context.DamagedEventInstruments.AddRange(source.DamagedEventInstruments.Where(
                value => value.Id == sourceInstrumentId.Value));
        }
        LogicalTrack track = new(context)
        {
            Id = sourceTrack.Id,
            Name = sourceTrack.Name,
            LastBoundEventInstrumentName = sourceTrack.LastBoundEventInstrumentName
        };
        track.Segments.Add(sourceSegment);
        AddPreviewLogicalTrack(context, track, sourceInstrumentId);

        long endTick = sourceSegment.LengthTicks > 0
            && sourceSegment.ProjectStartTick > long.MaxValue - sourceSegment.LengthTicks
                ? long.MaxValue
                : sourceSegment.ProjectStartTick + sourceSegment.LengthTicks;
        using MidoraCompiler compiler = new();
        return compiler.CompileFull(context, new CompilationRequest
        {
            Purpose = CompilationPurpose.SegmentPreview,
            StartTick = sourceSegment.ProjectStartTick,
            EndTick = endTick,
            IncludedTrackIds = [track.Id]
        });
    }

    private static MidoraProject CreateContextShell(MidoraProject source)
    {
        MidoraProject context = new(source.TicksPerQuarterNote, source.NextStableId);
        CopyState(source.GlobalInitialState, context.GlobalInitialState);
        CopyState(source.GlobalResetDefaults, context.GlobalResetDefaults);
        return context;
    }

    private static void AddInstrumentContext(
        MidoraProject context,
        EventInstrument instrument)
    {
        context.EventInstruments.Add(instrument);
    }

    private static EventInstrument CreateTimelineIndependentPreviewInstrument(
        MidoraProject context,
        EventInstrument source)
    {
        EventInstrument result = new(context)
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Color = source.Color,
            RootNote = source.RootNote,
            TemplateLengthTicks = source.TemplateLengthTicks,
            PreRollTicks = 0,
            RequiresChannelIsolation = source.RequiresChannelIsolation,
            OverlapPolicy = source.OverlapPolicy,
            OverlapScope = source.OverlapScope,
            ShortLifecycle = source.ShortLifecycle,
            LongLifecycle = source.LongLifecycle,
            LoopStartTick = source.LoopStartTick,
            LoopEndTick = source.LoopEndTick
        };
        CopyState(source.InitialState, result.InitialState);
        result.LogicalParameters.AddRange(source.LogicalParameters);
        result.SubVoices.AddRange(source.SubVoices);
        result.Envelopes.AddRange(source.Envelopes);
        result.MappingFunctions.AddRange(source.MappingFunctions);
        result.ParameterMappings.AddRange(source.ParameterMappings);
        return result;
    }

    private static void AddPreviewLogicalTrack(
        MidoraProject context,
        LogicalTrack track,
        MidoraId? eventInstrumentId)
    {
        if (eventInstrumentId is MidoraId definitionId)
        {
            EventInstrumentUsage usage = new(context)
            {
                EventInstrumentId = definitionId
            };
            context.EventInstrumentUsages.Add(usage);
            track.EventInstrumentUsageId = usage.Id;
        }
        context.Tracks.Add(track);
        context.ArrangementTracks.Add(new(
            ArrangementTrackKind.LogicalTrack,
            track.Id));
    }

    private static void CopyConductor(ConductorTrack source, MidoraProject targetProject)
    {
        ConductorTrack target = targetProject.Conductor;
        target.Tempos.AdoptSnapshot(source.Tempos.CaptureQuerySnapshot());
        target.TimeSignatures.AdoptSnapshot(source.TimeSignatures.CaptureQuerySnapshot());
        target.KeySignatures.AdoptSnapshot(source.KeySignatures.CaptureQuerySnapshot());
        target.Markers.AdoptSnapshot(source.Markers.CaptureQuerySnapshot());
        target.EndMarker = source.EndMarker is null
            ? null
            : new ProjectEndMarker(targetProject, source.EndMarker.Tick) { Id = source.EndMarker.Id };
    }

    private static decimal GetTempoAt(ConductorTrack conductor, long tick)
    {
        var snapshot = conductor.Tempos.CaptureQuerySnapshot();
        return snapshot.TryGetAtOrBeforeTick(tick, out var value) ? value.BeatsPerMinute
            : snapshot.Count != 0 ? snapshot.GetByOrdinal(0).BeatsPerMinute : 120m;
    }

    private static long AddPreviewDurationClamped(long left, long right) =>
        right >= long.MaxValue - left
            ? long.MaxValue
            : left + right;

    private static void CopyState(MidiInitialState source, MidiInitialState target)
    {
        target.BankMsb = source.BankMsb;
        target.BankLsb = source.BankLsb;
        target.Program = source.Program;
        target.PitchBend = source.PitchBend;
        target.PitchBendRangeSemitones = source.PitchBendRangeSemitones;
        target.PitchBendRangeCents = source.PitchBendRangeCents;
        foreach ((int key, int value) in source.Controllers)
        {
            target.Controllers.Add(key, value);
        }
        foreach ((int key, int value) in source.RegisteredParameters)
        {
            target.RegisteredParameters.Add(key, value);
        }
        foreach ((int key, int value) in source.NonRegisteredParameters)
        {
            target.NonRegisteredParameters.Add(key, value);
        }
    }
}
