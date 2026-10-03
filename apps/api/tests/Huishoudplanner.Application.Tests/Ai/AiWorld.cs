#pragma warning disable CA1861 // Literal request bodies of the scenarios.

using System.Text.Json;
using Huishoudplanner.Application.Ai;
using Huishoudplanner.Application.Tests.CyclePlans;
using Huishoudplanner.Application.Tests.Users;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Application.Tests.Ai;

/// <summary>A model that answers from a script and remembers every request. Never a provider.</summary>
internal sealed class ScriptedModel(Func<ChatRequest, int, OneOf<ChatReply, AiUnavailable, PortError>> answer) : ForChattingWithAModel
{
    private readonly List<ChatRequest> requests = [];

    public IReadOnlyList<ChatRequest> Requests => requests;

    public string Provider => "scripted";

    public Task<OneOf<ChatReply, AiUnavailable, PortError>> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        requests.Add(request);
        return Task.FromResult(answer(request, requests.Count - 1));
    }

    public static ScriptedModel Replying(Func<ChatRequest, int, string> text) =>
        new((request, attempt) => new ChatReply(text(request, attempt)));

    public static ScriptedModel Failing(OneOf<ChatReply, AiUnavailable, PortError> failure) => new((_, _) => failure);

    public PlanPromptPayload PlanPayload(int index = 0) => Payload<PlanPromptPayload>(index);

    public T Payload<T>(int index = 0) => JsonSerializer.Deserialize<T>(requests[index].Messages[^1].Content, PromptJson.Options)!;
}

/// <summary>Chooses the scripted model whatever the settings say and records what it was asked for.</summary>
internal sealed class FakeModels(ForChattingWithAModel model) : ForSelectingAModel
{
    public ForChattingWithAModel Model { get; set; } = model;

    public List<AiProviderSettings> Chosen { get; } = [];

    public ForChattingWithAModel ChooseFor(AiProviderSettings settings)
    {
        Chosen.Add(settings);
        return Model;
    }
}

/// <summary>The AI use cases over the in-memory ports of the cycle plan world: two tasks, two people and the model of the test.</summary>
internal sealed class AiWorld
{
    public const string Rationale = "[\"Week 1.\",\"Week 2.\",\"Week 3.\",\"Week 4.\"]";

    public CyclePlanWorld Plans { get; } = new();

    public FakeModels Models { get; }

    public AiService Service { get; }

    public Huishoudplanner.Domain.Rooms.Room Badkamer { get; }

    public Huishoudplanner.Domain.Rooms.Room Keuken { get; }

    public Huishoudplanner.Domain.Tasks.HouseholdTask Weekly { get; }

    public Huishoudplanner.Domain.Tasks.HouseholdTask Twice { get; }

    public Huishoudplanner.Domain.Users.User P1 { get; }

    public Huishoudplanner.Domain.Users.User P2 { get; }

    public CyclePlan Standaard { get; }

    public AiWorld(ForChattingWithAModel model)
    {
        Models = new FakeModels(model);
        Service = new AiService(
            Plans.Tasks, new FakeUserStore(Plans.People), Plans.RoomStore, Plans.Settings, Plans.Plans, Models, Plans.Transactions, Plans.Audit, Plans.Clock);
        Badkamer = Plans.Room("Badkamer");
        Keuken = Plans.Room("Keuken");
        Weekly = Plans.Task("Badkamer schoonmaken", Badkamer.Id, "1w", 30);
        Twice = Plans.Task("Wastafel", Badkamer.Id, "2w", 10);
        P1 = AddPerson("Persoon 1");
        P2 = AddPerson("Persoon 2");
        Standaard = Plans.Plan("Standaard", active: true, daysAgo: 5);
    }

    public static AiWorld Replying(Func<ChatRequest, int, string> text) => new(ScriptedModel.Replying(text));

    /// <summary>A person created after the previous ones: the list order is by creation time, so the tests see a fixed order.</summary>
    public Huishoudplanner.Domain.Users.User AddPerson(string name, bool active = true, int[]? unavailable = null)
    {
        var person = Plans.Person(name, active, unavailable);
        var index = Plans.People.Users.FindIndex(u => u.Id == person.Id);
        Plans.People.Users[index] = person with { CreatedAt = CyclePlanWorld.Now.AddMinutes(index) };
        return Plans.People.Users[index];
    }

    public ScriptedModel Model => (ScriptedModel)Models.Model;

    /// <summary>A valid plan built from the prompt the use case sent, by the deterministic mock of the demo provider.</summary>
    public static string ValidPlan(ChatRequest request) =>
        ValidPlanFromUser(request.Messages[^1].Content);

    public static string ValidPlanFromUser(string user)
    {
        // The deterministic plan is part of the demo provider (an adapter); the use case tests build theirs from the payload.
        var payload = JsonSerializer.Deserialize<PlanPromptPayload>(user, PromptJson.Options)!;
        var slots = PlanCompleter.CompleteRequiredOccurrences(payload, []);
        return JsonSerializer.Serialize(
            new
            {
                slots = slots.Select(s => new { taskId = s.TaskId, weekIndex = s.WeekIndex, weekday = s.Weekday, assigneeId = s.AssigneeId }),
                rationale = new[] { "Week 1.", "Week 2.", "Week 3.", "Week 4." },
            },
            PromptJson.Options);
    }

}
