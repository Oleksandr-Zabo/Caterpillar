using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    public class BeamSearchSolver : IAlgorithm
    {
        public string Name => "Beam Search";
        private readonly HashSet<(int x, int y)> _runtimeVisited = new();
        private readonly Queue<(int x, int y)> _committedRoute = new();
        private int _stepsLimit;
        private int _stepsTaken;

        public async Task PrepareAsync(GameBoard board, int stepsLimit, System.IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            _runtimeVisited.Clear();
            _committedRoute.Clear();
            _stepsLimit = Math.Max(1, stepsLimit);
            _stepsTaken = 0;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(
                () => AppleRoutePlanner.FindBestMove(
                    board,
                    board.CaterpillarHead,
                    visited: null,
                    previous: null,
                    preferGlobalFrontier: true),
                cancellationToken);
            progress?.Report("Beam Search: prepared density lookahead route.");
        }

        public (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            _runtimeVisited.Add(currentHead);

            var beamMove = AppleRoutePlanner.FindBestMove(
                board,
                currentHead,
                _runtimeVisited,
                lastPos,
                preferGlobalFrontier: true);
            if (beamMove.HasValue && board.IsValidMove(beamMove.Value))
            {
                _committedRoute.Clear();
                _stepsTaken++;
                return beamMove.Value;
            }

            if (_committedRoute.Count > 0 &&
                GetNeighbors(currentHead).Any(board.IsFruit) &&
                !board.IsFruit(_committedRoute.Peek()))
            {
                _committedRoute.Clear();
            }

            if (_committedRoute.Count == 0 ||
                !board.IsValidMove(_committedRoute.Peek()))
            {
                _committedRoute.Clear();
                var route = GraphRoutePlanner.FindBestRoute(
                    board,
                    currentHead,
                    Math.Max(0, _stepsLimit - _stepsTaken),
                    lastPos,
                    _runtimeVisited);
                if (route is not null)
                {
                    foreach (var move in route.Moves.Take(1))
                        _committedRoute.Enqueue(move);
                }
            }

            if (_committedRoute.Count > 0 &&
                board.IsValidMove(_committedRoute.Peek()))
            {
                _stepsTaken++;
                return _committedRoute.Dequeue();
            }

            var neighbors = GetNeighbors(currentHead)
                .Where(board.IsValidMove)
                .ToList();
            var best = neighbors
                .OrderBy(n => _runtimeVisited.Contains(n))
                .ThenBy(n => n.x)
                .ThenBy(n => n.y)
                .FirstOrDefault();
            if (neighbors.Contains(best) && board.IsValidMove(best))
            {
                _stepsTaken++;
                return best;
            }

            return currentHead;
        }

        private static IEnumerable<(int x, int y)> GetNeighbors((int x, int y) position)
        {
            yield return (position.x - 1, position.y);
            yield return (position.x + 1, position.y);
            yield return (position.x, position.y - 1);
            yield return (position.x, position.y + 1);
        }

    }
}
