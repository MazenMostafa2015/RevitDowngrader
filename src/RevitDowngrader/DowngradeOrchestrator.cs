using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RevitDowngrader
{
    public sealed class RunOptions
    {
        public string SourceFolder { get; set; }
        public bool IncludeSubfolders { get; set; }
        public bool KeepIfc { get; set; }
        public int NewerRevitYear { get; set; }
    }

    public sealed class ProgressInfo
    {
        public int Percent { get; set; }
        public string Status { get; set; }
    }

    public sealed class RunResult
    {
        public string OutputFolder { get; set; }
        public string ReportPath { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
    }

    internal sealed class FileJob
    {
        public string Id;
        public string SourcePath;
        public string RelativePath;
        public string IfcPath;
        public string OutputPath;
        public string OriginalStatsPath;
        public string DowngradedStatsPath;
        public bool Exported;
        public bool Imported;
        public string Error;
    }

    internal sealed class ItemResult
    {
        public bool Ok;
        public string Message;
    }

    public static class RevitLocator
    {
        public const int LegacyYear = 2023;

        public static string FindExe(int year)
        {
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };
            foreach (string root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
            {
                string exe = Path.Combine(root, "Autodesk", "Revit " + year, "Revit.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        public static IList<int> InstalledYears()
        {
            var years = new List<int>();
            for (int y = 2019; y <= DateTime.Now.Year + 2; y++)
                if (FindExe(y) != null) years.Add(y);
            return years;
        }
    }

    /// <summary>
    /// Drives the IFC round trip:
    ///   1. newer Revit  : stats of original + IFC export        (add-in command EXPORT)
    ///   2. Revit 2023   : open IFC, save as .rvt, stats of file (add-in command IMPORT)
    ///   3. this process : compare the stats and write the CSV report
    /// Each Revit session is a separate process that loads the RevitDowngrader add-in only
    /// because the REVIT_DOWNGRADER_JOB environment variable is set for it.
    /// </summary>
    public sealed class DowngradeOrchestrator
    {
        private const string JobEnvVar = "REVIT_DOWNGRADER_JOB";
        private const string AddinFileName = "RevitDowngrader.Addin.dll";
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(45);
        private static readonly Regex BackupFile =
            new Regex(@"\.\d{4}\.(rvt|rfa)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Action<string> _log;
        private readonly IProgress<ProgressInfo> _progress;

        public DowngradeOrchestrator(Action<string> log, IProgress<ProgressInfo> progress)
        {
            _log = log ?? (s => { });
            _progress = progress;
        }

        public async Task<RunResult> RunAsync(RunOptions options, CancellationToken ct)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (!Directory.Exists(options.SourceFolder))
                throw new DirectoryNotFoundException(options.SourceFolder);

            int legacy = RevitLocator.LegacyYear;
            if (RevitLocator.FindExe(options.NewerRevitYear) == null)
                throw new InvalidOperationException("Revit " + options.NewerRevitYear + " was not found.");
            if (RevitLocator.FindExe(legacy) == null)
                throw new InvalidOperationException("Revit " + legacy + " was not found.");
            RequireAddin(options.NewerRevitYear);
            RequireAddin(legacy);

            string outputFolder = Path.Combine(options.SourceFolder, "Revit2023_Downgraded");
            string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitDowngrader", "work", runId);
            string ifcDir = Path.Combine(workDir, "ifc");
            string statsDir = Path.Combine(workDir, "stats");
            Directory.CreateDirectory(ifcDir);
            Directory.CreateDirectory(statsDir);
            Directory.CreateDirectory(outputFolder);

            var result = new RunResult { OutputFolder = outputFolder };
            bool keepWorkDir = false;

            try
            {
                Report(0, "Scanning folder...");
                List<FileJob> jobs = DiscoverJobs(options, outputFolder, ifcDir, statsDir, result);
                if (jobs.Count == 0)
                {
                    _log("No Revit project files (.rvt) found.");
                    Report(100, "Nothing to do");
                    return result;
                }
                _log(string.Format("Found {0} project file(s) to process.", jobs.Count));

                // ---- Phase 1: newer Revit -> IFC (+ stats of the original) ----
                var exportLines = jobs.Select(j => string.Join("\t",
                    "EXPORT", j.Id, j.SourcePath, j.IfcPath, j.OriginalStatsPath)).ToList();
                Dictionary<string, ItemResult> exportResults = await RunRevitPhaseAsync(
                    options.NewerRevitYear, "export", "Exporting IFC", exportLines, workDir, 0, 45, ct);

                foreach (FileJob j in jobs)
                {
                    ItemResult r;
                    exportResults.TryGetValue(j.Id, out r);
                    if (r != null && r.Ok && File.Exists(j.IfcPath))
                    {
                        j.Exported = true;
                    }
                    else
                    {
                        j.Error = "IFC export failed: " + (r != null ? r.Message : "no result from Revit");
                        _log("  FAILED " + j.RelativePath + " - " + j.Error);
                    }
                }

                if (options.KeepIfc)
                {
                    string keepDir = Path.Combine(outputFolder, "_IFC");
                    Directory.CreateDirectory(keepDir);
                    foreach (FileJob j in jobs.Where(x => x.Exported))
                        File.Copy(j.IfcPath, Path.Combine(keepDir, Path.GetFileName(j.IfcPath)), true);
                }

                // ---- Phase 2: Revit 2023 imports IFC and saves .rvt (+ stats of the result) ----
                List<FileJob> toImport = jobs.Where(j => j.Exported).ToList();
                if (toImport.Count > 0)
                {
                    var importLines = toImport.Select(j => string.Join("\t",
                        "IMPORT", j.Id, j.IfcPath, j.OutputPath, j.DowngradedStatsPath)).ToList();
                    Dictionary<string, ItemResult> importResults = await RunRevitPhaseAsync(
                        legacy, "import", "Importing into Revit 2023", importLines, workDir, 45, 45, ct);

                    foreach (FileJob j in toImport)
                    {
                        ItemResult r;
                        importResults.TryGetValue(j.Id, out r);
                        if (r != null && r.Ok && File.Exists(j.OutputPath))
                        {
                            j.Imported = true;
                        }
                        else
                        {
                            j.Error = "Revit 2023 import failed: " + (r != null ? r.Message : "no result from Revit");
                            _log("  FAILED " + j.RelativePath + " - " + j.Error);
                        }
                    }
                }

                // ---- Phase 3: validation report ----
                Report(92, "Building validation report...");
                var inputs = jobs.Select(j => new ValidationInput
                {
                    DisplayName = j.RelativePath,
                    OriginalStatsPath = j.OriginalStatsPath,
                    DowngradedStatsPath = j.DowngradedStatsPath,
                    Error = j.Imported ? null : (j.Error ?? "Unknown failure")
                }).ToList();

                result.ReportPath = Path.Combine(outputFolder, "RevitDowngrade_Report_" + runId + ".csv");
                IList<ValidationSummary> summaries = FileValidator.WriteReport(inputs, result.ReportPath);
                LogSummaries(summaries);

                result.Succeeded = jobs.Count(j => j.Imported);
                result.Failed = jobs.Count - result.Succeeded;
                keepWorkDir = result.Failed > 0;

                _log("Report written: " + result.ReportPath);
                Report(100, "Done");
                return result;
            }
            finally
            {
                if (keepWorkDir)
                    _log("Work folder kept for diagnosis: " + workDir);
                else
                    DeleteDirectoryQuietly(workDir);
            }
        }

        // ------------------------------------------------------------------ discovery

        private List<FileJob> DiscoverJobs(RunOptions options, string outputFolder, string ifcDir,
                                           string statsDir, RunResult result)
        {
            var jobs = new List<FileJob>();
            SearchOption searchOption = options.IncludeSubfolders
                ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string outPrefix = outputFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string rootPrefix = options.SourceFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            int index = 0;
            foreach (string file in Directory.EnumerateFiles(options.SourceFolder, "*.*", searchOption)
                                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".rvt" && ext != ".rfa") continue;
                if (file.StartsWith(outPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (BackupFile.IsMatch(Path.GetFileName(file))) continue;

                string relative = file.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(rootPrefix.Length) : Path.GetFileName(file);

                if (ext == ".rfa")
                {
                    result.Skipped++;
                    _log("SKIPPED (family): " + relative +
                         " - IFC export only supports project files, so families need a different workflow.");
                    continue;
                }

                index++;
                string id = index.ToString("D4");
                string baseName = Path.GetFileNameWithoutExtension(file);
                string relativeDir = Path.GetDirectoryName(relative) ?? string.Empty;
                string outDir = Path.Combine(outputFolder, relativeDir);
                Directory.CreateDirectory(outDir);

                jobs.Add(new FileJob
                {
                    Id = id,
                    SourcePath = file,
                    RelativePath = relative,
                    IfcPath = Path.Combine(ifcDir, id + "_" + SafeName(baseName) + ".ifc"),
                    OutputPath = Path.Combine(outDir, baseName + ".rvt"),
                    OriginalStatsPath = Path.Combine(statsDir, id + "_original.tsv"),
                    DowngradedStatsPath = Path.Combine(statsDir, id + "_downgraded.tsv")
                });
            }
            return jobs;
        }

        // ------------------------------------------------------------------ Revit process handling

        private async Task<Dictionary<string, ItemResult>> RunRevitPhaseAsync(
            int year, string label, string statusPrefix, List<string> lines, string workDir,
            int percentStart, int percentSpan, CancellationToken ct)
        {
            var results = new Dictionary<string, ItemResult>(StringComparer.Ordinal);
            string exe = RevitLocator.FindExe(year);
            string jobFile = Path.Combine(workDir, label + "_job.txt");
            string resultFile = jobFile + ".result";
            string doneFile = jobFile + ".done";
            int total = lines.Count;

            File.WriteAllLines(jobFile, lines, new UTF8Encoding(false));
            DeleteFileQuietly(resultFile);
            DeleteFileQuietly(doneFile);

            string manifestPath = null;
            Process proc = null;
            try
            {
                manifestPath = InstallManifest(year, AddinPath(year));
                _log(string.Format(
                    "Launching Revit {0} ({1}, {2} file(s)). If Revit shows an unsigned add-in dialog, choose \"Always Load\".",
                    year, label, total));

                var psi = new ProcessStartInfo(exe, "/nosplash")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                psi.EnvironmentVariables[JobEnvVar] = jobFile;

                proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("Could not start " + exe);

                int linesSeen = 0;
                bool started = false;
                DateTime lastActivity = DateTime.UtcNow;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    int before = results.Count;
                    int newLines = ReadResultLines(resultFile, ref linesSeen, results);
                    if (newLines > 0)
                    {
                        started = true;
                        lastActivity = DateTime.UtcNow;
                    }
                    if (results.Count != before)
                        ReportPhase(statusPrefix, results.Count, total, percentStart, percentSpan);

                    if (File.Exists(doneFile))
                    {
                        ReadResultLines(resultFile, ref linesSeen, results);
                        break;
                    }
                    if (proc.HasExited)
                    {
                        ReadResultLines(resultFile, ref linesSeen, results);
                        _log("Revit exited before the job finished.");
                        break;
                    }
                    TimeSpan limit = started ? StallTimeout : StartupTimeout;
                    if (DateTime.UtcNow - lastActivity > limit)
                    {
                        _log("Timed out waiting for Revit - stopping it.");
                        break;
                    }
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                await ShutdownAsync(proc).ConfigureAwait(false);
                RemoveManifest(manifestPath);
            }

            foreach (string line in lines)
            {
                string id = line.Split('\t')[1];
                if (!results.ContainsKey(id))
                    results[id] = new ItemResult
                    {
                        Ok = false,
                        Message = "No result from Revit (crash, timeout, or stopped before this file)."
                    };
            }
            ReportPhase(statusPrefix, total, total, percentStart, percentSpan);
            return results;
        }

        private int ReadResultLines(string path, ref int linesSeen, Dictionary<string, ItemResult> results)
        {
            if (!File.Exists(path)) return 0;

            string text;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }
            }
            catch (IOException) { return 0; }

            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return 0;                       // only a partial line so far
            string[] all = text.Substring(0, lastNewline).Split('\n');

            int processed = 0;
            for (int i = linesSeen; i < all.Length; i++)
            {
                string[] p = all[i].TrimEnd('\r').Split('\t');
                processed++;
                if (p.Length < 3) continue;

                if (p[0] == "RESULT")
                {
                    results[p[1]] = new ItemResult { Ok = p[2] == "OK", Message = p.Length > 3 ? p[3] : string.Empty };
                }
                else if (p[0] == "LOG")
                {
                    _log("  [Revit] " + p[2]);
                }
            }
            linesSeen = all.Length;
            return processed;
        }

        private static async Task ShutdownAsync(Process proc)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    proc.CloseMainWindow();
                    bool exited = await Task.Run(() => proc.WaitForExit(30000)).ConfigureAwait(false);
                    if (!exited)
                    {
                        proc.Kill();
                        await Task.Run(() => proc.WaitForExit(10000)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception) { /* process already gone */ }
            finally { proc.Dispose(); }
        }

        // ------------------------------------------------------------------ add-in deployment

        private static string AddinPath(int year)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addin", year.ToString(), AddinFileName);
        }

        private static void RequireAddin(int year)
        {
            if (!File.Exists(AddinPath(year)))
                throw new FileNotFoundException(
                    "The Revit " + year + " add-in is missing. Re-run Build-Installer.ps1 on a machine where Revit " +
                    year + " is installed.", AddinPath(year));
        }

        /// <summary>Writes a per-user .addin manifest (no admin rights needed); removed again after the phase.</summary>
        private static string InstallManifest(int year, string addinDll)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", year.ToString());
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "RevitDowngrader.JobRunner.addin");

            string xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<RevitAddIns>\r\n" +
                "  <AddIn Type=\"Application\">\r\n" +
                "    <Name>RevitDowngrader Job Runner</Name>\r\n" +
                "    <Assembly>" + SecurityElement.Escape(addinDll) + "</Assembly>\r\n" +
                "    <AddInId>7C1E2D6A-4B5F-4E0A-9F3C-52A1D0B6E8A4</AddInId>\r\n" +
                "    <FullClassName>RevitDowngrader.Addin.JobRunnerApp</FullClassName>\r\n" +
                "    <VendorId>RDGR</VendorId>\r\n" +
                "    <VendorDescription>RevitDowngrader</VendorDescription>\r\n" +
                "  </AddIn>\r\n" +
                "</RevitAddIns>\r\n";
            File.WriteAllText(path, xml, new UTF8Encoding(false));
            return path;
        }

        private static void RemoveManifest(string path)
        {
            if (!string.IsNullOrEmpty(path)) DeleteFileQuietly(path);
        }

        // ------------------------------------------------------------------ helpers

        private void LogSummaries(IList<ValidationSummary> summaries)
        {
            _log("---- Validation summary ----");
            foreach (ValidationSummary s in summaries)
            {
                if (s.Status == "FAILED")
                {
                    _log("  FAILED  " + s.DisplayName + " - " + s.Message);
                    continue;
                }
                _log(string.Format(
                    "  OK      {0}: elements {1} -> {2} | schedules lost {3}/{4} | sheets lost {5}/{6} | parameters lost {7}/{8}",
                    s.DisplayName, s.OriginalElements, s.DowngradedElements,
                    s.SchedulesLost, s.OriginalSchedules,
                    s.SheetsLost, s.OriginalSheets,
                    s.ParametersLost, s.OriginalParameters));
            }
        }

        private void ReportPhase(string prefix, int done, int total, int start, int span)
        {
            int percent = start + (int)(span * (double)done / Math.Max(1, total));
            Report(percent, string.Format("{0}: {1}/{2}", prefix, done, total));
        }

        private void Report(int percent, string status)
        {
            if (_progress == null) return;
            _progress.Report(new ProgressInfo
            {
                Percent = Math.Max(0, Math.Min(100, percent)),
                Status = status
            });
        }

        private static string SafeName(string name)
        {
            return Regex.Replace(name, @"[^\w\-]+", "_");
        }

        private static void DeleteFileQuietly(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        private static void DeleteDirectoryQuietly(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception) { }
        }
    }
}
