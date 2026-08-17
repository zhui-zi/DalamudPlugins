using System;
using System.Collections.Generic;

namespace KeitaToolbox;

internal sealed class DeferredScheduler
{
    private readonly List<ScheduledAction> actions = [];
    private readonly HashSet<string> cancelledGroups = new(StringComparer.Ordinal);
    private readonly Action<Exception>? exceptionHandler;
    private bool clearRequested;
    private bool updating;

    internal DeferredScheduler(Action<Exception>? exceptionHandler = null) =>
        this.exceptionHandler = exceptionHandler;

    public void Schedule(string group, int delayMs, Action action)
    {
        actions.Add(new ScheduledAction(group, Environment.TickCount64 + Math.Max(0, delayMs), action));
    }

    public void Cancel(string group)
    {
        actions.RemoveAll(item => item.Group == group);
        if (updating)
            cancelledGroups.Add(group);
    }

    public void Clear()
    {
        actions.Clear();
        if (updating)
            clearRequested = true;
    }

    public void Update()
    {
        var now = Environment.TickCount64;
        var dueActions = new List<ScheduledAction>();
        for (var index = actions.Count - 1; index >= 0; index--)
        {
            var item = actions[index];
            if (item.ExecuteAt > now)
                continue;

            actions.RemoveAt(index);
            dueActions.Add(item);
        }

        updating = true;
        clearRequested = false;
        cancelledGroups.Clear();
        try
        {
            foreach (var item in dueActions)
            {
                if (clearRequested || cancelledGroups.Contains(item.Group))
                    continue;

                try
                {
                    item.Action();
                }
                catch (Exception ex)
                {
                    exceptionHandler?.Invoke(ex);
                }
            }
        }
        finally
        {
            updating = false;
            clearRequested = false;
            cancelledGroups.Clear();
        }
    }

    private readonly record struct ScheduledAction(string Group, long ExecuteAt, Action Action);
}
