using System;
using System.Collections.Generic;

namespace KeitaToolbox;

internal sealed class DeferredScheduler
{
    private readonly List<ScheduledAction> actions = [];

    public void Schedule(string group, int delayMs, Action action)
    {
        actions.Add(new ScheduledAction(group, Environment.TickCount64 + Math.Max(0, delayMs), action));
    }

    public void Cancel(string group) => actions.RemoveAll(item => item.Group == group);

    public void Clear() => actions.Clear();

    public void Update()
    {
        var now = Environment.TickCount64;
        for (var index = actions.Count - 1; index >= 0; index--)
        {
            var item = actions[index];
            if (item.ExecuteAt > now)
                continue;

            actions.RemoveAt(index);
            item.Action();
        }
    }

    private readonly record struct ScheduledAction(string Group, long ExecuteAt, Action Action);
}
