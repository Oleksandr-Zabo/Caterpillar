using System;
using System.Collections.Generic;
using System.Linq;
using Caterpillar.Models;

namespace Caterpillar.Algorithms;

internal static class GraphRoutePlanner
{
    private const double AppleOnPathReward = 10000.0;
    private const double ClusterReward = 120.0;
    private const double DistancePenalty = 2.0;
    private const double RevisitPenalty = 8.0;
    private const int ClusterRadius = 5;
    private const int LocalTargetDistance = 16;

    internal sealed record RoutePlan(
        IReadOnlyList<(int x, int y)> Moves,
        int ApplesCollected,
        double Score);

    public static RoutePlan? FindBestRoute(
        GameBoard board,
        (int x, int y) start,
        int remainingSteps,
        (int x, int y)? previous,
        IReadOnlySet<(int x, int y)>? visited)
    {
        var legalMoves = Neighbors(start)
            .Where(board.IsValidPosition)
            .ToList();
        if (legalMoves.Count == 0)
            return null;

        var apples = board.Cells
            .Cast<GridCell>()
            .Where(cell => cell.Type == CellType.Apple || cell.Type == CellType.Grape)
            .Select(cell => (x: cell.X, y: cell.Y))
            .ToHashSet();
        if (apples.Count == 0)
            return new RoutePlan(
                new[] { SelectFallback(legalMoves, previous, visited) },
                0,
                0.0);

        var immediateApples = legalMoves
            .Where(apples.Contains)
            .OrderByDescending(move => CountNearbyApples(board, move))
            .ToList();
        if (immediateApples.Count > 0)
        {
            return new RoutePlan(
                new[] { immediateApples[0] },
                1,
                AppleOnPathReward + CountNearbyApples(board, immediateApples[0]) * ClusterReward);
        }

        var distances = new Dictionary<(int x, int y), int> { [start] = 0 };
        var parents = new Dictionary<(int x, int y), (int x, int y)>();
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in Neighbors(current))
            {
                if (!board.IsValidPosition(next) || distances.ContainsKey(next))
                    continue;

                distances[next] = distances[current] + 1;
                parents[next] = current;
                queue.Enqueue(next);
            }
        }

        var effectiveLimit = Math.Max(1, remainingSteps);
        var nearestDistance = apples
            .Where(distances.ContainsKey)
            .Select(apple => distances[apple])
            .DefaultIfEmpty(effectiveLimit)
            .Min();
        var candidateDistanceLimit = Math.Min(
            effectiveLimit,
            Math.Min(LocalTargetDistance, nearestDistance + 2));
        RoutePlan? best = null;

        foreach (var apple in apples)
        {
            if (!distances.TryGetValue(apple, out var distance) ||
                distance > effectiveLimit ||
                distance > candidateDistanceLimit)
                continue;

            var path = BuildPath(start, apple, parents);
            var simulation = Simulate(board, path);
            var clusterCount = CountNearbyApples(board, apple);
            var score = simulation.ApplesCollected * AppleOnPathReward +
                        clusterCount * ClusterReward -
                        distance * DistancePenalty;
            var firstMove = path[0];

            if (previous.HasValue && firstMove == previous.Value)
                score -= 20.0;
            if (visited?.Contains(firstMove) == true)
                score -= RevisitPenalty;

            var candidate = new RoutePlan(path, simulation.ApplesCollected, score);
            if (best is null || candidate.Score > best.Score)
                best = candidate;
        }

        return best ?? new RoutePlan(
            new[] { SelectFallback(legalMoves, previous, visited) },
            0,
            double.NegativeInfinity);
    }

    public static (int x, int y)? FindBestMove(
        GameBoard board,
        (int x, int y) start,
        int remainingSteps,
        (int x, int y)? previous,
        IReadOnlySet<(int x, int y)>? visited)
    {
        return FindBestRoute(board, start, remainingSteps, previous, visited)
            ?.Moves.FirstOrDefault();
    }

    private static List<(int x, int y)> BuildPath(
        (int x, int y) start,
        (int x, int y) target,
        Dictionary<(int x, int y), (int x, int y)> parents)
    {
        var reversed = new List<(int x, int y)>();
        var current = target;
        while (current != start)
        {
            reversed.Add(current);
            current = parents[current];
        }

        reversed.Reverse();
        return reversed;
    }

    private static (int ApplesCollected, int Steps) Simulate(
        GameBoard source,
        IReadOnlyList<(int x, int y)> path)
    {
        var board = new GameBoard(source);
        var collected = 0;
        foreach (var move in path)
        {
            if (!board.TryMoveCaterpillarTo(move, out var ate))
                break;
            if (ate)
                collected++;
        }

        return (collected, path.Count);
    }

    private static int CountNearbyApples(GameBoard board, (int x, int y) center)
    {
        var count = 0;
        for (var x = Math.Max(0, center.x - ClusterRadius);
             x <= Math.Min(board.Rows - 1, center.x + ClusterRadius);
             x++)
        {
            for (var y = Math.Max(0, center.y - ClusterRadius);
                 y <= Math.Min(board.Cols - 1, center.y + ClusterRadius);
                 y++)
            {
                if (board.IsFruit((x, y)))
                    count++;
            }
        }

        return count;
    }

    private static (int x, int y) SelectFallback(
        List<(int x, int y)> moves,
        (int x, int y)? previous,
        IReadOnlySet<(int x, int y)>? visited) =>
        moves.OrderBy(move => previous.HasValue && move == previous.Value)
             .ThenBy(move => visited?.Contains(move) ?? false)
             .First();

    private static IEnumerable<(int x, int y)> Neighbors((int x, int y) position)
    {
        yield return (position.x - 1, position.y);
        yield return (position.x + 1, position.y);
        yield return (position.x, position.y - 1);
        yield return (position.x, position.y + 1);
    }
}
