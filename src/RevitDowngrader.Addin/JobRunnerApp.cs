using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitDowngrader.Addin
{
    /// <summary>
    /// Stays completely dormant in normal Revit use. It only activates when the process was started by
    /// RevitDowngrader.exe, which sets REVIT_DOWNGRADER_JOB to the path of a job file.
    /// </summary>
    public class JobRunnerApp : IExternalApplication
    {
        internal const string JobEnvVar = "REVIT_DOWNGRADER_JOB";

        private string _jobPath;
        private bool _started;

        public Result OnStartup(UIControlledApplication application)
        {
            _jobPath = Environment.GetEnvironmentVariable(JobEnvVar);
            if (string.IsNullOrWhiteSpace(_jobPath) || !File.Exists(_jobPath))
                return Result.Succeeded;

            application.DialogBoxShowing += OnDialogBoxShowing;
            application.ControlledApplication.FailuresProcessing += OnFailuresProcessing;
            application.Idling += OnIdling;
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.DialogBoxShowing -= OnDialogBoxShowing;
                application.ControlledApplication.FailuresProcessing -= OnFailuresProcessing;
                application.Idling -= OnIdling;
            }
            catch (Exception)
            {
                // Revit is closing anyway.
            }
            return Result.Succeeded;
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_started) return;
            _started = true;

            var uiApp = sender as UIApplication;
            if (uiApp == null) return;

            try
            {
                JobProcessor.Run(uiApp.Application, _jobPath);
            }
            catch (Exception ex)
            {
                JobProcessor.Log(_jobPath, "Fatal job error: " + ex);
            }
            finally
            {
                JobProcessor.MarkDone(_jobPath);
                RequestExit(uiApp);
            }
        }

        private static void RequestExit(UIApplication uiApp)
        {
            try
            {
                uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
            }
            catch (Exception)
            {
                // The orchestrator closes (or kills) Revit once it sees the .done marker.
            }
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                var taskDialog = e as TaskDialogShowingEventArgs;
                string text = taskDialog != null ? taskDialog.Message : string.Empty;
                JobProcessor.Log(_jobPath, "Auto-dismissed dialog '" + e.DialogId + "' " + text);
                e.OverrideResult(taskDialog != null ? (int)TaskDialogResult.Close : 1);
            }
            catch (Exception)
            {
                // Never let a dialog handler throw inside Revit.
            }
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            try
            {
                FailuresAccessor accessor = e.GetFailuresAccessor();
                accessor.DeleteAllWarnings();
                bool hasErrors = accessor.GetFailureMessages()
                                         .Any(m => m.GetSeverity() == FailureSeverity.Error);
                e.SetProcessingResult(hasErrors
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue);
            }
            catch (Exception)
            {
                // Swallow - Revit falls back to its default failure handling.
            }
        }
    }
}
