using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Helpers;
using DragDropEffects = System.Windows.DragDropEffects;
using DataObject = System.Windows.DataObject;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace QuickNotes.App.Views;

public partial class MainWindow : Window
{
    private bool _isExplicitExit;
    private bool _isFullscreen;
    private WindowState _preFullscreenWindowState = WindowState.Normal;
    private double _preFullscreenLeft;
    private double _preFullscreenTop;
    private double _preFullscreenWidth;
    private double _preFullscreenHeight;
    private WindowStyle _preFullscreenWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _preFullscreenResizeMode = ResizeMode.CanResize;

    public bool IsFullscreen => _isFullscreen;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        SizeChanged += MainWindow_SizeChanged;
        UpdateWorkspaceLayout();

        viewModel.RequestUnsavedEditorDecision ??= () => UnsavedEditorPrompt.Show(this);

        // Wire dialog requests
        viewModel.RequestToggleFullscreen = ToggleFullscreen;
        viewModel.RequestOpenNoteEditor += OpenNoteEditor;
        viewModel.RequestOpenTagEditor += OpenTagEditor;
        viewModel.RequestOpenSavedViewEditor = OpenSavedViewEditor;
        viewModel.RequestOpenSynonymsEditor += OpenSynonymsEditor;
        viewModel.RequestOpenTagRules += OpenTagRules;
        viewModel.RequestOpenChangeParentEditor += OpenChangeParentEditor;
        viewModel.RequestOpenTagMerge += OpenTagMerge;
        viewModel.RequestOpenRescanPreview += OpenRescanPreview;
        viewModel.RequestOpenTagSuggestions += OpenTagSuggestions;
        viewModel.RequestOpenSettings += OpenSettings;
        viewModel.RequestOpenImportExport += OpenImportExport;
        viewModel.RequestOpenNoteAssembly += OpenNoteAssembly;
        viewModel.RequestOpenSyncConflicts += OpenSyncConflicts;
        viewModel.RequestOpenTemplateManagement = OpenTemplateManagementDialog;
        viewModel.RequestBringToFront += BringWindowToFront;
        viewModel.RequestOpenHelp += OpenHelp;
        viewModel.RequestOpenCloudSetupWizard += OpenCloudSetupWizard;
        viewModel.RequestOpenFirstRun += OpenFirstRun;
        viewModel.RequestFocusNotesList += FocusNotesList;
        viewModel.RequestScrollEditorToSelection = ScrollDetailSelectionIntoView;
        WorkspaceModeToggle.PreviewMouseLeftButtonDown += WorkspaceModeToggle_PreviewMouseLeftButtonDown;

        // Wire password prompt for protected notes (unlock before opening the editor).
        viewModel.RequestProtectedNotePassword = noteId =>
        {
            var dlg = new ProtectionPasswordDialog(
                "Защищённая заметка",
                "Введите пароль для просмотра этой заметки.",
                ProtectionPasswordDialog.DialogMode.Unlock,
                owner: this);
            return dlg.ShowDialog() == true ? dlg.Password : null;
        };
        viewModel.RequestProtectedPasswordDialog = (title, message, isSetMode) =>
        {
            var dlg = new ProtectionPasswordDialog(
                title,
                message,
                isSetMode ? ProtectionPasswordDialog.DialogMode.SetPassword : ProtectionPasswordDialog.DialogMode.Unlock,
                owner: this);
            return dlg.ShowDialog() == true ? dlg.Password : null;
        };

        // Wire new note draft crash recovery prompt
        viewModel.RequestNewNoteDraftRecoveryPrompt = args =>
        {
            var dlg = new DraftRecoveryWindow(args) { Owner = this };
            dlg.ShowDialog();
            return dlg.Choice;
        };

