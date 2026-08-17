using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KeitaToolbox;

internal readonly record struct AggroAvoidanceZone(Vector3 Center, float Radius);

internal static class AggroAvoidancePolicy
{
    private const float NumericalEpsilon = 0.05f;
    private const float ArcClearance = 0.75f;
    private const float ArcStepRadians = MathF.PI / 8f;
    private const int MaximumDetours = 24;
    private const int MaximumPathPoints = 512;

    internal static bool TryBuild(
        IReadOnlyList<Vector3> sourcePath,
        IReadOnlyList<AggroAvoidanceZone> zones,
        float verticalTolerance,
        Func<Vector3, Vector3?> projector,
        out List<Vector3> safePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(projector);

        safePath = RemoveDuplicatePoints(sourcePath);
        if (safePath.Count < 2 || zones.Count == 0)
            return safePath.Count >= 2;

        var relevantZones = GetRelevantZones(zones, safePath[^1]);
        var originalPointCount = safePath.Count;
        safePath = safePath
                  .Where((point, index) =>
                      index == 0 ||
                      index == originalPointCount - 1 ||
                      !relevantZones.Any(zone => DistanceXZ(point, zone.Center) < zone.Radius + ArcClearance))
                  .ToList();

        for (var attempt = 0; attempt < MaximumDetours; attempt++)
        {
            if (!TryFindFirstBlockedSegment(
                    safePath,
                    relevantZones,
                    verticalTolerance,
                    out var segmentIndex,
                    out var blockedZone))
                return safePath.Count <= MaximumPathPoints;

            if (!TryCreateDetour(
                    safePath[segmentIndex],
                    safePath[segmentIndex + 1],
                    blockedZone,
                    relevantZones,
                    verticalTolerance,
                    projector,
                    out var detour))
                return false;

            safePath.InsertRange(segmentIndex + 1, detour);
            safePath = RemoveDuplicatePoints(safePath);
            if (safePath.Count > MaximumPathPoints)
                return false;
        }

        return IsPathClear(safePath, relevantZones, verticalTolerance);
    }

    internal static bool IsPathClear(
        IReadOnlyList<Vector3> path,
        IReadOnlyList<AggroAvoidanceZone> zones,
        float verticalTolerance) =>
        !TryFindFirstBlockedSegment(path, zones, verticalTolerance, out _, out _);

    private static AggroAvoidanceZone[] GetRelevantZones(
        IReadOnlyList<AggroAvoidanceZone> zones,
        Vector3 destination) =>
        zones.Where(zone => !ContainsXZ(zone, destination, NumericalEpsilon)).ToArray();

    private static bool TryFindFirstBlockedSegment(
        IReadOnlyList<Vector3> path,
        IReadOnlyList<AggroAvoidanceZone> zones,
        float verticalTolerance,
        out int segmentIndex,
        out AggroAvoidanceZone blockedZone)
    {
        for (var i = 0; i + 1 < path.Count; i++)
        {
            foreach (var zone in zones)
            {
                if (!SegmentEntersZone(path[i], path[i + 1], zone, verticalTolerance))
                    continue;

                segmentIndex = i;
                blockedZone = zone;
                return true;
            }
        }

        segmentIndex = -1;
        blockedZone = default;
        return false;
    }

    private static bool SegmentEntersZone(
        Vector3 start,
        Vector3 end,
        AggroAvoidanceZone zone,
        float verticalTolerance)
    {
        if (!IsVerticallyRelevant(start, end, zone.Center.Y, verticalTolerance))
            return false;

        var radius = Math.Max(0.1f, zone.Radius);
        var startDistance = DistanceXZ(start, zone.Center);
        var segmentDistance = DistanceToSegmentXZ(zone.Center, start, end);
        if (startDistance < radius - NumericalEpsilon)
            return segmentDistance + NumericalEpsilon < startDistance;

        return segmentDistance < radius - NumericalEpsilon;
    }

    private static bool TryCreateDetour(
        Vector3 start,
        Vector3 end,
        AggroAvoidanceZone blockedZone,
        IReadOnlyList<AggroAvoidanceZone> allZones,
        float verticalTolerance,
        Func<Vector3, Vector3?> projector,
        out List<Vector3> detour)
    {
        detour = [];
        var startDistance = DistanceXZ(start, blockedZone.Center);
        if (startDistance < blockedZone.Radius - NumericalEpsilon)
        {
            var exitRadius = blockedZone.Radius + ArcClearance;
            var exitDirection = NormalizeXZ(start - blockedZone.Center);
            if (exitDirection == Vector3.Zero)
            {
                var travelDirection = NormalizeXZ(end - start);
                exitDirection = travelDirection == Vector3.Zero
                                    ? Vector3.UnitX
                                    : new Vector3(-travelDirection.Z, 0f, travelDirection.X);
            }

            var exitPoint = new Vector3(
                blockedZone.Center.X + (exitDirection.X * exitRadius),
                start.Y,
                blockedZone.Center.Z + (exitDirection.Z * exitRadius));
            var projectedExit = projector(exitPoint);
            if (projectedExit is not { } projected || ContainsXZ(blockedZone, projected, NumericalEpsilon))
                return false;

            detour.Add(projected);
            return true;
        }

        var endDistance = DistanceXZ(end, blockedZone.Center);
        if (endDistance <= blockedZone.Radius + NumericalEpsilon)
            return false;

        var arcRadius = Math.Max(
            blockedZone.Radius + 0.15f,
            Math.Min(
                blockedZone.Radius + ArcClearance,
                Math.Min(startDistance, endDistance) - 0.1f));
        var startAngles = GetTangentAngles(start, blockedZone.Center, arcRadius);
        var endAngles = GetTangentAngles(end, blockedZone.Center, arcRadius);
        List<Vector3>? bestDetour = null;
        var bestLength = float.MaxValue;

        foreach (var startAngle in startAngles)
        foreach (var endAngle in endAngles)
        foreach (var direction in new[] { -1f, 1f })
        {
            var angleDelta = DirectedAngleDelta(startAngle, endAngle, direction);
            var stepCount = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(angleDelta) / ArcStepRadians));
            var candidate = new List<Vector3>(stepCount + 1);
            var fullyProjected = true;

