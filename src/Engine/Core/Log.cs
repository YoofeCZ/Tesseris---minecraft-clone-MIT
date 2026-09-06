using System.Diagnostics;
using System.Globalization;

namespace Tesseris.Engine.Core;

/// <summary>Úroveň závažnosti zprávy.</summary>
public enum LogLevel
{
    Trace,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Minimalistický logger do konzole.
/// Volá se z GL debug callbacku a později z worker vláken job systému, proto je zápis
/// pod zámkem — bez něj se řádky z různých vláken prokládají uprostřed slova.
/// </summary>
public static class Log
{
    // Pozor: System.Threading.Lock je až .NET 9, tenhle projekt cílí net8.0.
    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    /// <summary>Zprávy pod touto úrovní se zahodí. Ve výchozím stavu se Trace nevypisuje.</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static void Trace(string message) => Write(LogLevel.Trace, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel)
        {
            return;
        }

        // Čas od startu, ne wall clock: pro korelaci s frame timy je užitečnější.
        string stamp = Uptime.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        string tag = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Info => "INFO ",
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "?????",
        };

        lock (Gate)
        {
            TextWriter sink = level >= LogLevel.Warn ? Console.Error : Console.Out;
            sink.WriteLine($"[{stamp,9}] {tag} {message}");
        }
    }
}
