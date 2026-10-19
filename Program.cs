using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerToggle
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    class MainForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Undocumented message the shell itself uses for "Exit Explorer"
        // (Ctrl+Shift+right-click on the taskbar). A shell exited this way
        // is NOT auto-restarted by Windows, unlike a killed process.
        private const uint WM_EXIT_EXPLORER = 0x400 + 436;

        private const int MaxStopAttempts = 3;

        private readonly Panel indicator = new Panel();
        private readonly Label statusLabel = new Label();
        private readonly Label detailLabel = new Label();
        private readonly CheckBox explorerOffCheck = new CheckBox();
        private readonly CheckBox topMostCheck = new CheckBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private Color indicatorColor = Color.Gray;

        private bool busy = false;      // a stop is in progress
        private bool updating = false;  // suppress CheckedChanged when we set the box in code
        private int guardAttempts = 0;  // consecutive re-stops by the guard
        private DateTime? stoppedSince = null;

        public MainForm()
        {
            Text = "Explorer Toggle";
            ClientSize = new Size(300, 160);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // Colored status dot
            indicator.SetBounds(20, 17, 20, 20);
            indicator.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(indicatorColor))
                    e.Graphics.FillEllipse(brush, 0, 0, indicator.Width - 1, indicator.Height - 1);
            };

            statusLabel.SetBounds(48, 12, 242, 28);
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);

            detailLabel.SetBounds(48, 40, 242, 20);
            detailLabel.TextAlign = ContentAlignment.MiddleLeft;
            detailLabel.ForeColor = SystemColors.GrayText;

            // Main toggle: checked = Explorer terminated, unchecked = Explorer running
            explorerOffCheck.Text = "Terminate Explorer (uncheck to restart)";
            explorerOffCheck.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            explorerOffCheck.SetBounds(14, 78, 276, 28);
            explorerOffCheck.CheckedChanged += async (s, e) =>
            {
                if (updating) return;
                guardAttempts = 0;
                if (explorerOffCheck.Checked)
                    await StopExplorerAsync();
                else
                    StartExplorer();
            };

            topMostCheck.Text = "Keep on top";
            topMostCheck.SetBounds(14, 118, 276, 22);
            topMostCheck.Checked = true;
            TopMost = topMostCheck.Checked;
            topMostCheck.CheckedChanged += (s, e) => TopMost = topMostCheck.Checked;

            Controls.AddRange(new Control[]
            {
                indicator, statusLabel, detailLabel, explorerOffCheck, topMostCheck
            });

            // Start with the box matching reality
            SetToggle(!IsExplorerRunning());

            timer.Interval = 500;
            timer.Tick += (s, e) =>
            {
                RefreshStatus();
                RunGuard();
            };
            timer.Start();
            RefreshStatus();
        }

        private void SetToggle(bool value)
        {
            updating = true;
            explorerOffCheck.Checked = value;
            updating = false;
        }

        private static bool IsExplorerRunning()
        {
            var procs = Process.GetProcessesByName("explorer");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }

        private static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
            if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
            return $"{t.Minutes}m {t.Seconds}s";
        }

        // While the box is checked, keep Explorer down. If it comes back,
        // stop it again, but give up after a few tries instead of looping forever.
        private void RunGuard()
        {
            if (busy) return;

            bool running = IsExplorerRunning();

            if (running)
            {
                stoppedSince = null;
            }
            else
            {
                if (stoppedSince == null) stoppedSince = DateTime.Now;
                else if ((DateTime.Now - stoppedSince.Value).TotalSeconds > 5) guardAttempts = 0;
            }

            if (!explorerOffCheck.Checked || !running) return;

            if (guardAttempts >= MaxStopAttempts)
            {
                SetToggle(false);
                guardAttempts = 0;
                MessageBox.Show(
                    "Explorer keeps restarting and could not be held off, so the toggle was released.",
                    "Explorer Toggle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            guardAttempts++;
            _ = StopExplorerAsync();
        }

        private void UpdateStatus(bool isExplorerRunning, string statusText = null)
        {
            if (statusText != null && statusText != statusLabel.Text)
            {
                statusLabel.Text = statusText;
                if (isExplorerRunning)
                {
                    statusLabel.ForeColor = Color.DarkGreen;
                    indicatorColor = Color.LimeGreen;
                }
                else
                {
                    statusLabel.ForeColor = Color.DarkRed;
                    indicatorColor = Color.Red;
                }
                indicator.Invalidate();
            }
        }

        private void RefreshStatus()
        {
            var procs = Process.GetProcessesByName("explorer");
            bool running = procs.Length > 0;

            if (running)
            {
                string pids = string.Join(", ", procs.Select(p => p.Id));
                string label = procs.Length == 1 ? "PID" : "PIDs";
                try
                {
                    var uptime = DateTime.Now - procs.Min(p => p.StartTime);
                    detailLabel.Text = $"{label} {pids}  |  up {FormatUptime(uptime)}";
                }
                catch
                {
                    detailLabel.Text = $"{label} {pids}";
                }
                UpdateStatus(true, "Explorer is RUNNING");
            }
            else
            {
                UpdateStatus(false, "Explorer is STOPPED");
                detailLabel.Text = "No explorer.exe process found";
            }

            foreach (var p in procs) p.Dispose();

            explorerOffCheck.Enabled = !busy;
        }

        private static void RequestCleanExit()
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                PostMessage(taskbar, WM_EXIT_EXPLORER, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private async Task StopExplorerAsync()
        {
            if (busy) return;
            busy = true;
            RefreshStatus();

            try
            {
                // 1) Ask the shell to exit cleanly so Windows doesn't relaunch it.
                RequestCleanExit();
                for (int i = 0; i < 30 && IsExplorerRunning(); i++)
                    await Task.Delay(100);

                // 2) Fallback: force-kill (Windows may relaunch it; the guard handles that).
                if (IsExplorerRunning())
                {
                    foreach (var p in Process.GetProcessesByName("explorer"))
                    {
                        using (p)
                        {
                            try { p.Kill(); p.WaitForExit(3000); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not terminate Explorer:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                RefreshStatus();
            }
        }

        private void StartExplorer()
        {
            try
            {
                if (!IsExplorerRunning())
                {
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start Explorer:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshStatus();
        }
    }
}