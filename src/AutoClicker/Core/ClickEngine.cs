using System.Diagnostics;
using AutoClicker.Input;
using AutoClicker.Interop;

namespace AutoClicker.Core;

/// <summary>
/// Repeats a single input at a fixed rate on a dedicated high-priority thread.
/// Timing is driven off <see cref="Stopwatch"/> ticks with a coarse sleep followed by a
/// short spin, which holds the rate steady without burning a core.
/// </summary>
public sealed class ClickEngine : IDisposable
{
    public const double MinimumCps = 0.5;
    public const double MaximumCps = 2000.0;

    private const double MaxPressMilliseconds = 12.0;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _gate = new();

    private readonly SecureJitter _jitter = new();

    private volatile bool _alive = true;
    private volatile bool _active;
    private volatile bool _timerPeriodRaised;
    private InputKey _action = InputKey.None;
    private double _cps = 10;
    private double _variance;
    private InputKey? _pressed;

    public ClickEngine()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AutoClicker.Engine",
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    /// <summary>Raised (off the UI thread) whenever the engine starts or stops clicking.</summary>
    public event Action<bool>? ActiveChanged;

    public bool IsActive => _active;

    public double ClicksPerSecond
    {
        get => Volatile.Read(ref _cps);
        set => Volatile.Write(ref _cps, Math.Clamp(value, MinimumCps, MaximumCps));
    }

    /// <summary>
    /// Half-width of the random rate window, in clicks per second. At 10 CPS with a
    /// variance of 3, every single click is scheduled at a rate drawn uniformly from
    /// 7–13 CPS. Zero means perfectly regular timing.
    /// </summary>
    public double VarianceCps
    {
        get => Volatile.Read(ref _variance);
        set => Volatile.Write(ref _variance, Math.Clamp(value, 0, MaximumCps));
    }

    /// <summary>
    /// The variance actually usable at the given rate - it can never reach far enough
    /// down to produce a zero or negative rate.
    /// </summary>
    public static double EffectiveVariance(double cps, double variance) =>
        Math.Max(0, Math.Min(variance, cps - MinimumCps));

    public void Start(InputKey action)
    {
        if (!action.IsSet)
        {
            Stop();
            return;
        }

        lock (_gate)
        {
            _action = action;
            if (_active) return;
            _active = true;
        }

        if (!_timerPeriodRaised)
        {
            NativeMethods.TimeBeginPeriod(1);
            _timerPeriodRaised = true;
        }

        _wake.Set();
        ActiveChanged?.Invoke(true);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
        }

        _wake.Set();
        ActiveChanged?.Invoke(false);
    }

    private void Loop()
    {
        while (_alive)
        {
            if (!_active)
            {
                ReleaseStuckKey();
                if (_timerPeriodRaised)
                {
                    NativeMethods.TimeEndPeriod(1);
                    _timerPeriodRaised = false;
                }

                _wake.Wait();
                _wake.Reset();
                continue;
            }

            var next = Stopwatch.GetTimestamp();

            while (_alive && _active)
            {
                InputKey action;
                lock (_gate) action = _action;
                if (!action.IsSet) break;

                var (interval, pressTicks) = NextTiming();

                InputSender.Send(action, true);
                _pressed = action;

                if (!WaitUntil(next + pressTicks))
                {
                    InputSender.Send(action, false);
                    _pressed = null;
                    break;
                }

                InputSender.Send(action, false);
                _pressed = null;

                next += interval;

                // If we ever fall behind (very high rates, busy machine) resync instead of
                // firing a burst of catch-up clicks.
                long now = Stopwatch.GetTimestamp();
                if (next < now) next = now;

                if (!WaitUntil(next)) break;
            }
        }

        ReleaseStuckKey();
        if (_timerPeriodRaised)
        {
            NativeMethods.TimeEndPeriod(1);
            _timerPeriodRaised = false;
        }
    }

    /// <summary>
    /// Picks the timing for the next click. With variance switched on, the rate is drawn
    /// uniformly from [cps - variance, cps + variance] for every individual click:
    /// across a fixed range, a uniform draw is the highest-entropy - that is, the least
    /// predictable - choice available. The press duration is jittered too, so neither the
    /// gap between clicks nor the length of a click settles into a pattern.
    /// </summary>
    private (long Interval, long Press) NextTiming()
    {
        var cps = Volatile.Read(ref _cps);
        var variance = EffectiveVariance(cps, Volatile.Read(ref _variance));

        if (variance > 0)
            cps = Math.Clamp(cps + (_jitter.NextDouble() * 2.0 - 1.0) * variance, MinimumCps, MaximumCps);

        var interval = Math.Max(1, (long)(Stopwatch.Frequency / cps));

        var press = Math.Min(
            (long)(MaxPressMilliseconds / 1000.0 * Stopwatch.Frequency),
            Math.Max(1, interval / 3));

        if (variance > 0)
            press = Math.Max(1, (long)(press * (0.45 + _jitter.NextDouble() * 0.55)));

        return (interval, press);
    }

    /// <summary>Waits until the given timestamp. Returns false if the engine was stopped meanwhile.</summary>
    private bool WaitUntil(long target)
    {
        while (true)
        {
            if (!_alive || !_active) return false;

            long remaining = target - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;

            double ms = remaining * 1000.0 / Stopwatch.Frequency;
            if (ms > 2.0) Thread.Sleep((int)(ms - 1.5));
            else if (ms > 0.3) Thread.Sleep(0);
            else Thread.SpinWait(40);
        }
    }

    /// <summary>Makes sure we never leave a button or key logically held down.</summary>
    private void ReleaseStuckKey()
    {
        var pressed = _pressed;
        if (pressed is null) return;
        InputSender.Send(pressed, false);
        _pressed = null;
    }

    public void Dispose()
    {
        _alive = false;
        _active = false;
        _wake.Set();
        _thread.Join(500);
        ReleaseStuckKey();
        _wake.Dispose();
    }
}
