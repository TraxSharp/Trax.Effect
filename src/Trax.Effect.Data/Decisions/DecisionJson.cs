using System.Text.Json;
using System.Text.Json.Nodes;
using Trax.Core.Decisions;

namespace Trax.Effect.Data.Decisions;

/// <summary>
/// The stored form of questions and answers in <c>trax.decision</c>: plain JSON with a
/// <c>type</c> discriminator, readable in SQL and independent of .NET type names.
/// </summary>
internal static class DecisionJson
{
    public static string Kind(Question question) =>
        question switch
        {
            ChoiceQuestion => "choice",
            ScoreQuestion => "score",
            YesNoQuestion => "yes_no",
            _ => question.GetType().Name,
        };

    public static string Write(Question question)
    {
        var node = new JsonObject
        {
            ["type"] = Kind(question),
            ["key"] = question.Key,
            ["instructions"] = question.Instructions,
        };

        switch (question)
        {
            case ChoiceQuestion choice:
                node["options"] = Criteria(choice.Options);
                break;
            case ScoreQuestion score:
                node["levels"] = Criteria(score.Levels);
                break;
            case YesNoQuestion yesNo:
                node["yes"] = yesNo.Yes;
                node["no"] = yesNo.No;
                break;
        }

        return node.ToJsonString();
    }

    public static string Write(Answer answer) => Node(answer).ToJsonString();

    public static string? Write(IReadOnlyList<ShadowAnswer> shadows) =>
        shadows.Count == 0
            ? null
            : new JsonArray(
                shadows
                    .Select(s =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["decider"] = s.Decider.FullName,
                                ["agrees"] = s.Agrees,
                                ["error"] = s.Error,
                                ["answer"] = s.Answer is null ? null : Node(s.Answer),
                            }
                    )
                    .ToArray()
            ).ToJsonString();

    /// <summary>
    /// Reads a stored answer back, or throws <see cref="JsonException"/> when it cannot be: a replay
    /// that cannot read what it recorded must not quietly ask again.
    /// </summary>
    public static Answer ReadAnswer(string json)
    {
        var node =
            JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("A recorded answer is not a JSON object.");

        var model = node["model"]?.GetValue<string>();

        Answer answer = (string?)node["type"] switch
        {
            "choice" => new ChoiceAnswer(
                Required(node, "choice").GetValue<string>(),
                Required(node, "confidence").GetValue<double>(),
                node["probabilities"]
                    ?.AsObject()
                    .ToDictionary(p => p.Key, p => p.Value!.GetValue<double>())
            ),
            "score" => new ScoreAnswer(
                Required(node, "score").GetValue<double>(),
                Required(node, "confidence").GetValue<double>(),
                node["probabilities"]?.AsArray().Select(p => p!.GetValue<double>()).ToList()
            ),
            "yes_no" => new YesNoAnswer(Required(node, "probability").GetValue<double>()),
            var other => throw new JsonException(
                $"A recorded answer has the unknown type '{other}'."
            ),
        };

        return answer with
        {
            Model = model,
        };
    }

    private static JsonObject Node(Answer answer)
    {
        var node = answer switch
        {
            ChoiceAnswer c => new JsonObject
            {
                ["type"] = "choice",
                ["choice"] = c.Choice,
                ["confidence"] = c.Confidence,
                ["probabilities"] = c.Probabilities is null
                    ? null
                    : new JsonObject(
                        c.Probabilities.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))
                    ),
            },
            ScoreAnswer s => new JsonObject
            {
                ["type"] = "score",
                ["score"] = s.Score,
                ["confidence"] = s.Confidence,
                ["probabilities"] = s.Probabilities is null
                    ? null
                    : new JsonArray(s.Probabilities.Select(p => (JsonNode?)p).ToArray()),
            },
            YesNoAnswer y => new JsonObject
            {
                ["type"] = "yes_no",
                ["probability"] = y.Probability,
            },
            _ => new JsonObject { ["type"] = answer.GetType().Name },
        };

        node["model"] = answer.Model;
        return node;
    }

    private static JsonArray Criteria(IReadOnlyList<Criterion> criteria) =>
        new(
            criteria
                .Select(c =>
                    (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description }
                )
                .ToArray()
        );

    private static JsonNode Required(JsonObject node, string name) =>
        node[name] ?? throw new JsonException($"A recorded answer has no '{name}'.");
}
