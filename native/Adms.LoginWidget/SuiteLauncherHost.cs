using Osas.Core;

namespace Adms.LoginWidget;

/// <summary>
/// Starts the suite apps and Kivo's sign-in for the launcher. The launcher has no company login any more: Kivo owns it (contracts/session-broker.md), and
/// each app asks Kivo for a session when it needs the database. Apps start with the AI and database addresses in their environment and nothing else.
/// </summary>
internal sealed class SuiteLauncherHost
{
    internal static SuiteLauncherHost Instance { get; } = new();

    /// <summary>Start an app of the suite (brought to the front when it already runs).</summary>
    internal void LaunchApp(SuiteAppDescriptor app, string? ospPath = null)
    {
        var exe = SuiteCatalog.FindExecutable(app) ?? throw new InvalidOperationException($"{app.Title} is not installed.");
        SuiteCatalog.StartApp(exe, ospPath is null ? null : new Dictionary<string, string> { [SuiteWire.EnvProjectOsp] = ospPath });
    }

    /// <summary>
    /// "Open Kivo to sign in": starts Kivo with <c>--broker --signin</c>. Kivo shows its sign-in, creates the session pipe, and the database service picks the
    /// session up at its next look (within 5 seconds).
    /// </summary>
    internal void OpenKivoToSignIn()
    {
        var kivo = SuiteCatalog.Apps.First(a => string.Equals(a.Title, "Kivo", StringComparison.Ordinal));
        var exe = SuiteCatalog.FindExecutable(kivo) ?? throw new InvalidOperationException("Kivo is not installed.");
        SuiteCatalog.StartApp(exe, null, "--broker", "--signin");
    }

    /// <summary>Start the app that asked for the launcher (<see cref="LaunchRequest"/>): at once, since nothing has to be signed in here first.</summary>
    internal void RunPendingLaunch()
    {
        var request = LaunchRequest.Read();
        if (request == null)
            return;
        LaunchRequest.Clear();
        var descriptor = SuiteCatalog.Apps.FirstOrDefault(a =>
            string.Equals(a.Title, request.App, StringComparison.OrdinalIgnoreCase) || string.Equals(a.TicketApp, request.App, StringComparison.OrdinalIgnoreCase));
        if (descriptor == null)
            return;
        try
        {
            LaunchApp(descriptor, request.Osp);
        }
        catch (Exception ex)
        {
            MynaWindow.ShowError(ex.Message);
        }
    }

    internal static string TrayTooltip()
    {
        var s = SuiteStatus.Instance;
        return $"Myna — AI: {Word(s.Ai)}, Database: {s.Database}";
    }

    /// <summary>The first word of an AI line (<c>ready</c>, <c>starting</c>, …); <c>stopped</c> for anything that is not up.</summary>
    internal static string Word(string ai)
    {
        if (ai.StartsWith("ready", StringComparison.Ordinal))
            return "ready";
        if (ai.StartsWith("starting", StringComparison.Ordinal) || ai.StartsWith("checking", StringComparison.Ordinal))
            return "starting";
        return "stopped";
    }
}
