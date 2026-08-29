using System;

namespace KeitaToolbox;

internal sealed class OutOnALimbSearchPolicy
{
    private readonly int[] positions = new int[100];

    public void Reset() => Array.Clear(positions);

    public byte SelectNext(uint currentPosition, int result)
    {
        Record(currentPosition, result);

        var previousBoundary = 0;
        var bestStart = -1;
        var bestLength = 0;
        for (var index = 0; index < positions.Length; index++)
        {
            if (positions[index] != 0)
            {
                previousBoundary = index;
                continue;
            }

            var length = index - previousBoundary;
            if (length > bestLength)
            {
                bestStart = previousBoundary;
                bestLength = length;
            }
        }

        var next = bestStart + bestLength / 2;
        if (next == currentPosition)
            next++;

        return (byte)Math.Clamp(next, 0, 99);
    }

    private void Record(uint position, int result)
    {
        for (var index = 0; index < positions.Length; index++)
        {
            var distance = Math.Abs((long)position - index);
            switch (result)
            {
                case 1:
                    if (distance < 20 && positions[index] == 0)
                        positions[index] = -1;
                    break;
                case 2:
                    if (distance <= 5)
                        positions[index] = 1;
                    else if (distance > 25 && positions[index] == 0)
                        positions[index] = -1;
                    break;
                case 3:
                    if (distance == 0)
                        positions[index] = 2;
                    else if (distance > 5 && positions[index] == 0)
                        positions[index] = -1;
                    break;
            }
        }
    }
}
