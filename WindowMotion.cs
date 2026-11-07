using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace LockOsd;

/// <summary>
/// Slides and fades the whole window, acrylic backdrop included. XAML animations cannot do this
/// because the backdrop belongs to the window, so each frame moves the real HWND, paced by the
/// compositor clock on a thread that only exists while something is moving.
/// </summary>
internal sealed class WindowMotion
{
    // Fluent motion values from the Windows App SDK theme resources:
    // ControlFastOutSlowInKeySpline, ControlFasterAnimationDuration, ControlNormalAnimationDuration,
    // ControlFastAnimationDuration. A window only moves in whole pixels, so longer slides with this
    // curve end in a visible one-pixel creep.
    private const double EnterFadeMs = 83;
    private const double EnterSlideMs = 250;
    private const double ExitMs = 167;

    private static readonly CubicBezier Decelerate = new(0, 0, 0, 1);
    private static readonly CubicBezier Accelerate = new(1, 0, 1, 1);

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _ui;
    private readonly Action _onHidden;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();

    private PointInt32 _rest;
    private int _slidePx;
    private double _alpha;
    private double _offset;
    private double _startAlpha;
    private double _startOffset;
    private bool _targetVisible;
    private bool _isRunning;
    private int _generation;

    public WindowMotion(IntPtr hwnd, DispatcherQueue ui, Action onHidden)
    {
        _hwnd = hwnd;
        _ui = ui;
        _onHidden = onHidden;
    }

    /// <summary>True while any part of the window can be seen.</summary>
    public bool IsOnScreen
    {
        get { lock (_gate) { return _alpha > 0; } }
    }

    public void SetRest(PointInt32 rest, int slidePx)
    {
        lock (_gate)
        {
            _rest = rest;
            _slidePx = slidePx;
            if (!_isRunning && _alpha == 0)
            {
                _offset = slidePx;
            }
        }
    }

    public void ApplyHidden()
    {
        Frame frame;
        lock (_gate)
        {
            _alpha = 0;
            _offset = _slidePx;
            frame = CaptureFrame();
        }

        Apply(frame);
    }

    public void AnimateTo(bool visible)
    {
        lock (_gate)
        {
            // Start from the current values so an interrupted animation reverses without a jump.
            _startAlpha = _alpha;
            _startOffset = _offset;
            _targetVisible = visible;
            _generation++;
            _clock.Restart();

            if (!_isRunning)
            {
                _isRunning = true;
                new Thread(RunFrames) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
            }
        }
    }

    public void JumpTo(bool visible)
    {
        Frame frame;
        int generation;
        bool isRunning;
        lock (_gate)
        {
            _targetVisible = visible;
            generation = ++_generation;
            _alpha = visible ? 1 : 0;
            _offset = visible ? 0 : _slidePx;
            frame = CaptureFrame();
            isRunning = _isRunning;
        }

        Apply(frame);
        if (!visible && !isRunning)
        {
            PostHidden(generation);
        }
    }

    private void RunFrames()
    {
        DCompositionBoostCompositorClock(true);
        try
        {
            while (Step())
            {
                DCompositionWaitForCompositorClock(0, IntPtr.Zero, FrameTimeoutMs);
            }
        }
        finally
        {
            DCompositionBoostCompositorClock(false);
        }
    }

    private bool Step()
    {
        Frame frame;
        bool isDone;
        int generation;

        lock (_gate)
        {
            var elapsed = _clock.Elapsed.TotalMilliseconds;
            double alphaProgress, slideProgress;

            if (_targetVisible)
            {
                alphaProgress = Math.Min(1, elapsed / EnterFadeMs);
                slideProgress = Decelerate.Evaluate(Math.Min(1, elapsed / EnterSlideMs));
                _alpha = _startAlpha + ((1 - _startAlpha) * alphaProgress);
                _offset = _startOffset * (1 - slideProgress);
            }
            else
            {
                alphaProgress = Math.Min(1, elapsed / ExitMs);
                slideProgress = Accelerate.Evaluate(alphaProgress);
                _alpha = _startAlpha * (1 - alphaProgress);
                _offset = _startOffset + ((_slidePx - _startOffset) * slideProgress);
            }

            frame = CaptureFrame();
            generation = _generation;
            isDone = elapsed >= (_targetVisible ? EnterSlideMs : ExitMs);
        }

        // Outside the lock: SetWindowPos waits for the UI thread, and the UI thread takes this
        // lock on every key press, so holding it here could deadlock the two threads.
        Apply(frame);

        if (!isDone)
        {
            return true;
        }

        lock (_gate)
        {
            // A key press during the last frame started a new animation: keep running.
            if (generation != _generation)
            {
                return true;
            }

            _isRunning = false;
            if (!_targetVisible)
            {
                PostHidden(generation);
            }

            return false;
        }
    }

    private void PostHidden(int generation)
    {
        _ui.TryEnqueue(() =>
        {
            lock (_gate)
            {
                // A key press after this was queued must keep the window on screen.
                if (generation != _generation)
                {
                    return;
                }
            }

            _onHidden();
        });
    }

    private Frame CaptureFrame() => new(
        (byte)Math.Round(_alpha * 255),
        _rest.X,
        _rest.Y + (int)Math.Round(_offset));

    private void Apply(Frame frame)
    {
        SetLayeredWindowAttributes(_hwnd, 0, frame.Alpha, LwaAlpha);
        SetWindowPos(_hwnd, IntPtr.Zero, frame.X, frame.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private readonly record struct Frame(byte Alpha, int X, int Y);

    private readonly record struct CubicBezier(double X1, double Y1, double X2, double Y2)
    {
        public double Evaluate(double x)
        {
            if (x <= 0)
            {
                return 0;
            }

            if (x >= 1)
            {
                return 1;
            }

            double low = 0, high = 1;
            for (var i = 0; i < 24; i++)
            {
                var t = (low + high) / 2;
                if (Sample(X1, X2, t) < x)
                {
                    low = t;
                }
                else
                {
                    high = t;
                }
            }

            return Sample(Y1, Y2, (low + high) / 2);
        }

        private static double Sample(double a, double b, double t)
        {
            var inverse = 1 - t;
            return (3 * inverse * inverse * t * a) + (3 * inverse * t * t * b) + (t * t * t);
        }
    }

    private const uint FrameTimeoutMs = 16;
    private const uint LwaAlpha = 0x2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("dcomp.dll")]
    private static extern uint DCompositionWaitForCompositorClock(uint count, IntPtr handles, uint timeoutInMs);

    [DllImport("dcomp.dll")]
    private static extern int DCompositionBoostCompositorClock(bool enable);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
