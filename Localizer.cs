using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LockOsd;

internal enum LockKey
{
    CapsLock,
    NumLock,
    ScrollLock,
}

/// <summary>The schema of a locale file. A null value falls back to English.</summary>
internal sealed class LocaleFile
{
    public string? CapsLock { get; set; }

    public string? NumLock { get; set; }

    public string? ScrollLock { get; set; }

    public string? Enabled { get; set; }

    public string? Disabled { get; set; }
}

internal sealed class LocaleStrings
{
    private const string KeyPlaceholder = "{key}";

    private readonly Dictionary<LockKey, string> _keyNames;
    private readonly string _enabled;
    private readonly string _disabled;

    public LocaleStrings(LocaleFile file, LocaleFile fallback)
    {
        _keyNames = new Dictionary<LockKey, string>
        {
            [LockKey.CapsLock] = file.CapsLock ?? fallback.CapsLock!,
            [LockKey.NumLock] = file.NumLock ?? fallback.NumLock!,
            [LockKey.ScrollLock] = file.ScrollLock ?? fallback.ScrollLock!,
        };
        _enabled = file.Enabled ?? fallback.Enabled!;
        _disabled = file.Disabled ?? fallback.Disabled!;
    }

    public string Label(LockKey key, bool isOn) =>
        (isOn ? _enabled : _disabled).Replace(KeyPlaceholder, _keyNames[key]);

    /// <summary>Every label this language can show, used to size the OSD.</summary>
    public IReadOnlyList<string> AllLabels() =>
        Enum.GetValues<LockKey>()
            .SelectMany(key => new[] { Label(key, isOn: true), Label(key, isOn: false) })
            .ToArray();
}

/// <summary>
/// Resolves a language from the files in the user's locales folder first, then the languages
/// built into the app. "es-MX" tries es-MX and then es. English is the final fallback.
/// </summary>
internal static class Localizer
{
    private const string FallbackLanguage = "en";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static LocaleStrings Load(string language, string userLocalesDirectory)
    {
        var fallback = ReadEmbedded(FallbackLanguage)!;
        var requested = language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.CurrentUICulture.Name
            : language;

        foreach (var candidate in Candidates(requested))
        {
            var file = ReadUserFile(userLocalesDirectory, candidate) ?? ReadEmbedded(candidate);
            if (file is not null)
            {
                DiagnosticLog.Event($"language {candidate} (requested {language})");
                return new LocaleStrings(file, fallback);
            }
        }

        DiagnosticLog.Event($"language {FallbackLanguage} (no locale for {language})");
        return new LocaleStrings(fallback, fallback);
    }

    private static IEnumerable<string> Candidates(string language)
    {
        yield return language;

        var separator = language.IndexOf('-');
        if (separator > 0)
        {
            yield return language[..separator];
        }
    }

    private static LocaleFile? ReadUserFile(string directory, string language)
    {
        // A language code is a file name here, so it must not be able to leave the folder.
        if (language.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var path = Path.Combine(directory, $"{language}.yaml");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Deserializer.Deserialize<LocaleFile>(File.ReadAllText(path)) ?? new LocaleFile();
        }
        catch (Exception exception) when (exception is YamlException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Event($"locale {language}.yaml ignored: {exception.Message}");
            return null;
        }
    }

    private static LocaleFile? ReadEmbedded(string language)
    {
        var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(resource => resource.Equals($"locales.{language}.yaml", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Deserializer.Deserialize<LocaleFile>(reader.ReadToEnd());
    }
}
