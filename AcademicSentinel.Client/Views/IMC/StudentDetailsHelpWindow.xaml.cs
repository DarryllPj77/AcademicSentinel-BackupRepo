using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace AcademicSentinel.Client.Views.IMC
{
    /// <summary>
    /// Replica of the Student Details panel that appears in the live
    /// console when an instructor clicks a connected student. Opened
    /// from InstructorMonitoringHelpWindow when the user clicks the
    /// 4th tutorial student (dolfo). Visual mock only — no real
    /// Remove from Session call, no monitoring state mutation.
    ///
    /// The two action buttons (Violations Summary, Remove from Session)
    /// are clickable and each opens a MonitoringHelpInfoWindow popup
    /// describing what that button does in the real console.
    /// </summary>
    public partial class StudentDetailsHelpWindow : Window
    {
        // Tag → tutorial content for each clickable button. Keys must
        // match the Tag="..." values in the XAML.
        private static readonly Dictionary<string, ButtonInfo> Buttons = new()
        {
            ["violationsSummary"] = new ButtonInfo(
                Title:       "Violations Summary",
                IconKind:    PackIconKind.ClipboardListOutline,
                Description: "Opens a chronological breakdown of every detection event the student has triggered this session — focus losses, paste attempts, idle warnings, process-blacklist hits, hardware checks — each with its severity score and timestamp.",
                Purpose:     "Drill-down evidence view. Use it when the SAFE / SUSPICIOUS / CHEATING headline isn't enough and you need to see exactly what fired, when, and how severe, before deciding to intervene, send a warning, or document the incident.",
                Scenario:    "dolfo's risk level just bumped from SAFE to SUSPICIOUS. You open Violations Summary and see three CLIPBOARD_PASTE events within 10 seconds of each other — strong signal of copy-paste cheating. You note it for the post-session review and keep monitoring."),

            ["removeFromSession"] = new ButtonInfo(
                Title:       "Remove from Session",
                IconKind:    PackIconKind.AccountRemove,
                Description: "Force-ends this student's session from the IMC. Their SAC softlock UI closes, monitoring stops broadcasting their events, and the room continues uninterrupted for everyone else.",
                Purpose:     "Last-resort intervention for confirmed misconduct or a stuck client you can't recover. Removing one student is preferred over ending the whole session because the rest of the cohort keeps going on the same timer, unaffected.",
                Scenario:    "dolfo crossed into CHEATING (cumulative score ≥ 50) with confirmed PROCESS_DETECTED hits for Discord and AnyDesk plus repeated WINDOW_SWITCH violations. You click Remove from Session; dolfo's SAC softlock closes immediately, their row disappears from Taking, and the session continues with the rest of the class."),
        };

        public StudentDetailsHelpWindow()
        {
            InitializeComponent();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Borderless window — re-implement drag-to-move on the
        // root Border.
        private void Window_DragMove(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        // Click handler shared by every clickable button in the replica.
        // e.Handled = true so the click doesn't bubble up to the root
        // Border's drag-to-move handler.
        private void Button_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (sender is not Border border) return;
            if (border.Tag is not string key) return;
            if (!Buttons.TryGetValue(key, out var info)) return;

            var popup = new MonitoringHelpInfoWindow(
                info.Title,
                info.IconKind,
                info.Description,
                info.Purpose,
                info.Scenario)
            {
                Owner = this
            };
            popup.ShowDialog();
        }

        // Strongly-typed record for the per-button content table.
        private sealed record ButtonInfo(
            string       Title,
            PackIconKind IconKind,
            string       Description,
            string       Purpose,
            string       Scenario);
    }
}
