using Adms.Cloud;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>When cloud revision is ahead of the cached head, offer pull or backup-then-pull.</summary>
internal static class CloudSyncConflictDialog
{
    internal enum Choice
    {
        Cancel,
        PullCloud,
        BackupLocalThenPull,
    }

    internal static async Task<Choice> ShowAsync(XamlRoot xamlRoot, string projectName, long localRevision, long cloudRevision)
    {
        var dialog = new ContentDialog
        {
            Title = "Cloud is ahead",
            XamlRoot = xamlRoot,
            PrimaryButtonText = "Take cloud copy",
            SecondaryButtonText = "Back up local, then pull",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text =
                    $"“{projectName}” was updated on another device.\n\n"
                    + $"Local file revision: {localRevision}\n"
                    + $"Cloud revision: {cloudRevision}\n\n"
                    + "Take cloud copy replaces your local .osp with the cloud snapshot (MynaCAD and MynaOps pick it up on their next revision poll).\n\n"
                    + "Back up local saves a copy beside the project file, then pulls the cloud snapshot.",
            },
        };
        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => Choice.PullCloud,
            ContentDialogResult.Secondary => Choice.BackupLocalThenPull,
            _ => Choice.Cancel,
        };
    }

    internal static string BackupLocalCopy(string ospPath, long localRevision)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = $"{ospPath}.local-r{localRevision}-{stamp}.osp";
        File.Copy(ospPath, backup, overwrite: false);
        return backup;
    }

    /// <summary>Returns a status line when the conflict flow ran; null if the message was not a cloud-ahead push failure.</summary>
    internal static async Task<string?> TryResolveCloudAheadAsync(
        XamlRoot xamlRoot,
        long studioProjectId,
        string projectName,
        string ospPath,
        string pushMessage)
    {
        if (!pushMessage.Contains("Cloud is ahead", StringComparison.Ordinal))
            return null;

        long localRev = 0;
        if (File.Exists(ospPath))
        {
            using var odp = Office.Shared.OdpFile.Open(ospPath);
            localRev = odp.Meta().Revision;
        }

        var cloudRev = await CloudSyncHub.RefreshCloudRevisionAsync(studioProjectId, ospPath).ConfigureAwait(true)
            ?? localRev;
        if (cloudRev <= localRev)
            return pushMessage;

        var choice = await ShowAsync(xamlRoot, projectName, localRev, cloudRev).ConfigureAwait(true);
        if (choice == Choice.Cancel)
            return pushMessage;

        if (choice == Choice.BackupLocalThenPull)
        {
            try
            {
                var backup = BackupLocalCopy(ospPath, localRev);
                pushMessage = $"Local copy saved as {Path.GetFileName(backup)}. Pulling cloud…";
            }
            catch (Exception ex)
            {
                return "Could not back up the local file: " + ex.Message;
            }
        }

        var pull = await CloudSyncHub.PullNowAsync(studioProjectId, ospPath).ConfigureAwait(true);
        if (pull.Ok && pull.NewRevision is long nr)
        {
            var store = CloudSyncHub.Store;
            var s = store.Get(studioProjectId);
            s.LastKnownCloudRevision = nr;
            s.LastLocalRevision = nr;
            store.Projects[studioProjectId] = s;
            store.Save();
        }
        return pull.Message;
    }
}
