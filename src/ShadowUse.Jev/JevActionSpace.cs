// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
namespace ShadowUse.Jev;

/// <summary>
/// Builds a fresh, short-lived indexed action space for one Jev fan-out request:
/// one stable-for-this-observation index per element, grouped by supported operation.
/// Choice documents a 255-option ceiling per question, so both the whole element set
/// and any single operation's target group are capped here.
/// </summary>
internal static class JevActionSpace
{
    private const int MaxChoiceOptions = 255;

    internal sealed record Built(
        IReadOnlyDictionary<string, string> IndexById,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JevElement>> TargetsByOperation);

    public static Built Build(IReadOnlyList<JevElement> elements)
    {
        if (elements.Count > MaxChoiceOptions)
            throw new JevValidationException($"Jev Choice supports at most {MaxChoiceOptions} options; filter targets locally first.");

        var indexById = new Dictionary<string, string>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            var id = elements[i].Id;
            if (!indexById.TryAdd(id, (i + 1).ToString()))
                throw new JevValidationException($"Duplicate element id '{id}' supplied to Jev.");
        }

        var targets = new Dictionary<string, Dictionary<string, JevElement>>();
        foreach (var element in elements)
        {
            var index = indexById[element.Id];
            foreach (var operation in element.Operations)
            {
                if (!targets.TryGetValue(operation, out var group))
                {
                    group = [];
                    targets[operation] = group;
                }
                group[index] = element;
            }
        }

        foreach (var (operation, group) in targets)
        {
            if (group.Count > MaxChoiceOptions)
                throw new JevValidationException($"Operation '{operation}' has more than {MaxChoiceOptions} candidate targets; filter locally first.");
        }

        return new Built(
            indexById,
            targets.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, JevElement>)kv.Value));
    }
}
