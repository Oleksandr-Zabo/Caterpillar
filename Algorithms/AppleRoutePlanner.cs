using System;
using System.Collections.Generic;
using System.Linq;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    public static class AppleRoutePlanner
    {
        private const int ClusterRadius = 4;
        private const int CandidateLimit = 24;
        private const double DistanceWeight = 12.0;
        private const double ClusterWeight = 22.0;
        private const double AdjacentChainWeight = 35.0;

        public static (int x, int y)? FindBestMove(
            GameBoard board,
            (int x, int y) head,
            IReadOnlySet<(int x, int y)>? visited = null)
        {
            var moves = Neighbors(head)
                .Where(board.IsValidMove)
                .ToList();
            if (moves.Count == 0)
                return null;

            var adjacent = moves
                .Where(board.IsFruit)
                .OrderBy(move => visited?.Contains(move) ?? false)
                .FirstOrDefault();
            if (board.IsFruit(adjacent))
                return adjacent;

            var apples = board.Cells.Cast<GridCell>()
                .Where(cell => cell.Type == CellType.Apple || cell.Type == CellType.Grape)
                .Select(cell => (x: cell.X, y: cell.Y))
                .ToList();
            if (apples.Count == 0)
                return moves.OrderBy(move => visited?.Contains(move) ?? false).First();

            var targets = apples
                .OrderBy(apple => Manhattan(head, apple))
                .Take(CandidateLimit)
                .ToList();

            return moves
                .Select(move => new
                {
                    Move = move,
                    Score = targets.Max(apple => ScoreMove(board, move, apple, apples, visited))
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => visited?.Contains(item.Move) ?? false)
                .ThenBy(item => Manhattan(head, item.Move))
                .First().Move;
        }

        private static double ScoreMove(
            GameBoard board,
            (int x, int y) move,
            (int x, int y) target,
            List<(int x, int y)> apples,
            IReadOnlySet<(int x, int y)>? visited)
        {
            var distance = Manhattan(move, target);
            var cluster = apples.Count(apple => Manhattan(target, apple) <= ClusterRadius);
            var adjacentChain = Neighbors(target).Count(board.IsFruit);
            var revisitPenalty = visited?.Contains(move) == true ? 80.0 : 0.0;
            return cluster * ClusterWeight + adjacentChain * AdjacentChainWeight -
                   distance * DistanceWeight - revisitPenalty;
        }

        private static int Manhattan((int x, int y) a, (int x, int y) b) =>
            Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y);

        private static IEnumerable<(int x, int y)> Neighbors((int x, int y) p)
        {
            yield return (p.x - 1, p.y);
            yield return (p.x + 1, p.y);
            yield return (p.x, p.y - 1);
            yield return (p.x, p.y + 1);
        }
    }
}
