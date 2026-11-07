using System;
using System.IO;
using System.Reflection;
using Microsoft.UI.Dispatching;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LockOsd;

/// <summary>
/// Owns config.yaml and the locales folder under %APPDATA%\LockOsd, and reloads them when they
/// change. A file with errors is logged and ignored, so a typo never takes the OSD down.
/// </summary>
internal sealed class ConfigStore
{
    private const string ConfigFileName = "config.yaml";
    private const int MaxReadAttempts = 5;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LockOsd");

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _debounce;
    private FileSystemWatcher? _watcher;
    private int _readAttempts;

    public ConfigStore(DispatcherQueue queue)
    {
        _queue = queue;
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(250);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Reload();

        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(LocalesDirectory);
        WriteDefaultConfigIfMissing();

        Config = new AppConfig();
        Strings = Localizer.Load(Config.Language, LocalesDirectory);
        TryLoad();
        StartWatching();
    }

    public event Action? Changed;

    public AppConfig Config { get; private set; }

    public LocaleStrings Strings { get; private set; }

    private string ConfigPath => Path.Combine(_directory, ConfigFileName);

    private string LocalesDirectory => Path.Combine(_directory, "locales");

    private void WriteDefaultConfigIfMissing()
    {
        if (File.Exists(ConfigPath))
        {
            return;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("config.default.yaml")!;
        using var output = File.Create(ConfigPath);
        stream.CopyTo(output);
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(_directory, "*.yaml")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        // Editors save in bursts, and some replace the file, so several event types mean one save.
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Deleted += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
        _watcher.EnableRaisingEvents = true;
    }

    private void ScheduleReload() => _queue.TryEnqueue(() =>
    {
        _readAttempts = 0;
        _debounce.Stop();
        _debounce.Start();
    });

    private void Reload()
    {
        if (TryLoad())
        {
            DiagnosticLog.Event("config reloaded");
            Changed?.Invoke();
        }
    }

    private bool TryLoad()
    {
        try
        {
            // A deleted config.yaml means defaults, the same as a missing value.
            var config = File.Exists(ConfigPath)
                ? Deserializer.Deserialize<AppConfig>(ReadShared(ConfigPath)) ?? new AppConfig()
                : new AppConfig();

            config.Normalized();
            Strings = Localizer.Load(config.Language, LocalesDirectory);
            Config = config;
            return true;
        }
        catch (IOException) when (++_readAttempts < MaxReadAttempts)
        {
            // The editor still has the file open.
            _debounce.Start();
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException)
        {
            DiagnosticLog.Event($"config ignored, keeping previous settings: {exception.Message}");
            return false;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
