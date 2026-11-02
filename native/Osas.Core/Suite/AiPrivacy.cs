using System.Text.Json;

namespace Osas.Core.Suite;

/// <summary>
/// What the AI agent may use, set by the person at the launcher (Ask page, Privacy). Stored beside the other suite state as <c>ai-privacy.json</c>.
/// The agent always runs on this computer; these switches decide what it is given. Defaults are the careful ones.
/// </summary>
public sealed class AiPrivacy
{
    /// <summary>Tell the agent the name of the loaded project. Off: it is never told which project is loaded.</summary>
    public bool ShareProjectName { get; set; }

    /// <summary>Send the earlier questions and answers of this window with each question. Off: every question stands alone.</summary>
    public bool RememberConversation { get; set; }

    /// <summary>Allow the + button to attach a file to a question. Off: the button is disabled.</summary>
    public bool AllowAttachments { get; set; } = true;

    public const string FileName = "ai-privacy.json";

    static string FilePath => SuiteWire.LocalPath(FileName);

    public static AiPrivacy Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (File.Exists(path) && JsonSerializer.Deserialize<AiPrivacy>(File.ReadAllText(path)) is { } p)
                return p;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new AiPrivacy();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
