using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MsiGT;

internal sealed record FreeProgress(int Done, int Total, string Text);

internal sealed record FreeReport(int Remaining, IReadOnlyList<string> Problems);

/// <summary>
/// Closes the processes that keep the discrete GPU awake, as gently as each kind allows:
/// <list type="bullet">
/// <item>Helper processes (Chromium/Electron/WebView2 GPU processes…) are ended and recreated by their app,
/// which then picks the power-saving GPU. Each app's helpers are ended once per run: ending them repeatedly
/// makes Chromium turn off hardware acceleration.</item>
/// <item>Apps are asked to close the way Windows does at sign-out (WM_QUERYENDSESSION / WM_ENDSESSION with
/// ENDSESSION_CLOSEAPP), so they save their state; an app that says no (e.g. unsaved work) is left running.</item>
/// <item>Explorer is exited through its own "Exit Explorer" command and started again right away.</item>
/// <item>Shell hosts such as Start and Search are ended; Windows restarts them.</item>
/// </list>
/// Apps on the restart list are started again at the end, with their original command line and user token.
/// </summary>
internal static class GpuFreer
{
    private const int MaxPasses = 3;
    private const int SettleMilliseconds = 2000;

    public static FreeReport Run(DiscreteGpu gpu, Settings settings, IProgress<FreeProgress> progress)
    {
        var problems = new List<string>();
        var attempted = new HashSet<(int Pid, DateTime Start)>();
        var helperHostPass = new Dictionary<int, int>();
        var reportedHosts = new HashSet<int>();
        var relaunches = new List<Relaunch>();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.CloseTimeoutSeconds, 1, 60));
        int done = 0;

        try
        {
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                var todo = new List<GpuUser>();
                foreach (var user in GpuUsers.Scan(gpu, settings))
                {
                    if (!user.CanClose || !attempted.Add((user.Process.Pid, user.Process.StartTime)))
                        continue;
                    if (user.Kind == GpuUserKind.Helper)
                    {
                        if (helperHostPass.TryGetValue(user.HostPid, out int endedInPass) && endedInPass < pass)
                        {
                            if (reportedHosts.Add(user.HostPid))
                                problems.Add($"{user.DisplayName} started on the discrete GPU again. The app may be set to " +
                                             "\"High performance\" in Settings > System > Display > Graphics.");
                            continue;
                        }
                        helperHostPass[user.HostPid] = pass;
                    }
                    todo.Add(user);
                }
                if (todo.Count == 0)
                    break;

                todo = Order(todo);

                // Capture restart details before closing anything: closing an app can take its children with it.
                var relaunchFor = new Dictionary<GpuUser, Relaunch>();
                foreach (var user in todo.Where(u => u.Restart || u.Kind == GpuUserKind.Explorer))
                {
                    try
                    {
                        relaunchFor[user] = Relaunch.Capture(user);
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{user.DisplayName} can't be restarted automatically: {ex.Message}");
                    }
                }

                int total = done + todo.Count + 1;
                foreach (var user in todo)
                {
                    progress.Report(new FreeProgress(done, total, Describe(user)));
                    relaunchFor.TryGetValue(user, out var relaunch);
                    if (relaunch != null && user.Kind != GpuUserKind.Explorer)
                        relaunches.Add(relaunch); // skipped at the end if the app is still (or again) running
                    try
                    {
                        Close(user, relaunch, timeout, problems);
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"Couldn't close {user.DisplayName}: {ex.Message}");
                    }
                    finally
                    {
                        if (user.Kind == GpuUserKind.Explorer)
                            relaunch?.Dispose(); // Explorer is started again right away
                    }
                    done++;
                }

                progress.Report(new FreeProgress(done, total, "Waiting for the GPU to be released…"));
                Thread.Sleep(SettleMilliseconds);
            }

            int finalTotal = done + relaunches.Count + 1;
            foreach (var relaunch in relaunches)
            {
                progress.Report(new FreeProgress(done++, finalTotal, $"Restarting {relaunch.DisplayName}…"));
                // A restarted parent may already have started this one again (e.g. an app's own helper tools).
                if (IsRunning(relaunch.ExePath))
                    continue;
                try
                {
                    relaunch.Start();
                    Thread.Sleep(1500);
                }
                catch (Exception ex)
                {
                    problems.Add($"Couldn't restart {relaunch.DisplayName}: {ex.Message}");
                }
            }

            progress.Report(new FreeProgress(done, finalTotal, "Checking the GPU…"));
            int remaining = GpuUsers.Scan(gpu, settings).Count;
            progress.Report(new FreeProgress(finalTotal, finalTotal, "Done"));
            return new FreeReport(remaining, problems);
        }
        finally
        {
            foreach (var relaunch in relaunches)
                relaunch.Dispose();
        }
    }

    /// <summary>Apps first (parents before their children, which usually close along with them), helpers last.</summary>
    private static List<GpuUser> Order(List<GpuUser> users)
    {
        var pids = users.Select(u => u.Process.Pid).ToHashSet();
        int Depth(ProcessDetails p)
        {
            int depth = 0;
            for (var parent = p.GetLiveParent(); parent != null && depth < 8; parent = parent.GetLiveParent())
                if (pids.Contains(parent.Pid))
                    depth++;
            return depth;
        }

        return users
            .OrderBy(u => u.Kind switch
            {
                GpuUserKind.App => 0,
                GpuUserKind.Explorer => 1,
                GpuUserKind.WindowsComponent => 2,
                _ => 3,
            })
            .ThenBy(u => Depth(u.Process))
            .ToList();
    }

    private static string Describe(GpuUser user) => user.Kind switch
    {
        GpuUserKind.Explorer => "Restarting Windows Explorer…",
        GpuUserKind.App => $"Closing {user.DisplayName}…",
        _ => $"Ending {user.DisplayName}…",
    };

    private static void Close(GpuUser user, Relaunch? relaunch, TimeSpan timeout, List<string> problems)
    {
        using var process = OpenIfAlive(user.Process);
        if (process == null)
            return; // already gone, e.g. closed together with its parent

        switch (user.Kind)
        {
            case GpuUserKind.Helper:
            case GpuUserKind.WindowsComponent:
                Terminate(process);
                break;

            case GpuUserKind.Explorer:
                if (relaunch == null)
                    return; // Explorer is only exited when we know we can start it again (reported above)
                RestartExplorer(user, process, relaunch, timeout, problems);
                break;

            case GpuUserKind.App:
                if (!CloseApp(process, timeout))
                    problems.Add($"{user.DisplayName} didn't agree to close, possibly because of unsaved work. " +
                                 "Close it yourself, then try again.");
                break;
        }
    }

    private static Process? OpenIfAlive(ProcessDetails details)
    {
        try
        {
            var process = Process.GetProcessById(details.Pid);
            if (details.IsAlive())
                return process;
            process.Dispose();
        }
        catch (ArgumentException)
        {
        }
        return null;
    }

    private static void Terminate(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            return; // exited in the meantime
        }
        process.WaitForExit(3000);
    }

    /// <summary>
    /// Asks every top-level window of the process whether the app can close, then tells it to, like Windows
    /// does at sign-out; ends the process if it's still there after <paramref name="timeout"/>.
    /// Returns false (and leaves the app alone) if it refuses or is busy asking the user something.
    /// </summary>
    private static bool CloseApp(Process process, TimeSpan timeout)
    {
        var windows = GetTopLevelWindows(process.Id);
        var asked = new List<IntPtr>();
        bool refused = false;

        foreach (var window in windows)
        {
            if (SendMessageTimeoutW(window, WM_QUERYENDSESSION, IntPtr.Zero, ENDSESSION_CLOSEAPP, SMTO_ABORTIFHUNG, 5000, out var answer) == IntPtr.Zero)
            {
                if (!IsWindow(window) || IsHungAppWindow(window))
                    continue; // gone, or hung (then it gets ended below)
                refused = true; // busy, most likely showing a dialog
                break;
            }
            asked.Add(window);
            if (answer == IntPtr.Zero)
            {
                refused = true;
                break;
            }
        }

        if (refused)
        {
            foreach (var window in asked)
                SendMessageTimeoutW(window, WM_ENDSESSION, IntPtr.Zero, ENDSESSION_CLOSEAPP, SMTO_ABORTIFHUNG, 2000, out _);
            return false;
        }

        foreach (var window in asked)
            SendMessageTimeoutW(window, WM_ENDSESSION, 1, ENDSESSION_CLOSEAPP, SMTO_ABORTIFHUNG, 5000, out _);

        if (!process.WaitForExit(windows.Count > 0 ? timeout : TimeSpan.Zero))
            Terminate(process);
        return true;
    }

    private static void RestartExplorer(GpuUser user, Process process, Relaunch relaunch, TimeSpan timeout, List<string> problems)
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || GetWindowThreadProcessId(tray, out int shellPid) == 0 || shellPid != process.Id)
        {
            // A File Explorer window running in its own process, not the shell: just close it.
            if (!CloseApp(process, timeout))
                problems.Add($"{user.DisplayName} didn't agree to close.");
            return;
        }

        // What "Exit Explorer" in the taskbar's Ctrl+Shift+right-click menu sends.
        PostMessageW(tray, WM_USER + 436, IntPtr.Zero, IntPtr.Zero);
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            Terminate(process);

        // Windows restarts the shell itself after a crash; don't start a second one then.
        Thread.Sleep(1500);
        if (FindWindowW("Shell_TrayWnd", null) == IntPtr.Zero)
            relaunch.Start();
    }

    private static bool IsRunning(string exePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (process)
                if (string.Equals(ProcessDetails.TryGet(process.Id)?.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static List<IntPtr> GetTopLevelWindows(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out int owner) != 0 && owner == pid)
                windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_USER = 0x0400;
    private const nint ENDSESSION_CLOSEAPP = 0x1;
    private const uint SMTO_ABORTIFHUNG = 0x2;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr window);
}
