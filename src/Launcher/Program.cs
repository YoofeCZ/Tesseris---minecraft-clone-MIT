namespace Tesseris.Launcher;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            string distribution = AppContext.BaseDirectory;
            string localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tesseris");
            string runtimeRoot = Path.Combine(localData, "runtime");
            string installation = RuntimePayloadExtractor.TryExtractEmbedded(runtimeRoot, out ExtractedRuntime runtime)
                ? runtime.Directory
                : distribution;

            Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_ROOT", installation);
            Environment.SetEnvironmentVariable("TESSERIS_MODS", Path.Combine(distribution, "mods"));
            Environment.SetEnvironmentVariable("TESSERIS_MODLOADERS", Path.Combine(distribution, "modloaders"));

            var options = new LauncherOptions(
                installation,
                Path.Combine(installation, "Tesseris.dll"),
                Path.Combine(distribution, "mods"),
                Path.Combine(localData, "coremod-cache"),
                GameVersion: "1.0.0");
            return new LauncherBootstrap().Run(options, args);
        }
        catch (Exception exception)
        {
            try { CrashReporter.Report(exception); }
            catch (Exception reportException)
            {
                Console.Error.WriteLine($"Tesseris launcher failed: {exception}");
                Console.Error.WriteLine($"Crash report could not be written: {reportException.Message}");
            }
            return 1;
        }
    }
}
