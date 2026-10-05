using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Cutwright
{
    // A log file, so that "it did something odd" can be answered.
    //
    // There was no logging at all. The few Console.WriteLine calls that existed went nowhere, a
    // windowed application having no console attached, and the global exception handler showed
    // only Exception.Message - no stack, nothing kept. A copy of this on someone else's machine
    // producing a wrong number left nothing to look at.
    //
    // Deliberately a plain file rather than a logging package: one small append-only text file is
    // the whole requirement, and a dependency would be more to install and keep current than the
    // problem justifies.
    //
    // Nothing here may throw. A tool that fell over because it could not write its own log would
    // be worse than one that keeps no log, so every failure is swallowed on purpose - this is the
    // one place in the codebase where that is the right thing to do.
    internal static class Log
    {
        private static readonly object Gate = new();

        // Under the user's own profile: the install folder may be read-only, and per-user is right
        // for a per-user tool anyway.
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cutwright", "logs");

        private const int KeepDays = 30;

        private static string? _file;
        private static bool _failed;

        // Shown to the user when something goes wrong, so they can say where to look.
        public static string? CurrentFile => _file;

        public static void Info(string message) => Write("INFO ", message);

        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string context, Exception exception) =>
            Write("ERROR", $"{context}{Environment.NewLine}{exception}");

        // One banner per run, so entries can be attributed to a session and a version.
        public static void StartSession()
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

            Write("INFO ", $"--- Cutwright {version} started, {Environment.UserName} on " +
                           $"{Environment.MachineName}, {Environment.OSVersion} ---");

            PruneOldLogs();
        }

        private static void Write(string level, string text)
        {
            if (_failed)
                return;

            try
            {
                lock (Gate)
                {
                    if (_file is null)
                    {
                        Directory.CreateDirectory(Folder);
                        _file = Path.Combine(Folder,
                            $"spnest-{DateTime.Now:yyyy-MM-dd}.log");
                    }

                    var line = new StringBuilder()
                        .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                        .Append(' ')
                        .Append(level)
                        .Append(' ')
                        .Append(text)
                        .AppendLine();

                    File.AppendAllText(_file, line.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Stop trying rather than attempting a write per entry for the rest of the run.
                _failed = true;
            }
        }

        // Keeps the folder from growing without bound on a machine that is never cleaned up.
        private static void PruneOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-KeepDays);

                foreach (string path in Directory.GetFiles(Folder, "spnest-*.log"))
                {
                    if (File.GetLastWriteTime(path) < cutoff)
                        File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Old logs left in place is not a problem worth surfacing.
            }
        }
    }
}
