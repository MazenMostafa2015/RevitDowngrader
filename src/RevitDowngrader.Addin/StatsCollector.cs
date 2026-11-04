using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitDowngrader.Addin
{
    /// <summary>
    /// Writes a small tab-separated statistics file (kind, name, value) that the WPF app's
    /// FileValidator reads and compares. No JSON on purpose: it keeps the add-in free of
    /// dependencies that could conflict with Revit's own assemblies.
    /// </summary>
    internal static class StatsCollector
    {
        // Reading every parameter of every element is slow on big models; sample per category.
        private const int ParameterSamplePerCategory = 5000;

        public static void Write(Document doc, string path)
        {
            var sb = new StringBuilder();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Add(sb, "META", "File", string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName));
            Add(sb, "META", "RevitVersion", doc.Application.VersionNumber);

            // ---- elements per category + parameter names ----
            var categories = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sampled = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            using (FilteredElementCollector collector = new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                foreach (Element element in collector)
                {
                    try
                    {
                        if (element is View) continue;
                        Category category = element.Category;
                        if (category == null) continue;
                        if (category.CategoryType != CategoryType.Model &&
                            category.CategoryType != CategoryType.Annotation) continue;

                        string key = category.CategoryType + ": " + category.Name;
                        total++;

                        int count;
                        categories.TryGetValue(key, out count);
                        categories[key] = count + 1;

                        int seen;
                        sampled.TryGetValue(key, out seen);
                        if (seen < ParameterSamplePerCategory)
                        {
                            sampled[key] = seen + 1;
                            foreach (Parameter parameter in element.Parameters)
                            {
                                Definition definition = parameter.Definition;
                                if (definition != null && !string.IsNullOrEmpty(definition.Name))
                                    parameterNames.Add(definition.Name);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Some elements refuse to answer; skip them.
                    }
                }
            }

            Add(sb, "TOTAL", "Elements", total.ToString());
            foreach (KeyValuePair<string, int> pair in categories)
                Add(sb, "CAT", pair.Key, pair.Value.ToString());
            foreach (string name in parameterNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                Add(sb, "PARAM", name, "1");

            // ---- family types ----
            int familyTypes;
            using (FilteredElementCollector symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
            {
                familyTypes = symbols.GetElementCount();
            }
            Add(sb, "FAMTYPES", "Count", familyTypes.ToString());

            // ---- views, sheets, schedules ----
            var viewTypes = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var schedules = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            using (FilteredElementCollector views = new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                foreach (Element element in views)
                {
                    try
                    {
                        var view = element as View;
                        if (view == null || view.IsTemplate) continue;

                        var sheet = view as ViewSheet;
                        if (sheet != null)
                        {
                            sheets.Add(sheet.SheetNumber + " - " + sheet.Name);
                            continue;
                        }

                        var schedule = view as ViewSchedule;
                        if (schedule != null)
                        {
                            if (schedule.IsInternalKeynoteSchedule || schedule.IsTitleblockRevisionSchedule) continue;
                            schedules.Add(schedule.Name);
                            continue;
                        }

                        ViewType type = view.ViewType;
                        if (type == ViewType.ProjectBrowser || type == ViewType.SystemBrowser ||
                            type == ViewType.Internal || type == ViewType.Undefined) continue;

                        string typeName = type.ToString();
                        int count;
                        viewTypes.TryGetValue(typeName, out count);
                        viewTypes[typeName] = count + 1;
                    }
                    catch (Exception)
                    {
                        // Skip views that cannot be inspected.
                    }
                }
            }

            foreach (KeyValuePair<string, int> pair in viewTypes)
                Add(sb, "VIEWTYPE", pair.Key, pair.Value.ToString());
            foreach (string name in schedules)
                Add(sb, "SCHEDULE", name, "1");
            foreach (string name in sheets)
                Add(sb, "SHEET", name, "1");

            // ---- project / shared parameter bindings ----
            var projectParameters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
            while (iterator.MoveNext())
            {
                Definition definition = iterator.Key;
                if (definition != null && !string.IsNullOrEmpty(definition.Name))
                    projectParameters.Add(definition.Name);
            }
            foreach (string name in projectParameters)
                Add(sb, "PROJPARAM", name, "1");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void Add(StringBuilder sb, string kind, string name, string value)
        {
            sb.Append(kind).Append('\t').Append(Clean(name)).Append('\t').Append(value).Append('\n');
        }

        private static string Clean(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }
    }
}
