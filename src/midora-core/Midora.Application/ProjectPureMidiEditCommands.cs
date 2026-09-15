using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand CreatePureMidiTrackWithNewRoot(
        string? trackName = null,
        MidiChannelRootRoutingMode routingMode = MidiChannelRootRoutingMode.Auto,
        int oneBasedPort = 1,
        int oneBasedChannel = 1,
        MidiChannelMode channelMode = MidiChannelMode.Melodic,
        int? insertionIndex = null) =>
        Command("Create raw MIDI track", project =>
        {
            if (!Enum.IsDefined(routingMode)) throw new ArgumentOutOfRangeException(nameof(routingMode));
            if (!Enum.IsDefined(channelMode)) throw new ArgumentOutOfRangeException(nameof(channelMode));
            if (oneBasedPort is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedPort));
            if (oneBasedChannel is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedChannel));
            string normalizedTrackName = ProjectTextRules.NormalizeShortText(
                trackName ?? "MIDI Track",
                allowEmpty: true,
                nameof(trackName));
            int trackIndex = insertionIndex ?? project.ArrangementTracks.Count;
            ValidateInsertionIndex(trackIndex, project.ArrangementTracks.Count, nameof(insertionIndex));
            MidoraColor trackColor = ProjectTrackColorPolicy.ColorForPureMidiTrackInsertion(
                project,
                trackIndex);
            MidiChannelRoot? existingFixed = routingMode == MidiChannelRootRoutingMode.Fixed
                ? project.MidiChannelRoots.SingleOrDefault(value =>
                    value.RoutingMode == MidiChannelRootRoutingMode.Fixed
                    && value.FixedZeroBasedPort == oneBasedPort - 1
                    && value.FixedZeroBasedChannel == oneBasedChannel - 1)
                : null;
            if (existingFixed is not null && existingFixed.ChannelMode != channelMode)
            {
                throw new InvalidOperationException(
                    "The selected Fixed Port.Channel already exists with a different Channel Mode.");
            }
            MidiChannelRoot? createdRoot = null;
            PureMidiTrack? createdTrack = null;
            return Prepared(
                true,
                EverythingChange(),
                value =>
                {
                    if (existingFixed is null && createdRoot is null)
                    {
                        createdRoot = new(value)
                        {
                            Name = routingMode == MidiChannelRootRoutingMode.Fixed
                                ? $"Port {oneBasedPort} Channel {oneBasedChannel}"
                                : "MIDI Channel",
                            RoutingMode = routingMode,
                            FixedZeroBasedPort = checked((byte)(oneBasedPort - 1)),
                            FixedZeroBasedChannel = checked((byte)(oneBasedChannel - 1)),
                            ChannelMode = channelMode
                        };
                    }
                    MidiChannelRoot root = existingFixed ?? createdRoot!;
                    if (createdRoot is not null && !value.MidiChannelRoots.Contains(createdRoot))
                    {
                        EnsureMidiChannelRootIdAvailable(value, createdRoot.Id);
                        value.MidiChannelRoots.Add(createdRoot);
                    }
                    if (createdTrack is null)
                    {
                        createdTrack = new(value)
                        {
                            Name = normalizedTrackName,
                            MidiChannelRootId = root.Id,
                            Color = trackColor
                        };
                    }
                    else
                    {
                        EnsurePureMidiTrackIdAvailable(value, createdTrack.Id);
                    }
                    value.PureMidiTracks.Add(createdTrack);
                    value.ArrangementTracks.Insert(
                        trackIndex,
                        new(ArrangementTrackKind.PureMidiTrack, createdTrack.Id));
                },
                value =>
                {
                    RemoveRequired(
                        value.ArrangementTracks,
                        new ArrangementTrackReference(
                            ArrangementTrackKind.PureMidiTrack,
                            createdTrack!.Id),
                        "Arrangement Track reference");
                    RemoveRequired(value.PureMidiTracks, createdTrack!, "Pure MIDI Track");
                    if (createdRoot is not null)
                    {
                        RemoveRequired(value.MidiChannelRoots, createdRoot, "MIDI Channel Root");
                    }
                });
        });

    public static IProjectEditCommand ConfigureMidiChannelRoot(
        MidoraId rootId,
        string name,
        MidiChannelRootRoutingMode routingMode,
        int oneBasedPort,
        int oneBasedChannel,
        MidiChannelMode channelMode) =>
        Command("Configure MIDI Channel Root", project =>
        {
            MidiChannelRoot root = FindMidiChannelRoot(project, rootId);
            string normalized = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: false,
                nameof(name));
            if (!Enum.IsDefined(routingMode)) throw new ArgumentOutOfRangeException(nameof(routingMode));
            if (!Enum.IsDefined(channelMode)) throw new ArgumentOutOfRangeException(nameof(channelMode));
            if (oneBasedPort is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedPort));
            if (oneBasedChannel is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedChannel));
            MidiChannelRoot? existingFixed = routingMode == MidiChannelRootRoutingMode.Fixed
                ? project.MidiChannelRoots.SingleOrDefault(value => value.Id != rootId
                    && value.RoutingMode == MidiChannelRootRoutingMode.Fixed
                    && value.FixedZeroBasedPort == oneBasedPort - 1
                    && value.FixedZeroBasedChannel == oneBasedChannel - 1)
                : null;
            if (existingFixed is not null && existingFixed.ChannelMode != channelMode)
            {
                throw new InvalidOperationException(
                    "The selected Fixed Port.Channel already exists with a different Channel Mode.");
            }
            PureMidiTrack[] members = project.PureMidiTracks
                .Where(value => value.MidiChannelRootId == root.Id)
                .ToArray();
            if (members.Length == 0)
            {
                throw new InvalidOperationException(
                    "A MIDI Channel Root cannot be configured without a Track member.");
            }
            HashSet<MidoraId> memberIds = members.Select(value => value.Id).ToHashSet();
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            ArrangementTrackReference[] afterOrder = beforeOrder;
            if (existingFixed is null && routingMode == MidiChannelRootRoutingMode.Auto)
            {
                ArrangementTrackReference[] memberReferences = beforeOrder
                    .Where(value => value.Kind == ArrangementTrackKind.PureMidiTrack
                        && memberIds.Contains(value.TrackId))
                    .ToArray();
                if (memberReferences.Length != members.Length)
                {
                    throw new InvalidOperationException(
                        "A MIDI Channel member is missing from the Arrangement Track order.");
                }
                int firstOriginalIndex = beforeOrder
                    .Select((value, index) => (value, index))
                    .Where(value => memberReferences.Contains(value.value))
                    .Min(value => value.index);
                List<ArrangementTrackReference> gathered = beforeOrder
                    .Where(value => !memberReferences.Contains(value))
                    .ToList();
                int insertionIndex = beforeOrder
                    .Take(firstOriginalIndex)
                    .Count(value => !memberReferences.Contains(value));
                gathered.InsertRange(insertionIndex, memberReferences);
                afterOrder = gathered.ToArray();
            }
            RootConfiguration before = new(
                root.Name,
                root.RoutingMode,
                root.FixedZeroBasedPort,
                root.FixedZeroBasedChannel,
                root.ChannelMode);
            RootConfiguration after = new(
                normalized,
                routingMode,
                checked((byte)(oneBasedPort - 1)),
                checked((byte)(oneBasedChannel - 1)),
                channelMode);
            int rootIndex = project.MidiChannelRoots.IndexOf(root);
            bool mergeIntoExistingFixed = existingFixed is not null;
            return Prepared(
                before != after || mergeIntoExistingFixed || !beforeOrder.SequenceEqual(afterOrder),
                EverythingChange(),
                value =>
                {
                    if (mergeIntoExistingFixed)
                    {
                        foreach (PureMidiTrack member in members)
                        {
                            member.MidiChannelRootId = existingFixed!.Id;
                        }
                        RemoveRequired(value.MidiChannelRoots, root, "MIDI Channel Root");
                    }
                    else
                    {
                        ApplyRootConfiguration(root, after);
                    }
                    if (!beforeOrder.SequenceEqual(afterOrder))
                    {
                        ReplaceArrangementOrder(value, beforeOrder, afterOrder);
                    }
                },
                value =>
                {
                    if (!beforeOrder.SequenceEqual(afterOrder))
                    {
                        ReplaceArrangementOrder(value, afterOrder, beforeOrder);
                    }
                    if (mergeIntoExistingFixed)
                    {
                        InsertAt(value.MidiChannelRoots, rootIndex, root, "MIDI Channel Root");
                        foreach (PureMidiTrack member in members)
                        {
                            member.MidiChannelRootId = root.Id;
                        }
                    }
                    else
                    {
                        ApplyRootConfiguration(root, before);
                    }
                });
        });

    /// <summary>
    /// Changes the route presented on one Pure MIDI Track. The Root remains the
    /// single authority, so a shared source Root is split and an existing Fixed
    /// destination Root is reused instead of copying route fields onto the Track.
    /// </summary>
    public static IProjectEditCommand ConfigurePureMidiTrackRoute(
        MidoraId trackId,
        MidiChannelRootRoutingMode routingMode,
        int oneBasedPort,
        int oneBasedChannel,
        MidiChannelMode channelMode) =>
        Command("Configure MIDI Track route", project =>
        {
            if (!Enum.IsDefined(routingMode)) throw new ArgumentOutOfRangeException(nameof(routingMode));
            if (!Enum.IsDefined(channelMode)) throw new ArgumentOutOfRangeException(nameof(channelMode));
            if (oneBasedPort is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedPort));
            if (oneBasedChannel is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedChannel));

            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            MidiChannelRoot sourceRoot = FindMidiChannelRoot(project, track.MidiChannelRootId);
            PureMidiTrack[] sourceMembers = project.PureMidiTracks
                .Where(value => value.MidiChannelRootId == sourceRoot.Id)
                .ToArray();
            if (sourceMembers.Length == 0)
            {
                throw new InvalidOperationException(
                    "A MIDI Channel Root cannot be configured without a Track member.");
            }

            byte targetPort = checked((byte)(oneBasedPort - 1));
            byte targetChannel = checked((byte)(oneBasedChannel - 1));
            MidiChannelRoot? existingFixed = routingMode == MidiChannelRootRoutingMode.Fixed
                ? project.MidiChannelRoots.SingleOrDefault(value =>
                    value.RoutingMode == MidiChannelRootRoutingMode.Fixed
                    && value.FixedZeroBasedPort == targetPort
                    && value.FixedZeroBasedChannel == targetChannel)
                : null;
            if (existingFixed is not null
                && existingFixed.Id != sourceRoot.Id
                && existingFixed.ChannelMode != channelMode)
            {
                throw new InvalidOperationException(
                    "The selected Fixed Port.Channel already exists with a different Channel Mode.");
            }

            bool sameRoute = sourceRoot.RoutingMode == routingMode
                && sourceRoot.ChannelMode == channelMode
                && (routingMode == MidiChannelRootRoutingMode.Auto
                    || sourceRoot.FixedZeroBasedPort == targetPort
                        && sourceRoot.FixedZeroBasedChannel == targetChannel);
            if (sameRoute)
            {
                return Prepared(false, NoCompilationChange(), _ => { }, _ => { });
            }

            RootConfiguration before = new(
                sourceRoot.Name,
                sourceRoot.RoutingMode,
                sourceRoot.FixedZeroBasedPort,
                sourceRoot.FixedZeroBasedChannel,
                sourceRoot.ChannelMode);
            RootConfiguration after = new(
                routingMode == MidiChannelRootRoutingMode.Fixed
                    ? $"Port {oneBasedPort} Channel {oneBasedChannel}"
                    : "MIDI Channel",
                routingMode,
                targetPort,
                targetChannel,
                channelMode);

            // The Root is the single Channel Mode authority. Editing the same
            // Fixed address therefore updates that Root atomically, including
            // all of its members; the UI confirms this shared change first.
            if (existingFixed?.Id == sourceRoot.Id)
            {
                return Prepared(
                    before != after,
                    EverythingChange(),
                    _ => ApplyRootConfiguration(sourceRoot, after),
                    _ => ApplyRootConfiguration(sourceRoot, before));
            }

            // A singleton Root can keep its identity unless it is merging into an
            // already existing Fixed Root.
            if (sourceMembers.Length == 1 && existingFixed is null)
            {
                return Prepared(
                    before != after,
                    EverythingChange(),
                    _ => ApplyRootConfiguration(sourceRoot, after),
                    _ => ApplyRootConfiguration(sourceRoot, before));
            }

            ArrangementTrackReference reference = new(
                ArrangementTrackKind.PureMidiTrack,
                track.Id);
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            int currentIndex = project.ArrangementTracks.IndexOf(reference);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Pure MIDI Track is missing from the Arrangement Track order.");
            }
            bool leavesSharedAuto = sourceRoot.RoutingMode == MidiChannelRootRoutingMode.Auto
                && sourceMembers.Length > 1;
            ArrangementTrackReference[] afterOrder = leavesSharedAuto
                ? MoveReferenceOutsideGroup(
                    project,
                    beforeOrder,
                    reference,
                    currentIndex,
                    sourceRoot.Id)
                : beforeOrder;
            EnsureFormalGroupContiguity(
                project,
                afterOrder,
                reference,
                existingFixed?.Id);

            int sourceRootIndex = project.MidiChannelRoots.IndexOf(sourceRoot);
            bool removeSourceRoot = sourceMembers.Length == 1;
            MidiChannelRoot? createdRoot = null;
            return Prepared(
                true,
                EverythingChange(),
                value =>
                {
                    MidiChannelRoot destination;
                    if (existingFixed is not null)
                    {
                        destination = existingFixed;
                    }
                    else
                    {
                        if (createdRoot is null)
                        {
                            createdRoot = new(value) { Name = after.Name };
                            ApplyRootConfiguration(createdRoot, after);
                        }
                        else
                        {
                            EnsureMidiChannelRootIdAvailable(value, createdRoot.Id);
                        }
                        value.MidiChannelRoots.Add(createdRoot);
                        destination = createdRoot;
                    }
                    track.MidiChannelRootId = destination.Id;
                    if (removeSourceRoot)
                    {
                        RemoveRequired(value.MidiChannelRoots, sourceRoot, "MIDI Channel Root");
                    }
                    if (!beforeOrder.SequenceEqual(afterOrder))
                    {
                        ReplaceArrangementOrder(value, beforeOrder, afterOrder);
                    }
                },
                value =>
                {
                    if (!beforeOrder.SequenceEqual(afterOrder))
                    {
                        ReplaceArrangementOrder(value, afterOrder, beforeOrder);
                    }
                    if (removeSourceRoot)
                    {
                        EnsureMidiChannelRootIdAvailable(value, sourceRoot.Id);
                        InsertAt(
                            value.MidiChannelRoots,
                            sourceRootIndex,
                            sourceRoot,
                            "MIDI Channel Root");
                    }
                    track.MidiChannelRootId = sourceRoot.Id;
                    if (createdRoot is not null)
                    {
                        RemoveRequired(value.MidiChannelRoots, createdRoot, "MIDI Channel Root");
                    }
                });
        });

    public static IProjectEditCommand DeleteDamagedMidiChannelRoot(MidoraId placeholderId) =>
        Command("Delete damaged MIDI Channel Root", project =>
        {
            _ = project.DamagedMidiChannelRoots.SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedMidiChannelRootDeletion? deletion = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    DamagedMidiChannelRootDeletion applied =
                        DamagedProjectObjectEditing.DeleteMidiChannelRoot(value, placeholderId);
                    deletion ??= applied;
                },
                value => DamagedProjectObjectEditing.UndoDeleteMidiChannelRoot(
                    value,
                    deletion ?? throw new InvalidOperationException(
                        "The damaged MIDI Channel Root deletion was not applied.")));
        });

    public static IProjectEditCommand CreatePureMidiTrack(
        MidoraId rootId,
        string? name = null,
        int? insertionIndex = null) =>
        Command("Create Pure MIDI Track", project =>
        {
            MidiChannelRoot root = FindMidiChannelRoot(project, rootId);
            string normalized = ProjectTextRules.NormalizeShortText(
                name ?? "MIDI Track",
                allowEmpty: true,
                nameof(name));
            int requestedIndex = insertionIndex ?? project.ArrangementTracks.Count;
            ValidateInsertionIndex(requestedIndex, project.ArrangementTracks.Count, nameof(insertionIndex));
            int trackIndex = requestedIndex;
            if (root.RoutingMode == MidiChannelRootRoutingMode.Auto)
            {
                int first = project.ArrangementTracks.FindIndex(reference =>
                    reference.Kind == ArrangementTrackKind.PureMidiTrack
                    && FindPureMidiTrack(project, reference.TrackId).MidiChannelRootId == root.Id);
                int last = project.ArrangementTracks.FindLastIndex(reference =>
                    reference.Kind == ArrangementTrackKind.PureMidiTrack
                    && FindPureMidiTrack(project, reference.TrackId).MidiChannelRootId == root.Id);
                if (first >= 0 && (insertionIndex is null
                    || requestedIndex < first
                    || requestedIndex > last + 1))
                {
                    trackIndex = last + 1;
                }
            }
            MidoraColor trackColor = ProjectTrackColorPolicy.ColorForPureMidiTrackInsertion(
                project,
                trackIndex);
            return DeferredCreate(
                RootChange(root.Id),
                value =>
                {
                    PureMidiTrack track = new(value)
                    {
                        Name = normalized,
                        MidiChannelRootId = root.Id,
                        Color = trackColor
                    };
                    value.PureMidiTracks.Add(track);
                    value.ArrangementTracks.Insert(
                        trackIndex,
                        new(ArrangementTrackKind.PureMidiTrack, track.Id));
                    return track;
                },
                (value, track) =>
                {
                    EnsurePureMidiTrackIdAvailable(value, track.Id);
                    value.PureMidiTracks.Add(track);
                    InsertAt(
                        value.ArrangementTracks,
                        trackIndex,
                        new ArrangementTrackReference(ArrangementTrackKind.PureMidiTrack, track.Id),
                        "Arrangement Track reference");
                },
                (value, track) =>
                {
                    RemoveRequired(
                        value.ArrangementTracks,
                        new ArrangementTrackReference(ArrangementTrackKind.PureMidiTrack, track.Id),
                        "Arrangement Track reference");
                    RemoveRequired(value.PureMidiTracks, track, "Pure MIDI Track");
                });
        });

    public static IProjectEditCommand RenamePureMidiTrack(MidoraId trackId, string name) =>
        Command("Rename Pure MIDI Track", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            string normalized = ProjectTextRules.NormalizeShortText(name, allowEmpty: true, nameof(name));
            string before = track.Name;
            return Prepared(
                !string.Equals(before, normalized, StringComparison.Ordinal),
                PureMidiTrackChange(trackId),
                _ => track.Name = normalized,
                _ => track.Name = before);
        });

    public static IProjectEditCommand MovePureMidiTrack(
        MidoraId trackId,
        MidoraId targetRootId,
        int targetIndex) =>
        Command("Move Pure MIDI Track", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            MidiChannelRoot source = FindMidiChannelRoot(project, track.MidiChannelRootId);
            MidiChannelRoot target = FindMidiChannelRoot(project, targetRootId);
            ArrangementTrackReference reference = new(ArrangementTrackKind.PureMidiTrack, track.Id);
            int sourceIndex = project.ArrangementTracks.IndexOf(reference);
            if (sourceIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Pure MIDI Track is missing from the Arrangement Track order.");
            }
            ValidateExistingIndex(targetIndex, project.ArrangementTracks.Count, nameof(targetIndex));
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            ArrangementTrackReference[] afterOrder;
            if (ReferenceEquals(source, target))
            {
                afterOrder = MoveReference(beforeOrder, reference, targetIndex);
                EnsureFormalGroupContiguity(project, afterOrder);
            }
            else if (target.RoutingMode == MidiChannelRootRoutingMode.Auto)
            {
                List<ArrangementTrackReference> after = beforeOrder
                    .Where(value => value != reference)
                    .ToList();
                int lastTargetMember = after.FindLastIndex(value =>
                    value.Kind == ArrangementTrackKind.PureMidiTrack
                    && FindPureMidiTrack(project, value.TrackId).MidiChannelRootId == target.Id);
                if (lastTargetMember < 0)
                {
                    throw new InvalidOperationException(
                        "The target Auto MIDI Channel has no Arrangement member.");
                }
                after.Insert(lastTargetMember + 1, reference);
                afterOrder = after.ToArray();
                EnsureFormalGroupContiguity(project, afterOrder, reference, target.Id);
            }
            else
            {
                bool leavesSharedAutoRoot = source.RoutingMode == MidiChannelRootRoutingMode.Auto
                    && project.PureMidiTracks.Count(value => value.MidiChannelRootId == source.Id) > 1;
                afterOrder = leavesSharedAutoRoot
                    ? MoveReferenceOutsideGroup(
                        project,
                        beforeOrder,
                        reference,
                        targetIndex,
                        source.Id)
                    : MoveReference(beforeOrder, reference, targetIndex);
                EnsureFormalGroupContiguity(
                    project,
                    afterOrder,
                    reference,
                    target.Id);
            }
            MidoraId sourceRootId = source.Id;
            int sourceRootIndex = project.MidiChannelRoots.IndexOf(source);
            bool removeSourceRoot = !ReferenceEquals(source, target)
                && project.PureMidiTracks.Count(value => value.MidiChannelRootId == source.Id) == 1;
            return Prepared(
                !ReferenceEquals(source, target) || sourceIndex != targetIndex,
                EverythingChange(),
                value =>
                {
                    track.MidiChannelRootId = target.Id;
                    ReplaceArrangementOrder(value, beforeOrder, afterOrder);
                    if (removeSourceRoot)
                    {
                        RemoveRequired(value.MidiChannelRoots, source, "MIDI Channel Root");
                    }
                },
                value =>
                {
                    ReplaceArrangementOrder(value, afterOrder, beforeOrder);
                    if (removeSourceRoot)
                    {
                        InsertAt(
                            value.MidiChannelRoots,
                            sourceRootIndex,
                            source,
                            "MIDI Channel Root");
                    }
                    track.MidiChannelRootId = sourceRootId;
                });
        });

    public static IProjectEditCommand DuplicatePureMidiTrack(MidoraId trackId, string? name = null) =>
        new ProjectPresentationCloneCommand(DuplicatePureMidiTrackCore(trackId, name, detachedPreparation: false), PresentationCloneKind.MidiTrack, trackId);

    private static IProjectEditCommand DuplicatePureMidiTrackCore(MidoraId trackId, string? name, bool detachedPreparation) =>
        Command("Duplicate Pure MIDI Track", project =>
        {
            PureMidiTrack source = FindPureMidiTrack(project, trackId);
            if (!detachedPreparation && (source.Segments.Count > 4096 || RequiresBoundedMidiContent(source.Segments
                .Select((segment, index) => new MidiSegmentSelection(source, segment, index, segment.ProjectStartTick)))))
                return new SequentialProjectEditCommand("Duplicate Pure MIDI Track",
                    [draft => DuplicatePureMidiTrackCore(trackId, name, detachedPreparation: true)]).Prepare(project);
            MidiChannelRoot root = FindMidiChannelRoot(project, source.MidiChannelRootId);
            int trackIndex = project.ArrangementTracks.IndexOf(
                new(ArrangementTrackKind.PureMidiTrack, source.Id)) + 1;
            if (trackIndex == 0)
            {
                throw new InvalidOperationException(
                    "The Pure MIDI Track is missing from the Arrangement Track order.");
            }
            string normalized = ProjectTextRules.NormalizeShortText(
                name ?? $"{source.Name} Copy",
                allowEmpty: true,
                nameof(name));
            return DeferredCreate(
                RootChange(root.Id),
                value =>
                {
                    PureMidiTrack copy = ClonePureMidiTrack(value, source, root.Id);
                    copy.Name = normalized;
                    value.PureMidiTracks.Add(copy);
                    value.ArrangementTracks.Insert(
                        trackIndex,
                        new(ArrangementTrackKind.PureMidiTrack, copy.Id));
                    return copy;
                },
                (value, copy) =>
                {
                    EnsurePureMidiTrackIdAvailable(value, copy.Id);
                    value.PureMidiTracks.Add(copy);
                    InsertAt(
                        value.ArrangementTracks,
                        trackIndex,
                        new ArrangementTrackReference(ArrangementTrackKind.PureMidiTrack, copy.Id),
                        "Arrangement Track reference");
                },
                (value, copy) =>
                {
                    RemoveRequired(
                        value.ArrangementTracks,
                        new ArrangementTrackReference(ArrangementTrackKind.PureMidiTrack, copy.Id),
                        "Arrangement Track reference");
                    RemoveRequired(value.PureMidiTracks, copy, "Pure MIDI Track");
                });
        });

    public static IProjectEditCommand DeletePureMidiTrack(
        MidoraId trackId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete Pure MIDI Track", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            if (track.Segments.Count != 0 && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty Pure MIDI Track requires explicit confirmation.");
            }
            MidiChannelRoot root = FindMidiChannelRoot(project, track.MidiChannelRootId);
            int repositoryIndex = project.PureMidiTracks.IndexOf(track);
            ArrangementTrackReference reference = new(ArrangementTrackKind.PureMidiTrack, track.Id);
            int arrangementIndex = project.ArrangementTracks.IndexOf(reference);
            if (arrangementIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Pure MIDI Track is missing from the Arrangement Track order.");
            }
            int rootIndex = project.MidiChannelRoots.IndexOf(root);
            bool removeRoot = project.PureMidiTracks.Count(
                value => value.MidiChannelRootId == root.Id) == 1;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    RemoveRequired(value.ArrangementTracks, reference, "Arrangement Track reference");
                    RemoveRequired(value.PureMidiTracks, track, "Pure MIDI Track");
                    if (removeRoot)
                    {
                        RemoveRequired(value.MidiChannelRoots, root, "MIDI Channel Root");
                    }
                },
                value =>
                {
                    if (removeRoot)
                    {
                        EnsureMidiChannelRootIdAvailable(value, root.Id);
                        InsertAt(value.MidiChannelRoots, rootIndex, root, "MIDI Channel Root");
                    }
                    EnsurePureMidiTrackIdAvailable(value, track.Id);
                    InsertAt(value.PureMidiTracks, repositoryIndex, track, "Pure MIDI Track");
                    InsertAt(
                        value.ArrangementTracks,
                        arrangementIndex,
                        reference,
                        "Arrangement Track reference");
                });
        });

    public static IProjectEditCommand DeleteDamagedPureMidiTrack(MidoraId placeholderId) =>
        Command("Delete damaged Pure MIDI Track", project =>
        {
            _ = project.DamagedPureMidiTracks.SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedPureMidiTrackDeletion? deletion = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    DamagedPureMidiTrackDeletion applied =
                        DamagedProjectObjectEditing.DeletePureMidiTrack(value, placeholderId);
                    deletion ??= applied;
                },
                value => DamagedProjectObjectEditing.UndoDeletePureMidiTrack(
                    value,
                    deletion ?? throw new InvalidOperationException(
                        "The damaged Pure MIDI Track deletion was not applied.")));
        });

    public static IProjectEditCommand CreateMidiSegment(
        MidoraId trackId,
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick = 0) =>
        Command("Create MIDI Segment", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            ValidateSegmentRange(projectStartTick, lengthTicks, contentOffsetTick);
            EnsureNoMidiSegmentOverlap(track, null, projectStartTick, lengthTicks);
            return DeferredCreate(
                PureMidiTrackChange(track.Id),
                value =>
                {
                    MidiSegment segment = new(value)
                    {
                        ProjectStartTick = projectStartTick,
                        LengthTicks = lengthTicks,
                        ContentOffsetTick = contentOffsetTick
                    };
                    InsertMidiSegmentByTime(track.Segments, segment);
                    return segment;
                },
                (_, segment) => InsertMidiSegmentByTime(track.Segments, segment),
                (_, segment) => RemoveRequired(track.Segments, segment, "MIDI Segment"));
        });

    public static IProjectEditCommand SplitMidiSegment(
        MidoraId segmentId,
        long projectSplitTick) => SplitMidiSegmentCore(segmentId, projectSplitTick, detachedPreparation: false);

    private static IProjectEditCommand SplitMidiSegmentCore(MidoraId segmentId, long projectSplitTick, bool detachedPreparation) =>
        Command("Split MIDI Segment", project =>
        {
            MidiSegmentLocation source = FindMidiSegment(project, segmentId);
            if (projectSplitTick <= source.Segment.ProjectStartTick
                || projectSplitTick >= source.Segment.ProjectRange.EndTick)
            {
                throw new ArgumentOutOfRangeException(nameof(projectSplitTick));
            }

            if (!detachedPreparation && RequiresBoundedMidiContent(
                [new MidiSegmentSelection(source.Track, source.Segment, source.Index, source.Segment.ProjectStartTick)]))
                return new SequentialProjectEditCommand("Split MIDI Segment",
                    [draft => SplitMidiSegmentCore(segmentId, projectSplitTick, detachedPreparation: true)]).Prepare(project);

            MidiSegmentSplitResult? result = null;
            return Prepared(
                hasChanges: true,
                PureMidiTrackChange(source.Track.Id),
                value =>
                {
                    result ??= SplitMidiSegmentContent(value, source.Segment, projectSplitTick);
                    RequireContains(source.Track.Segments, source.Segment, "MIDI Segment");
                    source.Track.Segments.RemoveAt(source.Track.Segments.IndexOf(source.Segment));
                    source.Track.Segments.Insert(source.Index, result.Value.Left);
                    source.Track.Segments.Insert(source.Index + 1, result.Value.Right);
                },
                _ =>
                {
                    if (result is null)
                    {
                        throw new InvalidOperationException(
                            "A MIDI Segment split cannot be undone before its first Apply.");
                    }

                    RemoveRequired(source.Track.Segments, result.Value.Right, "right MIDI Segment");
                    RemoveRequired(source.Track.Segments, result.Value.Left, "left MIDI Segment");
                    InsertAt(source.Track.Segments, source.Index, source.Segment, "MIDI Segment");
                });
        });

    public static IProjectEditCommand CreateDirectMidiNote(
        MidoraId segmentId,
        long startTick,
        long lengthTicks,
        int key,
        int noteOnVelocity,
        int noteOffVelocity = 0) =>
        Command("Create Direct MIDI Note", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            ValidateDirectMidiNote(startTick, lengthTicks, key, noteOnVelocity, noteOffVelocity);
            if (location.Segment.Notes.Count > 4096)
                return PrepareBoundedDirectMidiNoteAppend(project, segmentId, firstId =>
                    [new DirectMidiNoteValue(new(firstId), startTick, lengthTicks, key, noteOnVelocity,
                        noteOffVelocity, checked(firstId * 2), checked(firstId * 2 + 1))]);
            return ResolveTargetedExactDirectMidiCollisions(DeferredCreate(
                PureMidiTrackChange(location.Track.Id),
                value =>
                {
                    DirectMidiNote note = new(value)
                    {
                        StartTick = startTick,
                        LengthTicks = lengthTicks,
                        Key = key,
                        NoteOnVelocity = noteOnVelocity,
                        NoteOffVelocity = noteOffVelocity
                    };
                    note.NoteOnOrder = note.Id.Value * 2;
                    note.NoteOffOrder = checked(note.NoteOnOrder + 1);
                    location.Segment.Notes.Add(note);
                    return note;
                },
                (_, note) => location.Segment.Notes.Add(note),
                (_, note) => location.Segment.Notes.Remove(note)),
                noteTargets: [new(location.Segment, startTick, key)]);
        });

    public static IProjectEditCommand CreateDirectMidiChannelEvent(
        MidoraId segmentId,
        long tick,
        DirectMidiChannelEventKind kind,
        int data1,
        int data2 = 0,
        long? order = null) =>
        Command("Create Direct MIDI Event", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            ValidateDirectMidiEvent(tick, kind, data1, data2);
            if (location.Segment.ChannelEvents.Count > 4096)
                return PrepareBoundedDirectMidiEventAppend(project, segmentId, firstId =>
                    [new DirectMidiChannelEventValue(new(firstId), tick, kind, data1, data2, order ?? checked(firstId + 1))]);
            return ResolveTargetedExactDirectMidiCollisions(DeferredCreate(
                PureMidiTrackChange(location.Track.Id),
                value =>
                {
                    DirectMidiChannelEvent directEvent = new(value)
                    {
                        Tick = tick,
                        Kind = kind,
                        Data1 = data1,
                        Data2 = data2,
                        Order = order ?? value.NextStableId
                    };
                    location.Segment.ChannelEvents.Add(directEvent);
                    return directEvent;
                },
                (_, directEvent) => location.Segment.ChannelEvents.Add(directEvent),
                (_, directEvent) => RemoveRequired(
                    location.Segment.ChannelEvents,
                    directEvent,
                    "Direct MIDI Event")),
                eventTargets: [new(location.Segment, tick, kind, data1)]);
        });

    private static MidiChannelRoot FindMidiChannelRoot(MidoraProject project, MidoraId rootId) =>
        project.MidiChannelRoots.SingleOrDefault(value => value.Id == rootId)
        ?? throw new ArgumentOutOfRangeException(nameof(rootId));

    private static PureMidiTrack FindPureMidiTrack(MidoraProject project, MidoraId trackId) =>
        project.PureMidiTracks.SingleOrDefault(value => value.Id == trackId)
        ?? throw new ArgumentOutOfRangeException(nameof(trackId));

    private static MidiSegmentLocation FindMidiSegment(MidoraProject project, MidoraId segmentId)
    {
        MidiSegmentIndexEntry result = ProjectSegmentIndex.FindMidi(project, segmentId)
            ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
        return new(result.Track, result.Segment, result.Index);
    }

    private static MidiSegmentSplitResult SplitMidiSegmentContent(
        MidoraProject project,
        MidiSegment source,
        long projectSplitTick)
    {
        long leftLength = checked(projectSplitTick - source.ProjectStartTick);
        long splitContentTick = checked(source.ContentOffsetTick + leftLength);
        MidiSegment left = new(project, source.Id)
        {
            ProjectStartTick = source.ProjectStartTick,
            LengthTicks = leftLength,
            ContentOffsetTick = source.ContentOffsetTick
        };
        MidiSegment right = new(project)
        {
            ProjectStartTick = projectSplitTick,
            LengthTicks = checked(source.LengthTicks - leftLength),
            ContentOffsetTick = splitContentTick
        };

        var notes = source.Notes.CreateObjectSource();
        var events = source.ChannelEvents.CreateObjectSource();
        var opaque = source.OpaqueEvents.CreateObjectSource();
        var progress = new DirectContentCopyProgress(checked(DirectContentRecordCount(source) * 2));
        AdoptBoundedDirectMidiNotes(project, left, progress.Read(notes)
            .Where(value => value.StartTick < splitContentTick).Select(value => value with
            { LengthTicks = Math.Min(value.LengthTicks, checked(splitContentTick - value.StartTick)) }));
        AdoptBoundedDirectMidiNotes(project, right, progress.Read(notes)
            .Where(value => value.StartTick >= splitContentTick));
        AdoptBoundedDirectMidiEvents(project, left, progress.Read(events)
            .Where(value => value.Tick < splitContentTick));
        AdoptBoundedDirectMidiEvents(project, right, progress.Read(events)
            .Where(value => value.Tick >= splitContentTick));
        AdoptBoundedOpaqueMidiEvents(project, left, progress.Read(opaque)
            .Where(value => value.Tick < splitContentTick));
        AdoptBoundedOpaqueMidiEvents(project, right, progress.Read(opaque)
            .Where(value => value.Tick >= splitContentTick));

        foreach (var group in source.InstrumentChanges.Values)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            if (InstrumentChangeResolver.TryRead(left, group, out _)) left.InstrumentChanges = left.InstrumentChanges.Add(group, true);
            else if (InstrumentChangeResolver.TryRead(right, group, out _)) right.InstrumentChanges = right.InstrumentChanges.Add(group, true);
        }

        return new(left, right);
    }

    private static void EnsureNoMidiSegmentOverlap(
        PureMidiTrack track,
        MidiSegment? excluded,
        long projectStartTick,
        long lengthTicks)
    {
        TickRange candidate = new(projectStartTick, checked(projectStartTick + lengthTicks));
        if (track.Segments.Any(value => !ReferenceEquals(value, excluded)
            && candidate.Intersects(value.ProjectRange)))
        {
            throw new InvalidOperationException(
                "MIDI Segments on the same Pure MIDI Track cannot overlap.");
        }
    }

    private static void ValidateDirectMidiNote(
        long startTick,
        long lengthTicks,
        int key,
        int noteOnVelocity,
        int noteOffVelocity)
    {
        if (startTick < 0) throw new ArgumentOutOfRangeException(nameof(startTick));
        if (lengthTicks <= 0) throw new ArgumentOutOfRangeException(nameof(lengthTicks));
        _ = checked(startTick + lengthTicks);
        if (key is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(key));
        if (noteOnVelocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(noteOnVelocity));
        if (noteOffVelocity is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(noteOffVelocity));
    }

    private static void ValidateDirectMidiEvent(
        long tick,
        DirectMidiChannelEventKind kind,
        int data1,
        int data2)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (data1 is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(data1));
        bool oneDataByte = kind is DirectMidiChannelEventKind.ProgramChange
            or DirectMidiChannelEventKind.ChannelPressure;
        if (data2 is < 0 or > 127 || oneDataByte && data2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(data2));
        }
    }

    private static PureMidiTrack ClonePureMidiTrack(
        MidoraProject project,
        PureMidiTrack source,
        MidoraId targetRootId)
    {
        PureMidiTrack result = new(project)
        {
            Name = source.Name,
            MidiChannelRootId = targetRootId,
            Color = source.Color
        };
        var progress = new DirectContentCopyProgress(source.Segments.Sum(DirectContentRecordCount));
        foreach (MidiSegment segment in source.Segments)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            result.Segments.Add(CloneMidiSegment(project, segment, segment.ProjectStartTick, progress));
        }
        return result;
    }

    private static void InsertMidiSegmentByTime(List<MidiSegment> segments, MidiSegment segment)
    {
        int index = 0;
        while (index < segments.Count
            && (segments[index].ProjectStartTick < segment.ProjectStartTick
                || segments[index].ProjectStartTick == segment.ProjectStartTick
                && segments[index].Id.CompareTo(segment.Id) < 0))
        {
            index++;
        }
        segments.Insert(index, segment);
    }

    private static void EnsureMidiChannelRootIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.MidiChannelRoots.Any(value => value.Id == id)
            || project.DamagedMidiChannelRoots.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The MIDI Channel Root stable ID is already present.");
        }
    }

    private static void EnsurePureMidiTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.PureMidiTracks.Any(value => value.Id == id)
            || project.DamagedPureMidiTracks.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The Pure MIDI Track stable ID is already present.");
        }
    }

    private static ProjectChangeSet RootChange(params MidoraId[] rootIds)
    {
        ProjectChangeSet result = new();
        result.MidiChannelRootIds.UnionWith(rootIds);
        return result;
    }

    private static ProjectChangeSet PureMidiTrackChange(params MidoraId[] trackIds)
    {
        ProjectChangeSet result = new();
        result.PureMidiTrackIds.UnionWith(trackIds);
        return result;
    }

    private static void ApplyRootConfiguration(MidiChannelRoot root, RootConfiguration value)
    {
        root.Name = value.Name;
        root.RoutingMode = value.RoutingMode;
        root.FixedZeroBasedPort = value.Port;
        root.FixedZeroBasedChannel = value.Channel;
        root.ChannelMode = value.ChannelMode;
    }

    private readonly record struct RootConfiguration(
        string Name,
        MidiChannelRootRoutingMode RoutingMode,
        byte Port,
        byte Channel,
        MidiChannelMode ChannelMode);

    private readonly record struct MidiSegmentLocation(
        PureMidiTrack Track,
        MidiSegment Segment,
        int Index);

    private readonly record struct MidiSegmentSplitResult(
        MidiSegment Left,
        MidiSegment Right);
}
