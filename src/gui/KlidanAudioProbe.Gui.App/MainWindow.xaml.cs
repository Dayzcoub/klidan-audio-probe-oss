using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using KlidanAudioProbe.Gui.Core;
using KlidanAudioProbe.Gui.Worker;
using System.ComponentModel;
using Microsoft.Win32;

namespace KlidanAudioProbe.Gui.App;

public partial class MainWindow : Window
{
    private readonly NativeWorkerClient _worker = new();
    private readonly MainViewModel _vm;
    private readonly string? _profileDirectory;
    private readonly bool _ownsWorkerLifecycle;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _stopTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _durationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remainingSeconds;
    private Guid? _timedSessionId;
    private bool _closeAfterWorker;
    private bool _closeRequested;

    public MainWindow() : this(null) { }

    public MainWindow(MainViewModel? viewModel, string? profileDirectory = null, bool developerToolsAvailable = true)
    {
        InitializeComponent();
        Title = KlidanAudioProbe.Gui.Core.Localization.Get("AppTitle");
        _ownsWorkerLifecycle = viewModel is null;
        _profileDirectory = profileDirectory;
        _vm = viewModel ?? new MainViewModel(worker: _worker, automaticScanWorkflow: new WorkerAutomaticScanWorkflow(_worker), duplexRepeatabilityWorkflow: new DuplexRepeatabilityWorkerWorkflow(_worker), exclusivePeriodDiscoveryWorkflow: new ExclusivePeriodDiscoveryWorkerWorkflow(_worker), customPeriodDuplexWorkflow: new CustomPeriodDuplexWorkerWorkflow(_worker), profileDirectory: profileDirectory, bundleExportInteraction: new WpfLaboratoryEvidenceBundleExportInteraction(this), developerToolsAvailable: developerToolsAvailable);
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MainViewModel.Screen) or nameof(MainViewModel.WorkflowStep) or nameof(MainViewModel.DeveloperToolsOpen)) { UpdateScreenVisibility(); UpdateResponsiveLayout(); } if (e.PropertyName == nameof(MainViewModel.SelectedWindowsEndpoint)) UpdateWindowsEndpointDetailsVisibility(); };
        DataContextChanged += (_, _) => UpdateWindowsEndpointDetailsVisibility();
        Loaded += (_, _) => UpdateResponsiveLayout();
        _meterTimer.Tick += (_, _) => _vm.NextPulseCommand.Execute(null);
        _stopTimer.Tick += (_, _) => { _stopTimer.Stop(); _vm.CompleteStop(); };
        _durationTimer.Tick += (_, _) =>
        {
            if (_timedSessionId is not Guid id || _vm.Session is not { Lifecycle: SessionLifecycle.Started } session || session.Id != id)
            {
                _durationTimer.Stop();
                return;
            }
            if (--_remainingSeconds <= 0) { _durationTimer.Stop(); _vm.CompleteSimulation(id); }
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.Session)) return;
            var session = _vm.Session;
            if (session is { Mode: SessionMode.Capture, Lifecycle: SessionLifecycle.Started }) _meterTimer.Start();
            else _meterTimer.Stop();
            if (session?.Lifecycle == SessionLifecycle.Started)
            {
                if (_timedSessionId != session.Id) { _timedSessionId = session.Id; _remainingSeconds = session.DurationSeconds; }
                _durationTimer.Start();
            }
            else { _durationTimer.Stop(); _timedSessionId = null; }
            if (session?.Lifecycle == SessionLifecycle.Stopping) _stopTimer.Start();
            else _stopTimer.Stop();
        };
        Closed += (_, _) => { _meterTimer.Stop(); _stopTimer.Stop(); _durationTimer.Stop(); };
        Closing += Window_Closing;
        UpdateScreenVisibility();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_ownsWorkerLifecycle) { _meterTimer.Stop(); _stopTimer.Stop(); _durationTimer.Stop(); _closeAfterWorker = true; return; }
        if (_closeAfterWorker) return;
        if (_vm.WorkerState.State is not (WorkerConnectionState.ConnectedFake or WorkerConnectionState.Stopping or WorkerConnectionState.Starting or WorkerConnectionState.Connecting or WorkerConnectionState.Handshaking) && !_vm.AutomaticScanBusy)
        {
            _closeAfterWorker = true;
            await _worker.DisposeAsync();
            return;
        }
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        _meterTimer.Stop(); _stopTimer.Stop(); _durationTimer.Stop();
        _vm.CancelAutomaticScanCommand.Execute(null);
        _vm.CancelDeviceScanCommand.Execute(null);
        await _vm.ShutdownWorkerAsync();
        await Dispatcher.InvokeAsync(() => { _closeAfterWorker = true; Close(); }, DispatcherPriority.ApplicationIdle).Task;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateResponsiveLayout();
    }
    private void UpdateResponsiveLayout()
    {
        var collapsed = ActualWidth < 1280;
        InspectorColumn.Width = collapsed ? new GridLength(0) : new GridLength(292);
        var showInspector = !collapsed && (_vm.DeveloperToolsOpen || _vm.WorkflowStep == OperatorStep.Device);
        Inspector.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        UpdateWindowsEndpointDetailsVisibility();
    }
    private void UpdateWindowsEndpointDetailsVisibility() => WindowsEndpointInlineDetails.Visibility = ActualWidth < 1280 && (DataContext as MainViewModel)?.SelectedWindowsEndpoint is not null ? Visibility.Visible : Visibility.Collapsed;
    private void WorkerFakeSettingsGrid_Loaded(object sender, RoutedEventArgs e) => UpdateWorkerFakeSettingsLayout(WorkerFakeSettingsGrid.ActualWidth);
    private void WorkerFakeSettingsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkerFakeSettingsLayout(e.NewSize.Width);
    private void UpdateWorkerFakeSettingsLayout(double width)
    {
        var narrow = width < 560;
        WorkerFakeSettingsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        WorkerFakeSettingsGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        var settings = new[] { WorkerDurationSetting, WorkerPeriodSetting, WorkerMmcssSetting, WorkerScenarioSetting };
        for (var i = 0; i < settings.Length; i++)
        {
            Grid.SetRow(settings[i], narrow ? i : i / 2);
            Grid.SetColumn(settings[i], narrow ? 0 : i % 2);
            settings[i].Margin = narrow
                ? new Thickness(0, 0, 0, i == settings.Length - 1 ? 0 : 8)
                : new Thickness(i % 2 == 0 ? 0 : 8, 0, i % 2 == 0 ? 8 : 0, i < 2 ? 8 : 0);
        }
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize_Click(sender, e); else DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void SaveDeviceProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = _vm.CreateCurrentDeviceProfile();
            var path = DeviceConfigurationProfileStorage.SaveLocal(profile, _profileDirectory);
            _vm.RecordDeviceProfileSaved(path, profile);
        }
        catch (Exception ex) { _vm.SetDeviceProfileSaveMessage($"Профиль не сохранён: {ex.Message}"); }
    }
    private void UpdateScreenVisibility()
    {
        var tools = _vm.DeveloperToolsOpen;
        DevicesPanel.Visibility = _vm.Screen == ProbeScreen.Devices && _vm.WorkflowStep == OperatorStep.Device ? Visibility.Visible : Visibility.Collapsed;
        OperatorTestPanel.Visibility = tools && _vm.Screen == ProbeScreen.Devices && _vm.WorkflowStep == OperatorStep.Test ? Visibility.Visible : Visibility.Collapsed;
        AutomaticScanPanel.Visibility = !tools && _vm.WorkflowStep == OperatorStep.Test ? Visibility.Visible : Visibility.Collapsed;
        OperatorResultPanel.Visibility = _vm.Screen == ProbeScreen.Devices && _vm.WorkflowStep == OperatorStep.Result ? Visibility.Visible : Visibility.Collapsed;
        FormatsPanel.Visibility = tools && _vm.Screen == ProbeScreen.Formats ? Visibility.Visible : Visibility.Collapsed;
        CapturePanel.Visibility = tools && _vm.Screen == ProbeScreen.Capture ? Visibility.Visible : Visibility.Collapsed;
        RenderPanel.Visibility = tools && _vm.Screen == ProbeScreen.Render ? Visibility.Visible : Visibility.Collapsed;
        ReportsPanel.Visibility = tools && _vm.Screen == ProbeScreen.Reports ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = tools && _vm.Screen == ProbeScreen.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
    }
}

