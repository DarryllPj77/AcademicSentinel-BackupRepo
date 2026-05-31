using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace AcademicSentinel.Client.Views.IMC
{
    /// <summary>
    /// Interactive tutorial for the Instructor Monitoring Console.
    /// Visually mirrors LiveSessionMonitoringWindow (header,
    /// sidebar, participants card with 4 dummy student rows, and
    /// the right-pane log feed pre-populated with matching dummy
    /// entries). Every clickable region opens a small popup that
    /// explains it.
    ///
    /// Two popup window types:
    ///   • MonitoringHelpInfoWindow — generic Description/Purpose/
    ///     Scenario card used for everything except…
    ///   • StudentDetailsHelpWindow — visual replica of the live
    ///     console's Student Details panel, opened when the user
    ///     clicks the 4th tutorial student (the "connected" one).
    ///
    /// Safe by construction: no SignalR, HttpClient, or
    /// SessionManager. Region_Click only opens popups.
    /// </summary>
    public partial class InstructorMonitoringHelpWindow : Window
    {
        // Region key → content. Keys must match the Tag="..."
        // values on the clickable Borders in the XAML.
        private static readonly Dictionary<string, RegionInfo> Regions = new()
        {
            // ---------- Console chrome ----------
            ["header"] = new RegionInfo(
                Title:       "Header & End Session",
                IconKind:    PackIconKind.SchoolOutline,
                Description: "The green bar at the top of the console identifies the room you're monitoring and exposes the only safe way to finish a session.",
                Purpose:     "Lets you confirm the correct room at a glance and gives you the End Session control. End Session cleanly closes monitoring, broadcasts SessionEnded to every student, and archives the session for review.",
                Scenario:    "After the last student submits their exam, click End Session. The console flips to 'Ended', students get the exit signal in their SAC, and the session appears under Past Sessions in the room view."),

            ["countdown"] = new RegionInfo(
                Title:       "Session Countdown",
                IconKind:    PackIconKind.TimerOutline,
                Description: "Status indicator showing whether the room is running a countdown before a session starts, or sitting idle (NOT ACTIVE).",
                Purpose:     "Gives you a clear visual that the room is waiting / counting down / actively monitoring without having to read the status bar.",
                Scenario:    "You scheduled the exam to start at 10:30. At 10:29 the chip flips from 'NOT ACTIVE' to the live countdown so you know the session is about to flip Active."),

            ["viewParticipants"] = new RegionInfo(
                Title:       "View Participants List",
                IconKind:    PackIconKind.AccountMultipleOutline,
                Description: "Opens the detailed roster window for the room — every enrolled student with their join, leave, and risk history.",
                Purpose:     "Shortcut to the deeper roster view when the compact Participants panel isn't enough (e.g. contact info, history across sessions).",
                Scenario:    "A student says they joined but you don't see them in the panel. Open the participants list to confirm enrollment and pull up their last join timestamp."),

            ["start"] = new RegionInfo(
                Title:       "Start Session Monitoring",
                IconKind:    PackIconKind.Play,
                Description: "The green call-to-action that flips the room from 'waiting' to live monitoring.",
                Purpose:     "Begins detection-event capture for every student in the waiting area. The button toggles to Pause once monitoring is live so you can pause for an announcement without ending the session.",
                Scenario:    "Three students are in the waiting area. You click Start Session Monitoring at 10:30. All three flip from 'Awaiting' to 'Connected', the timer starts, and the Global Log Feed begins recording events."),

            ["participants"] = new RegionInfo(
                Title:       "Participants Panel",
                IconKind:    PackIconKind.AccountMultiple,
                Description: "Live roster of every student in the room with their connection state, plus filter tabs (Taking / Done / Finished) and a search box.",
                Purpose:     "Real-time view of who's in, who's idle, who's disconnected. Click any student card to load them into the Selected Student dashboard for detail.",
                Scenario:    "Mid-exam you notice a student's status dot turn yellow. You click their card — the Risk Dashboard updates and the Global Log Feed shows the matching event."),

            ["log"] = new RegionInfo(
                Title:       "Global Log Feed",
                IconKind:    PackIconKind.FormatListBulleted,
                Description: "Chronological stream of every monitoring event across all students in the room — joins, focus losses, hand-raises, leave-approval requests, and hardware status.",
                Purpose:     "Scan it to spot patterns the per-student dashboards hide. Multiple simultaneous 'focus lost' lines across students usually means a network blip, not coordinated misconduct.",
                Scenario:    "At 10:33 you see three back-to-back 'focus lost' lines for different students. Same second — likely the projector flickered, not cheating. You note it and continue."),

            // ---------- Student-state regions (sidebar) ----------
            ["studentDone"] = new RegionInfo(
                Title:       "Student State: Done",
                IconKind:    PackIconKind.CheckCircle,
                Description: "A green 'Done' badge means the student tapped the Done button on their SAC softlock UI to mark the assessment as finished. Their softlock stays open and their connection stays live for a few seconds while the SAC sends the final ack, then it unlocks so they can close it.",
                Purpose:     "Lets you see at a glance — without leaving the IMC — who is finished, so you can concentrate live monitoring on the students still taking the exam.",
                Scenario:    "Alice Cruz finishes early at 10:30. On her end, her SAC softlock confirms submission; on your IMC, her row turns green with the 'Done' badge and her count moves from Taking to Done. You leave her session alone and focus on the rest of the cohort."),

            ["studentRaiseHand"] = new RegionInfo(
                Title:       "Student State: Raise Hand",
                IconKind:    PackIconKind.HandBackRight,
                Description: "An orange 'Raised' badge with a hand icon means the student tapped Raise Hand on their SAC softlock UI — the only way to get your attention without breaking softlock isolation.",
                Purpose:     "Replaces literal hand-raising in a fully remote, locked-down session. Lets the student flag a question (test typo, can't access a file, technical issue) without alt-tabbing, opening chat, or any other channel that would trigger a violation.",
                Scenario:    "Bob Dela Cruz raises his hand at 10:32. On the IMC his row goes orange-raised and the Global Log Feed shows 'Bob Dela Cruz raised their hand'. You approve the raise hand from the IMC to grant him temporary allowed-app access (e.g. Microsoft Teams) so he can ask without producing a WINDOW_SWITCH violation."),

            ["studentRequestApproval"] = new RegionInfo(
                Title:       "Student State: Request Approval",
                IconKind:    PackIconKind.HelpCircle,
                Description: "An orange 'Pending' badge means the student tapped Request to Leave on their SAC softlock UI and is waiting for your decision from the IMC.",
                Purpose:     "Gives you explicit control over every mid-session leave in a remote setup. The softlock keeps them in the session and on the timer until you approve from the IMC — they can't just exit unmonitored.",
                Scenario:    "Carol Estrada needs a quick break. She taps 'Request to Leave' on her SAC softlock; her row in the IMC goes orange-pending. You click her card, review her current risk level in the Student Details panel, then approve or deny from there. The decision is pushed back to her SAC immediately."),

            // ---------- Selected student → opens the dedicated replica popup ----------
            // (Special-cased in Region_Click — opens StudentDetailsHelpWindow
            //  instead of the generic MonitoringHelpInfoWindow.)
            ["studentDetails"] = new RegionInfo(
                Title:       "Student Details Panel (replica popup)",
                IconKind:    PackIconKind.AccountDetails,
                Description: "Clicking a connected student opens a side panel with their avatar, environment profile (Native OS vs VM, Remote Access status), live violation count, and a SAFE/WARN/HIGH risk level. From there you can review violations or remove the student from the session.",
                Purpose:     "Drill-down view for the student currently in focus. Lets you decide quickly whether someone needs intervention.",
                Scenario:    "Clicking dolfo's row opens the Student Details popup — the same panel you see in the live console. (This region opens a visual replica instead of the generic popup so you recognise the real thing.)"),

            // ---------- Filter dropdown ----------
            ["filter"] = new RegionInfo(
                Title:       "Log Feed Filter",
                IconKind:    PackIconKind.FilterVariant,
                Description: "Three-option dropdown that controls what shows in the Global Log Feed:\n   • All Entries — every event\n   • Violations Only — focus-loss, idle, process, clipboard, etc.\n   • Connections Only — joins, leaves, disconnects, reconnects.",
                Purpose:     "Lets you cut the noise when one type of signal is what you care about. Connections Only is great for tracking down 'who dropped just now'; Violations Only is the audit lens during review.",
                Scenario:    "A student claims they were never disconnected. Switch the filter to Connections Only — their full join/leave timeline is the only thing on screen, and you can confirm or refute the claim in two seconds."),

            // ---------- Log entry types ----------
            ["logEntry_completed"] = new RegionInfo(
                Title:       "Log Entry: Completed Exam",
                IconKind:    PackIconKind.CheckCircle,
                Description: "A green 'completed the exam' line means the student's SAC submitted the final ack and their state flipped to Done.",
                Purpose:     "Authoritative record that the student finished cleanly. Used by the post-session report as the per-student end time.",
                Scenario:    "You see '10:30:00 — Alice Cruz completed the exam'. That timestamp becomes Alice's submission time on the archive."),

            ["logEntry_handraised"] = new RegionInfo(
                Title:       "Log Entry: Raise Hand",
                IconKind:    PackIconKind.HandBackRight,
                Description: "A 'raised their hand' line corresponds to the student tapping Raise Hand in the SAC. Stays in the feed even after you walk over and resolve the question, so the audit trail is preserved.",
                Purpose:     "Lets you reconstruct after the fact who needed attention and roughly when, in case a grade challenge requires it.",
                Scenario:    "'10:32:15 — Bob Dela Cruz raised their hand'. You see it pop in, address Bob's question, and the entry stays in the log for the rest of the session."),

            ["logEntry_approval"] = new RegionInfo(
                Title:       "Log Entry: Leave Approval Request",
                IconKind:    PackIconKind.HelpCircle,
                Description: "An 'approval to leave' line corresponds to the student tapping Request to Leave. The next log line will be either 'leave approved' or 'leave denied' once you decide.",
                Purpose:     "Auditable record of every mid-session leave. Pairs with the approve/deny action so reviewers can see who asked, when, and how it was handled.",
                Scenario:    "'10:33:42 — Carol Estrada requested approval to leave the session'. You approve; a follow-up line 'Leave approved for Carol Estrada' appears at 10:33:48."),

            ["logEntry_join"] = new RegionInfo(
                Title:       "Log Entry: Joined Session",
                IconKind:    PackIconKind.AccountPlus,
                Description: "A green 'joined the session' line is logged when a student's SAC successfully negotiates the SignalR connection and they appear in the Participants panel.",
                Purpose:     "Marks the canonical start of a student's session timer. Late-joins are easy to spot against the configured start time.",
                Scenario:    "'10:34:11 — dolfo joined the session'. Four minutes late — you check whether that puts dolfo over the configured grace window before deciding to admit them."),
        };

        public InstructorMonitoringHelpWindow()
        {
            InitializeComponent();
        }

        // Single handler for every clickable region in the replica.
        // Sets e.Handled = true so nested clicks (e.g. a student row
        // sitting inside the Participants region) don't ALSO fire
        // the parent region's popup.
        private void Region_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (sender is not Border border) return;
            if (border.Tag is not string key) return;
            if (!Regions.TryGetValue(key, out var info)) return;

            // Special-case the 4th student: open the dedicated
            // Student Details replica popup so the user sees what
            // the real side-panel looks like, not just generic text.
            if (key == "studentDetails")
            {
                var detailsPopup = new StudentDetailsHelpWindow { Owner = this };
                detailsPopup.ShowDialog();
                return;
            }

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

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Strongly-typed record for the region content table.
        private sealed record RegionInfo(
            string       Title,
            PackIconKind IconKind,
            string       Description,
            string       Purpose,
            string       Scenario);
    }
}
