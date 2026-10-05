using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Cutwright
{
    // One connection to SolidWorks for the length of an export, and the thread every call to it
    // runs on.
    //
    // SolidWorks is a separate process reached over COM, which wants its caller on a
    // single-threaded apartment thread - and a batch of parts takes long enough that doing it on
    // the UI thread would freeze Cutwright. So Run hands the whole batch to one dedicated STA thread.
    //
    // Nothing here may be touched on a machine without SolidWorks until IsInstalled has said yes:
    // the interop types are embedded in Cutwright, so merely loading this class is safe, but creating
    // the application object is not.
    internal sealed class SolidWorksSession : IDisposable
    {
        private readonly bool previousInputDimValOnCreate;
        private readonly string? previousMaterialFolders;
        private bool disposed;

        public ISldWorks App { get; }

        private SolidWorksSession(ISldWorks app)
        {
            App = app;

            // Smart Dimension's value prompt would sit waiting for a click on every dimension.
            previousInputDimValOnCreate = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);

            // HDPE and UHMW come from the shop material library, and SolidWorks only looks for
            // material libraries in the folders under Options > File Locations > Material Databases -
            // anywhere else, SetMaterialPropertyName2 silently keeps the template's material. So the
            // shop's folder is added for the length of the session, if it is not there already, and
            // the user's own list is put back afterwards.
            int key = (int)swUserPreferenceStringValue_e.swFileLocationsMaterialDatabases;
            string folders = app.GetUserPreferenceStringValue(key) ?? string.Empty;
            string shopFolder = Path.GetDirectoryName(SolidWorksPaths.MaterialLibrary) ?? string.Empty;

            if (shopFolder.Length > 0 && !folders.Split(';').Any(f => string.Equals(f.Trim().TrimEnd('\\'), shopFolder, StringComparison.OrdinalIgnoreCase)))
            {
                previousMaterialFolders = folders;
                app.SetUserPreferenceStringValue(key, folders.Length == 0 ? shopFolder : folders + ";" + shopFolder);
            }

            // Parts are deliberately NOT built hidden (ISldWorks.DocumentVisible false), tempting as
            // it is: SolidWorks 2024 will not open a sketch in an invisible document - InsertSketch
            // silently does nothing and every sketch entity comes back null. Each part opens in a
            // window of its own and closes again once saved.
        }

        // Whether SolidWorks is registered on this machine at all. Safe to call anywhere.
        public static bool IsInstalled => Type.GetTypeFromProgID("SldWorks.Application") is not null;

        // Starts SolidWorks, or attaches to the one already running. Call on the thread Run gives.
        public static SolidWorksSession Connect()
        {
            Type type = Type.GetTypeFromProgID("SldWorks.Application")
                ?? throw new InvalidOperationException("SolidWorks is not installed on this computer.");

            var app = (ISldWorks)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks could not be started."));

            app.Visible = true;
            return new SolidWorksSession(app);
        }

        public string Revision => App.RevisionNumber();

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            try
            {
                App.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, previousInputDimValOnCreate);

                if (previousMaterialFolders is not null)
                    App.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsMaterialDatabases, previousMaterialFolders);
            }
            catch (COMException)
            {
                // SolidWorks was closed out from under us - nothing left to restore.
            }
        }

        // Runs work on a fresh STA thread with COM's busy-retry filter in place, and completes when
        // it does. Everything that talks to SolidWorks belongs inside one of these.
        public static Task Run(Action work)
        {
            var done = new TaskCompletionSource();

            var thread = new Thread(() =>
            {
                MessageFilter.Register();
                try
                {
                    work();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
                finally
                {
                    MessageFilter.Revoke();
                }
            })
            {
                IsBackground = true,
                Name = "SolidWorks"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return done.Task;
        }

        // --- ANSI profile sizes -----------------------------------------------------------------

        // Every size in the ANSI profile library. The names live inside each .sldlfp as
        // configurations, which only SolidWorks can read - so the first export on a machine asks
        // SolidWorks (roughly a minute), and every one after reads the answer back from a cache file.
        // The cache is keyed by SolidWorks version, so an upgrade rereads it.
        public static List<SwProfileEntry>? CachedAnsiProfiles(string revision) =>
            ReadCache(CachePath(revision));

        // Whatever revision is cached, for opening the review window without SolidWorks running.
        public static List<SwProfileEntry>? AnyCachedAnsiProfiles()
        {
            try
            {
                string? latest = Directory.Exists(CacheFolder)
                    ? Directory.GetFiles(CacheFolder, "ansi-profiles-*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
                return latest is null ? null : ReadCache(latest);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public List<SwProfileEntry> ReadAnsiProfiles()
        {
            string revision = Revision;
            if (CachedAnsiProfiles(revision) is { } cached)
                return cached;

            var lines = new List<string>();
            foreach (string file in SwProfileLibrary.AnsiFilesToRead(SolidWorksPaths.AnsiProfileFolder))
            {
                if (!File.Exists(file))
                    continue;

                foreach (string config in ConfigurationNames(file))
                    lines.Add(file + "\t" + config);
            }

            try
            {
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllLines(CachePath(revision), lines);
            }
            catch (IOException ex)
            {
                Log.Warn($"Could not cache the ANSI profile list: {ex.Message}");
            }

            return Parse(lines);
        }

        private IEnumerable<string> ConfigurationNames(string file)
        {
            int errors = 0, warnings = 0;
            var doc = App.OpenDoc6(file, (int)swDocumentTypes_e.swDocPART,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                "", ref errors, ref warnings);

            if (doc is null)
            {
                Log.Warn($"SolidWorks could not open {file} (error {errors})");
                return Array.Empty<string>();
            }

            try
            {
                return ((object[]?)doc.GetConfigurationNames() ?? Array.Empty<object>()).Select(o => o.ToString()!).ToList();
            }
            finally
            {
                App.CloseDoc(doc.GetTitle());
            }
        }

        private static string CacheFolder =>
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Cutwright");

        private static string CachePath(string revision) =>
            Path.Combine(CacheFolder, $"ansi-profiles-{revision}.txt");

        private static List<SwProfileEntry>? ReadCache(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllLines(path)) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static List<SwProfileEntry> Parse(IEnumerable<string> lines) =>
            lines.Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 2)
                .GroupBy(parts => parts[0], parts => parts[1])
                .SelectMany(g => SwProfileLibrary.ParseAnsi(g.Key, g))
                .ToList();

        // --- COM busy handling ------------------------------------------------------------------

        // SolidWorks answers "busy, call back later" while it is starting up or rebuilding, which
        // COM otherwise turns straight into an exception. This is the standard OLE message filter
        // that retries instead.
        [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleMessageFilter
        {
            [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
            [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
            [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
        }

        private sealed class MessageFilter : IOleMessageFilter
        {
            private const int SERVERCALL_RETRYLATER = 2;
            private const int RetryForMilliseconds = 120_000;

            [DllImport("ole32.dll")]
            private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

            public static void Register() => _ = CoRegisterMessageFilter(new MessageFilter(), out _);

            public static void Revoke() => _ = CoRegisterMessageFilter(null, out _);

            public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

            // Retry after 100ms, for up to two minutes, then give up and let the call fail.
            public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType) =>
                dwRejectType == SERVERCALL_RETRYLATER && dwTickCount < RetryForMilliseconds ? 100 : -1;

            public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2; // PENDINGMSG_WAITDEFPROCESS
        }
    }
}
