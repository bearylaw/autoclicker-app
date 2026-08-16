using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AutoClicker.Core;
using AutoClicker.Input;
using AutoClicker.Interop;

namespace AutoClicker;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private enum CaptureSlot
    {
        None = 0,
        HoldTrigger,
        HoldAction,
        ToggleTrigger,
        ToggleAction
    }

    private const int VkP = 0x50;

    /// <summary>
    /// Variance used by "human-like" mode, as a fraction of the click rate. A uniform draw
    /// across ±15% has a standard deviation of about 8.7% of the rate, which sits in the
    /// range measured for humans tapping repeatedly - fast enough to look deliberate,
    /// loose enough that no two gaps match.
    /// </summary>
    private const double HumanVarianceFraction = 0.15;

    private readonly AppSettings _settings;
    private readonly GlobalInputHook _hook = new();
    private readonly ClickEngine _engine = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _captureTimeout;
    private readonly DispatcherTimer _flashTimer;

    // --- state shared with the hook thread (reference/bool writes are atomic) ---
    private volatile bool _cfgHoldEnabled;
    private volatile bool _cfgToggleEnabled;
    private volatile InputKey _cfgHoldTrigger = InputKey.None;
    private volatile InputKey _cfgHoldAction = InputKey.None;
    private volatile InputKey _cfgToggleTrigger = InputKey.None;
    private volatile InputKey _cfgToggleAction = InputKey.None;
    private volatile int _captureSlot;
    private volatile InputKey? _suppressUpFor;
    private volatile bool _holdDown;
    private volatile bool _toggleLatched;
    private volatile string? _activeMode;

    private double _cps = 10;
    private string _cpsText = "10";
    private string _varianceText = "0";
    private bool _syncingSlider;
    private bool _syncingVariance;
    private bool _updatingCaptureUi;
    private bool _isRunning;
    private string? _flashMessage;

    public MainWindow()
    {
        _settings = AppSettings.Load();

        InitializeComponent();
        DataContext = this;

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background,
            (_, _) => { _saveTimer!.Stop(); _settings.Save(); }, Dispatcher);
        _captureTimeout = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Normal,
            (_, _) => CancelCapture(), Dispatcher);
        _flashTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal,
            (_, _) => { _flashTimer!.Stop(); _flashMessage = null; RefreshStatus(); }, Dispatcher);
        _saveTimer.Stop();
        _captureTimeout.Stop();
        _flashTimer.Stop();

        _cfgHoldEnabled = _settings.HoldEnabled;
        _cfgToggleEnabled = _settings.ToggleEnabled;
        _cfgHoldTrigger = _settings.HoldTrigger;
        _cfgHoldAction = _settings.HoldAction;
        _cfgToggleTrigger = _settings.ToggleTrigger;
        _cfgToggleAction = _settings.ToggleAction;

        SetCps(_settings.ClicksPerSecond, updateText: true, persist: false);

        _engine.ActiveChanged += OnEngineActiveChanged;
        _hook.Input += OnHookInput;
        _hook.Start();

        RefreshBindings();
        RefreshStatus();
    }

    // ==================== Bindable surface ====================

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set { if (_isRunning == value) return; _isRunning = value; OnPropertyChanged(); }
    }

    public string StatusText => IsRunning ? "RUNNING" : "IDLE";

    public string StatusDetail
    {
        get
        {
            if (_flashMessage is not null) return _flashMessage;

            if (IsRunning)
            {
                var action = _activeMode == "TOGGLE" ? _cfgToggleAction : _cfgHoldAction;
                var variance = ClickEngine.EffectiveVariance(_cps, CurrentVariance);
                var rate = variance > 0
                    ? $"{FormatCps(_cps - variance)}–{FormatCps(_cps + variance)}"
                    : FormatCps(_cps);
                return $"Sending {action.DisplayName} at {rate} per second";
            }

            var armed = new List<string>();
            if (_cfgHoldEnabled && _cfgHoldTrigger.IsSet) armed.Add($"hold {_cfgHoldTrigger.DisplayName}");
            if (_cfgToggleEnabled && _cfgToggleTrigger.IsSet) armed.Add($"tap {_cfgToggleTrigger.DisplayName}");

            return armed.Count == 0
                ? "No hotkeys armed — enable a mode below"
                : "Waiting for " + string.Join(" or ", armed);
        }
    }

    public string ActiveModeLabel => _activeMode ?? string.Empty;

    public string CpsText
    {
        get => _cpsText;
        set
        {
            if (_cpsText == value) return;
            _cpsText = value;
            OnPropertyChanged();

            if (TryParseCps(value, out var parsed) && parsed >= 1 && parsed <= 1000)
                SetCps(parsed, updateText: false);
        }
    }

    public string VarianceText
    {
        get => _varianceText;
        set
        {
            if (_varianceText == value) return;
            _varianceText = value;
            OnPropertyChanged();

            if (TryParseCps(value, out var parsed) && parsed >= 0 && parsed <= 1000)
                SetVariance(parsed, updateText: false);
        }
    }

    /// <summary>Human-like mode derives the variance from the rate, so the manual controls go read-only.</summary>
    public bool HumanVariance
    {
        get => _settings.HumanVariance;
        set
        {
            if (_settings.HumanVariance == value) return;
            _settings.HumanVariance = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VarianceEditable));
            ApplyVariance();
            SaveSoon();
        }
    }

    public bool VarianceEditable => !_settings.HumanVariance;

    /// <summary>The variance in force right now: derived from the rate, or the manual value.</summary>
    private double CurrentVariance =>
        _settings.HumanVariance ? _cps * HumanVarianceFraction : _settings.VarianceCps;

    public string RangeText
    {
        get
        {
            var variance = ClickEngine.EffectiveVariance(_cps, CurrentVariance);
            if (variance <= 0) return $"Every click at exactly {FormatCps(_cps)} CPS — no variance";

            var range = $"Every click lands between {FormatCps(_cps - variance)} and {FormatCps(_cps + variance)} CPS";
            return _settings.HumanVariance ? range + " · human-like" : range;
        }
    }

    public bool HoldEnabled
    {
        get => _settings.HoldEnabled;
        set
        {
            if (_settings.HoldEnabled == value) return;
            _settings.HoldEnabled = value;
            _cfgHoldEnabled = value;
            if (!value) _holdDown = false;
            OnPropertyChanged();
            ApplyEngineState();
            SaveSoon();
        }
    }

    public bool ToggleEnabled
    {
        get => _settings.ToggleEnabled;
        set
        {
            if (_settings.ToggleEnabled == value) return;
            _settings.ToggleEnabled = value;
            _cfgToggleEnabled = value;
            if (!value) _toggleLatched = false;
            OnPropertyChanged();
            ApplyEngineState();
            SaveSoon();
        }
    }

    public string HoldTriggerName => _settings.HoldTrigger.DisplayName;
    public string HoldActionName => _settings.HoldAction.DisplayName;
    public string ToggleTriggerName => _settings.ToggleTrigger.DisplayName;
    public string ToggleActionName => _settings.ToggleAction.DisplayName;

    public bool HoldConflict => _settings.HoldTrigger.IsSet && _settings.HoldTrigger.Equals(_settings.HoldAction);
    public bool ToggleConflict => _settings.ToggleTrigger.IsSet && _settings.ToggleTrigger.Equals(_settings.ToggleAction);

    // ==================== Hook handling (hook thread) ====================

    private void OnHookInput(object? sender, InputHookEventArgs e)
    {
        var slot = (CaptureSlot)_captureSlot;
        if (slot != CaptureSlot.None)
        {
            // Swallow everything while listening so the captured key never leaks
            // into whatever window is underneath.
            e.Suppress = true;
            if (!e.IsDown) return;

            _captureSlot = (int)CaptureSlot.None;
            _suppressUpFor = e.Key;
            var captured = e.Key;
            Dispatcher.BeginInvoke(() => CompleteCapture(slot, captured));
            return;
        }

        var pendingUp = _suppressUpFor;
        if (pendingUp is not null && !e.IsDown && pendingUp.Equals(e.Key))
        {
            _suppressUpFor = null;
            e.Suppress = true;
            return;
        }

        if (e.IsDown && e.Key.Kind == InputKind.Keyboard && e.Key.Code == VkP && IsCtrlAltDown())
        {
            Dispatcher.BeginInvoke(PanicStop);
            return;
        }

        var changed = false;

        if (_cfgHoldEnabled && _cfgHoldTrigger.IsSet && _cfgHoldTrigger.Equals(e.Key) && _holdDown != e.IsDown)
        {
            _holdDown = e.IsDown;
            changed = true;
        }

        if (e.IsDown && _cfgToggleEnabled && _cfgToggleTrigger.IsSet && _cfgToggleTrigger.Equals(e.Key))
        {
            _toggleLatched = !_toggleLatched;
            changed = true;
        }

        if (changed) ApplyEngineState();
    }

    private static bool IsCtrlAltDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0 &&
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

    /// <summary>Recomputes what the engine should be doing. Safe to call from any thread.</summary>
    private void ApplyEngineState()
    {
        var holdOn = _holdDown && _cfgHoldEnabled && _cfgHoldAction.IsSet;
        var toggleOn = _toggleLatched && _cfgToggleEnabled && _cfgToggleAction.IsSet;

        if (holdOn)
        {
            _activeMode = "HOLD";
            _engine.Start(_cfgHoldAction);
        }
        else if (toggleOn)
        {
            _activeMode = "TOGGLE";
            _engine.Start(_cfgToggleAction);
        }
        else
        {
            _activeMode = null;
            _engine.Stop();
        }

        Dispatcher.BeginInvoke(RefreshStatus);
    }

    private void OnEngineActiveChanged(bool active) => Dispatcher.BeginInvoke(() =>
    {
        IsRunning = active;
        RefreshStatus();
    });

    private void PanicStop()
    {
        _holdDown = false;
        _toggleLatched = false;
        ApplyEngineState();

        _flashMessage = "Emergency stop — everything halted";
        _flashTimer.Stop();
        _flashTimer.Start();
        RefreshStatus();
    }

    // ==================== Key capture ====================

    // The checked state of the capture buttons - not their Click event - drives capture:
    // IsChecked can also be flipped by keyboard and by automation without a click.
    private void Capture_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingCaptureUi) return;

        var button = (ToggleButton)sender;

        _updatingCaptureUi = true;
        foreach (var other in CaptureButtons)
            if (!ReferenceEquals(other, button))
                other.IsChecked = false;
        _updatingCaptureUi = false;

        // Never leave the clicker running while the user is rebinding.
        _holdDown = false;
        _toggleLatched = false;
        ApplyEngineState();

        _captureSlot = (int)Enum.Parse<CaptureSlot>((string)button.Tag);
        _captureTimeout.Stop();
        _captureTimeout.Start();
    }

    private void Capture_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingCaptureUi) return;
        CancelCapture();
    }

    private ToggleButton[] CaptureButtons =>
        [HoldTriggerButton, HoldActionButton, ToggleTriggerButton, ToggleActionButton];

    private void CompleteCapture(CaptureSlot slot, InputKey key)
    {
        _captureTimeout.Stop();
        ClearListeningState();

        // Escape means "never mind".
        if (key.Kind == InputKind.Keyboard && key.Code == NativeMethods.VK_ESCAPE) return;

        switch (slot)
        {
            case CaptureSlot.HoldTrigger:
                _settings.HoldTrigger = key;
                _cfgHoldTrigger = key;
                break;
            case CaptureSlot.HoldAction:
                _settings.HoldAction = key;
                _cfgHoldAction = key;
                break;
            case CaptureSlot.ToggleTrigger:
                _settings.ToggleTrigger = key;
                _cfgToggleTrigger = key;
                break;
            case CaptureSlot.ToggleAction:
                _settings.ToggleAction = key;
                _cfgToggleAction = key;
                break;
        }

        RefreshBindings();
        RefreshStatus();
        SaveSoon();
    }

    private void CancelCapture()
    {
        _captureTimeout.Stop();
        _captureSlot = (int)CaptureSlot.None;
        ClearListeningState();
    }

    private void ClearListeningState()
    {
        _updatingCaptureUi = true;
        foreach (var button in CaptureButtons) button.IsChecked = false;
        _updatingCaptureUi = false;
    }

    // ==================== Speed ====================

    private void SetCps(double value, bool updateText, bool persist = true)
    {
        _cps = Math.Clamp(Math.Round(value, 1), 1, 1000);
        _engine.ClicksPerSecond = _cps;
        _settings.ClicksPerSecond = _cps;

        if (updateText)
        {
            _cpsText = FormatCps(_cps);
            OnPropertyChanged(nameof(CpsText));
        }

        _syncingSlider = true;
        SpeedSlider.Value = Math.Min(_cps, SpeedSlider.Maximum);
        _syncingSlider = false;

        // Human-like variance is a fraction of the rate, so it moves with it.
        ApplyVariance();

        RefreshStatus();
        if (persist) SaveSoon();
    }

    private void SetVariance(double value, bool updateText, bool persist = true)
    {
        _settings.VarianceCps = Math.Clamp(Math.Round(value, 1), 0, 1000);
        ApplyVariance(updateText);
        if (persist) SaveSoon();
    }

    /// <summary>Pushes the variance in force to the engine and re-syncs the controls that display it.</summary>
    private void ApplyVariance(bool updateText = true)
    {
        var variance = CurrentVariance;
        _engine.VarianceCps = variance;

        if (updateText)
        {
            _varianceText = FormatCps(variance);
            OnPropertyChanged(nameof(VarianceText));
        }

        _syncingVariance = true;
        VarianceSlider.Value = Math.Min(variance, VarianceSlider.Maximum);
        _syncingVariance = false;

        OnPropertyChanged(nameof(RangeText));
        RefreshStatus();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || !IsLoaded) return;
        SetCps(Math.Round(e.NewValue), updateText: true);
    }

    private void VarianceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingVariance || !IsLoaded || _settings.HumanVariance) return;
        SetVariance(Math.Round(e.NewValue, 1), updateText: true);
    }

    private void VarianceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        CommitVarianceText();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void VarianceBox_LostFocus(object sender, RoutedEventArgs e) => CommitVarianceText();

    private void CommitVarianceText()
    {
        if (_settings.HumanVariance) return;
        if (TryParseCps(_varianceText, out var parsed)) SetVariance(parsed, updateText: true);
        else SetVariance(_settings.VarianceCps, updateText: true);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && double.TryParse(tag, CultureInfo.InvariantCulture, out var value))
            SetCps(value, updateText: true);
    }

    private void CpsBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        CommitCpsText();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CpsBox_LostFocus(object sender, RoutedEventArgs e) => CommitCpsText();

    private void CommitCpsText()
    {
        if (TryParseCps(_cpsText, out var parsed)) SetCps(parsed, updateText: true);
        else SetCps(_cps, updateText: true);
    }

    private static bool TryParseCps(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string FormatCps(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);

    // ==================== Plumbing ====================

    private void RefreshBindings()
    {
        OnPropertyChanged(nameof(HoldTriggerName));
        OnPropertyChanged(nameof(HoldActionName));
        OnPropertyChanged(nameof(ToggleTriggerName));
        OnPropertyChanged(nameof(ToggleActionName));
        OnPropertyChanged(nameof(HoldConflict));
        OnPropertyChanged(nameof(ToggleConflict));
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ActiveModeLabel));
    }

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag started - nothing to do.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _hook.Input -= OnHookInput;
        _hook.Dispose();
        _engine.ActiveChanged -= OnEngineActiveChanged;
        _engine.Dispose();
        _saveTimer.Stop();
        _settings.Save();
        base.OnClosed(e);
    }
}
