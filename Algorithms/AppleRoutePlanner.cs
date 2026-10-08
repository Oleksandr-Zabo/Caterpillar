using System;
using System.Collections.Generic;
using System.Linq;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    public static class AppleRoutePlanner
    {
        private const int SearchDepth = 20;
        private const int EndgameSearchDepth = 14;
        private const int BeamWidth = 40;
        private const int EndgameBeamWidth = 24;
        private const int EndgameAppleThreshold = 140;
        private const double AppleReward = 10000.0;
        private const double FutureAppleReward = 70.0;
        private const double TravelPenalty = 1.0;
        private const double ReversePenalty = 35.0;
        private const double RevisitPenalty = 0.25;
        private const double FrontierReward = 180.0;

        public static (int x, int y)? FindBestMove(
            GameBoard board,
            (int x, int y) head,
            IReadOnlySet<(int x, int y)>? visited = null,
            (int x, int y)? previous = null,
            bool preferGlobalFrontier = true)
        {
            var legalMoves = Neighbors(head).Where(board.IsValidPosition).ToList();
            if (legalMoves.Count == 0)
                return null;

            var apples = board.Cells.Cast<GridCell>()
                .Where(cell => cell.Type == CellType.Apple || cell.Type == CellType.Grape)
                .Select(cell => (x: cell.X, y: cell.Y))
                .ToHashSet();
            if (apples.Count == 0)
                return SelectFallback(legalMoves, previous, visited);

            var searchDepth = apples.Count <= EndgameAppleThreshold ? EndgameSearchDepth : SearchDepth;
            var beamWidth = apples.Count <= EndgameAppleThreshold ? EndgameBeamWidth : BeamWidth;
            var frontier = new List<SearchNode>();
            foreach (var move in legalMoves)
            {
                var remaining = new HashSet<(int x, int y)>(apples);
                var collected = remaining.Remove(move) ? 1 : 0;
                frontier.Add(new SearchNode(
                    move,
                    move,
                    head,
                    1,
                    collected,
                    remaining));
            }

            SearchNode? best = null;
            for (var depth = 1; depth <= searchDepth && frontier.Count > 0; depth++)
            {
                foreach (var node in frontier)
                {
                    if (best is null || Evaluate(node, apples, visited, previous, preferGlobalFrontier) >
                        Evaluate(best, apples, visited, previous, preferGlobalFrontier))
                        best = node;
                }

                var nextFrontier = new List<SearchNode>();
                foreach (var node in frontier)
                {
                    foreach (var next in Neighbors(node.Position))
                    {
                        if (!board.IsValidPosition(next))
                            continue;

                        var remaining = new HashSet<(int x, int y)>(node.Remaining);
                        var collected = node.Collected;
                        if (remaining.Remove(next))
                            collected++;

                        nextFrontier.Add(new SearchNode(
                            next,
                            node.FirstMove,
                            node.Position,
                            node.Depth + 1,
                            collected,
                            remaining));
                    }
                }

                frontier = nextFrontier
                    .OrderByDescending(node => Evaluate(node, apples, visited, previous, preferGlobalFrontier))
                    .Take(beamWidth)
                    .ToList();
            }

            return best?.FirstMove ?? SelectFallback(legalMoves, previous, visited);
        }

        private static double Evaluate(
            SearchNode node,
            HashSet<(int x, int y)> allApples,
            IReadOnlySet<(int x, int y)>? visited,
            (int x, int y)? previous,
            bool preferGlobalFrontier)
        {
            var futureDensity = Neighbors(node.Position).Count(node.Remaining.Contains);
            var score = node.Collected * AppleReward +
                        futureDensity * FutureAppleReward -
                        node.Depth * TravelPenalty;

            if (preferGlobalFrontier)
            {
                var frontier = node.Remaining.Count(apple =>
                    Math.Abs(apple.x - node.Position.x) <= 3 &&
                    Math.Abs(apple.y - node.Position.y) <= 3);
                score += frontier * FrontierReward;
            }

            if (previous.HasValue && node.FirstMove == previous.Value)
                score -= ReversePenalty;
            if (visited?.Contains(node.FirstMove) == true)
                score -= RevisitPenalty;
            return score;
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

        private sealed record SearchNode(
            (int x, int y) Position,
            (int x, int y) FirstMove,
            (int x, int y) PreviousPosition,
            int Depth,
            int Collected,
            HashSet<(int x, int y)> Remaining);
    }
}