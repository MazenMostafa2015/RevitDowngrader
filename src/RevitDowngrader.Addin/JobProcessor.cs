using System;
using System.IO;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RevitDowngrader.Addin
{
    /// <summary>
    /// Job file format (UTF-8, tab separated, one command per line):
    ///   EXPORT  id  rvtPath  ifcPath      statsPath    (any Revit that can open the file)
    ///   IMPORT  id  ifcPath  outRvtPath   statsPath    (Revit 2023 build only)
    /// Progress is appended to "&lt;job&gt;.result" ("RESULT" and "LOG" lines); "&lt;job&gt;.done" marks completion.
    /// </summary>
    internal static class JobProcessor
    {
        private static readonly object Gate = new object();

        public static void Run(Application app, string jobPath)
        {
            foreach (string raw in File.ReadAllLines(jobPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string[] p = raw.Split('\t');
                if (p.Length < 5) continue;

                string command = p[0];
                string id = p[1];
                try
                {
                    switch (command)
                    {
                        case "EXPORT": ExportIfc(app, p[2], p[3], p[4]); break;
                        case "IMPORT": ImportIfc(app, p[2], p[3], p[4]); break;
                        default: throw new NotSupportedException("Unknown command: " + command);
                    }
                    WriteResult(jobPath, id, true, "OK");
                }
                catch (Exception ex)
                {
                    WriteResult(jobPath, id, false, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------ EXPORT (newer Revit)

        private static void ExportIfc(Application app, string rvtPath, string ifcPath, string statsPath)
        {
            Document doc = null;
            try
            {
                var open = new OpenOptions
                {
                    // Never touch the central model: always work on a detached copy in memory.
                    DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets,
                    Audit = false
                };
                open.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));

                doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(rvtPath), open);
                if (doc == null) throw new InvalidOperationException("Revit returned no document.");
                if (doc.IsFamilyDocument)
                    throw new NotSupportedException("Family documents cannot be exported to IFC.");

                // Statistics of the ORIGINAL file (read before anything else happens).
                StatsCollector.Write(doc, statsPath);

                var ifc = new IFCExportOptions
                {
                    FileVersion = IFCVersion.IFC2x3CV2,   // most compatible with Revit 2023's IFC importer
                    ExportBaseQuantities = true,
                    WallAndColumnSplitting = false
                };
                ifc.AddOption("ExportInternalRevitPropertySets", "true");
                ifc.AddOption("ExportSchedulesAsPsets", "true");
                ifc.AddOption("ExportUserDefinedPropertySets", "false");
                ifc.AddOption("Export2DElements", "false");
                ifc.AddOption("ExportLinkedFiles", "false");

                string folder = Path.GetDirectoryName(ifcPath);
                string name = Path.GetFileNameWithoutExtension(ifcPath);
                Directory.CreateDirectory(folder);

                bool exported;
                using (var tx = new Transaction(doc, "RevitDowngrader IFC export"))
                {
                    if (tx.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Could not start a transaction.");
                    try
                    {
                        exported = doc.Export(folder, name, ifc);
                    }
                    finally
                    {
                        // The export must not modify the model: always roll back.
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    }
                }

                if (!exported || !File.Exists(ifcPath))
                    throw new InvalidOperationException("Revit did not produce an IFC file.");
            }
            finally
            {
                CloseQuietly(doc);
            }
        }

        // ------------------------------------------------------------------ IMPORT (Revit 2023)

        private static void ImportIfc(Application app, string ifcPath, string outputRvtPath, string statsPath)
        {
#if REVIT2023
            Document ifcDoc = null;
            Document saved = null;
            try
            {
                // Revit has no API to import IFC into an already open blank project;
                // OpenIFCDocument is the supported way to turn an IFC into a Revit document.
                ifcDoc = app.OpenIFCDocument(ifcPath);
                if (ifcDoc == null) throw new InvalidOperationException("Revit 2023 could not open the IFC file.");

                Directory.CreateDirectory(Path.GetDirectoryName(outputRvtPath));
                ifcDoc.SaveAs(outputRvtPath, new SaveAsOptions { OverwriteExistingFile = true });
                CloseQuietly(ifcDoc);
                ifcDoc = null;

                // Re-open the saved .rvt from disk so the statistics describe the real output file.
                saved = app.OpenDocumentFile(outputRvtPath);
                if (saved == null) throw new InvalidOperationException("Could not re-open the saved Revit 2023 file.");
                StatsCollector.Write(saved, statsPath);
            }
            finally
            {
                CloseQuietly(ifcDoc);
                CloseQuietly(saved);
            }
#else
            throw new NotSupportedException("IMPORT is only implemented in the Revit 2023 build of this add-in.");
#endif
        }

        // ------------------------------------------------------------------ result / log plumbing

        public static void WriteResult(string jobPath, string id, bool ok, string message)
        {
            Append(jobPath, "RESULT\t" + id + "\t" + (ok ? "OK" : "FAIL") + "\t" + Clean(message));
        }

        public static void Log(string jobPath, string message)
        {
            Append(jobPath, "LOG\t-\t" + Clean(message));
        }

        public static void MarkDone(string jobPath)
        {
            try { File.WriteAllText(jobPath + ".done", DateTime.UtcNow.ToString("o")); }
            catch (Exception) { }
        }

        private static void Append(string jobPath, string line)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(jobPath + ".result", line + "\n", new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // Logging must never break the job.
            }
        }

        private static string Clean(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }

        private static void CloseQuietly(Document doc)
        {
            try
            {
                if (doc != null && doc.IsValidObject) doc.Close(false);
            }
            catch (Exception)
            {
                // Already closed or Revit is shutting down.
            }
        }
    }
}
