using System;
using System.Collections.Generic;
using System.Linq;

namespace KeitaToolbox;

internal readonly record struct PluginSwitchRule(string DisableList, string EnableList);
internal readonly record struct PluginStateChange(string InternalName, bool Enabled);

internal static class PluginSwitchingPolicy
{
    internal static Dictionary<string, bool> BuildDesiredStates(IEnumerable<PluginSwitchRule> rules)
    {
        var desired = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            foreach (var name in ParseList(rule.EnableList))
                desired.TryAdd(name, true);

            // Disabling wins when active rules conflict.
            foreach (var name in ParseList(rule.DisableList))
                desired[name] = false;
        }

        return desired;
    }

    internal static List<string> ParseList(string value) =>
        value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static List<PluginStateChange> PlanChanges(
        IDictionary<string, bool> originalStates,
        IReadOnlyDictionary<string, bool> desiredStates,
        Func<string, bool?> getCurrentState)
    {
        var changes = new List<PluginStateChange>();
        foreach (var (name, originalState) in originalStates.ToArray())
        {
            if (desiredStates.ContainsKey(name))
                continue;

            var currentState = getCurrentState(name);
            if (!currentState.HasValue)
            {
                originalStates.Remove(name);
                continue;
            }

            if (currentState.Value != originalState)
            {
                changes.Add(new PluginStateChange(name, originalState));
                continue;
            }

            originalStates.Remove(name);
        }

        foreach (var (name, desiredState) in desiredStates)
        {
            var currentState = getCurrentState(name);
            if (!currentState.HasValue)
                continue;

            originalStates.TryAdd(name, currentState.Value);
            if (currentState.Value != desiredState)
                changes.Add(new PluginStateChange(name, desiredState));
        }

        return changes;
    }
}
