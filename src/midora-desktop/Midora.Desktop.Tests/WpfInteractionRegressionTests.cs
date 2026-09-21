using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed partial class WpfInteractionRegressionTests
{
    private static void AssertSharedTaskProgressStyleResolvesInPropertiesBamlAndRealMainWindowOverlayMarkup(
        System.Windows.Application application)
    {
        // Reuse the theme test's one Application on its owning STA. WPF does
        // not permit constructing a second Application after Shutdown.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument appXaml = XDocument.Load(Path.Combine(FindRepositoryRoot(),
            "src", "midora-desktop", "Midora.Desktop", "App.xaml"));
        XElement applicationDictionary = appXaml.Root!
            .Element(presentation + "Application.Resources")!
            .Element(presentation + "ResourceDictionary")!;
        XElement localEntries = new(presentation + "ResourceDictionary",
            applicationDictionary.Elements()
                .Where(element => element.Name != presentation + "ResourceDictionary.MergedDictionaries")
                .Select(element => new XElement(element)));
        localEntries.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        // Palette/icons/Controls are already installed in their actual order;
        // parse only App.xaml's own entries against those application resources.
        ResourceDictionary localResources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(localEntries.ToString());
        foreach (System.Collections.DictionaryEntry entry in localResources)
            application.Resources[entry.Key] = entry.Value;
        Style shared = Assert.IsType<Style>(application.Resources["TaskProgressBar"]);
        ObjectPropertiesDialog properties = new(new ObjectPropertiesViewModel(), _ => true);
        try
        {
            AssertProgressTemplate(Assert.IsType<Border>(properties.FindName("PropertyTaskOverlay")), shared);
            XDocument mainXaml = XDocument.Load(Path.Combine(FindRepositoryRoot(),
                "src", "midora-desktop", "Midora.Desktop", "MainWindow.xaml"));
            XElement taskMarkup = new(mainXaml.Descendants(presentation + "Border")
                .Single(element => (string?)element.Attribute(x + "Name") == "TaskLockOverlay"));
            taskMarkup.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            // Event handlers require MainWindow's code-behind connector;
            // this isolated template test verifies resources and controls.
            foreach (XAttribute handler in taskMarkup.DescendantsAndSelf().Attributes("Click").ToArray())
                handler.Remove();
            Border taskOverlay = (Border)System.Windows.Markup.XamlReader.Parse(taskMarkup.ToString());
            AssertProgressTemplate(taskOverlay, shared);
        }
        finally
        {
            properties.Close();
            DrainDispatcher();
        }

        static void AssertProgressTemplate(Border overlay, Style shared)
        {
            ProgressBar progress = Assert.Single(LogicalDescendants(overlay).OfType<ProgressBar>());
            Assert.Same(shared, progress.Style.BasedOn);
            progress.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            Assert.True(progress.ApplyTemplate());
            Assert.IsType<Border>(progress.Template.FindName("PART_Track", progress));
            Assert.IsType<Border>(progress.Template.FindName("PART_Indicator", progress));
        }
        static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject value) continue;
                yield return value;
                foreach (var descendant in LogicalDescendants(value)) yield return descendant;
            }
        }
    }

    [Fact]
    public void SingleLineCodeEditorPasteRemainsSafeInsideAnOpenUndoGroup()
    {
        RunOnSta(() =>
        {
            TextEditor editor = new() { Text = "=v0" };
            SingleLineCodeEditorInput.Attach(editor);
            Assert.Contains(
                editor.TextArea.CommandBindings.Cast<CommandBinding>(),
                binding => binding.Command == ApplicationCommands.Paste);
            editor.CaretOffset = editor.Text.Length;
            editor.Document.UndoStack.StartUndoGroup();
            try
            {
                SingleLineCodeEditorInput.InsertText(editor, "\r\n+ k0\n");
            }
            finally
            {
                editor.Document.UndoStack.EndUndoGroup();
            }

            Assert.Equal("=v0 + k0 ", editor.Text);
            Assert.DoesNotContain('\r', editor.Text);
            Assert.DoesNotContain('\n', editor.Text);
        });
    }

    [Fact]
    public void ImportedOpaqueLaneNeverRoutesToLogicalParameterLaneCreation()
    {
        ParameterLaneOption opaque = new(
            default,
            null,
            "Imported Meta / SysEx",
            IsOpaqueMidiLane: true);
        ParameterLaneOption logical = new(
            new MidoraId(10),
            null,
            "Pressure · Empty");

        Assert.False(MainWindow.ShouldCreateLogicalParameterLane(opaque));
        Assert.True(MainWindow.ShouldCreateLogicalParameterLane(logical));
    }

    [Fact]
    public void AncestorLookupTraversesDocumentContentElementsWithoutTreatingThemAsVisuals()
    {
        RunOnSta(() =>
        {
            Border root = new();
            TextBlock text = new();
            Run run = new("Diagnostic message");
            text.Inlines.Add(run);
            root.Child = text;

            Assert.Same(text, MainWindow.FindVisualAncestor<TextBlock>(run));
            Assert.Same(root, MainWindow.FindVisualAncestor<Border>(run));
        });
    }

    [Fact]
    public void InputMethodPolicyEnablesOnlyActualTextEntryTargets()
    {
        RunOnSta(() =>
        {
            TextBox textBox = new();
            RichTextBox richTextBox = new();
            PasswordBox passwordBox = new();
            ComboBox editableComboBox = new() { IsEditable = true };
            ComboBox selectionComboBox = new();
            Button button = new();

            Assert.True(MainWindow.IsInputMethodTextTarget(textBox));
            Assert.True(MainWindow.IsInputMethodTextTarget(richTextBox));
            Assert.True(MainWindow.IsInputMethodTextTarget(passwordBox));
            Assert.True(MainWindow.IsInputMethodTextTarget(editableComboBox));
            Assert.False(MainWindow.IsInputMethodTextTarget(selectionComboBox));
            Assert.False(MainWindow.IsInputMethodTextTarget(button));

            MainWindow.ApplyInputMethodPolicy(textBox);
            MainWindow.ApplyInputMethodPolicy(button);
            Assert.True(InputMethod.GetIsInputMethodEnabled(textBox));
            Assert.False(InputMethod.GetIsInputMethodEnabled(button));
        });
    }

    [Fact]
    public void ProjectEditsQueueBoundCollectionRefreshesOnTheDispatcher()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            int dispatcherThreadId = Environment.CurrentManagedThreadId;
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "WPF refresh",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
                EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
                DrainDispatcher();

                ProjectTreeNode tracks = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.LogicalTracks);
                Assert.Empty(tracks.Children);
                List<int> collectionChangeThreads = [];
                session.ProjectTree.CollectionChanged += (_, _) =>
                    collectionChangeThreads.Add(Environment.CurrentManagedThreadId);
                long refreshPassBeforeEdit = session.ModelRefreshPassCount;

                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));

                Assert.Single(session.Project!.Tracks);
                Assert.Empty(tracks.Children);
                Assert.Equal(refreshPassBeforeEdit, session.ModelRefreshPassCount);
                DrainDispatcher();
                Assert.Equal(refreshPassBeforeEdit + 1, session.ModelRefreshPassCount);

                ProjectTreeNode refreshedTracks = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.LogicalTracks);
                Assert.Single(refreshedTracks.Children);
                Assert.NotEmpty(collectionChangeThreads);
                Assert.All(collectionChangeThreads, threadId =>
                    Assert.Equal(dispatcherThreadId, threadId));

                collectionChangeThreads.Clear();
                PumpUntil(Task.Run(() =>
                    session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Background Instrument"))));
                DrainDispatcher();
                ProjectTreeNode library = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.InstrumentLibrary);
                Assert.Contains(library.Children, item => item.Title == "Background Instrument");
                Assert.NotEmpty(collectionChangeThreads);
                Assert.All(collectionChangeThreads, threadId =>
                    Assert.Equal(dispatcherThreadId, threadId));

                Assert.IsType<InstrumentWorkspaceViewModel>(session.OpenInstrument(instrument.Id));
                Assert.Equal(
                    WorkspaceKind.ConductorTrack,
                    session.OpenWorkspace(session.ProjectTree.Single(
                        item => item.Kind == ProjectTreeNodeKind.Conductor)).Kind);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void MultipleContentChangesInOneDispatcherFrameMergeWithoutLosingAffectedWorkspaces()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Merged content refresh",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
                EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track A", instrument.Id));
                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track B", instrument.Id));
                LogicalTrack firstTrack = session.Project.Tracks[0];
                LogicalTrack secondTrack = session.Project.Tracks[1];
                session.Execute(ProjectDomainEditCommands.CreateSegment(firstTrack.Id, 0, 480));
                session.Execute(ProjectDomainEditCommands.CreateSegment(secondTrack.Id, 0, 480));
                Segment firstSegment = Assert.Single(firstTrack.Segments);
                Segment secondSegment = Assert.Single(secondTrack.Segments);
                session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
                    firstSegment.Id,
                    0,
                    120,
                    60,
                    100));
                session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
                    secondSegment.Id,
                    0,
                    120,
                    64,
                    100));
                LogicalNote firstNote = Assert.Single(firstSegment.Notes);
                LogicalNote secondNote = Assert.Single(secondSegment.Notes);
                DrainDispatcher();

                TimelineWorkspaceViewModel firstWorkspace = session.OpenSegment(firstSegment.Id);
                TimelineWorkspaceViewModel secondWorkspace = session.OpenSegment(secondSegment.Id);
                long passesBeforeEdits = session.ModelRefreshPassCount;
                long rebuildsBeforeEdits = session.WorkspaceRebuildCount;

                session.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
                    firstSegment.Id,
                    [firstNote.Id],
                    24,
                    0));
                session.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
                    secondSegment.Id,
                    [secondNote.Id],
                    48,
                    0));

                Assert.Equal(passesBeforeEdits, session.ModelRefreshPassCount);
                Assert.True(firstWorkspace.Snapshot!.TryGetItem(
                    firstNote.Id,
                    out TimelineRenderItem firstBeforeDrain));
                Assert.True(secondWorkspace.Snapshot!.TryGetItem(
                    secondNote.Id,
                    out TimelineRenderItem secondBeforeDrain));
                Assert.Equal(0, firstBeforeDrain.StartTick);
                Assert.Equal(0, secondBeforeDrain.StartTick);

                DrainDispatcher();

                Assert.Equal(passesBeforeEdits + 1, session.ModelRefreshPassCount);
                Assert.Equal(rebuildsBeforeEdits + 3, session.WorkspaceRebuildCount);
                Assert.True(firstWorkspace.Snapshot!.TryGetItem(
                    firstNote.Id,
                    out TimelineRenderItem firstAfterDrain));
                Assert.True(secondWorkspace.Snapshot!.TryGetItem(
                    secondNote.Id,
                    out TimelineRenderItem secondAfterDrain));
                Assert.Equal(24, firstAfterDrain.StartTick);
                Assert.Equal(48, secondAfterDrain.StartTick);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void CaptionButtonStyleIsInteractiveInsideWindowChrome()
    {
        RunOnSta(() =>
        {
            ResourceDictionary controls = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri(
                    "/Midora.Desktop.Presentation;component/Themes/Controls.xaml",
                    UriKind.Relative));
            Style caption = Assert.IsType<Style>(controls["Button.Caption"]);

            Setter setter = Assert.Single(caption.Setters.OfType<Setter>(), item =>
                item.Property == WindowChrome.IsHitTestVisibleInChromeProperty);
            Assert.Equal(true, setter.Value);
        });
    }

    [Fact]
    public void PianoRollVerticalViewportStaysInsideMidiPitchRange()
    {
        RunOnSta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                LaneHeight = 24
            };
            surface.Measure(new Size(500, 504));
            surface.Arrange(new Rect(0, 0, 500, 504));

            surface.FirstLane = int.MaxValue;
            Assert.Equal(20, surface.VisibleLaneCount);
            Assert.Equal(108, surface.MaximumFirstLane);
            Assert.Equal(108, surface.FirstLane);

            surface.FirstLane = -1;
            Assert.Equal(0, surface.FirstLane);

            TimelineSurface partialLaneSurface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                LaneHeight = 18
            };
            partialLaneSurface.Measure(new Size(500, 500));
            partialLaneSurface.Arrange(new Rect(0, 0, 500, 500));
            partialLaneSurface.FirstLane = int.MaxValue;

            Assert.Equal(26, partialLaneSurface.VisibleLaneCount);
            Assert.Equal(102, partialLaneSurface.MaximumFirstLane);
            Assert.Equal(102, partialLaneSurface.FirstLane);

            const int width = 500;
            const int height = 1_112;
            TimelineSurface oversizedSurface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                Width = width,
                Height = height
            };
            oversizedSurface.Measure(new Size(width, height));
            oversizedSurface.Arrange(new Rect(0, 0, width, height));
            oversizedSurface.LaneHeight = TimelineSurface.MinimumPianoLaneHeight;

            Assert.Equal(128, oversizedSurface.VisibleLaneCount);
            Assert.Equal(0, oversizedSurface.MaximumFirstLane);
            Assert.Equal(0, oversizedSurface.FirstLane);

            RenderTargetBitmap target = new(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(oversizedSurface);
            byte[] pixel = new byte[4];
            target.CopyPixels(new Int32Rect(100, height - 8, 1, 1), pixel, 4, 0);
            Assert.Equal([14, 11, 9, 255], pixel);
        });
    }

    [Fact]
    public void OptInImportedPagedMidiRendersThroughTheWpfTimelineSurface()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_UI_SAMPLE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(track => track.Segments)
                .First(candidate => candidate.Notes.Count != 0);
            DirectMidiNoteValue first = segment.Notes.QueryValues(
                    segment.ContentOffsetTick,
                    segment.ContentEndTick)
                .First();
            TimelineWorkspaceViewModel workspace = new(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI Segment",
                TimelineWorkspaceMode.Segment);
            workspace.Rebuild(imported.Project, revision: 1);

            RunOnSta(() =>
            {
                const int width = 800;
                const int height = 260;
                int noteLane = 127 - first.Key;
                long startTick = Math.Max(
                    segment.ContentOffsetTick,
                    first.StartTick - Math.Min(16, first.StartTick));
                long tickSpan = Math.Max(64, checked(first.LengthTicks + 32));
                TimelineSurface surface = new()
                {
                    SurfaceMode = TimelineSurfaceMode.PianoRoll,
                    StartTick = startTick,
                    TickSpan = tickSpan,
                    FirstLane = Math.Max(0, noteLane - 4),
                    LaneHeight = 18,
                    RangeStartTick = segment.ContentOffsetTick,
                    RangeEndTick = segment.ContentEndTick,
                    GridVisible = false
                };
                surface.Measure(new Size(width, height));
                surface.Arrange(new Rect(0, 0, width, height));
                TimelineRasterCacheSession.Clear();

                RenderTargetBitmap target = new(width, height, 96, 96, PixelFormats.Pbgra32);
                target.Render(surface);
                byte[] baseline = new byte[width * height * 4];
                target.CopyPixels(baseline, width * 4, 0);

                surface.Snapshot = workspace.Snapshot;
                bool changed = false;
                for (int attempt = 0; attempt < 200 && !changed; attempt++)
                {
                    DrainDispatcher();
                    Thread.Sleep(5);
                    target = new(width, height, 96, 96, PixelFormats.Pbgra32);
                    target.Render(surface);
                    byte[] actual = new byte[baseline.Length];
                    target.CopyPixels(actual, width * 4, 0);
                    changed = !actual.AsSpan().SequenceEqual(baseline);
                }

                Assert.True(changed, "The source-backed MIDI Note tiles never reached the WPF surface.");
            });
        }
        finally
        {
            imported.Project.Dispose();
        }
    }

    [Fact]
    public void ComboAndScrollBarThemesProvideDedicatedTemplatesAndChevronGeometry()
    {
        RunOnSta(() =>
        {
            ResourceDictionary icons = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/FluentSystemIcons.xaml", UriKind.Relative));
            ResourceDictionary windowControlIcons = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/WindowControlIcons.xaml", UriKind.Relative));
            ResourceDictionary palette = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/Palette.xaml", UriKind.Relative));
            System.Windows.Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(palette);
            application.Resources.MergedDictionaries.Add(icons);
            application.Resources.MergedDictionaries.Add(windowControlIcons);
            ResourceDictionary controls = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/Controls.xaml", UriKind.Relative));
            application.Resources.MergedDictionaries.Add(controls);
            try
            {
                Style combo = Assert.IsType<Style>(controls[typeof(ComboBox)]);
                AssertA1DialogLayoutsAndInitialSettingsPage();
                AssertInstrumentSelectionDialogBamlAndDraft();
                Style comboItem = Assert.IsType<Style>(controls[typeof(ComboBoxItem)]);
                Style scrollBar = Assert.IsType<Style>(controls[typeof(ScrollBar)]);
                Style menuSeparator = Assert.IsType<Style>(
                    controls[MenuItem.SeparatorStyleKey]);
                AssertMenuIconsHaveTheirOwnFullWidthSlot();

                Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate);
                Assert.Contains(scrollBar.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate);
                Assert.Equal(typeof(Separator), menuSeparator.TargetType);
                Assert.Contains(menuSeparator.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.TemplateProperty
                    && setter.Value is ControlTemplate);
                Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.VerticalContentAlignmentProperty
                    && Equals(setter.Value, VerticalAlignment.Center));
                Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                    setter.Property == ComboBoxWheelSelectionGuard.IsEnabledProperty
                    && Equals(setter.Value, true));
                ControlTemplate comboItemTemplate = Assert.IsType<ControlTemplate>(
                    Assert.Single(comboItem.Setters.OfType<Setter>(), setter =>
                        setter.Property == Control.TemplateProperty).Value);
                Trigger disabledComboItemTrigger = Assert.Single(
                    comboItemTemplate.Triggers.OfType<Trigger>(), trigger =>
                        trigger.Property == UIElement.IsEnabledProperty
                        && Equals(trigger.Value, false));
                Assert.Contains(disabledComboItemTrigger.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.ForegroundProperty
                    && ReferenceEquals(setter.Value, palette["Brush.Text.Disabled"]));

                ComboBoxItem disabledComboItem = new()
                {
                    Content = "External Relative Reference",
                    IsEnabled = false,
                    Style = comboItem
                };
                disabledComboItem.Measure(new Size(300, 32));
                disabledComboItem.Arrange(new Rect(0, 0, 300, 32));
                disabledComboItem.ApplyTemplate();
                ContentPresenter disabledContent = Assert.IsType<ContentPresenter>(
                    disabledComboItem.Template.FindName("ContentSite", disabledComboItem));
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(palette["Brush.Text.Disabled"]).Color,
                    Assert.IsType<SolidColorBrush>(
                        System.Windows.Documents.TextElement.GetForeground(disabledContent)).Color);
                Assert.Equal(0.45d, disabledContent.Opacity);

                ComboBox displayMemberCombo = new()
                {
                    DisplayMemberPath = nameof(MidiControlChangeInfo.DisplayName),
                    ItemsSource = new[] { new MidiControlChangeInfo(1, "Modulation Wheel (MSB)") },
                    SelectedIndex = 0,
                    Background = Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Surface.0"]),
                    BorderBrush = Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Border.Strong"]),
                    Foreground = Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Text.Primary"])
                };
                displayMemberCombo.Style = combo;
                displayMemberCombo.Measure(new Size(300, 32));
                displayMemberCombo.Arrange(new Rect(0, 0, 300, 32));
                displayMemberCombo.ApplyTemplate();
                Assert.True(ComboBoxWheelSelectionGuard.GetIsEnabled(displayMemberCombo));
                ContentPresenter contentSite = Assert.IsType<ContentPresenter>(
                    displayMemberCombo.Template.FindName("ContentSite", displayMemberCombo));
                Assert.NotNull(contentSite.ContentTemplateSelector);

                MappingFunctionDialog mappingFunctionDialog = new(
                    "New Mapping Function",
                    "Function",
                    "value",
                    _ => null);
                Assert.NotNull(mappingFunctionDialog.Content);

                MidoraProject bindingProject = new(480);
                EventInstrument bindingInstrument = new(bindingProject)
                {
                    Name = "Binding Instrument",
                    TemplateLengthTicks = 480
                };
                SubVoice bindingSubVoice = new(bindingProject) { Name = "Main" };
                bindingInstrument.SubVoices.Add(bindingSubVoice);
                for (int index = 1; index < 256; index++)
                {
                    bindingInstrument.SubVoices.Add(new SubVoice(bindingProject)
                    {
                        Name = $"SubVoice {index + 1}"
                    });
                }

                bindingProject.EventInstruments.Add(bindingInstrument);
                LogicalParameterEventBindingDialog bindingDialog = new(
                    bindingInstrument,
                    bindingSubVoice.Id);
                Assert.NotNull(bindingDialog.Content);
                Assert.Equal(
                    MidiValueKind.ControlChange,
                    Assert.IsType<ComboBox>(bindingDialog.FindName("KindBox")).SelectedItem);
                ComboBox bindingControllerBox = Assert.IsType<ComboBox>(
                    bindingDialog.FindName("ControllerBox"));
                Assert.Same(MidiControlChangeCatalog.EditableControllers, bindingControllerBox.ItemsSource);
                MidiControlChangeInfo selectedBindingController = Assert.IsType<MidiControlChangeInfo>(
                    bindingControllerBox.SelectedItem);
                Assert.Equal(11, selectedBindingController.Number);
                Assert.Equal("11 - Expression (MSB)", selectedBindingController.DisplayName);
                Assert.Equal(
                    0,
                    Assert.IsType<ComboBox>(bindingDialog.FindName("ScopeBox")).SelectedIndex);
                Assert.Equal(
                    LogicalParameterEventBindingOperation.Override,
                    Assert.IsType<ComboBox>(bindingDialog.FindName("OperationBox")).SelectedItem);
                Assert.False(string.IsNullOrWhiteSpace(
                    Assert.IsType<TextBox>(bindingDialog.FindName("NameBox")).Text));
                Assert.Equal(
                    "0",
                    Assert.IsType<TextBox>(bindingDialog.FindName("SourceMinimumBox")).Text);
                Assert.Equal(
                    "127",
                    Assert.IsType<TextBox>(bindingDialog.FindName("SourceMaximumBox")).Text);
                ComboBox conflictBox = Assert.IsType<ComboBox>(
                    bindingDialog.FindName("ConflictBox"));
                Assert.Same(comboItem, Assert.IsType<Style>(conflictBox.ItemContainerStyle).BasedOn);
                ItemsControl subVoiceList = Assert.IsType<ItemsControl>(
                    bindingDialog.FindName("SubVoiceList"));
                Assert.IsNotAssignableFrom<Selector>(subVoiceList);
                Assert.False(subVoiceList.IsEnabled);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(subVoiceList));
                Assert.Equal(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(subVoiceList));
                Assert.True(ScrollViewer.GetCanContentScroll(subVoiceList));
                Assert.True(subVoiceList.ApplyTemplate());
                ScrollViewer subVoiceScrollViewer = Assert.IsType<ScrollViewer>(
                    subVoiceList.Template.FindName("PART_ScrollViewer", subVoiceList));
                Assert.Equal(
                    Colors.Transparent,
                    Assert.IsType<SolidColorBrush>(subVoiceScrollViewer.Background).Color);
                Assert.IsType<ComboBox>(bindingDialog.FindName("ScopeBox")).SelectedIndex = 1;
                Assert.True(subVoiceList.IsEnabled);
                subVoiceList.Measure(new Size(320, 150));
                subVoiceList.Arrange(new Rect(0, 0, 320, 150));
                subVoiceList.UpdateLayout();
                ItemsPresenter subVoiceItemsPresenter = Assert.IsType<ItemsPresenter>(
                    subVoiceList.Template.FindName("PART_ItemsPresenter", subVoiceList));
                subVoiceItemsPresenter.ApplyTemplate();
                VirtualizingStackPanel subVoiceItemsPanel = Assert.IsType<VirtualizingStackPanel>(
                    VisualTreeHelper.GetChild(subVoiceItemsPresenter, 0));
                Assert.InRange(VisualTreeHelper.GetChildrenCount(subVoiceItemsPanel), 1, 64);
                bindingDialog.Close();

                MidoraProject emptyBindingProject = new(480);
                EventInstrument emptyBindingInstrument = new(emptyBindingProject)
                {
                    Name = "Empty Binding Instrument",
                    TemplateLengthTicks = 480
                };
                emptyBindingProject.EventInstruments.Add(emptyBindingInstrument);
                LogicalParameterEventBindingDialog emptyBindingDialog = new(
                    emptyBindingInstrument,
                    currentSubVoiceId: null);
                Assert.Equal(
                    2,
                    Assert.IsType<ComboBox>(emptyBindingDialog.FindName("ScopeBox")).SelectedIndex);
                emptyBindingDialog.Close();

                MouseWheelEventArgs closedWheel = new(Mouse.PrimaryDevice, 0, 120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = displayMemberCombo
                };
                displayMemberCombo.RaiseEvent(closedWheel);
                Assert.True(closedWheel.Handled);

                Assert.IsAssignableFrom<System.Windows.Media.Geometry>(icons["Fluent.ChevronDown20Regular"]);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(66, 78, 88),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment"]).Color);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(2, 3, 4),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment.PianoOutside"]).Color);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(189, 199, 207),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment.NotePreview"]).Color);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(163, 178, 190),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment.PianoNote"]).Color);
                Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.PianoKey.White"]);
                Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.PianoKey.Black"]);
                AssertSharedTaskProgressStyleResolvesInPropertiesBamlAndRealMainWindowOverlayMarkup(application);
                TimelineGenerationDialogTests.VerifyThemeConstructionDraftAndValidation();
                TimelineObjectListIntegrationTests.VerifyActualMainWindowTemplatesAndHandlers();
                HostedWorkspaceLifecycleTests.VerifyLoadedTemplatesAndModalReturnTargets();
            }
            finally
            {
                application.Shutdown();
            }
        }, TimeSpan.FromSeconds(180)); // Whole copy/Undo + native menu matrix; individual async assertions retain their 8-second gate.
    }

    [Fact]
    public void ColdUndoRestoresSelectionInsideTheCoalescedWorkspaceRebuild()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Undo selection",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
                EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
                LogicalTrack track = Assert.Single(session.Project.Tracks);
                session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
                Segment segment = Assert.Single(track.Segments);
                session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
                    segment.Id,
                    0,
                    120,
                    60,
                    100));
                LogicalNote note = Assert.Single(segment.Notes);
                DrainDispatcher();

                TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
                _ = session.OpenInstrument(instrument.Id);
                workspace.Selection.Replace(note.Id);
                session.RefreshWorkspaceSelection(workspace);
                long projectTreeRefreshesBeforeMove = session.ProjectTreeRefreshCount;
                long workspaceRebuildsBeforeMove = session.WorkspaceRebuildCount;
                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.MoveLogicalNotes(
                        segment.Id,
                        [note.Id],
                        24,
                        0),
                    workspace);
                DrainDispatcher();

                Assert.Equal(projectTreeRefreshesBeforeMove, session.ProjectTreeRefreshCount);
                Assert.Equal(workspaceRebuildsBeforeMove + 2, session.WorkspaceRebuildCount);

                workspace.Selection.Clear();
                session.RefreshWorkspaceSelection(workspace);
                long selectionRefreshesBeforeUndo = session.WorkspaceSelectionRefreshCount;
                long passesBeforeUndo = session.ModelRefreshPassCount;

                session.Undo();

                Assert.Equal(selectionRefreshesBeforeUndo, session.WorkspaceSelectionRefreshCount);
                Assert.Equal(passesBeforeUndo, session.ModelRefreshPassCount);
                Assert.Empty(workspace.Selection.Ids);

                DrainDispatcher();

                Assert.Contains(note.Id, workspace.Selection.Ids);
                Assert.Equal(passesBeforeUndo + 1, session.ModelRefreshPassCount);
                Assert.Equal(selectionRefreshesBeforeUndo, session.WorkspaceSelectionRefreshCount);

                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.MoveLogicalNotes(
                        segment.Id,
                        [note.Id],
                        48,
                        0),
                    workspace);
                DrainDispatcher();
                Assert.Equal(2, session.WorkspaceSelectionHistoryStateCount);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void SelectionHistoryCapturesThePostRebuildSelectionForRedo()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Post rebuild selection",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
                EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
                LogicalTrack track = Assert.Single(session.Project.Tracks);
                session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
                Segment segment = Assert.Single(track.Segments);
                session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
                    segment.Id,
                    0,
                    120,
                    60,
                    100));
                session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
                    segment.Id,
                    120,
                    120,
                    70,
                    100));
                LogicalNote retained = segment.Notes[0];
                LogicalNote discarded = segment.Notes[1];
                DrainDispatcher();

                TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
                workspace.Selection.Add(retained.Id, makePrimary: false);
                workspace.Selection.Add(discarded.Id, makePrimary: true);
                session.RefreshWorkspaceSelection(workspace);

                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.TransposeLogicalNotes(
                        segment.Id,
                        [retained.Id, discarded.Id],
                        semitones: 64),
                    workspace);

                // The WPF projection intentionally rebuilds at Render priority. The
                // history bookmark for the new state must be captured after that
                // rebuild has pruned the note deleted by the transform.
                Assert.Contains(discarded.Id, workspace.Selection.Ids);
                DrainDispatcher();
                Assert.Equal([retained.Id], workspace.Selection.Ids);

                session.Undo();
                DrainDispatcher();
                Assert.Equal([retained.Id, discarded.Id], workspace.Selection.Ids);
                Assert.Equal(discarded.Id, workspace.Selection.Primary);

                session.Redo();
                DrainDispatcher();
                Assert.Equal([retained.Id], workspace.Selection.Ids);
                Assert.Equal(retained.Id, workspace.Selection.Primary);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void SelectionHistoryRestoresEveryOpenWorkspaceBookmark()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "All workspace selections",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                TestSelectionWorkspaceViewModel first = new();
                TestSelectionWorkspaceViewModel second = new(WorkspaceKind.ProjectSettings);
                session.Workspaces.Add(first);
                session.Workspaces.Add(second);
                MidoraId firstId = new(20_000_001);
                MidoraId secondId = new(20_000_002);
                first.Selection.Replace(firstId);
                second.Selection.Replace(secondId);

                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.CreateProjectMarker(120, "All workspaces"),
                    first);
                DrainDispatcher();
                first.Selection.Clear();
                second.Selection.Clear();

                session.Undo();
                DrainDispatcher();

                Assert.Equal([firstId], first.Selection.Ids);
                Assert.Equal([secondId], second.Selection.Ids);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void SequentialEditsFlushPendingSelectionBookmarksBeforeTheNextEdit()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Sequential selection history",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                TestSelectionWorkspaceViewModel workspace = new();
                session.Workspaces.Add(workspace);
                MidoraId firstId = new(30_000_001);
                MidoraId secondId = new(30_000_002);
                MidoraId thirdId = new(30_000_003);
                workspace.Selection.Replace(firstId);

                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.CreateProjectMarker(120, "First"),
                    workspace);
                workspace.Selection.Replace(secondId);

                // This second edit runs before the Render-priority callback for the
                // first edit. It must synchronously finish the first bookmark rather
                // than overwrite the pending state id.
                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.CreateProjectMarker(240, "Second"),
                    workspace);
                workspace.Selection.Replace(thirdId);
                DrainDispatcher();

                session.Undo();
                DrainDispatcher();
                Assert.Equal([secondId], workspace.Selection.Ids);

                session.Redo();
                DrainDispatcher();
                Assert.Equal([thirdId], workspace.Selection.Ids);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void ColdUndoWithSixtyThousandSelectedIdsDoesNotResolveOnTheCallStack()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Large selection undo",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                TestSelectionWorkspaceViewModel workspace = new();
                session.Workspaces.Add(workspace);
                MidoraId[] ids = Enumerable.Range(0, 60_000)
                    .Select(index => new MidoraId(1_000_000L + index))
                    .ToArray();
                workspace.Selection.ReplaceAll(ids, ids[0]);
                session.RefreshWorkspaceSelection(workspace);
                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.CreateProjectMarker(120, "Undo target"),
                    workspace);
                DrainDispatcher();
                workspace.Selection.Clear();
                session.RefreshWorkspaceSelection(workspace);
                long selectionRefreshesBeforeUndo = session.WorkspaceSelectionRefreshCount;

                Stopwatch stopwatch = Stopwatch.StartNew();
                session.Undo();
                stopwatch.Stop();

                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                    $"Undo call stack took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
                Assert.Empty(workspace.Selection.Ids);
                Assert.Equal(selectionRefreshesBeforeUndo, session.WorkspaceSelectionRefreshCount);

                DrainDispatcher();

                Assert.Equal(ids.Length, workspace.Selection.Ids.Count);
                Assert.Equal(
                    selectionRefreshesBeforeUndo + 1,
                    session.WorkspaceSelectionRefreshCount);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void LargeTimelineSelectionPublishesIdsImmediatelyThenRestoresFullMetrics()
    {
        RunOnSta(() =>
        {
            const int noteCount = 4_097;
            using MidoraProject project = new(192);
            MidiChannelRoot root = new(project) { Name = "Root" };
            PureMidiTrack track = new(project) { Name = "Track" };
            MidiSegment segment = new(project)
            {
                LengthTicks = noteCount * 4L + 4
            };
            DirectMidiNote[] notes = Enumerable.Range(0, noteCount)
                .Select(index => new DirectMidiNote(project)
                {
                    StartTick = index * 4L,
                    LengthTicks = 2,
                    Key = index % 128,
                    NoteOnVelocity = 1 + index % 127,
                    NoteOnOrder = index * 2L,
                    NoteOffOrder = index * 2L + 1
                })
                .ToArray();
            segment.Notes.AddRange(notes);
            track.Segments.Add(segment);
            ProjectGraphConstruction.AddPureMidiTrack(
                project,
                root,
                track,
                addRoot: true);

            TimelineWorkspaceViewModel workspace = new(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI Segment",
                TimelineWorkspaceMode.Segment);
            workspace.Rebuild(project, revision: 1);
            MidoraId[] ids = notes.Select(static note => note.Id).ToArray();
            workspace.Selection.ReplaceAll(ids, ids[0]);
            TaskCompletionSource metricsPublished = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(WorkspaceViewModel.SelectionSnapshot)
                    && workspace.SelectionSnapshot.TryGetMetrics(
                        TimelineItemKind.DirectMidiNote,
                        out TimelineSelectionMetrics noteMetrics)
                    && noteMetrics.Count == noteCount
                    && workspace.SelectionSnapshot.TryGetMetrics(
                        TimelineItemKind.Velocity,
                        out TimelineSelectionMetrics velocityMetrics)
                    && velocityMetrics.Count == noteCount)
                {
                    metricsPublished.TrySetResult();
                }
            };

            Stopwatch stopwatch = Stopwatch.StartNew();
            workspace.RefreshSelectionPresentation();
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Large selection refresh blocked for {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
            Assert.Equal(noteCount, workspace.SelectionSnapshot.Count);
            Assert.False(workspace.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.DirectMidiNote,
                out _));

            PumpUntil(metricsPublished.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(workspace.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.DirectMidiNote,
                out TimelineSelectionMetrics finalNoteMetrics));
            Assert.Equal(noteCount, finalNoteMetrics.Count);
            Assert.Equal(0, finalNoteMetrics.MinimumStartTick);
            Assert.Equal((noteCount - 1) * 4L + 2, finalNoteMetrics.MaximumEndTick);
            Assert.True(workspace.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.Velocity,
                out TimelineSelectionMetrics finalVelocityMetrics));
            Assert.Equal(noteCount, finalVelocityMetrics.Count);
        });
    }

    [Theory]
    [InlineData(60_000)]
    [InlineData(1_000_000)]
    public void FirstEditBookmarksLargePersistentSelectionWithoutCopyingEveryId(int idCount)
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Persistent selection bookmark",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                TestSelectionWorkspaceViewModel workspace = new();
                session.Workspaces.Add(workspace);
                CompressedMidoraIdSet ids = CompressedMidoraIdSet.Create(
                    Enumerable.Range(0, idCount)
                        .Select(static index => new MidoraId(10_000_000L + index)));
                MidoraId primary = new(10_000_000);
                workspace.Selection.AdoptMaterialized(ids, primary, primary);
                session.RefreshWorkspaceSelection(workspace);
                IProjectEditCommand command =
                    ProjectDomainEditCommands.CreateProjectMarker(120, "Bookmark");

                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                Stopwatch stopwatch = Stopwatch.StartNew();
                session.ExecutePreservingWorkspaceSelection(command, workspace);
                stopwatch.Stop();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                    $"Bookmarking {idCount:N0} selected IDs took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
                Assert.True(
                    allocated < 1_000_000,
                    $"Bookmarking {idCount:N0} selected IDs allocated {allocated:N0} UI-thread bytes.");
                Assert.Same(ids, workspace.Selection.IdSet);

                DrainDispatcher();
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    private sealed class TestSelectionWorkspaceViewModel(
        WorkspaceKind kind = WorkspaceKind.EventInstrumentLibrary) : WorkspaceViewModel(
        WorkspaceKey.ForType(kind),
        "Selection Test")
    {
        public override void Rebuild(MidoraProject project, long revision)
        {
        }
    }

    [Fact]
    public void DiagnosticsUseSingleItemWheelScrollingWithoutDisablingVirtualization()
    {
        XDocument document = XDocument.Load(Path.Combine(FindRepositoryRoot(),
            "src", "midora-desktop", "Midora.Desktop", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement list = document.Descendants(presentation + "ListBox")
            .Single(element => (string?)element.Attribute(x + "Name") == "DiagnosticList");

        Assert.Equal("OnDiagnosticListPreviewMouseWheel", (string?)list.Attribute("PreviewMouseWheel"));
        Assert.Equal("True", (string?)list.Attribute("ScrollViewer.CanContentScroll"));
        Assert.Equal("Item", (string?)list.Attribute("VirtualizingPanel.ScrollUnit"));
        Assert.Equal("True", (string?)list.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)list.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("OnDiagnosticDoubleClick", (string?)list.Attribute("MouseDoubleClick"));
    }

    [Fact]
    public void ComboDropDownWheelAdvancesByOneScrollLine()
    {
        RunOnSta(() =>
        {
            ScrollViewer viewer = new()
            {
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 500 }
            };
            Border popupRoot = new() { Child = viewer };
            ComboBoxWheelSelectionGuard.SetUseSingleStepDropDownWheel(popupRoot, true);
            popupRoot.Measure(new Size(300, 100));
            popupRoot.Arrange(new Rect(0, 0, 300, 100));
            popupRoot.UpdateLayout();

            MouseWheelEventArgs wheel = new(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = viewer
            };
            viewer.RaiseEvent(wheel);
            DrainDispatcher();

            Assert.True(wheel.Handled);
            Assert.Equal(16, viewer.VerticalOffset);
        });
    }

    [Fact]
    public void ComboDropDownWheelIsConsumedEvenWithoutScrollableContent()
    {
        RunOnSta(() =>
        {
            Border popupRoot = new() { Child = new TextBlock { Text = "Only item" } };
            ComboBoxWheelSelectionGuard.SetUseSingleStepDropDownWheel(popupRoot, true);

            MouseWheelEventArgs wheel = new(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = popupRoot.Child
            };
            popupRoot.Child.RaiseEvent(wheel);

            Assert.True(wheel.Handled);
        });
    }

    [Fact]
    public void OpenComboWheelDoesNotMoveAnOuterFormScrollViewer()
    {
        RunOnSta(() =>
        {
            ComboBox combo = new() { IsDropDownOpen = true };
            ComboBoxWheelSelectionGuard.SetIsEnabled(combo, true);
            StackPanel content = new();
            content.Children.Add(combo);
            content.Children.Add(new Border { Height = 500 });
            ScrollViewer outer = new()
            {
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            };
            ScrollViewerWheelRouter.SetIsEnabled(outer, true);
            outer.Measure(new Size(300, 100));
            outer.Arrange(new Rect(0, 0, 300, 100));
            outer.UpdateLayout();

            MouseWheelEventArgs wheel = new(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = combo
            };
            combo.RaiseEvent(wheel);

            Assert.True(wheel.Handled);
            Assert.Equal(0, outer.VerticalOffset);
        });
    }

    [Fact]
    public void MappingFunctionDialogUsesOnlyApplicationScopedLabelStyles()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MappingFunctionDialog.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement[] labels = document.Descendants(presentation + "TextBlock")
            .Where(element => (string?)element.Attribute("Text") is "NAME" or "EXPRESSION")
            .ToArray();
        Assert.Equal(2, labels.Length);
        Assert.All(labels, label => Assert.Equal(
            "{StaticResource Text.Caption}",
            (string?)label.Attribute("Style")));
        Assert.DoesNotContain(
            "PropertyLabel",
            document.ToString(SaveOptions.DisableFormatting),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EventInstrumentSidebarIconsRetainTheSharedFluentIconTemplate()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement iconStyle = document.Descendants(presentation + "Style").Single(element =>
            string.Equals(
                (string?)element.Attribute("TargetType"),
                "{x:Type ui:FluentIcon}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{StaticResource Icon.CommandBar}",
            (string?)iconStyle.Attribute("BasedOn"));
        Assert.Null(iconStyle.Attribute(x + "Key"));
    }

    [Fact]
    public void PrimaryTransportIsNotAKeyboardPreviewPrioritySurface()
    {
        RunOnSta(() =>
        {
            Button primaryTransport = new();
            Button otherCommand = new();

            Assert.False(MainWindow.IsPreviewPriorityPointerTarget(
                primaryTransport,
                primaryTransport));
            Assert.True(MainWindow.IsPreviewPriorityPointerTarget(
                otherCommand,
                primaryTransport));
        });
    }

    [Fact]
    public void DialogActionStylesProvideOneWidthAndKeyboardContract()
    {
        RunOnSta(() =>
        {
            ResourceDictionary controls = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri(
                    "/Midora.Desktop.Presentation;component/Themes/Controls.xaml",
                    UriKind.Relative));
            Style cancel = Assert.IsType<Style>(controls["Button.Dialog.Cancel"]);
            Style confirm = Assert.IsType<Style>(controls["Button.Dialog.Confirm"]);

            Assert.Equal(
                112d,
                Assert.Single(cancel.Setters.OfType<Setter>(), setter =>
                    setter.Property == FrameworkElement.WidthProperty).Value);
            Assert.Equal(
                true,
                Assert.Single(cancel.Setters.OfType<Setter>(), setter =>
                    setter.Property == Button.IsCancelProperty).Value);
            Assert.Equal(
                112d,
                Assert.Single(confirm.Setters.OfType<Setter>(), setter =>
                    setter.Property == FrameworkElement.WidthProperty).Value);
            Assert.Equal(
                true,
                Assert.Single(confirm.Setters.OfType<Setter>(), setter =>
                    setter.Property == Button.IsDefaultProperty).Value);
            Assert.Same(controls["Button.Primary"], confirm.BasedOn);
        });
    }

    [Fact]
    public void XamlDialogsRegisterExactlyOneEscapeCancelTarget()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dialogDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (string path in Directory.EnumerateFiles(dialogDirectory, "*Dialog.xaml"))
        {
            if (Path.GetFileName(path).Equals("MessageDialog.xaml", StringComparison.Ordinal))
            {
                continue;
            }
            XElement[] buttons = XDocument.Load(path)
                .Descendants(presentation + "Button")
                .ToArray();
            XElement[] cancelTargets = buttons
                .Where(button =>
                    string.Equals((string?)button.Attribute("IsCancel"), "True", StringComparison.OrdinalIgnoreCase)
                    || ((string?)button.Attribute("Style"))?.Contains(
                        "Button.Dialog.Cancel",
                        StringComparison.Ordinal) == true)
                .ToArray();

            Assert.True(
                cancelTargets.Length == 1,
                $"{Path.GetFileName(path)} must register exactly one Escape cancel target, but registered {cancelTargets.Length}.");
            Assert.DoesNotContain(buttons, button =>
                ((string?)button.Attribute("Style"))?.Contains(
                    "Button.Caption.Close",
                    StringComparison.Ordinal) == true
                && string.Equals(
                    (string?)button.Attribute("IsCancel"),
                    "True",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void DiagnosticsTemplateUsesSeverityColorsAndScrollableSelectedItemDetails()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement severityStyle = document.Descendants(presentation + "Style").Single(element =>
            string.Equals(
                (string?)element.Attribute(x + "Key"),
                "DiagnosticSeverityText",
                StringComparison.Ordinal));
        string severityContract = severityStyle.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("Brush.Info", severityContract, StringComparison.Ordinal);
        Assert.Contains("Value=\"Warning\"", severityContract, StringComparison.Ordinal);
        Assert.Contains("Brush.Warning", severityContract, StringComparison.Ordinal);
        Assert.Contains("Value=\"Error\"", severityContract, StringComparison.Ordinal);
        Assert.Contains("Brush.Red.Hover", severityContract, StringComparison.Ordinal);

        XElement diagnosticsTemplate = document.Descendants(presentation + "DataTemplate")
            .Single(element => ((string?)element.Attribute("DataType"))?.Contains(
                "DiagnosticsWorkspaceViewModel",
                StringComparison.Ordinal) == true);
        Assert.Contains(diagnosticsTemplate.Descendants(presentation + "ListBox"), element =>
            string.Equals((string?)element.Attribute(x + "Name"), "DiagnosticList", StringComparison.Ordinal));
        Assert.Contains(diagnosticsTemplate.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "MESSAGE", StringComparison.Ordinal));
        Assert.Contains(diagnosticsTemplate.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "SOURCE", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnosticsTemplate.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "SOURCE PATH", StringComparison.Ordinal));
        Assert.Contains(diagnosticsTemplate.Descendants(presentation + "Button"), element =>
            string.Equals((string?)element.Attribute("Content"), "Go to Source", StringComparison.Ordinal)
            && string.Equals(
                (string?)element.Attribute("Click"),
                "OnNavigateDiagnosticClick",
                StringComparison.Ordinal));
        Assert.True(diagnosticsTemplate.Descendants(presentation + "ScrollViewer").Count() >= 2);
        Assert.Empty(diagnosticsTemplate.Descendants(presentation + "Run"));
    }

    [Fact]
    public void AllSubVoiceEditorsDisableTimeRangeSelectionAndSplitUsesOpticalSize()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string name in new[] { "SubVoiceNoteTimeline", "SubVoiceVelocityTimeline", "SubVoiceEventTimeline" })
        {
            XElement timeline = document.Descendants().Single(element =>
                string.Equals((string?)element.Attribute(x + "Name"), name, StringComparison.Ordinal));
            Assert.Equal("False", (string?)timeline.Attribute("IsTimeRangeSelectionEnabled"));
        }

        XElement splitButton = document.Descendants().Single(element =>
            element.Name.LocalName == "ToggleButton"
            && string.Equals((string?)element.Attribute("Tag"), "Split", StringComparison.Ordinal));
        XElement splitIcon = splitButton.Descendants().Single(element =>
            element.Name.LocalName == "FluentIcon");
        Assert.Equal("16", (string?)splitIcon.Attribute("Width"));
        Assert.Equal("16", (string?)splitIcon.Attribute("Height"));
        Assert.Equal("Center", (string?)splitIcon.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)splitIcon.Attribute("VerticalAlignment"));

        XElement subVoiceLowerEditor = document.Descendants().Single(element =>
            element.Name.LocalName == "LaneTabHost"
            && ((string?)element.Attribute("SelectedIndex"))?.Contains(
                "ActiveLowerEditorIndex",
                StringComparison.Ordinal) == true);
        Assert.Equal(
            "OnSubVoiceLowerEditorSelectionChanged",
            (string?)subVoiceLowerEditor.Attribute("SelectionChanged"));
    }

    [Fact]
    public void MainMenuIsHostedInsideTheCustomTitleBar()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement menu = document.Descendants(presentation + "Menu").Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), "MainMenu", StringComparison.Ordinal));
        Assert.Equal("29", (string?)menu.Attribute("Height"));
        Assert.Equal("Center", (string?)menu.Attribute("VerticalAlignment"));
        Assert.Contains(menu.Ancestors(presentation + "Grid"), element =>
            string.Equals((string?)element.Attribute("Grid.Row"), "0", StringComparison.Ordinal));
        Assert.Contains(menu.Elements(presentation + "MenuItem"), element =>
            string.Equals((string?)element.Attribute("Header"), "Application", StringComparison.Ordinal));

        XElement titleContent = Assert.IsType<XElement>(menu.Parent);
        Assert.DoesNotContain(titleContent.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "MIDORA", StringComparison.Ordinal));
        Assert.DoesNotContain(titleContent.Elements(presentation + "Border"), element =>
            string.Equals((string?)element.Attribute("Width"), "1", StringComparison.Ordinal));
        Assert.Equal("1", (string?)menu.Attribute("Grid.Column"));
        XElement projectNameFrame = titleContent.Elements(presentation + "Border").Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), "TitleBarProjectNameFrame", StringComparison.Ordinal));
        Assert.Equal("2", (string?)projectNameFrame.Attribute("Grid.Column"));
        Assert.Null(projectNameFrame.Attribute("Height"));
        Assert.Equal("8,0,0,0", (string?)projectNameFrame.Attribute("Margin"));
        Assert.Null(projectNameFrame.Attribute("Padding"));
        Assert.Equal("Left", (string?)projectNameFrame.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)projectNameFrame.Attribute("VerticalAlignment"));
        Assert.Null(projectNameFrame.Attribute("BorderBrush"));
        Assert.Null(projectNameFrame.Attribute("BorderThickness"));
        XElement projectNameGrid = projectNameFrame.Elements(presentation + "Grid").Single();
        XElement visualFrame = projectNameGrid.Elements(presentation + "Border").Single();
        Assert.Equal(
            "{StaticResource Brush.Border}",
            (string?)visualFrame.Attribute("BorderBrush"));
        Assert.Equal("1", (string?)visualFrame.Attribute("BorderThickness"));
        Assert.Equal("3", (string?)visualFrame.Attribute("CornerRadius"));
        XElement translate = visualFrame
            .Element(presentation + "Border.RenderTransform")!
            .Element(presentation + "TranslateTransform")!;
        Assert.Equal("1", (string?)translate.Attribute("Y"));
        XElement projectName = projectNameGrid.Elements(presentation + "TextBlock").Single(element =>
            string.Equals(
                (string?)element.Attribute("Text"),
                "{Binding TitleBarProjectDisplayName}",
                StringComparison.Ordinal));
        Assert.Equal("8,2", (string?)projectName.Attribute("Margin"));
        Assert.Equal("Center", (string?)projectName.Attribute("HorizontalAlignment"));
        Assert.Equal("12", (string?)projectName.Attribute("FontSize"));
        Assert.Equal(
            "{StaticResource Brush.Text.Tertiary}",
            (string?)projectName.Attribute("Foreground"));
        Assert.Equal("Center", (string?)projectName.Attribute("VerticalAlignment"));

        XElement commandBarStyle = document.Descendants(presentation + "Style").Single(element =>
            string.Equals((string?)element.Attribute(x + "Key"), "Button.CommandBarText", StringComparison.Ordinal));
        XElement disabledTrigger = commandBarStyle
            .Descendants(presentation + "Trigger")
            .Single(element =>
                string.Equals((string?)element.Attribute("Property"), "IsEnabled", StringComparison.Ordinal) &&
                string.Equals((string?)element.Attribute("Value"), "False", StringComparison.Ordinal));
        Assert.Contains(disabledTrigger.Elements(presentation + "Setter"), element =>
            string.Equals((string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal) &&
            string.Equals(
                (string?)element.Attribute("Value"),
                "#8A939F",
                StringComparison.Ordinal));

        XElement commandTextStyle = document.Descendants(presentation + "Style").Single(element =>
            string.Equals((string?)element.Attribute(x + "Key"), "Text.CommandBarButton", StringComparison.Ordinal));
        XElement commandTextDisabledTrigger = commandTextStyle
            .Descendants(presentation + "DataTrigger")
            .Single(element => string.Equals((string?)element.Attribute("Value"), "False", StringComparison.Ordinal));
        Assert.Contains(commandTextDisabledTrigger.Elements(presentation + "Setter"), element =>
            string.Equals((string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal) &&
            string.Equals((string?)element.Attribute("Value"), "#8A939F", StringComparison.Ordinal));
        Assert.Contains(commandTextStyle.Elements(presentation + "Setter"), element =>
            string.Equals((string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal) &&
            string.Equals((string?)element.Attribute("Value"), "#F1F3F5", StringComparison.Ordinal));

        foreach (string command in new[] { "Compile", "MIDI Export", "Audio Export" })
        {
            XElement button = document.Descendants(presentation + "Button").Single(element =>
                string.Equals(
                    (string?)element.Attribute("AutomationProperties.Name"),
                    command,
                    StringComparison.Ordinal));
            Assert.Equal(command == "Compile" ? "{Binding CompileButtonText}" : command,
                (string?)button.Element(presentation + "TextBlock")?.Attribute("Text"));
            Assert.Equal(
                "{StaticResource Button.CommandBarText}",
                (string?)button.Attribute("Style"));
            Assert.Equal(
                command == "Compile" ? "{StaticResource Text.CompileCommandBarButton}" : "{StaticResource Text.CommandBarButton}",
                (string?)button.Element(presentation + "TextBlock")?.Attribute("Style"));
        }
    }

    [Fact]
    public void ApplicationBrandingUsesTheProductIconAssets()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "Midora.Desktop.csproj");
        XDocument project = XDocument.Load(projectPath);

        Assert.Equal(
            @"..\..\..\assets\midora.ico",
            project.Descendants("ApplicationIcon").Single().Value);
        Assert.Contains(project.Descendants("Resource"), resource =>
            string.Equals(
                (string?)resource.Attribute("Include"),
                @"..\..\..\assets\midora.ico",
                StringComparison.Ordinal)
            && string.Equals(
                (string?)resource.Attribute("Link"),
                @"Assets\midora.ico",
                StringComparison.Ordinal));
        Assert.Contains(project.Descendants("Resource"), resource =>
            string.Equals(
                (string?)resource.Attribute("Include"),
                @"..\..\..\assets\midora-note-transparent-256x256.png",
                StringComparison.Ordinal)
            && string.Equals(
                (string?)resource.Attribute("Link"),
                @"Assets\midora-note-transparent-256x256.png",
                StringComparison.Ordinal));

        string mainWindowPath = Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument windowDocument = XDocument.Load(mainWindowPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Equal(
            "pack://application:,,,/Midora;component/Assets/midora.ico",
            (string?)windowDocument.Root?.Attribute("Icon"));
        XElement applicationMark = windowDocument.Descendants(presentation + "Image").Single(element =>
            string.Equals(
                (string?)element.Attribute("Source"),
            "pack://application:,,,/Midora;component/Assets/midora-note-transparent-256x256.png",
                StringComparison.Ordinal)
            && string.Equals(
                (string?)element.Attribute("Grid.Column"),
                "0",
                StringComparison.Ordinal));
        Assert.Equal("0", (string?)applicationMark.Attribute("Grid.Column"));
        Assert.Equal("20", (string?)applicationMark.Attribute("Width"));
        Assert.Equal("20", (string?)applicationMark.Attribute("Height"));
        Assert.Equal("0,0,5,0", (string?)applicationMark.Attribute("Margin"));
        Assert.Equal("HighQuality", (string?)applicationMark.Attribute("RenderOptions.BitmapScalingMode"));
    }

    [Fact]
    public void ExportTrackListsUseFineWheelScrollingAndSoundFontsShowSelectedCount()
    {
        string repositoryRoot = FindRepositoryRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string fileName in new[] { "MidiExportDialog.xaml", "AudioRenderDialog.xaml" })
        {
            string path = Path.Combine(
                repositoryRoot,
                "src",
                "midora-desktop",
                "Midora.Desktop",
                fileName);
            XDocument document = XDocument.Load(path);
            XElement trackList = document.Descendants(presentation + "ListBox").Single(element =>
                string.Equals(
                    (string?)element.Attribute(x + "Name"),
                    "TrackListBox",
                    StringComparison.Ordinal));
            Assert.Equal(
                "OnTrackListPreviewMouseWheel",
                (string?)trackList.Attribute("PreviewMouseWheel"));
            Assert.Equal("True", (string?)trackList.Attribute("VirtualizingPanel.IsVirtualizing"));
            Assert.Equal("Recycling", (string?)trackList.Attribute("VirtualizingPanel.VirtualizationMode"));
        }

        string preferencesPath = Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "ApplicationPreferencesDialog.xaml");
        XDocument preferences = XDocument.Load(preferencesPath);
        XElement countText = preferences.Descendants(presentation + "TextBlock").Single(element =>
            string.Equals(
                (string?)element.Attribute(x + "Name"),
                "SoundFontCountText",
                StringComparison.Ordinal));
        Assert.Equal("Center", (string?)countText.Attribute("VerticalAlignment"));
        Assert.Equal("{StaticResource Text.Caption}", (string?)countText.Attribute("Style"));
        Assert.Equal("12,0,0,0", (string?)countText.Attribute("Margin"));
        Assert.Equal(4, countText.Parent!.Elements(presentation + "Button").Count());

        string preferencesCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "ApplicationPreferencesDialog.xaml.cs"));
        Assert.Contains("_soundFonts.Count(item => item.Enabled)", preferencesCode, StringComparison.Ordinal);
        Assert.Contains("SoundFonts selected", preferencesCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SoundFonts configured", preferencesCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalCommandsAndEmptyStateExposeTheRequiredProjectAndApplicationActions()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string header in new[] { "Close Project", "Arrangement", "Diagnostics" })
        {
            XElement menuItem = document.Descendants(presentation + "MenuItem").Single(element =>
                string.Equals((string?)element.Attribute("Header"), header, StringComparison.Ordinal));
            Assert.Equal("{Binding HasProject}", (string?)menuItem.Attribute("IsEnabled"));
        }

        XElement projectSettings = document.Descendants(presentation + "Button").Single(element =>
            string.Equals((string?)element.Attribute("ToolTip"), "Project Settings", StringComparison.Ordinal));
        Assert.Equal("{Binding HasProject}", (string?)projectSettings.Attribute("IsEnabled"));
        Assert.Equal("OnOpenSettingsClick", (string?)projectSettings.Attribute("Click"));
        Assert.Contains(projectSettings.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("Data"),
                "{StaticResource Fluent.Settings20Regular}",
                StringComparison.Ordinal));
        XElement projectSettingsIcon = projectSettings.Descendants().Single(element =>
            string.Equals(
                (string?)element.Attribute("Data"),
                "{StaticResource Fluent.Settings20Regular}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{StaticResource Icon.DesignCanvas20At16}",
            (string?)projectSettingsIcon.Attribute("Style"));
        Assert.Equal("Emphasized", (string?)projectSettingsIcon.Attribute("Tag"));

        XElement applicationPreferences = document.Descendants(presentation + "Button").Single(element =>
            string.Equals((string?)element.Attribute("ToolTip"), "Application Preferences", StringComparison.Ordinal));
        Assert.Equal("{Binding CanStartForegroundTask}", (string?)applicationPreferences.Attribute("IsEnabled"));
        Assert.Equal("OnApplicationPreferencesClick", (string?)applicationPreferences.Attribute("Click"));
        Assert.Contains(applicationPreferences.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("Data"),
                "{StaticResource Fluent.WrenchScrewdriver20Regular}",
                StringComparison.Ordinal));
        XElement applicationPreferencesIcon = applicationPreferences.Descendants().Single(element =>
            string.Equals(
                (string?)element.Attribute("Data"),
                "{StaticResource Fluent.WrenchScrewdriver20Regular}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{StaticResource Icon.DesignCanvas20At16}",
            (string?)applicationPreferencesIcon.Attribute("Style"));
        Assert.Equal("Emphasized", (string?)applicationPreferencesIcon.Attribute("Tag"));

        foreach (string tooltip in new[] { "Undo", "Redo" })
        {
            XElement button = document.Descendants(presentation + "Button").Single(element =>
                string.Equals((string?)element.Attribute("ToolTip"), tooltip, StringComparison.Ordinal));
            Assert.Equal("{Binding HasProject}", (string?)button.Attribute("IsEnabled"));
        }

        XElement emptyState = document.Descendants(presentation + "Border").Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), "EmptyState", StringComparison.Ordinal));
        XElement welcomeHeadline = emptyState.Descendants(presentation + "TextBlock").Single();
        Assert.Equal("24", (string?)welcomeHeadline.Attribute("FontSize"));
        Assert.Equal("Light", (string?)welcomeHeadline.Attribute("FontWeight"));
        Assert.Equal("{Binding WelcomeHeadline}", (string?)welcomeHeadline.Attribute("Text"));
        Assert.Equal("Stretch", (string?)welcomeHeadline.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)welcomeHeadline.Attribute("TextAlignment"));
        Assert.Equal("None", (string?)welcomeHeadline.Attribute("TextTrimming"));
        Assert.Equal("Wrap", (string?)welcomeHeadline.Attribute("TextWrapping"));
        XElement welcomeTranslation = welcomeHeadline.Descendants(presentation + "TranslateTransform").Single();
        Assert.Equal("-18", (string?)welcomeTranslation.Attribute("Y"));
        Assert.Empty(emptyState.Descendants(presentation + "Image"));
        Assert.DoesNotContain(emptyState.Descendants(presentation + "TextBlock"), element =>
            string.Equals(
                (string?)element.Attribute("Text"),
                "Open an existing Project or create a new one.",
                StringComparison.Ordinal));
        Assert.Contains(emptyState.Descendants(presentation + "Button"), element =>
            string.Equals(
                (string?)element.Attribute("Content"),
                "Open MIDI as New Project",
                StringComparison.Ordinal)
            && string.Equals(
                (string?)element.Attribute("Click"),
                "OnOpenMidiAsNewProjectClick",
                StringComparison.Ordinal));

        XElement workspaceTabs = document.Descendants(presentation + "TabControl").Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), "WorkspaceTabs", StringComparison.Ordinal));
        Assert.Equal(
            "{Binding HasProject, Converter={StaticResource BooleanToVisibility}}",
            (string?)workspaceTabs.Attribute("Visibility"));

        XElement aboutMenuItem = document.Descendants(presentation + "MenuItem").Single(element =>
            string.Equals((string?)element.Attribute("Header"), "About Midora", StringComparison.Ordinal));
        Assert.Equal("F12", (string?)aboutMenuItem.Attribute("InputGestureText"));

        string aboutPath = Path.Combine(Path.GetDirectoryName(path)!, "AboutDialog.xaml");
        XDocument aboutDocument = XDocument.Load(aboutPath);
        Assert.Contains(aboutDocument.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "Runtime resources", StringComparison.Ordinal));
        Assert.Contains(aboutDocument.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "Combined", StringComparison.Ordinal));
        string aboutCode = File.ReadAllText(Path.ChangeExtension(aboutPath, ".xaml.cs"));
        Assert.Contains("MidoraSoftwareVersion.ProductVersion", aboutCode, StringComparison.Ordinal);
        Assert.Contains("AudioWorkerProcessGroup.CaptureResources", aboutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("0.1 development build", aboutCode, StringComparison.Ordinal);

        string mainCode = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        Assert.Contains("e.Key == Key.F12", mainCode, StringComparison.Ordinal);
        Assert.Contains("OpenAboutDialog()", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PianoRollVerticalZoomPairsUseCenteredEqualSpacing()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement[] zoomOutButtons = document.Descendants(presentation + "Button")
            .Where(element => string.Equals(
                (string?)element.Attribute("ToolTip"),
                "Vertical zoom out",
                StringComparison.Ordinal))
            .Where(element => string.Equals((string?)element.Attribute("Width"), "22", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, zoomOutButtons.Length);
        List<string?> opticalOffsets = [];
        foreach (XElement zoomOut in zoomOutButtons)
        {
            XElement grid = Assert.IsType<XElement>(zoomOut.Parent);
            Assert.Equal("Grid", grid.Name.LocalName);
            Assert.Equal("52", (string?)grid.Attribute("Width"));
            Assert.Equal("24", (string?)grid.Attribute("Height"));
            Assert.Null(grid.Attribute("BorderThickness"));
            Assert.Equal(
                new[] { "3", "22", "2", "22", "3" },
                grid.Element(presentation + "Grid.ColumnDefinitions")!
                    .Elements(presentation + "ColumnDefinition")
                    .Select(element => (string?)element.Attribute("Width")));
            Assert.Equal(
                new[] { "3", "18", "3" },
                grid.Element(presentation + "Grid.RowDefinitions")!
                    .Elements(presentation + "RowDefinition")
                    .Select(element => (string?)element.Attribute("Height")));
            Assert.Contains(grid.Elements(presentation + "Border"), element =>
                string.Equals((string?)element.Attribute("Width"), "1", StringComparison.Ordinal) &&
                string.Equals((string?)element.Attribute("HorizontalAlignment"), "Right", StringComparison.Ordinal));
            Assert.Contains(grid.Elements(presentation + "Border"), element =>
                string.Equals((string?)element.Attribute("Height"), "1", StringComparison.Ordinal) &&
                string.Equals((string?)element.Attribute("VerticalAlignment"), "Bottom", StringComparison.Ordinal));
            Assert.Equal("1", (string?)zoomOut.Attribute("Grid.Column"));
            Assert.Equal("1", (string?)zoomOut.Attribute("Grid.Row"));
            Assert.Equal("18", (string?)zoomOut.Attribute("Height"));
            string? opticalOffset = (string?)zoomOut
                .Element(presentation + "Button.RenderTransform")?
                .Element(presentation + "TranslateTransform")?
                .Attribute("Y");
            opticalOffsets.Add(opticalOffset);
            Assert.Equal("Center", (string?)zoomOut.Attribute("HorizontalContentAlignment"));
            Assert.Equal("Center", (string?)zoomOut.Attribute("VerticalContentAlignment"));
            Assert.Contains(zoomOut.Descendants(), element =>
                string.Equals(
                    (string?)element.Attribute("Style"),
                    "{StaticResource Icon.NativeCanvas16}",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("Data"),
                    "{StaticResource Fluent.ZoomOut16Regular}",
                    StringComparison.Ordinal));

            XElement zoomIn = grid.Elements(presentation + "Button").Single(element =>
                string.Equals((string?)element.Attribute("ToolTip"), "Vertical zoom in", StringComparison.Ordinal));
            Assert.Equal("3", (string?)zoomIn.Attribute("Grid.Column"));
            Assert.Equal("1", (string?)zoomIn.Attribute("Grid.Row"));
            Assert.Equal(
                opticalOffset,
                (string?)zoomIn
                    .Element(presentation + "Button.RenderTransform")?
                    .Element(presentation + "TranslateTransform")?
                    .Attribute("Y"));
            Assert.Equal("Center", (string?)zoomIn.Attribute("HorizontalContentAlignment"));
            Assert.Equal("Center", (string?)zoomIn.Attribute("VerticalContentAlignment"));
        }
        Assert.Equal(new[] { "-2", "-1" }, opticalOffsets);
    }

    [Fact]
    public void EveryTimelineSnapButtonAdvertisesTheGlobalShortcut()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement[] snapButtons = document
            .Descendants(presentation + "ToggleButton")
            .Where(element => string.Equals(
                (string?)element.Attribute("Content"),
                "Snap",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, snapButtons.Length); // Piano/Arrangement template buttons.
        XDocument header = XDocument.Load(Path.Combine(Path.GetDirectoryName(path)!, "LaneTabHeader.xaml"));
        snapButtons = snapButtons.Concat(header.Descendants(presentation + "ToggleButton")
            .Where(element => (string?)element.Attribute("Content") == "Snap")).ToArray();
        Assert.Equal(3, snapButtons.Length); // One shared header serves all event hosts.
        Assert.All(snapButtons, button => Assert.Equal(
            "Enable/Disable Snap (A)",
            (string?)button.Attribute("ToolTip")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DrawSegmentSelectionReplacementWaitsUntilAnUnmovedPointerUp(
        bool preserveSelectionForPotentialDrag,
        bool expectedPreserved)
    {
        bool replace = MainWindow.ShouldReplaceDrawSegmentSelection(
            TimelineToolMode.Draw,
            TimelineItemKind.Segment,
            ModifierKeys.None,
            preserveSelectionForPotentialDrag);

        Assert.Equal(expectedPreserved, !replace);
    }

    [Fact]
    public void OuterFormScrollViewerConsumesWheelAboveNonScrollingInput()
    {
        RunOnSta(() =>
        {
            TextBox input = new()
            {
                Height = 32,
                Text = "Draft"
            };
            StackPanel content = new();
            content.Children.Add(input);
            content.Children.Add(new Border { Height = 500 });
            ScrollViewer viewer = new()
            {
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            };
            ScrollViewerWheelRouter.SetIsEnabled(viewer, true);
            viewer.Measure(new Size(300, 100));
            viewer.Arrange(new Rect(0, 0, 300, 100));
            viewer.UpdateLayout();
            Assert.True(viewer.ScrollableHeight > 0);

            MouseWheelEventArgs wheel = new(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = input
            };
            input.RaiseEvent(wheel);
            DrainDispatcher();

            Assert.True(wheel.Handled);
            Assert.Equal(40, viewer.VerticalOffset);
        });
    }

    private static void PumpUntil(Task task)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherFrame frame = new();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action, TimeSpan? timeout = null)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout ?? TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException($"The WPF Dispatcher regression test did not complete. Last object-list stage: {TimelineObjectListIntegrationTests.CurrentTestStage}");
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "midora-desktop",
                    "Midora.Desktop")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate the Midora repository above '{AppContext.BaseDirectory}'.");
    }

}
