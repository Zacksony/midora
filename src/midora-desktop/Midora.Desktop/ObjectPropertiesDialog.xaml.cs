using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop;

public partial class ObjectPropertiesDialog : Window
{
    private readonly DesktopSessionController? _session;
    private readonly WorkspaceViewModel? _workspace;
    private readonly ObjectPropertiesViewModel _properties;
    private readonly Func<IReadOnlyDictionary<string, string>, bool>? _customSubmit;
    private bool _isSubmitting;
    private readonly Func<IReadOnlyDictionary<string, string>, IProjectEditCommand?>? _customCommand;
    private readonly ObjectPropertiesSelectionContext? _frozenSelection;
    private readonly CompressedMidoraIdSet? _retainedSelectionIds;

    internal ObjectPropertiesDialog(DesktopSessionController session, WorkspaceViewModel workspace,
        ObjectPropertiesViewModel properties, ObjectPropertiesSelectionContext selection,
        CompressedMidoraIdSet retainedSelectionIds) : this(session, workspace, properties)
    {
        _frozenSelection = selection;
        _retainedSelectionIds = retainedSelectionIds;
    }

    public ObjectPropertiesDialog(
        DesktopSessionController session,
        WorkspaceViewModel workspace,
        ObjectPropertiesViewModel? properties = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workspace);
        _session = session;
        _workspace = workspace;
        _properties = properties ?? session.CreateObjectProperties(workspace);
        CanEdit = session.CanEditProject;
        InitializeComponent();
        DataContext = _properties;
    }

    public ObjectPropertiesDialog(
        ObjectPropertiesViewModel properties,
        Func<IReadOnlyDictionary<string, string>, bool> submit)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(submit);
        _properties = properties;
        _customSubmit = submit;
        CanEdit = true;
        InitializeComponent();
        DataContext = _properties;
    }

    public bool CanEdit { get; }

    internal ObjectPropertiesDialog(DesktopSessionController session, WorkspaceViewModel workspace,
        ObjectPropertiesViewModel properties, Func<IReadOnlyDictionary<string, string>, IProjectEditCommand?> command)
        : this(session, workspace, properties) => _customCommand = command;

    private void OnActivateMixedClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PropertyField property })
        {
            property.ActivateMixedEdit();
        }
    }

    private void OnResetFieldClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PropertyField property })
        {
            property.Reset();
        }
    }

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting) return;
        try
        {
            if (_customCommand is not null)
            {
                var pending = _properties.Fields.Where(static field => field.HasPendingChange)
                    .ToDictionary(static field => field.Key, static field => field.Value, StringComparer.Ordinal);
                if (_customCommand(pending) is { } command && !await ApplyPreparedPropertiesAsync(command)) return;
            }
            else if (_customSubmit is not null)
            {
                Dictionary<string, string> values = _properties.Fields
                    .Where(property => property.IsEditable)
                    .ToDictionary(
                        property => property.Key,
                        property => property.Value,
                        StringComparer.Ordinal);
                if (!_customSubmit(values)) return;
            }
            else
            {
                PropertyField[] pending = _properties.Fields.Where(property => property.HasPendingChange).ToArray();
                IProjectEditCommand? command = _frozenSelection is not null
                    ? pending.Length == 0 ? null : ObjectPropertiesProjection.CreateDeferredMultiSelectionEdit(
                        _frozenSelection, pending.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))
                    : _session!.CreateObjectPropertiesEdit(_workspace!, pending);
                if (command is not null && _retainedSelectionIds is { Count: > 0 }
                    && MainWindow.TryGetTimelineObjectOwner(_workspace!, out var owner))
                    command = ProjectDomainEditCommands.WithTimelineObjectSelection(command, owner, _frozenSelection!.Ids);
                if (command is not null && !await ApplyPreparedPropertiesAsync(command)) return;
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _properties.ErrorText = exception.Message;
        }
    }

    private async Task<bool> ApplyPreparedPropertiesAsync(IProjectEditCommand command)
    {
        DesktopTaskViewModel task = _session!.BeginTask("Apply Properties", canCancel: true,
            DesktopTaskLockLevel.MainWindow);
        _isSubmitting = true;
        PropertyTaskOverlay.DataContext = task;
        PropertyTaskOverlay.Visibility = Visibility.Visible;
        try
        {
            await Dispatcher.InvokeAsync(() => PropertyTaskCancelButton.Focus(), DispatcherPriority.Input);
            using DispatcherCoalescingProgress<TimelineEditPreparationProgress> progress = new(
                Dispatcher, TimeSpan.FromMilliseconds(100),
                value => task.Report(MainWindow.FormatTimelineEditPreparationProgress(value), value.IsIndeterminate ? null : value.OverallFraction));
            using StagedProjectEdit staged = await Task.Run(
                () => _session.PrepareProjectEdit(command, _workspace!, task.CancellationToken, progress,
                    retainedSelectionIds: _retainedSelectionIds is { Count: > 0 } ? _retainedSelectionIds : null),
                task.CancellationToken);
            progress.Flush();
            task.SealCancellationBeforePublication();
            task.Report("Publishing prepared properties", 1);
            _session.ExecutePreparedPreservingWorkspaceSelection(staged, _workspace!);
            _session.CompleteTask(task, "Succeeded");
            return true;
        }
        catch (OperationCanceledException) when (task.CancellationToken.IsCancellationRequested)
        {
            _session.CompleteTask(task, "Cancelled", "Cancelled by user.");
            return false;
        }
        catch (Exception exception)
        {
            _session.CompleteTask(task, "Failed", exception.Message);
            throw;
        }
        finally
        {
            _isSubmitting = false;
            PropertyTaskOverlay.Visibility = Visibility.Collapsed;
            PropertyTaskOverlay.DataContext = null;
        }
    }

    private void OnCancelTaskClick(object sender, RoutedEventArgs e) =>
        (PropertyTaskOverlay.DataContext as DesktopTaskViewModel)?.RequestCancel();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_isSubmitting && e.Key == Key.Escape)
        {
            (PropertyTaskOverlay.DataContext as DesktopTaskViewModel)?.RequestCancel();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isSubmitting)
        {
            e.Cancel = true;
            (PropertyTaskOverlay.DataContext as DesktopTaskViewModel)?.RequestCancel();
        }
        base.OnClosing(e);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting)
            (PropertyTaskOverlay.DataContext as DesktopTaskViewModel)?.RequestCancel();
        else DialogResult = false;
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