            for (var step = 0; step <= stepCount; step++)
            {
                var progress = (float)step / stepCount;
                var angle = startAngle + (angleDelta * progress);
                var point = new Vector3(
                    blockedZone.Center.X + (MathF.Cos(angle) * arcRadius),
                    start.Y + ((end.Y - start.Y) * progress),
                    blockedZone.Center.Z + (MathF.Sin(angle) * arcRadius));
                var projectedPoint = projector(point);
                if (projectedPoint is not { } projected ||
                    ContainsXZ(blockedZone, projected, NumericalEpsilon))
                {
                    fullyProjected = false;
                    break;
                }

                candidate.Add(projected);
            }

            if (!fullyProjected)
                continue;

            var candidatePath = new List<Vector3>(candidate.Count + 2) { start };
            candidatePath.AddRange(candidate);
            candidatePath.Add(end);
            if (!IsPathClear(candidatePath, allZones, verticalTolerance))
                continue;

            var length = CalculateLength(candidatePath);
            if (length >= bestLength)
                continue;

            bestLength = length;
            bestDetour = candidate;
        }

        if (bestDetour == null)
            return false;

        detour = bestDetour;
        return true;
    }

    private static float[] GetTangentAngles(Vector3 point, Vector3 center, float radius)
    {
        var offset = point - center;
        var distance = Math.Max(radius + NumericalEpsilon, DistanceXZ(point, center));
        var baseAngle = MathF.Atan2(offset.Z, offset.X);
        var tangentOffset = MathF.Acos(Math.Clamp(radius / distance, -1f, 1f));
        return [baseAngle - tangentOffset, baseAngle + tangentOffset];
    }

    private static float DirectedAngleDelta(float start, float end, float direction)
    {
        var delta = NormalizeAngle(end - start);
        if (direction > 0f && delta < 0f)
            delta += MathF.PI * 2f;
        else if (direction < 0f && delta > 0f)
            delta -= MathF.PI * 2f;
        return delta;
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle <= -MathF.PI)
            angle += MathF.PI * 2f;
        while (angle > MathF.PI)
            angle -= MathF.PI * 2f;
        return angle;
    }

    private static bool IsVerticallyRelevant(Vector3 start, Vector3 end, float y, float tolerance)
    {
        var minimum = Math.Min(start.Y, end.Y) - Math.Max(0f, tolerance);
        var maximum = Math.Max(start.Y, end.Y) + Math.Max(0f, tolerance);
        return y >= minimum && y <= maximum;
    }

    private static bool ContainsXZ(AggroAvoidanceZone zone, Vector3 point, float epsilon) =>
        DistanceXZ(point, zone.Center) < zone.Radius - epsilon;

    private static float DistanceToSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
    {
        var deltaX = end.X - start.X;
        var deltaZ = end.Z - start.Z;
        var lengthSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
        if (lengthSquared <= float.Epsilon)
            return DistanceXZ(point, start);

        var progress = (((point.X - start.X) * deltaX) + ((point.Z - start.Z) * deltaZ)) / lengthSquared;
        progress = Math.Clamp(progress, 0f, 1f);
        var closestX = start.X + (deltaX * progress);
        var closestZ = start.Z + (deltaZ * progress);
        var distanceX = point.X - closestX;
        var distanceZ = point.Z - closestZ;
        return MathF.Sqrt((distanceX * distanceX) + (distanceZ * distanceZ));
    }

    private static float DistanceXZ(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static Vector3 NormalizeXZ(Vector3 value)
    {
        var length = MathF.Sqrt((value.X * value.X) + (value.Z * value.Z));
        return length <= float.Epsilon
                   ? Vector3.Zero
                   : new Vector3(value.X / length, 0f, value.Z / length);
    }

    private static float CalculateLength(IReadOnlyList<Vector3> path)
    {
        var length = 0f;
        for (var i = 1; i < path.Count; i++)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }

    private static List<Vector3> RemoveDuplicatePoints(IReadOnlyList<Vector3> path)
    {
        var result = new List<Vector3>(path.Count);
        foreach (var point in path)
        {
            if (result.Count != 0 && Vector3.DistanceSquared(result[^1], point) <= 0.01f)
                continue;
            result.Add(point);
        }

        return result;
    }
}
