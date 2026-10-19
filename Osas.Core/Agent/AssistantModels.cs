namespace Osas.Core.Agent;

/// <summary>A model the assistant can use, with why one would choose it.</summary>
public sealed record AssistantModel(string Id, string Label, string Why);

/// <summary>
/// The models offered. The first is the default: it is the best balance of care and cost for looking things up across the practice's
/// records and explaining them. The smaller one is cheaper and quicker for plain lookups; the largest is for slower, harder questions.
/// </summary>
public static class AssistantModels
{
    public static readonly IReadOnlyList<AssistantModel> All =
    [
        new("claude-sonnet-5-5", "Claude Sonnet 5.5 (recommended)", "Careful with several records at once, reliable at using the tools, moderate cost."),
        new("claude-haiku-5-5", "Claude Haiku 5.5 (faster, cheaper)", "Quick lookups of one client, project or meeting."),
        new("claude-opus-5-5", "Claude Opus 5.5 (most capable)", "Harder questions that need weighing many records. Slower and dearer."),
    ];

    public static AssistantModel Default => All[0];

    public static AssistantModel Find(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Default;
}
