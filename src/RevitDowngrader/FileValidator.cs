using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitDowngrader
{
    /// <summary>Statistics written by the add-in (StatsCollector) for one Revit file.</summary>
    public sealed class FileStats
    {
        public string FileName = string.Empty;
        public string RevitVersion = string.Empty;
        public int TotalElements;
        public int FamilyTypes;
        public readonly Dictionary<string, int> Categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> ViewTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Schedules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Sheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ProjectParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int ViewCount { get { return ViewTypes.Values.Sum(); } }

        public static FileStats Load(string path)
        {
            var stats = new FileStats();
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                string[] p = line.Split('\t');
                if (p.Length < 3) continue;

                int n;
                int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n);

                switch (p[0])
                {
                    case "META":
                        if (p[1] == "File") stats.FileName = p[2];
                        else if (p[1] == "RevitVersion") stats.RevitVersion = p[2];
                        break;
                    case "TOTAL": stats.TotalElements = n; break;
                    case "FAMTYPES": stats.FamilyTypes = n; break;
                    case "CAT": stats.Categories[p[1]] = n; break;
                    case "VIEWTYPE": stats.ViewTypes[p[1]] = n; break;
                    case "SCHEDULE": stats.Schedules.Add(p[1]); break;
                    case "SHEET": stats.Sheets.Add(p[1]); break;
                    case "PARAM": stats.Parameters.Add(p[1]); break;
                    case "PROJPARAM": stats.ProjectParameters.Add(p[1]); break;
                }
            }
            return stats;
        }
    }

    public sealed class ValidationInput
    {
        public string DisplayName { get; set; }
        public string OriginalStatsPath { get; set; }
        public string DowngradedStatsPath { get; set; }
        public string Error { get; set; }
    }

    public sealed class ValidationSummary
    {
        public string DisplayName { get; set; }
        public string Status { get; set; }          // OK | FAILED
        public string Message { get; set; }
        public int OriginalElements { get; set; }
        public int DowngradedElements { get; set; }
        public int OriginalSchedules { get; set; }
        public int SchedulesLost { get; set; }
        public int OriginalSheets { get; set; }
        public int SheetsLost { get; set; }
        public int OriginalParameters { get; set; }
        public int ParametersLost { get; set; }
        public int OriginalProjectParameters { get; set; }
        public int ProjectParametersLost { get; set; }
    }

    public static class FileValidator
    {
        private const string Header = "File,Section,Item,Original,Downgraded,Delta,Status";

        /// <summary>Compares original vs downgraded statistics and writes one CSV for the whole run.</summary>
        public static IList<ValidationSummary> WriteReport(IEnumerable<ValidationInput> inputs, string csvPath)
        {
            var summaries = new List<ValidationSummary>();
            var sb = new StringBuilder();
            sb.AppendLine(Header);

            foreach (ValidationInput input in inputs)
            {
                var summary = new ValidationSummary { DisplayName = input.DisplayName };
                summaries.Add(summary);

                if (!string.IsNullOrEmpty(input.Error))
                {
                    summary.Status = "FAILED";
                    summary.Message = input.Error;
                    Row(sb, input.DisplayName, "Status", input.Error, "", "", "", "FAILED");
                    continue;
                }

                FileStats original;
                FileStats downgraded;
                try
                {
                    original = FileStats.Load(input.OriginalStatsPath);
                    downgraded = FileStats.Load(input.DowngradedStatsPath);
                }
                catch (Exception ex)
                {
                    summary.Status = "FAILED";
                    summary.Message = "Statistics unavailable: " + ex.Message;
                    Row(sb, input.DisplayName, "Status", summary.Message, "", "", "", "FAILED");
                    continue;
                }

                Compare(input.DisplayName, original, downgraded, sb, summary);
            }

            string dir = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(true));   // BOM so Excel reads UTF-8
            return summaries;
        }

        private static void Compare(string name, FileStats o, FileStats d, StringBuilder sb, ValidationSummary s)
        {
            s.Status = "OK";
            s.OriginalElements = o.TotalElements;
            s.DowngradedElements = d.TotalElements;
            s.OriginalSchedules = o.Schedules.Count;
            s.OriginalSheets = o.Sheets.Count;
            s.OriginalParameters = o.Parameters.Count;
            s.OriginalProjectParameters = o.ProjectParameters.Count;

            // ---- summary ----
            SummaryRow(sb, name, "Elements (model + annotation)", o.TotalElements, d.TotalElements);
            SummaryRow(sb, name, "Family types", o.FamilyTypes, d.FamilyTypes);
            SummaryRow(sb, name, "Views (excl. sheets and schedules)", o.ViewCount, d.ViewCount);
            SummaryRow(sb, name, "Schedules", o.Schedules.Count, d.Schedules.Count);
            SummaryRow(sb, name, "Sheets", o.Sheets.Count, d.Sheets.Count);
            SummaryRow(sb, name, "Instance parameter names (sampled)", o.Parameters.Count, d.Parameters.Count);
            SummaryRow(sb, name, "Project parameters", o.ProjectParameters.Count, d.ProjectParameters.Count);

            // ---- element counts per category ----
            foreach (string cat in o.Categories.Keys.Union(d.Categories.Keys, StringComparer.OrdinalIgnoreCase)
                                                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                int a = Get(o.Categories, cat), b = Get(d.Categories, cat);
                Row(sb, name, "Category", cat, a, b, b - a, StatusOf(a, b));
            }

            // ---- views per type ----
            foreach (string vt in o.ViewTypes.Keys.Union(d.ViewTypes.Keys, StringComparer.OrdinalIgnoreCase)
                                                  .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                int a = Get(o.ViewTypes, vt), b = Get(d.ViewTypes, vt);
                Row(sb, name, "View type", vt, a, b, b - a, StatusOf(a, b));
            }

            // ---- items lost (only lost items are listed; counts are in the summary) ----
            s.SchedulesLost = ListLost(sb, name, "Schedule lost", o.Schedules, d.Schedules);
            s.SheetsLost = ListLost(sb, name, "Sheet lost", o.Sheets, d.Sheets);
            s.ParametersLost = ListLost(sb, name, "Parameter lost", o.Parameters, d.Parameters);
            s.ProjectParametersLost = ListLost(sb, name, "Project parameter lost", o.ProjectParameters, d.ProjectParameters);
        }

        private static int ListLost(StringBuilder sb, string file, string section,
                                    HashSet<string> original, HashSet<string> downgraded)
        {
            int lost = 0;
            foreach (string item in original.Where(x => !downgraded.Contains(x))
                                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                lost++;
                Row(sb, file, section, item, 1, 0, -1, "LOST");
            }
            return lost;
        }

        private static void SummaryRow(StringBuilder sb, string file, string item, int original, int downgraded)
        {
            Row(sb, file, "Summary", item, original, downgraded, downgraded - original, StatusOf(original, downgraded));
        }

        private static string StatusOf(int original, int downgraded)
        {
            if (original == downgraded) return "OK";
            return downgraded < original ? "LOST" : "GAINED";
        }

        private static int Get(Dictionary<string, int> map, string key)
        {
            int v;
            return map.TryGetValue(key, out v) ? v : 0;
        }

        private static void Row(StringBuilder sb, params object[] cells)
        {
            sb.AppendLine(string.Join(",", cells.Select(c => Quote(Convert.ToString(c, CultureInfo.InvariantCulture)))));
        }

        private static string Quote(string value)
        {
            if (value == null) return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
