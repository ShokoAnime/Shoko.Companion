using System.IO;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using Shoko.Companion.Configuration;

namespace Shoko.Companion.Logging;

/// <summary>
/// Initialises the NLog logging infrastructure (JSONL file + console targets).
/// </summary>
public static class LogService
{
    private static bool _initialized;
    private static readonly object Lock = new();

    /// <summary>
    /// Configures NLog with a JSONL file target and a console target.
    /// Safe to call multiple times — only the first call takes effect.
    /// </summary>
    public static void InitLogger()
    {
        lock (Lock)
        {
            if (_initialized) return;
            _initialized = true;
        }

        if (!CompanionPaths.IsInitialized)
            CompanionPaths.Initialize();

        var config = new LoggingConfiguration();

        // JSONL file target — same layout as the server
        var fileTarget = new FileTarget("file")
        {
            FileName = Path.Combine(CompanionPaths.LogsPath, "${shortdate}.jsonl"),
            ArchiveAboveSize = 52_428_800,
            ArchiveFileName = Path.Combine(CompanionPaths.LogsPath, "${shortdate}.{#####}.jsonl"),
            KeepFileOpen = false,
            Layout = GetJsonLayout()
        };
        config.AddTarget(fileTarget);

        // Console target
        var consoleTarget = new ConsoleTarget("console")
        {
            Layout = "${date:format=HH\\:mm\\:ss}| ${logger:shortname=true} --- ${message}${onexception:\\: ${exception:format=tostring}}"
        };
        config.AddTarget(consoleTarget);

        // Rules
        config.LoggingRules.Add(new LoggingRule("*", LogLevel.Info, fileTarget));
        config.LoggingRules.Add(new LoggingRule("*", LogLevel.Trace, consoleTarget));

        LogManager.Configuration = config;
        LogManager.ReconfigExistingLoggers();
    }

    private static JsonLayout GetJsonLayout()
    {
        var layout = new JsonLayout();
        layout.Attributes.Add(new JsonAttribute("timestamp", "${date:format=o}"));
        layout.Attributes.Add(new JsonAttribute("level", "${level}"));
        layout.Attributes.Add(new JsonAttribute("logger", "${logger}"));
        layout.Attributes.Add(new JsonAttribute("caller", "${callsite:className=true:methodName=true}"));
        layout.Attributes.Add(new JsonAttribute("threadId", "${threadid}"));
        layout.Attributes.Add(new JsonAttribute("processId", "${processid}"));
        layout.Attributes.Add(new JsonAttribute("message", "${message}"));
        layout.Attributes.Add(new JsonAttribute("exception", "${exception:format=tostring}"));
        return layout;
    }
}
