using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Tesseris.Launcher;

internal static class CrashReporter
{
    public static string Report(Exception exception)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tesseris",
            "crashes");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        var text = new StringBuilder()
            .AppendLine("Tesseris Pre-Alpha crash report")
            .AppendLine($"UTC: {DateTime.UtcNow:O}")
            .AppendLine($"OS: {RuntimeInformation.OSDescription}")
            .AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"Command: {Environment.CommandLine}")
            .AppendLine()
            .AppendLine(exception.ToString())
            .ToString();
        File.WriteAllText(path, text);

        string message = $"Tesseris could not start or has crashed.\n\nA crash report was saved to:\n{path}";
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(exception);
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBox(IntPtr.Zero, message, "Tesseris Pre-Alpha", 0x10u);
        }
        return path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
