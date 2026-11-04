using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace RevitDowngrader
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cts;
        private string _reportPath;
        private string _outputFolder;
        private bool _busy;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var years = RevitLocator.InstalledYears()
                                    .Where(y => y > RevitLocator.LegacyYear)
                                    .OrderByDescending(y => y)
                                    .ToList();
            NewerVersionCombo.ItemsSource = years;
            if (years.Count > 0) NewerVersionCombo.SelectedIndex = 0;

            bool legacyOk = RevitLocator.FindExe(RevitLocator.LegacyYear) != null;
            LegacyStatusText.Text = legacyOk ? "Revit 2023: found" : "Revit 2023: NOT FOUND";
            LegacyStatusText.Foreground = legacyOk ? Brushes.ForestGreen : Brushes.Firebrick;

            AppendLog(years.Count == 0
                ? "No Revit 2024 or newer installation was found."
                : "Detected newer Revit versions: " + string.Join(", ", years));
            UpdateStartEnabled();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select the folder that contains .rvt / .rfa files (Revit 2024 or newer)";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(FolderBox.Text)) dialog.SelectedPath = FolderBox.Text;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    FolderBox.Text = dialog.SelectedPath;
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = (FolderBox.Text ?? string.Empty).Trim();
            if (!Directory.Exists(folder))
            {
                MessageBox.Show(this, "Please select an existing folder.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!(NewerVersionCombo.SelectedItem is int))
            {
                MessageBox.Show(this, "No Revit 2024+ installation is selected.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var options = new RunOptions
            {
                SourceFolder = folder,
                IncludeSubfolders = SubfoldersCheck.IsChecked == true,
                KeepIfc = KeepIfcCheck.IsChecked == true,
                NewerRevitYear = (int)NewerVersionCombo.SelectedItem
            };

            LogBox.Clear();
            RunProgress.Value = 0;
            StatusText.Text = "Starting...";
            _reportPath = null;
            _outputFolder = null;
            OpenReportButton.IsEnabled = false;
            OpenOutputButton.IsEnabled = false;

            _cts = new CancellationTokenSource();
            SetBusy(true);

            var progress = new Progress<ProgressInfo>(p =>
            {
                RunProgress.Value = p.Percent;
                StatusText.Text = p.Status;
            });
            var orchestrator = new DowngradeOrchestrator(AppendLog, progress);

            try
            {
                RunResult result = await Task.Run(() => orchestrator.RunAsync(options, _cts.Token));
                _reportPath = result.ReportPath;
                _outputFolder = result.OutputFolder;
                RunProgress.Value = 100;
                StatusText.Text = string.Format("Finished: {0} converted, {1} failed, {2} skipped",
                    result.Succeeded, result.Failed, result.Skipped);
                AppendLog(StatusText.Text);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Cancelled";
                AppendLog("Run cancelled.");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed";
                AppendLog("FATAL: " + ex);
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                SetBusy(false);
                OpenReportButton.IsEnabled = _reportPath != null && File.Exists(_reportPath);
                OpenOutputButton.IsEnabled = _outputFolder != null && Directory.Exists(_outputFolder);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                AppendLog("Cancelling - stopping Revit...");
                _cts.Cancel();
            }
        }

        private void OpenReportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_reportPath != null)
                    Process.Start(new ProcessStartInfo(_reportPath) { UseShellExecute = true });
            }
            catch (Exception ex) { AppendLog("Could not open report: " + ex.Message); }
        }

        private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_outputFolder != null)
                    Process.Start("explorer.exe", "\"" + _outputFolder + "\"");
            }
            catch (Exception ex) { AppendLog("Could not open folder: " + ex.Message); }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_busy)
            {
                var answer = MessageBox.Show(this,
                    "A run is in progress. Stop it and close?", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                if (_cts != null) _cts.Cancel();
            }
            base.OnClosing(e);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BrowseButton.IsEnabled = !busy;
            FolderBox.IsEnabled = !busy;
            NewerVersionCombo.IsEnabled = !busy;
            SubfoldersCheck.IsEnabled = !busy;
            KeepIfcCheck.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            UpdateStartEnabled();
        }

        private void UpdateStartEnabled()
        {
            StartButton.IsEnabled = !_busy
                                    && NewerVersionCombo.SelectedItem is int
                                    && RevitLocator.FindExe(RevitLocator.LegacyYear) != null;
        }

        private void AppendLog(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
                return;
            }
            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
    }
}