file sealed class WpfLaboratoryEvidenceBundleExportInteraction(Window owner) : ILaboratoryEvidenceBundleExportInteraction
{
    public Task<string?> PickDestinationAsync(string suggestedFileName)
    {
        var dialog = new SaveFileDialog { Title = "Экспортировать результаты", Filter = "ZIP archive (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, FileName = suggestedFileName, OverwritePrompt = true };
        return Task.FromResult(dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }

    public Task<bool> ConfirmAsync(string message) => Task.FromResult(MessageBox.Show(owner, message, "Экспорт результатов", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
}

public sealed class DbfsProgressConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double db && double.IsFinite(db) ? Math.Clamp(db + 60d, 0d, 60d) : 0d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility visibility && visibility != Visibility.Visible;
}

public sealed class FormatStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is FormatStatus status ? MainViewModel.StatusLabel(status) : MainViewModel.StatusLabel(FormatStatus.Unknown);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EventKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ProbeEventKind.SessionRequested => "Запрошена сессия",
        ProbeEventKind.ConsentAccepted => "Согласие принято",
        ProbeEventKind.ConsentCancelled => "Согласие отменено",
        ProbeEventKind.Preparing => "Подготовка",
        ProbeEventKind.Started => "Запуск",
        ProbeEventKind.MeterSnapshot => "Снимок измерителя",
        ProbeEventKind.Stopping => "Остановка",
        ProbeEventKind.Stopped => "Остановлено",
        ProbeEventKind.Completed => "Завершено",
        ProbeEventKind.Warning => "Предупреждение",
        ProbeEventKind.Failure => "Ошибка",
        ProbeEventKind.DeviceLoss => "Потеря устройства",
        ProbeEventKind.WorkerDisconnected => "Worker отключён",
        _ => value?.ToString() ?? "Неизвестное событие"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SessionStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SessionMode.Capture => "Вход · Capture",
        SessionMode.Render => "Выход · Render",
        SessionLifecycle.Preparing => "Подготовка",
        SessionLifecycle.Started => "ЗАПУЩЕНА · демо",
        SessionLifecycle.Stopping => "ОСТАНОВКА · демо",
        SessionLifecycle.Stopped => "ОСТАНОВЛЕНА · демо",
        SessionLifecycle.Completed => "ЗАВЕРШЕНА · демо",
        SessionLifecycle.Failed => "ОШИБКА · демо",
        SessionLifecycle.Unknown => "НЕИЗВЕСТНО",
        StreamQuality.Clean => "ЧИСТО · проверочные данные",
        StreamQuality.Warning => "ПРЕДУПРЕЖДЕНИЕ · проверочные данные",
        StreamQuality.Failed => "ОШИБКА · проверочные данные",
        StreamQuality.Unknown => "НЕИЗВЕСТНО",
        _ => value?.ToString() ?? "Неизвестно"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