        DetailBodyBox.PreviewKeyDown += DetailBodyBox_PreviewKeyDown;
        DetailBodyBox.GotKeyboardFocus += (_, _) =>
        {
            if (DataContext is MainViewModel ocrHost)
            {
                ocrHost.SetOcrHotkeySuspended(true);
            }
        };
        DetailBodyBox.LostKeyboardFocus += (_, _) =>
        {
            if (DataContext is MainViewModel ocrHost)
            {
                ocrHost.SetOcrHotkeySuspended(false);
            }
        };
        DetailPreviewViewer.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnHyperlinkRequestNavigate));
        AttachDetailEditor(viewModel.DetailEditor);

        Loaded += MainWindow_Loaded;
    }

    public static Rect[] GetCurrentScreenWorkAreas(Visual? visual = null)
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens != null && screens.Length > 0)
            {
                visual ??= System.Windows.Application.Current?.MainWindow;
                if (visual != null)
                {
                    var source = PresentationSource.FromVisual(visual);
                    if (source?.CompositionTarget != null)
                    {
                        var transform = source.CompositionTarget.TransformFromDevice;
                        return screens.Select(s =>
                        {
                            var devRect = new Rect(s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Width, s.WorkingArea.Height);
                            return WorkspaceLayoutHelper.DevicePixelsToDip(devRect, transform);
                        }).ToArray();
                    }

                    var dpi = VisualTreeHelper.GetDpi(visual);
                    return screens.Select(s =>
                    {
                        var devRect = new Rect(s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Width, s.WorkingArea.Height);
                        return WorkspaceLayoutHelper.DevicePixelsToDip(devRect, dpi.DpiScaleX, dpi.DpiScaleY);
                    }).ToArray();
                }

                return screens.Select(s => new Rect(s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Width, s.WorkingArea.Height)).ToArray();
            }
        }
        catch
        {
            // Fallback to primary work area
        }
        return new[] { SystemParameters.WorkArea };
    }

    public void EnterFullscreen()
    {
        if (_isFullscreen) return;

        _preFullscreenWindowState = WindowState;
        _preFullscreenWindowStyle = WindowStyle;
        _preFullscreenResizeMode = ResizeMode;

        if (WindowState == WindowState.Maximized)
        {
            Rect rb = RestoreBounds;
            _preFullscreenLeft = rb.Left;
            _preFullscreenTop = rb.Top;
            _preFullscreenWidth = rb.Width;
            _preFullscreenHeight = rb.Height;
            WindowState = WindowState.Normal;
        }
        else
        {
            _preFullscreenLeft = Left;
            _preFullscreenTop = Top;
            _preFullscreenWidth = Width;
            _preFullscreenHeight = Height;
        }

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        _isFullscreen = true;
        if (DataContext is MainViewModel vm)
        {
            vm.IsFullscreen = true;
        }
    }

    public void ExitFullscreen()
    {
        if (!_isFullscreen) return;

        WindowState = WindowState.Normal;
        WindowStyle = _preFullscreenWindowStyle;
        ResizeMode = _preFullscreenResizeMode;

        if (_preFullscreenWindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            Left = _preFullscreenLeft;
            Top = _preFullscreenTop;
            Width = _preFullscreenWidth;
            Height = _preFullscreenHeight;

            var workAreas = GetCurrentScreenWorkAreas(this);
            var clamped = WorkspaceLayoutHelper.ClampWindowBounds(
                Left, Top, Width, Height, workAreas, SystemParameters.WorkArea);

            Left = clamped.Left;
            Top = clamped.Top;
            Width = clamped.Width;
            Height = clamped.Height;
        }

        _isFullscreen = false;
        if (DataContext is MainViewModel vm)
        {
            vm.IsFullscreen = false;
            if (vm.NavigationPanelState == NavigationPanelState.Hidden)
            {
                vm.NavigationPanelState = NavigationPanelState.Normal;
            }
        }
    }

    public void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var workAreas = GetCurrentScreenWorkAreas(this);
            var clampedRect = WorkspaceLayoutHelper.ClampWindowBounds(
                vm.WindowLeft, vm.WindowTop, vm.WindowWidth, vm.WindowHeight, workAreas, SystemParameters.WorkArea);

            Left = clampedRect.Left;
            Top = clampedRect.Top;
            Width = clampedRect.Width;
            Height = clampedRect.Height;

            var state = WorkspaceLayoutHelper.NormalizeWindowState(vm.WindowState);
            if (state != WindowState.Minimized)
            {
                WindowState = state;
            }

            vm.NavigationPanelWidth = WorkspaceLayoutHelper.ClampNavWidth(vm.NavigationPanelWidth);
            vm.NavigationPanelState = WorkspaceLayoutHelper.ClampNavigationPanelState(vm.NavigationPanelState, isFullscreenFocus: false);
            vm.NoteListPanelWidth = WorkspaceLayoutHelper.ClampListWidth(vm.NoteListPanelWidth);

            vm.IsNarrow = WorkspaceLayoutHelper.IsNarrowMode(ActualWidth > 0 ? ActualWidth : Width);
            UpdateLayoutForNarrowMode(vm.IsNarrow, vm.IsDetailActiveInNarrow);

            Dispatcher.BeginInvoke(new Action(vm.TryShowFirstRun));
            Dispatcher.BeginInvoke(new Action(vm.CheckAndPromptNewNoteRecoveries));
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            bool isNarrow = WorkspaceLayoutHelper.IsNarrowMode(e.NewSize.Width);
            if (vm.IsNarrow != isNarrow)
            {
                vm.IsNarrow = isNarrow;
            }
            else
            {
                UpdateWorkspaceLayout();
            }

            if (vm.WorkspaceViewMode == WorkspaceViewMode.Board)
            {
                UpdateBoardLayout(e.NewSize.Width);
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (e.PropertyName == nameof(MainViewModel.IsNarrow) ||
            e.PropertyName == nameof(MainViewModel.IsDetailActiveInNarrow) ||
            e.PropertyName == nameof(MainViewModel.WorkspaceViewMode) ||
            e.PropertyName == nameof(MainViewModel.NavigationPanelState) ||
            e.PropertyName == nameof(MainViewModel.IsFullscreen))
        {
            UpdateWorkspaceLayout();
        }
        else if (e.PropertyName == nameof(MainViewModel.NavigationPanelWidth))
        {
            if (!vm.IsNarrow && vm.DetailEditor?.ViewMode != MarkdownViewMode.Split)
            {
                NavCol.Width = new GridLength(vm.NavigationPanelWidth);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.NoteListPanelWidth))
        {
            if (!vm.IsNarrow && vm.WorkspaceViewMode == WorkspaceViewMode.List)
            {
                double targetWidth = vm.DetailEditor?.ViewMode == MarkdownViewMode.Split
                    ? Math.Min(vm.NoteListPanelWidth, WorkspaceLayoutHelper.MinListWidth)
                    : vm.NoteListPanelWidth;
                ListCol.Width = new GridLength(targetWidth);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.DetailEditor))
        {
            AttachDetailEditor(vm.DetailEditor);
            UpdateWorkspaceLayout();
        }
    }

    public void UpdateWorkspaceLayout()
    {
        if (DataContext is not MainViewModel vm) return;

        if (vm.WorkspaceViewMode == WorkspaceViewMode.Board)
        {
            // W2 read-only adaptive board on existing cards.
            BackToMasterButton.Visibility = Visibility.Collapsed;
            BoardHost.Visibility = Visibility.Visible;

            NavBorder.Visibility = Visibility.Visible;
            if (vm.NavigationPanelState == NavigationPanelState.Compact)
            {
                NavCol.MinWidth = WorkspaceLayoutHelper.CompactNavWidth;
                NavCol.MaxWidth = WorkspaceLayoutHelper.CompactNavWidth;
                NavCol.Width = new GridLength(WorkspaceLayoutHelper.CompactNavWidth);
                NavSplitterCol.Width = new GridLength(0);
                NavSplitter.Visibility = Visibility.Collapsed;
            }
            else
            {
                NavCol.MinWidth = WorkspaceLayoutHelper.MinNavWidth;
                NavCol.MaxWidth = WorkspaceLayoutHelper.MaxNavWidth;
                NavCol.Width = new GridLength(vm.NavigationPanelWidth);
                NavSplitterCol.Width = GridLength.Auto;
                NavSplitter.Visibility = Visibility.Visible;
            }

            NotesListGrid.Visibility = Visibility.Collapsed;
            ListCol.MinWidth = 0;
            ListCol.MaxWidth = 0;
            ListCol.Width = new GridLength(0);

            DetailSplitter.Visibility = Visibility.Collapsed;
            DetailSplitterCol.Width = new GridLength(0);
            DetailPaneBorder.Visibility = Visibility.Collapsed;
            DetailCol.MinWidth = 0;
            DetailCol.MaxWidth = 0;
            DetailCol.Width = new GridLength(0);

            UpdateBoardLayout();
            UpdateSplitPaneOrientation();
            return;
        }

        if (vm.WorkspaceViewMode == WorkspaceViewMode.Focus)
        {
            // W3 focus: the selected note occupies the primary workspace using the
            // existing detail editor surface. The neighbor card list is hidden
            // (documented choice); navigation stays visible on wide layouts.
            BackToMasterButton.Visibility = Visibility.Collapsed;
            BoardHost.Visibility = Visibility.Collapsed;

            bool showNav = !vm.IsNarrow && vm.NavigationPanelState != NavigationPanelState.Hidden;
            NavBorder.Visibility = showNav ? Visibility.Visible : Visibility.Collapsed;
            if (showNav)
            {
                if (vm.NavigationPanelState == NavigationPanelState.Compact)
                {
                    NavCol.MinWidth = WorkspaceLayoutHelper.CompactNavWidth;
                    NavCol.MaxWidth = WorkspaceLayoutHelper.CompactNavWidth;
                    NavCol.Width = new GridLength(WorkspaceLayoutHelper.CompactNavWidth);
                    NavSplitterCol.Width = new GridLength(0);
                    NavSplitter.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NavCol.MinWidth = WorkspaceLayoutHelper.MinNavWidth;
                    NavCol.MaxWidth = WorkspaceLayoutHelper.MaxNavWidth;
                    NavCol.Width = new GridLength(vm.NavigationPanelWidth);
                    NavSplitterCol.Width = GridLength.Auto;
                    NavSplitter.Visibility = Visibility.Visible;
                }
            }
            else
            {
                NavCol.MinWidth = 0;
                NavCol.MaxWidth = 0;
                NavCol.Width = new GridLength(0);
                NavSplitterCol.Width = new GridLength(0);
                NavSplitter.Visibility = Visibility.Collapsed;
            }

            NotesListGrid.Visibility = Visibility.Collapsed;
            ListCol.MinWidth = 0;
            ListCol.MaxWidth = 0;
            ListCol.Width = new GridLength(0);

            DetailSplitter.Visibility = Visibility.Collapsed;
            DetailSplitterCol.Width = new GridLength(0);

            DetailPaneBorder.Visibility = Visibility.Visible;
            DetailCol.MinWidth = 250;
            DetailCol.MaxWidth = double.PositiveInfinity;
            DetailCol.Width = new GridLength(1, GridUnitType.Star);

            UpdateSplitPaneOrientation();
            return;
        }

        BoardHost.Visibility = Visibility.Collapsed;

        bool isNarrow = vm.IsNarrow;
        bool isDetailActive = vm.IsDetailActiveInNarrow;
        bool isSplit = vm.DetailEditor?.ViewMode == MarkdownViewMode.Split;

        if (!isNarrow)
        {
            // Wide mode (>= 900 DIP)
            BackToMasterButton.Visibility = Visibility.Collapsed;

            if (isSplit)
            {
                // Practical Split Mode in Wide Layout (§7.2 Defect 2 fix):
                // Dynamically reclaim navigation space to provide a practical detail workspace.
                // Navigation pane is collapsed (0 width) reclaiming 240+ DIP.
                // List pane is kept visible at a compact 280 DIP so that note
                // selection is visibly preserved and interactive, yielding >= 816 DIP to Detail workspace.
                NavBorder.Visibility = Visibility.Collapsed;
                NavCol.MinWidth = 0;
                NavCol.MaxWidth = 0;
                NavCol.Width = new GridLength(0);
                NavSplitterCol.Width = new GridLength(0);
                NavSplitter.Visibility = Visibility.Collapsed;

                NotesListGrid.Visibility = Visibility.Visible;
                ListCol.MinWidth = WorkspaceLayoutHelper.SplitModeListWidth;
                ListCol.MaxWidth = WorkspaceLayoutHelper.MaxListWidth;
                double compactListWidth = Math.Min(vm.NoteListPanelWidth, WorkspaceLayoutHelper.SplitModeListWidth);
                ListCol.Width = new GridLength(compactListWidth);
                DetailSplitterCol.Width = GridLength.Auto;
                DetailSplitter.Visibility = Visibility.Visible;

                DetailPaneBorder.Visibility = Visibility.Visible;
                DetailCol.MinWidth = 250;
                DetailCol.MaxWidth = double.PositiveInfinity;
                DetailCol.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                // Full 3-pane mode (Text or Preview mode):
                // Restore previous workspace state: Navigation, List, Detail
                NavBorder.Visibility = Visibility.Visible;
                if (vm.NavigationPanelState == NavigationPanelState.Compact)
                {
                    NavCol.MinWidth = WorkspaceLayoutHelper.CompactNavWidth;
                    NavCol.MaxWidth = WorkspaceLayoutHelper.CompactNavWidth;
                    NavCol.Width = new GridLength(WorkspaceLayoutHelper.CompactNavWidth);
                    NavSplitterCol.Width = new GridLength(0);
                    NavSplitter.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NavCol.MinWidth = WorkspaceLayoutHelper.MinNavWidth;
                    NavCol.MaxWidth = WorkspaceLayoutHelper.MaxNavWidth;
                    NavCol.Width = new GridLength(vm.NavigationPanelWidth);
                    NavSplitterCol.Width = GridLength.Auto;
                    NavSplitter.Visibility = Visibility.Visible;
                }

                NotesListGrid.Visibility = Visibility.Visible;
                ListCol.MinWidth = WorkspaceLayoutHelper.MinListWidth;
                ListCol.MaxWidth = WorkspaceLayoutHelper.MaxListWidth;
                ListCol.Width = new GridLength(vm.NoteListPanelWidth);
                DetailSplitterCol.Width = GridLength.Auto;
                DetailSplitter.Visibility = Visibility.Visible;

                DetailPaneBorder.Visibility = Visibility.Visible;
                DetailCol.MinWidth = 250;
                DetailCol.MaxWidth = double.PositiveInfinity;
                DetailCol.Width = new GridLength(1, GridUnitType.Star);
            }
        }
        else
        {
            // Narrow master/detail mode (< 900 DIP)
            if (!isDetailActive)
            {
                // Master view: Nav & List visible, Detail hidden
                BackToMasterButton.Visibility = Visibility.Collapsed;
                DetailPaneBorder.Visibility = Visibility.Collapsed;
                DetailSplitter.Visibility = Visibility.Collapsed;
                DetailSplitterCol.Width = new GridLength(0);
                DetailCol.MinWidth = 0;
                DetailCol.MaxWidth = 0;
                DetailCol.Width = new GridLength(0);

                NavBorder.Visibility = Visibility.Visible;
                if (vm.NavigationPanelState == NavigationPanelState.Compact)
                {
                    NavCol.MinWidth = WorkspaceLayoutHelper.CompactNavWidth;
                    NavCol.MaxWidth = WorkspaceLayoutHelper.CompactNavWidth;
                    NavCol.Width = new GridLength(WorkspaceLayoutHelper.CompactNavWidth);
                    NavSplitterCol.Width = new GridLength(0);
                    NavSplitter.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NavCol.MinWidth = WorkspaceLayoutHelper.MinNavWidth;
                    NavCol.MaxWidth = WorkspaceLayoutHelper.MaxNavWidth;
                    NavCol.Width = new GridLength(vm.NavigationPanelWidth);
                    NavSplitterCol.Width = GridLength.Auto;
                    NavSplitter.Visibility = Visibility.Visible;
                }

                NotesListGrid.Visibility = Visibility.Visible;
                ListCol.MinWidth = 200;
                ListCol.MaxWidth = double.PositiveInfinity;
                ListCol.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                // Detail view: Detail visible 100%, Nav & List hidden
                BackToMasterButton.Visibility = Visibility.Visible;
                NavBorder.Visibility = Visibility.Collapsed;
                NavCol.MinWidth = 0;
                NavCol.MaxWidth = 0;
                NavCol.Width = new GridLength(0);
                NavSplitterCol.Width = new GridLength(0);
                NavSplitter.Visibility = Visibility.Collapsed;

                NotesListGrid.Visibility = Visibility.Collapsed;
                ListCol.MinWidth = 0;
                ListCol.MaxWidth = 0;
                ListCol.Width = new GridLength(0);
                DetailSplitterCol.Width = new GridLength(0);
                DetailSplitter.Visibility = Visibility.Collapsed;

                DetailPaneBorder.Visibility = Visibility.Visible;
                DetailCol.MinWidth = 250;
                DetailCol.MaxWidth = double.PositiveInfinity;
                DetailCol.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        UpdateSplitPaneOrientation();
    }

    private void UpdateLayoutForNarrowMode(bool isNarrow, bool isDetailActive)
    {
        UpdateWorkspaceLayout();
    }

    public static readonly DependencyProperty BoardCardWidthProperty =
        DependencyProperty.Register(
            nameof(BoardCardWidth),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(WorkspaceLayoutHelper.DefaultBoardCardWidth));

    public double BoardCardWidth
    {
        get => (double)GetValue(BoardCardWidthProperty);
        set => SetValue(BoardCardWidthProperty, value);
    }

    public void UpdateBoardLayout(double? windowWidthOverride = null)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.WorkspaceViewMode != WorkspaceViewMode.Board) return;

        double windowWidth = windowWidthOverride ?? (ActualWidth > 0 ? ActualWidth : Width);
        if (double.IsNaN(windowWidth) || double.IsInfinity(windowWidth) || windowWidth <= 0)
        {
            windowWidth = WorkspaceLayoutHelper.DefaultWindowWidth;
        }

        double navWidth = vm.NavigationPanelState == NavigationPanelState.Compact
            ? WorkspaceLayoutHelper.CompactNavWidth
            : WorkspaceLayoutHelper.ClampNavWidth(NavCol.ActualWidth > 0 ? NavCol.ActualWidth : vm.NavigationPanelWidth);
        double splitterWidth = vm.NavigationPanelState == NavigationPanelState.Compact
            ? 0.0
            : (NavSplitter.ActualWidth > 0 ? NavSplitter.ActualWidth : 6.0);

        BoardHost.Margin = new Thickness(navWidth + splitterWidth, 0, 0, 0);

        double availableBoardWidth;
        if (BoardHost.ActualWidth > 0)
        {
            availableBoardWidth = BoardHost.ActualWidth;
        }
        else
        {
            availableBoardWidth = Math.Max(0, windowWidth - navWidth - splitterWidth);
        }

        bool isNarrow = vm.IsNarrow;
        int columns = WorkspaceLayoutHelper.CalculateBoardColumnCount(availableBoardWidth, isNarrow);
        double cardWidth = WorkspaceLayoutHelper.CalculateBoardCardWidth(availableBoardWidth, columns, WorkspaceLayoutHelper.BoardCardSpacing);
        BoardCardWidth = WorkspaceLayoutHelper.ClampBoardCardWidth(cardWidth);
    }

    public void UpdateSplitPaneOrientation()
    {
        if (DataContext is not MainViewModel vm || vm.DetailEditor == null) return;
        if (DetailEditorPreviewGrid == null) return;

        if (vm.DetailEditor.ViewMode != MarkdownViewMode.Split)
        {
            ApplySplitOrientation(sideBySide: true);
            return;
        }

        double availableWidth = DetailPaneBorder.ActualWidth > 0
            ? DetailPaneBorder.ActualWidth
            : (ActualWidth > 0 ? ActualWidth : Width);
        if (availableWidth <= 0) availableWidth = 1100;

        // Subtract internal detail margins and padding (~40 DIP)
        double netContentWidth = Math.Max(0, availableWidth - 40);

        bool canSideBySide = WorkspaceLayoutHelper.CanPanesBeSideBySide(netContentWidth);
        ApplySplitOrientation(canSideBySide);
    }

    private void ApplySplitOrientation(bool sideBySide)
    {
        if (DetailEditorPreviewGrid == null || DetailEditorCol == null || DetailEditorSplitterCol == null || DetailPreviewCol == null) return;

        if (sideBySide)
        {
            DetailEditorCol.Width = new GridLength(1, GridUnitType.Star);
            DetailEditorSplitterCol.Width = GridLength.Auto;
            DetailPreviewCol.Width = new GridLength(1, GridUnitType.Star);

            DetailEditorRow.Height = new GridLength(1, GridUnitType.Star);
            DetailSplitterRow.Height = new GridLength(0);
            DetailPreviewRow.Height = new GridLength(0);

            Grid.SetRow(DetailBodyBox, 0);
            Grid.SetRow(DetailSplitDivider, 0);
            Grid.SetRow(DetailPreviewViewer, 0);

            Grid.SetColumn(DetailBodyBox, 0);
            Grid.SetColumn(DetailSplitDivider, 1);
            Grid.SetColumn(DetailPreviewViewer, 2);

            DetailSplitDivider.Width = 4;
            DetailSplitDivider.Height = double.NaN;
            DetailSplitDivider.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            DetailSplitDivider.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            DetailSplitDivider.Cursor = System.Windows.Input.Cursors.SizeWE;

            Grid.SetRow(DetailPreviewViewer, 0);
            Grid.SetColumn(DetailPreviewViewer, 2);

            // Restore column span
            Grid.SetColumnSpan(DetailBodyBox, 1);
            Grid.SetColumnSpan(DetailSplitDivider, 1);
            Grid.SetColumnSpan(DetailPreviewViewer, 1);
        }
        else
        {
            DetailEditorCol.Width = new GridLength(1, GridUnitType.Star);
            DetailEditorSplitterCol.Width = new GridLength(0);
            DetailPreviewCol.Width = new GridLength(0);

            DetailEditorRow.Height = new GridLength(1, GridUnitType.Star);
            DetailSplitterRow.Height = GridLength.Auto;
            DetailPreviewRow.Height = new GridLength(1, GridUnitType.Star);

            Grid.SetColumn(DetailBodyBox, 0);
            Grid.SetColumn(DetailSplitDivider, 0);
            Grid.SetColumn(DetailPreviewViewer, 0);

            Grid.SetRow(DetailBodyBox, 0);
            Grid.SetRow(DetailSplitDivider, 1);
            Grid.SetRow(DetailPreviewViewer, 2);

            DetailSplitDivider.Width = double.NaN;
            DetailSplitDivider.Height = 4;
            DetailSplitDivider.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            DetailSplitDivider.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            DetailSplitDivider.Cursor = System.Windows.Input.Cursors.SizeNS;
        }
    }

    private void NavSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsNarrow)
        {
            vm.NavigationPanelWidth = WorkspaceLayoutHelper.ClampNavWidth(NavCol.ActualWidth);
            if (vm.WorkspaceViewMode == WorkspaceViewMode.Board)
            {
                UpdateBoardLayout();
            }
        }
    }

    private void TagSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        NavSplitter_DragCompleted(sender, e);
    }

    private void DetailSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsNarrow)
        {
            vm.NoteListPanelWidth = WorkspaceLayoutHelper.ClampListWidth(ListCol.ActualWidth);
        }
    }

    internal static Action? ApplicationShutdownOverrideForTests { get; set; }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        if (DataContext is MainViewModel vm)
        {
            vm.CancelSync();
        }
        (DataContext as IDisposable)?.Dispose();
        Close();
        if (ApplicationShutdownOverrideForTests != null)
        {
            ApplicationShutdownOverrideForTests();
        }
        else
        {
            System.Windows.Application.Current?.Shutdown();
        }
    }

    /// <summary>
    /// Closes this window for real without shutting down the process. User close
    /// still hides to the tray; test and host disposal must not leave a loaded
    /// hidden MainWindow in <see cref="Application.Windows"/>.
    /// </summary>
    internal void CloseWithoutShutdown()
    {
        if (Dispatcher != null && !Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(CloseWithoutShutdown);
            return;
        }

        try
        {
            _isExplicitExit = true;
            Close();
        }
        catch
        {
            // Already closed or closing
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            double navWidth = NavCol.ActualWidth > 0 ? NavCol.ActualWidth : vm.NavigationPanelWidth;
            double listWidth = vm.WorkspaceViewMode == WorkspaceViewMode.List && ListCol.ActualWidth > 0
                ? ListCol.ActualWidth
                : vm.NoteListPanelWidth;

            Rect bounds;
            WindowState stateToPersist;
            if (_isFullscreen)
            {
                bounds = new Rect(_preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight);
                stateToPersist = _preFullscreenWindowState;
            }
            else
            {
                bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                stateToPersist = WindowState;
            }

            vm.SaveWindowBoundsAndLayout(
                bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                stateToPersist, navWidth, listWidth);

            if (!_isExplicitExit)
            {
                var decision = vm.DecideUserClose();
                if (decision == CloseMainWindowDecision.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (decision == CloseMainWindowDecision.Hide)
                {
                    e.Cancel = true;
                    if (_isFullscreen)
                    {
                        ExitFullscreen();
                    }
                    Hide();
                    return;
                }

                _isExplicitExit = true;
                if (ApplicationShutdownOverrideForTests != null)
                {
                    ApplicationShutdownOverrideForTests();
                }
                else
                {
                    System.Windows.Application.Current?.Shutdown();
                }
                return;
            }
        }
        else if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CancelSync();
        }
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose();
    }

    public void BringWindowToFront()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            QuickNotes.App.Helpers.Win32Helper.ShowWindow(handle, QuickNotes.App.Helpers.Win32Helper.SW_RESTORE);
            QuickNotes.App.Helpers.Win32Helper.SetForegroundWindow(handle);
        }
    }

    // Single click anywhere on the card selects. Narrow layout also opens the detail pane.
    // Double-click is not a command (UX-C14). Header and body share this handler via CardBorder.
    private void CardSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin
            && FindAncestor<Button>(origin) != null)
        {
            return;
        }

        if (e.ClickCount != 1)
        {
            e.Handled = true;
            return;
        }

        if (DataContext is MainViewModel vm && (sender as FrameworkElement)?.DataContext is NoteCardViewModel card)
        {
            vm.OpenNoteInDetailCommand.Execute(card);
            if (vm.IsNarrow)
            {
                e.Handled = true;
            }
        }
    }

    internal static T? FindAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = GetTreeParent(current);
        }

        return null;
    }

    // Run / other content elements are not Visual; VisualTreeHelper.GetParent throws on them.
    private static DependencyObject? GetTreeParent(DependencyObject current)
    {
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current)
                ?? LogicalTreeHelper.GetParent(current);
        }

        return LogicalTreeHelper.GetParent(current);
    }

    private System.Windows.Point _dragStartPoint;
    private TagTreeItemViewModel? _draggedTag;
    private TagTreeItemViewModel? _currentDropTarget;

    private void TagTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SelectedTag = e.NewValue as TagTreeItemViewModel;
        }
    }

    private static TagTreeItemViewModel? FindTagViewModel(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement fe && fe.DataContext is TagTreeItemViewModel vm)
            {
                return vm;
            }
            if (source is TreeViewItem tvi && tvi.DataContext is TagTreeItemViewModel tviVm)
            {
                return tviVm;
            }
            if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
            {
                source = VisualTreeHelper.GetParent(source);
            }
            else
            {
                source = LogicalTreeHelper.GetParent(source);
            }
        }
        return null;
    }

    private static bool IsDescendantOf<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T) return true;
            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            else
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        return false;
    }

    private static bool IsWithin(DependencyObject? current, DependencyObject ancestor)
    {
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    // The top bar is a two-segment Список / Доска control. Each segment explicitly
    // selects its representation; in Focus the same click is a leave-Focus request.
    private void WorkspaceModeToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        e.Handled = true;
        WorkspaceModeToggle.ApplyTemplate();
        var origin = e.OriginalSource as DependencyObject;
        var listSegment = WorkspaceModeToggle.Template?.FindName("ListSegment", WorkspaceModeToggle) as DependencyObject;
        var boardSegment = WorkspaceModeToggle.Template?.FindName("BoardSegment", WorkspaceModeToggle) as DependencyObject;

        if (listSegment != null && IsWithin(origin, listSegment))
        {
            vm.RequestWorkspaceViewMode(WorkspaceViewMode.List);
            return;
        }

        if (boardSegment != null && IsWithin(origin, boardSegment))
        {
            vm.RequestWorkspaceViewMode(WorkspaceViewMode.Board);
            return;
        }

        vm.RequestWorkspaceViewMode(vm.IsBoardMode ? WorkspaceViewMode.List : WorkspaceViewMode.Board);
    }

    private void TagTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDescendantOf<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject))
        {
            _draggedTag = null;
            return;
        }

        _dragStartPoint = e.GetPosition(this);
        _draggedTag = FindTagViewModel(e.OriginalSource as DependencyObject);
    }

    private void TagTreeView_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTag == null)
        {
            return;
        }

        System.Windows.Point currentPoint = e.GetPosition(this);
        Vector diff = _dragStartPoint - currentPoint;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var tagToDrag = _draggedTag;
            _draggedTag = null;

            var data = new DataObject("QuickNotes.TagTreeItemViewModel", tagToDrag);
            DragDrop.DoDragDrop(TagTree, data, DragDropEffects.Move);
            ClearDropTarget();
        }
    }

    private void TagTreeView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedTag = null;
    }

    private void TagTreeView_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("QuickNotes.TagTreeItemViewModel"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceTag = e.Data.GetData("QuickNotes.TagTreeItemViewModel") as TagTreeItemViewModel;
        var targetTag = FindTagViewModel(e.OriginalSource as DependencyObject);
        int? targetParentId = targetTag?.Id;

        if (DataContext is MainViewModel vm && sourceTag != null)
        {
            if (vm.CanMoveTag(sourceTag.Id, targetParentId))
            {
                e.Effects = DragDropEffects.Move;
                SetDropTarget(targetTag);
            }
            else
            {
                e.Effects = DragDropEffects.None;
                ClearDropTarget();
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
            ClearDropTarget();
        }

        e.Handled = true;
    }

    private void TagTreeView_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ClearDropTarget();
    }

    private void TagTreeView_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ClearDropTarget();

        if (e.Data.GetDataPresent("QuickNotes.TagTreeItemViewModel") && DataContext is MainViewModel vm)
        {
            var sourceTag = e.Data.GetData("QuickNotes.TagTreeItemViewModel") as TagTreeItemViewModel;
            var targetTag = FindTagViewModel(e.OriginalSource as DependencyObject);
            int? targetParentId = targetTag?.Id;

            if (sourceTag != null)
            {
                vm.MoveTag(sourceTag.Id, targetParentId);
            }
        }

        e.Handled = true;
    }

    private void SetDropTarget(TagTreeItemViewModel? target)
    {
        if (_currentDropTarget != target)
        {
            if (_currentDropTarget != null)
            {
                _currentDropTarget.IsDropTarget = false;
            }
            _currentDropTarget = target;
            if (_currentDropTarget != null)
            {
                _currentDropTarget.IsDropTarget = true;
            }
        }
    }

    private void ClearDropTarget()
    {
        if (_currentDropTarget != null)
        {
            _currentDropTarget.IsDropTarget = false;
            _currentDropTarget = null;
        }
    }


    private bool? OpenNoteEditor(NoteEditorViewModel vm)
    {
        // Wire password dialogs for protection actions.
        vm.RequestPasswordDialog = (title, message) =>
        {
            var dlg = new ProtectionPasswordDialog(title, message, ProtectionPasswordDialog.DialogMode.Unlock, owner: this);
            return dlg.ShowDialog() == true ? dlg.Password : null;
        };
        vm.RequestSetPasswordDialog = (title, message) =>
        {
            var dlg = new ProtectionPasswordDialog(title, message, ProtectionPasswordDialog.DialogMode.SetPassword, owner: this);
            return dlg.ShowDialog() == true ? dlg.Password : null;
        };

        var win = new NoteEditorWindow(vm);
        if (DataContext is MainViewModel main)
        {
            vm.SetOcrHotkeySuspended = main.SetOcrHotkeySuspended;
        }
        vm.RequestUnsavedEditorDecision ??= () => UnsavedEditorPrompt.Show(win);
        return QuickNotes.App.Helpers.WindowActivationHelper.PrepareAndShowEditor(
            win,
            IsVisible && WindowState != WindowState.Minimized ? this : null);
    }

    private bool? OpenTagEditor(TagEditViewModel vm)
    {
        var win = new TagEditDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenSavedViewEditor(SavedViewEditViewModel vm)
    {
        var win = new SavedViewEditDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenSynonymsEditor(TagSynonymsViewModel vm)
    {
        var win = new TagSynonymsDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenTagRules(TagRuleViewModel vm)
    {
        var win = new TagRuleDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenChangeParentEditor(ChangeParentViewModel vm)
    {
        var win = new ChangeParentDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenTagMerge(TagMergeViewModel vm)
    {
        var win = new TagMergeDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenRescanPreview(TagRescanPreviewViewModel vm)
    {
        var win = new TagRescanPreviewDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenTagSuggestions(TagSuggestionsViewModel vm)
    {
        var win = new TagSuggestionsDialog(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenSettings(SettingsViewModel vm)
    {
        var win = new SettingsWindow(vm);
        if (IsVisible) win.Owner = this;
        var result = win.ShowDialog();
        if (result == true && DataContext is MainViewModel mainVm)
        {
            mainVm.RefreshHotkeyPrompts();
        }
        return result;
    }

    private bool? OpenNoteAssembly(NoteAssemblyViewModel vm)
    {
        var win = new NoteAssemblyWindow(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenImportExport(ImportExportViewModel vm)
    {
        var win = new ImportExportWindow(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenSyncConflicts(SyncConflictsViewModel vm)
    {
        var win = new SyncConflictsWindow(vm);
        if (IsVisible) win.Owner = this;
        return win.ShowDialog();
    }

    private bool? OpenTemplateManagementDialog(TemplateManagementViewModel vm)
    {
        var win = new TemplateManagementDialog(vm);
        var owner = QuickNotes.App.Helpers.WindowOwnerResolver.GetActiveWindowOwner();
        if (owner != null && owner.IsVisible)
        {
            win.Owner = owner;
        }
        else if (IsVisible)
        {
            win.Owner = this;
        }
        return win.ShowDialog();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool isWin = (Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Never intercept standard text editing / clipboard shortcuts
        if ((isCtrl && (key == Key.C || key == Key.V || key == Key.X || key == Key.A || key == Key.Z || key == Key.Y)) ||
            (isShift && key == Key.Insert))
        {
            return;
        }

        // F6 / Shift+F6: Cycle focus between main panes (SearchBox -> TagTree -> NotesListBox -> DetailEditor)
        if (key == Key.F6)
        {
            CyclePanes(isShift);
            e.Handled = true;
            return;
        }

        // F11: Toggle fullscreen mode
        if (key == Key.F11 && !isCtrl && !isAlt && !isShift)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        // F4: Toggle navigation panel state (Normal / Compact / Hidden in Focus)
        if (key == Key.F4 && !isCtrl && !isAlt && !isShift)
        {
            if (DataContext is MainViewModel navVm && navVm.ToggleNavigationStateCommand.CanExecute(null))
            {
                navVm.ToggleNavigationStateCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Focus mode: Escape leaves through the shared unsaved/journal contract.
        if (key == Key.Escape &&
            DataContext is MainViewModel focusVm && focusVm.IsFocusMode)
        {
            if (focusVm.DetailEditor?.IsFindOpen == true)
            {
                focusVm.DetailEditor.CloseFindCommand.Execute(null);
                DetailBodyBox.Focus();
                e.Handled = true;
                return;
            }

            focusVm.LeaveFocus(focusVm.FocusReturnViewMode);
            e.Handled = true;
            return;
        }

        // Narrow mode: Back command on Escape or Alt+Left
        if ((key == Key.Escape || (isAlt && key == Key.Left)) &&
            DataContext is MainViewModel narrowVm && narrowVm.IsNarrow && narrowVm.IsDetailActiveInNarrow)
        {
            if (key == Key.Escape && narrowVm.DetailEditor?.IsFindOpen == true)
            {
                narrowVm.DetailEditor.CloseFindCommand.Execute(null);
                DetailBodyBox.Focus();
                e.Handled = true;
                return;
            }

            if (key == Key.Escape && _isFullscreen)
            {
                ExitFullscreen();
                e.Handled = true;
                return;
            }

            narrowVm.BackToMasterCommand.Execute(null);
            FocusNotesList();
            e.Handled = true;
            return;
        }

        // Ctrl+S: Save note in detail editor
        if (isCtrl && !isAlt && !isShift && key == Key.S)
        {
            if (DataContext is MainViewModel saveVm && saveVm.DetailEditor != null && saveVm.DetailEditor.SaveCommand.CanExecute(null))
            {
                saveVm.DetailEditor.SaveCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+P: Toggle Markdown preview in detail editor
        if (isCtrl && !isAlt && !isShift && key == Key.P)
        {
            if (DataContext is MainViewModel previewVm && previewVm.DetailEditor != null)
            {
                previewVm.DetailEditor.TogglePreviewModeCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+F or F3: Focus Search box (or in-editor find if editor has focus)
        if ((isCtrl && key == Key.F) || key == Key.F3)
        {
            if (DataContext is MainViewModel findVm && findVm.DetailEditor != null && (DetailBodyBox.IsKeyboardFocused || DetailTitleTextBox.IsKeyboardFocused))
            {
                findVm.DetailEditor.OpenFindCommand.Execute(null);
                DetailFindInputBox.Focus();
                DetailFindInputBox.SelectAll();
                e.Handled = true;
                return;
            }

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Ctrl+N: New empty note
        if (isCtrl && !isShift && key == Key.N)
        {
            if (DataContext is MainViewModel vm && vm.CreateEmptyNoteCommand.CanExecute(null))
            {
                vm.CreateEmptyNoteCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // F1: Help
        if (key == Key.F1)
        {
            OpenHelp();
            e.Handled = true;
            return;
        }

        // F5: manual sync
        if (key == Key.F5 && !isCtrl && !isAlt && !isShift)
        {
            if (DataContext is MainViewModel syncVm && syncVm.SyncNowCommand.CanExecute(null))
            {
                syncVm.SyncNowCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+Shift+E: import/export
        if (isCtrl && isShift && key == Key.E)
        {
            if (DataContext is MainViewModel exportVm && exportVm.OpenImportExportCommand.CanExecute(null))
            {
                exportVm.OpenImportExportCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Alt+M: overflow menu
        if (isAlt && key == Key.M)
        {
            ShowMoreMenu(MoreMenuButton);
            e.Handled = true;
            return;
        }

        // Ctrl+Enter: processed and next (inbox queue) or save note
        if (isCtrl && key == Key.Enter)
        {
            if (DataContext is MainViewModel inboxVm && inboxVm.SelectedNote != null && inboxVm.SelectedNote.IsInbox)
            {
                inboxVm.MarkProcessedAndNext(inboxVm.SelectedNote.Id);
                e.Handled = true;
                return;
            }
            if (DataContext is MainViewModel detailVm && detailVm.DetailEditor != null && detailVm.DetailEditor.SaveCommand.CanExecute(null))
            {
                detailVm.DetailEditor.SaveCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Screen capture and OCR — do not steal Markdown numbered-list chords from the body editor
        if (DataContext is MainViewModel ocrVm &&
            !DetailBodyBox.IsKeyboardFocusWithin &&
            ocrVm.MatchesOcrHotkey(key, isCtrl, isShift, isAlt, isWin))
        {
            if (ocrVm.ScreenOcrCommand.CanExecute(null))
            {
                ocrVm.ScreenOcrCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+E: Edit selected note in modal window. In Focus the note already
        // lives in the in-window editor, so route to it instead of opening a second
        // editor for the same note (prevents two commits of the same edit).
        if (isCtrl && key == Key.E)
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.IsFocusMode && vm.DetailEditor != null)
                {
                    DetailBodyBox.Focus();
                    e.Handled = true;
                    return;
                }

                if (vm.SelectedNote != null && vm.EditNoteCommand.CanExecute(vm.SelectedNote))
                {
                    vm.EditNoteCommand.Execute(vm.SelectedNote);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Alt+Down / Alt+Up: Select next/previous note
        if (isAlt && (key == Key.Down || key == Key.Up))
        {
            if (key == Key.Down)
            {
                SelectNextNote();
            }
            else
            {
                SelectPreviousNote();
            }
            e.Handled = true;
            return;
        }

        // Enter:
        // When focused on SearchBox, advance focus to notes list.
        // When a card list (List or Board) has keyboard focus, open Focus for the
        // selected note using the existing detail editor.
        if (key == Key.Enter && !isCtrl && !isAlt && !isShift)
        {
            if (SearchBox.IsKeyboardFocused)
            {
                if (DataContext is MainViewModel vm && vm.Notes.Count > 0)
                {
                    if (vm.SelectedNote == null)
                    {
                        vm.SelectedNote = vm.Notes[0];
                    }
                    FocusNotesList();
                    e.Handled = true;
                    return;
                }
            }
            else if (NotesListBox.IsKeyboardFocusWithin || BoardListBox.IsKeyboardFocusWithin)
            {
                if (DataContext is MainViewModel vm && vm.SelectedNote != null
                    && !(e.OriginalSource is DependencyObject enterOrigin && FindAncestor<Button>(enterOrigin) != null))
                {
                    if (vm.EnterFocus(vm.SelectedNote))
                    {
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        // Escape:
        // In detail find bar: close find bar
        // In fullscreen: exit fullscreen
        // In search box: clear search if not empty, otherwise move focus to notes list
        if (key == Key.Escape)
        {
            if (DataContext is MainViewModel vm && vm.DetailEditor?.IsFindOpen == true)
            {
                vm.DetailEditor.CloseFindCommand.Execute(null);
                DetailBodyBox.Focus();
                e.Handled = true;
                return;
            }

            if (_isFullscreen)
            {
                ExitFullscreen();
                e.Handled = true;
                return;
            }

            if (SearchBox.IsKeyboardFocused)
            {
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                else
                {
                    FocusNotesList();
                }
                e.Handled = true;
                return;
            }
        }
    }

    private void SelectNextNote()
    {
        if (DataContext is not MainViewModel vm || vm.Notes.Count == 0) return;
        int currentIndex = vm.SelectedNote != null ? vm.Notes.IndexOf(vm.SelectedNote) : -1;
        int nextIndex = currentIndex + 1;
        if (nextIndex >= vm.Notes.Count) nextIndex = vm.Notes.Count - 1;
        vm.SelectedNote = vm.Notes[nextIndex];
        FocusNotesList();
    }

    private void SelectPreviousNote()
    {
        if (DataContext is not MainViewModel vm || vm.Notes.Count == 0) return;
        int currentIndex = vm.SelectedNote != null ? vm.Notes.IndexOf(vm.SelectedNote) : 0;
        int prevIndex = currentIndex - 1;
        if (prevIndex < 0) prevIndex = 0;
        vm.SelectedNote = vm.Notes[prevIndex];
        FocusNotesList();
    }

    private void FocusNotesList()
    {
        if (DataContext is MainViewModel vm && vm.WorkspaceViewMode == WorkspaceViewMode.Board)
        {
            if (vm.SelectedNote != null)
            {
                BoardListBox.ScrollIntoView(vm.SelectedNote);
            }
            BoardListBox.Focus();
            return;
        }

        if (DataContext is MainViewModel vm2 && vm2.SelectedNote != null)
        {
            NotesListBox.ScrollIntoView(vm2.SelectedNote);
        }
        NotesListBox.Focus();
    }

    private void ScrollDetailSelectionIntoView()
    {
        if (!DetailBodyBox.IsVisible)
        {
            return;
        }

        int start = DetailBodyBox.SelectionStart;
        if (start < 0)
        {
            return;
        }

        int line = DetailBodyBox.GetLineIndexFromCharacterIndex(Math.Min(start, Math.Max(0, DetailBodyBox.Text.Length)));
        if (line >= 0)
        {
            DetailBodyBox.ScrollToLine(line);
        }
    }

    private void CyclePanes(bool reverse)
    {
        var panes = new System.Collections.Generic.List<UIElement>();
        if (SearchBox.IsVisible) panes.Add(SearchBox);
        if (TagTree.IsVisible) panes.Add(TagTree);
        if (NotesListBox.IsVisible) panes.Add(NotesListBox);
        if (TasksListBox.IsVisible) panes.Add(TasksListBox);
        if (BoardListBox.IsVisible) panes.Add(BoardListBox);
        if (BoardTasksListBox.IsVisible) panes.Add(BoardTasksListBox);
        if (DetailPaneBorder.IsVisible)
        {
            if (DetailBodyBox.IsVisible && DetailBodyBox.IsEnabled)
            {
                panes.Add(DetailBodyBox);
            }
            else if (BackToMasterButton.IsVisible)
            {
                panes.Add(BackToMasterButton);
            }
        }

        if (panes.Count == 0) return;

        int current = -1;
        for (int i = 0; i < panes.Count; i++)
        {
            if (panes[i].IsKeyboardFocusWithin)
            {
                current = i;
                break;
            }
        }

        int next;
        if (reverse)
        {
            next = current <= 0 ? panes.Count - 1 : current - 1;
        }
        else
        {
            next = current >= panes.Count - 1 ? 0 : current + 1;
        }

        var target = panes[next];
        target.Focus();
        if (target is TextBox tb)
        {
            tb.SelectAll();
        }
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CreateEmptyNote();
        }
    }

    private void NewNoteButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowTemplatesContextMenu(NewNoteSplitRoot ?? sender as FrameworkElement);
        e.Handled = true;
    }

    private void NewNoteDropdownButton_Click(object sender, RoutedEventArgs e)
    {
        ShowTemplatesContextMenu(NewNoteSplitRoot ?? sender as FrameworkElement);
    }

    private void ShowTemplatesContextMenu(FrameworkElement? target)
    {
        if (DataContext is not MainViewModel vm || target == null)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom
        };

        menu.SetResourceReference(ContextMenu.BackgroundProperty, "MenuBgBrush");
        menu.SetResourceReference(ContextMenu.ForegroundProperty, "MenuFgBrush");
        menu.SetResourceReference(ContextMenu.BorderBrushProperty, "LineBrush");

        var emptyItem = new MenuItem
        {
            Header = "Пустая заметка",
            InputGestureText = "Ctrl+N",
            ToolTip = "Создать чистую заметку без шаблона"
        };
        emptyItem.Click += (_, _) => vm.CreateEmptyNote();
        menu.Items.Add(emptyItem);

        menu.Items.Add(new Separator());

        if (vm.Templates.Count > 0)
        {
            foreach (var template in vm.Templates)
            {
                var t = template;
                var tItem = new MenuItem
                {
                    Header = t.Title,
                    ToolTip = !string.IsNullOrWhiteSpace(t.Text)
                        ? (t.Text.Length > 80 ? t.Text[..77] + "..." : t.Text)
                        : "Шаблон заметки"
                };
                tItem.Click += (_, _) => vm.CreateNoteFromTemplate(t);
                menu.Items.Add(tItem);
            }

            menu.Items.Add(new Separator());
        }

        var manageItem = new MenuItem
        {
            Header = "Управление шаблонами…",
            ToolTip = "Открыть окно управления шаблонами"
        };
        manageItem.Click += (_, _) => vm.OpenTemplateManagement();
        menu.Items.Add(manageItem);

        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMoreMenu(MoreMenuButton ?? sender as FrameworkElement);
    }

    private void SearchHelpButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSearchHelpMenu(SearchHelpButton ?? sender as FrameworkElement);
    }

    private void ShowMoreMenu(FrameworkElement? target)
    {
        if (DataContext is not MainViewModel vm || target == null)
            return;

        var menu = CreateThemedContextMenu(target);

        AddMenuCommand(menu, vm.FullscreenButtonText, vm.ToggleFullscreenCommand, vm.FullscreenButtonTooltip, "F11");
        AddMenuCommand(menu, vm.NavigationStateToggleText, vm.ToggleNavigationStateCommand, "Переключить состояние навигационной панели", "F4");
        AddMenuCommand(menu, vm.CompactButtonText, vm.ToggleCompactCommand, "Переключить плотность карточек");
        AddMenuCommand(menu, "Пересканировать теги…", vm.RescanAllCommand, "Заново определить теги для всех активных заметок");
        AddMenuCommand(menu, UserTaskCopy.MenuTransfer, vm.OpenImportExportCommand, "Текстовый обмен и полный архив с паролем", "Ctrl+Shift+E");
        AddMenuCommand(menu, "Собрать заметки…", vm.OpenNoteAssemblyCommand, "Собрать выбранные заметки в одну с предпросмотром Markdown");
        menu.Items.Add(new Separator());
        AddMenuCommand(menu, "Синхронизировать", vm.SyncNowCommand, vm.SyncStatusTooltip, "F5");
        AddMenuCommand(menu, UserTaskCopy.MenuCloudWizard, vm.OpenCloudSetupWizardCommand, "Пошаговое подключение облачных копий");
        AddMenuCommand(menu, "Настройки…", vm.OpenSettingsCommand, "Основные параметры, поиск и облако");
        menu.Items.Add(new Separator());
        AddMenuCommand(menu, "Справка", vm.OpenHelpCommand, "Горячие клавиши, данные, резервные копии и облако", "F1");

        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ShowSearchHelpMenu(FrameworkElement? target)
    {
        if (DataContext is not MainViewModel vm || target == null)
            return;

        var menu = CreateThemedContextMenu(target);

        AddSearchExample(menu, vm, "tag:работа", "Заметки с тегом «работа»");
        AddSearchExample(menu, vm, "created:today", "Созданные сегодня");
        AddSearchExample(menu, vm, "updated:week", "Изменённые за неделю");
        AddSearchExample(menu, vm, "untagged:true", "Без тегов");
        AddSearchExample(menu, vm, "source:chrome", "Источник — процесс или заголовок окна");
        AddSearchExample(menu, vm, "url:https", "Заметки со ссылкой в источнике");
        menu.Items.Add(new Separator());
        AddSearchExample(menu, vm, "tag:работа AND tag:срочно", "Оба тега сразу");
        AddSearchExample(menu, vm, "tag:работа OR tag:личное", "Любой из тегов");
        AddSearchExample(menu, vm, "tag:работа WITHOUT tag:архив", "Исключить тег");

        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void DetailMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.DetailEditor == null || sender is not FrameworkElement target)
            return;

        var editor = vm.DetailEditor;
        var menu = CreateThemedContextMenu(target);

        AddMenuCommand(menu, "Сохранить", editor.SaveCommand, "Сохранить изменения (Ctrl+S / Ctrl+Enter)", "Ctrl+S");
        AddMenuCommand(menu, editor.IsPinned ? "Открепить" : "Закрепить", editor.TogglePinCommand, "Закрепить или открепить заметку");
        AddMenuCommand(menu, editor.IsFavorite ? "Удалить из избранного" : "В избранное", editor.ToggleFavoriteCommand, "Добавить в избранное или удалить");
        menu.Items.Add(new Separator());
        AddMenuCommand(menu, "Вставить ссылку…", editor.InsertLinkCommand, "Вставить Markdown-ссылку на другую заметку", "Ctrl+K");
        AddMenuCommand(menu, "Прикрепить файл…", editor.AddAttachmentCommand, "Добавить файл к заметке");
        menu.Items.Add(new Separator());

        if (editor.IsNoteProtected)
        {
            if (editor.IsNoteUnlocked)
            {
                AddMenuCommand(menu, "Заблокировать", editor.LockNoteCommand, "Скрыть содержимое защищённой заметки");
                AddMenuCommand(menu, "Сменить пароль защиты…", editor.ChangePasswordCommand, "Установить новый пароль для этой заметки");
                AddMenuCommand(menu, "Снять защиту…", editor.RemoveProtectionCommand, "Сделать заметку обычной (не зашифрованной)");
            }
            else
            {
                AddMenuCommand(menu, "Ввести пароль…", editor.UnlockNoteCommand, "Открыть защищённую заметку");
            }
        }
        else
        {
            AddMenuCommand(menu, "Защитить паролем…", editor.ProtectNoteCommand, "Зашифровать заметку мастер-паролем");
        }

        menu.Items.Add(new Separator());
        if (vm.SelectedNote != null)
        {
            AddMenuCommand(menu, "Собрать заметки…", vm.OpenNoteAssemblyCommand, "Собрать исходники в новую заметку с предпросмотром");
            AddMenuCommand(menu, "В корзину", vm.DeleteNoteCommand, vm.SelectedNote, "Переместить заметку в корзину", "Del");
        }

        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static ContextMenu CreateThemedContextMenu(FrameworkElement target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom
        };
        menu.SetResourceReference(ContextMenu.BackgroundProperty, "MenuBgBrush");
        menu.SetResourceReference(ContextMenu.ForegroundProperty, "MenuFgBrush");
        menu.SetResourceReference(ContextMenu.BorderBrushProperty, "LineBrush");
        return menu;
    }

    private static void AddMenuCommand(ContextMenu menu, string header, ICommand? command, string? tooltip, string? gesture = null)
    {
        AddMenuCommand(menu, header, command, null, tooltip, gesture);
    }

    private static void AddMenuCommand(ContextMenu menu, string header, ICommand? command, object? commandParameter, string? tooltip, string? gesture = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            CommandParameter = commandParameter,
            ToolTip = tooltip
        };
        if (!string.IsNullOrWhiteSpace(gesture))
        {
            item.InputGestureText = gesture;
        }
        menu.Items.Add(item);
    }

    private static void AddSearchExample(ContextMenu menu, MainViewModel vm, string example, string tooltip)
    {
        var item = new MenuItem
        {
            Header = example,
            ToolTip = tooltip
        };
        item.Click += (_, _) => vm.ApplySearchExample(example);
        menu.Items.Add(item);
    }

    private void OpenHelp()
    {
        var ocrGesture = DataContext is MainViewModel mainVm
            ? mainVm.OcrHotkeyGestureText
            : ShortcutCatalog.DefaultOcrGesture;
        var win = new HelpWindow(ocrGesture);
        if (IsVisible) win.Owner = this;
        win.ShowDialog();
    }

    private void OpenCloudSetupWizard()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var settingsVm = vm.CreateSettingsViewModel();
        settingsVm.SettingsSectionIndex = 1;
        var win = new CloudSetupWizardWindow(settingsVm);
        if (IsVisible) win.Owner = this;
        win.ShowDialog();
        vm.NotifyCloudSettingsChanged();
    }

    private void OpenFirstRun()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var win = new FirstRunWindow(vm);
        if (IsVisible) win.Owner = this;
        win.ShowDialog();
    }

    private Action<MarkdownOutlineItem>? _detailOutlineNavigateHandler;
    private NoteEditorViewModel? _currentDetailEditor;
    private PropertyChangedEventHandler? _detailEditorPropertyChangedHandler;

    private void AttachDetailEditor(NoteEditorViewModel? editor)
    {
        if (_currentDetailEditor != null && _detailEditorPropertyChangedHandler != null)
        {
            _currentDetailEditor.PropertyChanged -= _detailEditorPropertyChangedHandler;
        }

        _currentDetailEditor = editor;
        if (editor == null) return;

        _detailEditorPropertyChangedHandler = (s, e) =>
        {
            if (e.PropertyName == nameof(NoteEditorViewModel.ViewMode))
            {
                UpdateWorkspaceLayout();
            }
        };
        editor.PropertyChanged += _detailEditorPropertyChangedHandler;

        editor.GetSelection = () => (DetailBodyBox.SelectionStart, DetailBodyBox.SelectionLength);
        editor.SetSelection = (start, len) =>
        {
            if (!editor.IsEditorVisible)
            {
                editor.ViewMode = MarkdownViewMode.Edit;
            }
            DetailBodyBox.Focus();
            DetailBodyBox.Select(
                Math.Clamp(start, 0, DetailBodyBox.Text.Length),
                Math.Clamp(len, 0, Math.Max(0, DetailBodyBox.Text.Length - start)));
        };

        _detailOutlineNavigateHandler = item =>
        {
            if (editor.IsEditorVisible)
            {
                DetailBodyBox.Focus();
                if (item.CharacterIndex >= 0 && item.CharacterIndex <= DetailBodyBox.Text.Length)
                {
                    DetailBodyBox.Select(item.CharacterIndex, 0);
                    int line = DetailBodyBox.GetLineIndexFromCharacterIndex(item.CharacterIndex);
                    if (line >= 0)
                    {
                        DetailBodyBox.ScrollToLine(line);
                    }
                }
            }
            if (editor.IsPreviewVisible && DetailPreviewViewer?.Document != null)
            {
                foreach (var block in DetailPreviewViewer.Document.Blocks)
                {
                    if (block.Tag is string tag && string.Equals(tag, item.AnchorId, StringComparison.OrdinalIgnoreCase))
                    {
                        block.BringIntoView();
                        break;
                    }
                }
            }
        };
        editor.RequestNavigateToOutline += _detailOutlineNavigateHandler;

        editor.InsertTextAtCaret = marker =>
        {
            if (!editor.IsEditorVisible)
            {
                editor.ViewMode = MarkdownViewMode.Edit;
            }
            DetailBodyBox.Focus();
            int start = DetailBodyBox.SelectionStart;
            DetailBodyBox.SelectedText = marker;
            DetailBodyBox.SelectionStart = start + marker.Length;
            DetailBodyBox.SelectionLength = 0;
        };

        editor.PickAttachmentFile ??= () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите файл для прикрепления",
                Filter = "Все файлы (*.*)|*.*|Изображения (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Документы (*.pdf;*.txt;*.md;*.docx;*.xlsx)|*.pdf;*.txt;*.md;*.docx;*.xlsx"
            };
            if (dlg.ShowDialog(this) == true)
            {
                return dlg.FileName;
            }
            return null;
        };

        editor.PickLinkedNote ??= () =>
        {
            var pickerVm = new NoteLinkPickerViewModel(editor.ContextFactory, editor.NoteId);
            var dlg = new NoteLinkPickerDialog(pickerVm) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                return pickerVm.SelectedNote;
            }
            return null;
        };
        editor.PickLinkNoteId ??= () => editor.PickLinkedNote?.Invoke()?.Id;
    }

    private void DetailBodyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel mainVm || mainVm.DetailEditor is not { } editor)
            return;

        if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (e.Key != Key.ImeProcessed)
            {
                var enterRes = MarkdownEditorOperations.HandleEnter(DetailBodyBox.Text, DetailBodyBox.SelectionStart, DetailBodyBox.SelectionLength);
                if (enterRes.Handled)
                {
                    e.Handled = true;
                    if (enterRes.Action == ListContinuationAction.Terminate)
                    {
                        int lineStart = DetailBodyBox.Text.LastIndexOf('\n', Math.Max(0, DetailBodyBox.SelectionStart - 1)) + 1;
                        int lineEnd = DetailBodyBox.Text.IndexOf('\n', DetailBodyBox.SelectionStart);
                        if (lineEnd < 0) lineEnd = DetailBodyBox.Text.Length;
                        DetailBodyBox.Select(lineStart, lineEnd - lineStart);
                        DetailBodyBox.SelectedText = string.Empty;
                    }
                    else
                    {
                        int selStart = DetailBodyBox.SelectionStart;
                        string cont = enterRes.Text.Substring(selStart, enterRes.SelectionStart - selStart);
                        DetailBodyBox.SelectedText = cont;
                    }
                    return;
                }
            }
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (e.Key == Key.B && !isShift)
            {
                editor.ToggleBoldCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.I && !isShift)
            {
                editor.ToggleItalicCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (ShortcutCatalog.IsHeading(e))
            {
                editor.ToggleHeadingCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (isShift && e.Key == Key.C)
            {
                editor.ToggleCodeCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (isShift && e.Key == Key.U)
            {
                editor.ToggleBulletListCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (ShortcutCatalog.IsNumberedList(e))
            {
                editor.ToggleNumberedListCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (isShift && e.Key == Key.X)
            {
                editor.ToggleCheckboxCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.K && !isShift)
            {
                if (DetailBodyBox.SelectionLength > 0)
                {
                    editor.InsertMarkdownLinkCommand.Execute(null);
                }
                else if (editor.InsertLinkCommand.CanExecute(null))
                {
                    editor.InsertLinkCommand.Execute(null);
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.V && System.Windows.Clipboard.ContainsImage())
            {
                int caret = DetailBodyBox.SelectionStart;
                if (editor.TryPasteImageFromClipboard(caret, out var pasteMessage))
                {
                    e.Handled = true;
                }
                else if (!string.IsNullOrEmpty(pasteMessage))
                {
                    MessageBox.Show(pasteMessage, "Вложение", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Handled = true;
                }
                return;
            }
        }
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri != null)
        {
            if (e.Uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                e.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = e.Uri.AbsoluteUri,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex)
                {
                    QuickNotes.App.Services.ErrorLogService.Write("OpenBrowser", ex);
                }
            }
            else if (e.Uri.Scheme.Equals("note", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = !string.IsNullOrEmpty(e.Uri.Host) ? e.Uri.Host : e.Uri.AbsolutePath.Trim('/');
                if (int.TryParse(idStr, out int targetNoteId) && DataContext is MainViewModel vm)
                {
                    var noteCard = vm.Notes.FirstOrDefault(n => n.Id == targetNoteId);
                    if (noteCard != null)
                    {
                        vm.SelectedNote = noteCard;
                    }
                }
            }
        }
        e.Handled = true;
    }
}
