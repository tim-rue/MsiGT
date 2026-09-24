using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MsiGT;

internal enum GpuUserKind
{
    /// <summary>Core Windows or system-session process; never touched.</summary>
    System,
    /// <summary>On the user's never-close list.</summary>
    Ignored,
    /// <summary>A Windows component, but the user turned closing those off.</summary>
    WindowsSkipped,
    /// <summary>Child of a running process with the same executable (Chromium/Electron/WebView2 GPU and utility
    /// processes, Firefox's GPU process…). The parent recreates it, so it is ended and never restarted.</summary>
    Helper,
    /// <summary>Shell host that Windows restarts by itself (Start, Search, touch keyboard…).</summary>
    WindowsComponent,
    /// <summary>The Explorer shell: exited cleanly and started again.</summary>
    Explorer,
    /// <summary>An app: asked to close the way Windows does at sign-out, ended if it doesn't, optionally restarted.</summary>
    App,
}

/// <summary>A process keeping the discrete GPU awake, and what freeing the GPU will do with it.</summary>
internal sealed record GpuUser(ProcessDetails Process, GpuUserKind Kind, bool Restart, string DisplayName, int HostPid)
{
    public bool CanClose => Kind is GpuUserKind.Helper or GpuUserKind.WindowsComponent or GpuUserKind.Explorer or GpuUserKind.App;

    public string ActionText => Kind switch
    {
        GpuUserKind.System => "System process, left alone",
        GpuUserKind.Ignored => "On your never-close list",
        GpuUserKind.WindowsSkipped => "Windows component, skipped (see Settings)",
        GpuUserKind.Helper => "End it, the app recreates it",
        GpuUserKind.WindowsComponent => "End it, Windows restarts it",
        GpuUserKind.Explorer => "Restart Explorer",
        _ => Restart ? "Close and restart" : "Close",
    };
}

internal static class GpuUsers
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string SystemAppsDir = Path.Combine(WindowsDir, "SystemApps");

    /// <summary>Shell hosts outside SystemApps that Windows also restarts on its own.</summary>
    private static readonly HashSet<string> RestartingWindowsHosts = new(StringComparer.OrdinalIgnoreCase) { "ShellHost.exe", "TabTip.exe" };

    /// <summary>The MSI service's user-session components; MsiGT itself depends on them.</summary>
    private static readonly HashSet<string> ProtectedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "OmApSvcBroker.exe", "MSI.TerminalServer.exe",
    };

    /// <summary>Windows programs that are ordinary user apps and can simply be closed.</summary>
    private static readonly HashSet<string> ClosableWindowsApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemSettings.exe", "ApplicationFrameHost.exe",
    };

    public static List<GpuUser> Scan(DiscreteGpu gpu, Settings settings)
    {
        int self = Environment.ProcessId;
        string? desktopUser = GetDesktopUserSid();
        return gpu.GetProcessIds()
            .Where(pid => pid != self)
            .Select(ProcessDetails.TryGet)
            .OfType<ProcessDetails>()
            .Select(p => Classify(p, desktopUser, settings))
            .OrderBy(u => u.CanClose ? 0 : 1)
            .ThenBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(u => u.Process.Pid)
            .ToList();
    }

    private static GpuUser Classify(ProcessDetails p, string? desktopUser, Settings settings)
    {
        // Services, other accounts, and processes we can't inspect are not ours to close.
        if (p.ExePath == null || p.SessionId == 0 || p.UserSid == null || p.UserSid != desktopUser
            || ProtectedPrograms.Contains(p.FileName))
            return new GpuUser(p, GpuUserKind.System, false, p.Description, p.Pid);

        if (settings.IsNeverClose(p.FileName))
            return new GpuUser(p, GpuUserKind.Ignored, false, p.Description, p.Pid);

        var parent = p.GetLiveParent();
        if (parent != null && p.SameExeAs(parent))
            return DescribeHelper(p, parent);

        GpuUserKind? windowsKind = null;
        if (string.Equals(p.ExePath, Path.Combine(WindowsDir, "explorer.exe"), StringComparison.OrdinalIgnoreCase))
            windowsKind = GpuUserKind.Explorer;
        else if (IsInDirectory(p.ExePath, SystemAppsDir) || RestartingWindowsHosts.Contains(p.FileName))
            windowsKind = GpuUserKind.WindowsComponent;
        else if (IsInDirectory(p.ExePath, WindowsDir) && !ClosableWindowsApps.Contains(p.FileName))
            return new GpuUser(p, GpuUserKind.System, false, p.Description, p.Pid);

        if (windowsKind is { } kind)
            return new GpuUser(p, settings.CloseWindowsComponents ? kind : GpuUserKind.WindowsSkipped, false, p.Description, p.Pid);

        return new GpuUser(p, GpuUserKind.App, settings.ShouldRestart(p.FileName), p.Description, p.Pid);
    }

    /// <summary>The account that owns the desktop (Explorer). Differs from ours if elevated with other credentials.</summary>
    private static string? GetDesktopUserSid()
    {
        var shell = GetShellWindow();
        if (shell != IntPtr.Zero && GetWindowThreadProcessId(shell, out int pid) != 0
            && ProcessDetails.TryGet(pid)?.UserSid is { } sid)
            return sid;
        return WindowsIdentity.GetCurrent().User?.Value;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

    /// <summary>Names a helper after the app it serves, e.g. "Microsoft Outlook (WebView2 GPU process)".</summary>
    private static GpuUser DescribeHelper(ProcessDetails helper, ProcessDetails parent)
    {
        // The top of the same-executable chain is the process that recreates the helper.
        var host = parent;
        for (int depth = 0; depth < 8; depth++)
        {
            var next = host.GetLiveParent();
            if (next == null || !next.SameExeAs(host))
                break;
            host = next;
        }

        string app = host.Description;
        string prefix = "";
        if (string.Equals(host.FileName, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "WebView2 ";
            var embedder = host.GetLiveParent();
            if (embedder != null)
                app = embedder.Description;
        }

        string commandLine = helper.CommandLine ?? "";
        string role =
            commandLine.Contains("--type=gpu-process", StringComparison.OrdinalIgnoreCase) || commandLine.EndsWith(" gpu", StringComparison.OrdinalIgnoreCase)
                ? "GPU process"
                : commandLine.Contains("--type=utility", StringComparison.OrdinalIgnoreCase)
                    ? "utility process"
                    : "helper process";

        return new GpuUser(helper, GpuUserKind.Helper, false, $"{app} ({prefix}{role})", host.Pid);
    }

    private static bool IsInDirectory(string path, string directory) =>
        path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
