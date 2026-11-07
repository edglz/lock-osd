using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LockOsd;

public partial class App : Application
{
    private const int VkCapital = 0x14;
    private const int VkNumLock = 0x90;
    private const int VkScroll = 0x91;

    private static readonly (int Vk, LockKey Key)[] Keys =
    [
        (VkCapital, LockKey.CapsLock),
        (VkNumLock, LockKey.NumLock),
        (VkScroll, LockKey.ScrollLock),
    ];

    private readonly bool[] _states = new bool[Keys.Length];
    private Mutex? _singleInstance;
    private ConfigStore? _config;
    private OsdWindow? _osd;
    private DispatcherQueueTimer? _poll;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => DiagnosticLog.Crash(e.Exception);

        // The XAML event above only covers the UI thread.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagnosticLog.Crash(e.ExceptionObject);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DiagnosticLog.Event($"exit pid={Environment.ProcessId}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, @"Local\LockOsd.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Exit();
            return;
        }

        DiagnosticLog.Event($"start pid={Environment.ProcessId} version={typeof(App).Assembly.GetName().Version}");

        var queue = DispatcherQueue.GetForCurrentThread();
        _config = new ConfigStore(queue);
        _osd = new OsdWindow(_config.Strings.AllLabels(), _config.Config.Display);
        _config.Changed += () => _osd.ApplySettings(_config.Strings.AllLabels(), _config.Config.Display);

        // Baseline only: launching must not flash the OSD for keys that are already on.
        for (var i = 0; i < Keys.Length; i++)
        {
            _states[i] = IsToggled(Keys[i].Vk);
        }

        // Polling needs no global keyboard hook, so this app never sees what the user types.
        _poll = queue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(60);
        _poll.IsRepeating = true;
        _poll.Tick += (_, _) => CheckKeys();
        _poll.Start();
    }

    private void CheckKeys()
    {
        for (var i = 0; i < Keys.Length; i++)
        {
            var isOn = IsToggled(Keys[i].Vk);
            if (isOn != _states[i])
            {
                _states[i] = isOn;
                DiagnosticLog.Event($"key {Keys[i].Key}={(isOn ? "on" : "off")}");
                if (IsEnabled(Keys[i].Key))
                {
                    _osd?.ShowState(_config!.Strings.Label(Keys[i].Key, isOn), isOn);
                }
            }
        }
    }

    private bool IsEnabled(LockKey key)
    {
        var keys = _config!.Config.Keys;
        return key switch
        {
            LockKey.CapsLock => keys.CapsLock,
            LockKey.NumLock => keys.NumLock,
            _ => keys.ScrollLock,
        };
    }

    private static bool IsToggled(int vk) => (GetKeyState(vk) & 1) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
