using System;
using System.Linq;
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

    private static readonly (int Vk, string Name)[] Keys =
    [
        (VkCapital, "Bloq Mayús"),
        (VkNumLock, "Bloq Num"),
        (VkScroll, "Bloq Despl"),
    ];

    private readonly bool[] _states = new bool[Keys.Length];
    private Mutex? _singleInstance;
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
        _osd = new OsdWindow(Keys.Select(key => key.Name));

        // Baseline only: launching must not flash the OSD for keys that are already on.
        for (var i = 0; i < Keys.Length; i++)
        {
            _states[i] = IsToggled(Keys[i].Vk);
        }

        // Polling needs no global keyboard hook, so this app never sees what the user types.
        _poll = DispatcherQueue.GetForCurrentThread().CreateTimer();
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
                DiagnosticLog.Event($"key {Keys[i].Name}={(isOn ? "on" : "off")}");
                _osd?.ShowState(Keys[i].Name, isOn);
            }
        }
    }

    private static bool IsToggled(int vk) => (GetKeyState(vk) & 1) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
