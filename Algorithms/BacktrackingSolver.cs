using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Caterpillar.DataStructures;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    // Simple backtracking / greedy solver that tries to eat nearby apples using DFS with custom stack
    public class BacktrackingSolver : IAlgorithm
    {
        private const int LocalAppleLookahead = 3;
        private const int ComponentDistancePenalty = 4;
        public string Name => "Backtracking";
        private Queue<(int x, int y)> _plan = new();
        private int _plannedApples = 0;
        private readonly HashSet<(int x, int y)> _runtimeVisited = new();

        public int PlannedApples => _plannedApples;

        public Task PrepareAsync(GameBoard board, int stepsLimit, System.IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            // Movement is selected online so every step can use the current three-move apple horizon.
            _plan.Clear();
            _runtimeVisited.Clear();
            cancellationToken.ThrowIfCancellationRequested();
            _plannedApples = 0;
            progress?.Report("Backtracking: using three-move local apple planning.");
            return Task.CompletedTask;
        }

        public (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            _runtimeVisited.Add(currentHead);
            if (_plan.Count > 0)
            {
                // return next valid planned move; skip any that became invalid
                while (_plan.Count > 0)
                {
                    var p = _plan.Dequeue();
                    if (p.x < 0 || p.x >= board.Rows || p.y < 0 || p.y >= board.Cols) continue;
                    if (!board.IsValidMove(p)) continue;
                    return p;
                }
            }

            var neighbors = GetNeighbors(currentHead, board)
                .Where(board.IsValidMove)
                .ToList();

            var adjacentApple = neighbors
                .Where(board.IsFruit)
                .OrderBy(n => _runtimeVisited.Contains(n))
                .FirstOrDefault();
            if (board.IsFruit(adjacentApple))
                return adjacentApple;

            var plannedMove = AppleRoutePlanner.FindBestMove(board, currentHead, _runtimeVisited);
            if (plannedMove.HasValue)
                return plannedMove.Value;

            var best = neighbors
                .OrderBy(n => _runtimeVisited.Contains(n))
                .ThenBy(n => n.x)
                .ThenBy(n => n.y)
                .FirstOrDefault();
            if (neighbors.Contains(best) && board.IsValidMove(best))
                return best;

            return currentHead;
        }

        private (int x, int y)? FindClosestAppleMove(
            GameBoard board,
            (int x, int y) start,
            List<(int x, int y)> neighbors)
        {
            var apples = board.Cells.Cast<GridCell>()
                .Where(cell => cell.Type == CellType.Apple)
                .Select(cell => (cell.X, cell.Y))
                .ToList();
            if (apples.Count == 0)
                return null;

            return neighbors
                .OrderBy(move => apples.Min(apple => Math.Abs(move.Item1 - apple.X) + Math.Abs(move.Item2 - apple.Y)))
                .ThenBy(move => _runtimeVisited.Contains(move))
                .FirstOrDefault();
        }

        private (int x, int y)? FindBestAppleComponentMove(GameBoard board, (int x, int y) start)
        {
            var components = FindAppleComponents(board);
            if (components.Count == 0)
                return null;

            var selected = components
                .OrderByDescending(component => component.Count * 100 -
                    ManhattanDistanceToComponent(start, component) * ComponentDistancePenalty)
                .ThenBy(component => ManhattanDistanceToComponent(start, component))
                .First();

            return FindPathMoveToTargets(board, start, selected);
        }

        private List<List<(int x, int y)>> FindAppleComponents(GameBoard board)
        {
            var components = new List<List<(int x, int y)>>();
            var visited = new HashSet<(int x, int y)>();
            for (var x = 0; x < board.Rows; x++)
            {
                for (var y = 0; y < board.Cols; y++)
                {
                    var start = (x, y);
                    if (!board.IsFruit(start) || !visited.Add(start)) continue;
                    var component = new List<(int x, int y)>();
                    var queue = new Queue<(int x, int y)>();
                    queue.Enqueue(start);
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        component.Add(current);
                        foreach (var next in GetNeighbors(current, board))
                            if (board.IsFruit(next) && visited.Add(next)) queue.Enqueue(next);
                    }
                    components.Add(component);
                }
            }
            return components;
        }

        private static int ManhattanDistanceToComponent((int x, int y) start, List<(int x, int y)> component) =>
            component.Min(cell => Math.Abs(cell.x - start.x) + Math.Abs(cell.y - start.y));

        private (int x, int y)? FindPathMoveToTargets(
            GameBoard board,
            (int x, int y) start,
            List<(int x, int y)> targets)
        {
            var targetSet = targets.ToHashSet();
            var queue = new Queue<((int x, int y) position, (int x, int y) first)>();
            var visited = new HashSet<(int x, int y)> { start };
            queue.Enqueue((start, start));
            while (queue.Count > 0)
            {
                var (current, first) = queue.Dequeue();
                foreach (var next in GetNeighbors(current, board))
                {
                    if (!board.IsValidPosition(next) || !visited.Add(next)) continue;
                    var firstStep = current == start ? next : first;
                    if (targetSet.Contains(next)) return firstStep;
                    queue.Enqueue((next, firstStep));
                }
            }
            return null;
        }

        private (int x, int y)? FindLocalFruitMove(GameBoard board, (int x, int y) start)
        {
            var queue = new Queue<((int x, int y) position, (int x, int y) first, int distance)>();
            var visited = new HashSet<(int x, int y)> { start };
            queue.Enqueue((start, start, 0));
            var candidates = new List<((int x, int y) apple, (int x, int y) first, int distance)>();

            while (queue.Count > 0)
            {
                var (current, first, distance) = queue.Dequeue();
                if (distance >= LocalAppleLookahead)
                    continue;

                foreach (var next in GetNeighbors(current, board))
                {
                    if (!board.IsValidPosition(next) || !visited.Add(next))
                        continue;

                    var firstStep = current == start ? next : first;
                    var nextDistance = distance + 1;
                    if (board.IsFruit(next))
                        candidates.Add((next, firstStep, nextDistance));
                    queue.Enqueue((next, firstStep, nextDistance));
                }
            }

            return candidates
                .OrderBy(candidate => candidate.distance)
                .ThenBy(candidate => candidate.apple.x)
                .ThenBy(candidate => candidate.apple.y)
                .Select(candidate => ((int x, int y)?)candidate.first)
                .FirstOrDefault();
        }


        private (int x, int y)? FindNextFruitMove(GameBoard board, (int x, int y) start)
        {
            var queue = new Queue<(int x, int y)>();
            var visited = new HashSet<(int x, int y)> { start };
            var parent = new Dictionary<(int x, int y), (int x, int y)>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var next in GetNeighbors(current, board))
                {
                    if (!IsFree(board, next) || !visited.Add(next))
                        continue;

                    parent[next] = current;
                    if (board.IsFruit(next))
                    {
                        var step = next;
                        while (parent[step] != start)
                            step = parent[step];
                        return step;
                    }
                    queue.Enqueue(next);
                }
            }

            return null;
        }

        private static bool IsFree(GameBoard board, (int x, int y) position)
        {
            return position.x >= 0 && position.x < board.Rows &&
                   position.y >= 0 && position.y < board.Cols &&
                   true;
        }

        private int CountFreeArea(GameBoard board, (int x, int y) start)
        {
            var queue = new Queue<(int x, int y)>();
            var visited = new HashSet<(int x, int y)> { start };
            queue.Enqueue(start);
            var count = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                count++;
                foreach (var next in GetNeighbors(current, board))
                    if (IsFree(board, next) && visited.Add(next))
                        queue.Enqueue(next);
            }
            return count;
        }

        private IEnumerable<(int x, int y)> GetNeighbors((int x, int y) p, GameBoard board)
        {
            yield return (p.x - 1, p.y);
            yield return (p.x + 1, p.y);
            yield return (p.x, p.y - 1);
            yield return (p.x, p.y + 1);
        }
    }
}
