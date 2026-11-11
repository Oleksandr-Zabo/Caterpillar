using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Caterpillar.Models;

namespace Caterpillar.Algorithms;

public sealed record AlgorithmEvaluationResult(
    string Algorithm,
    int Rows,
    int Columns,
    int InitialApples,
    int ApplesCollected,
    int StepsTaken,
    int InvalidMoves,
    int Revisits,
    int AdjacentAppleOpportunities,
    int AdjacentAppleMisses);

public static class AlgorithmEvaluator
{
    public static async Task<AlgorithmEvaluationResult> EvaluateAsync(
        IAlgorithm algorithm,
        GameBoard source,
        int stepsLimit,
        CancellationToken cancellationToken = default)
    {
        var board = new GameBoard(source);
        var initialApples = board.ApplesRemaining;
        await algorithm.PrepareAsync(board, stepsLimit, null!, cancellationToken);

        board = new GameBoard(source);
        var visited = new HashSet<(int x, int y)>();
        var applesCollected = 0;
        var invalidMoves = 0;
        var revisits = 0;
        var adjacentAppleOpportunities = 0;
        var adjacentAppleMisses = 0;
        var stepsTaken = 0;

        for (; stepsTaken < stepsLimit && board.ApplesRemaining > 0; stepsTaken++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = board.CaterpillarHead;
            var adjacentApples = Neighbors(current).Any(board.IsFruit);
            if (adjacentApples)
                adjacentAppleOpportunities++;
            var next = algorithm.GetNextMove(board, current, board.LastPosition);

            if (!board.IsValidMove(next))
            {
                invalidMoves++;
                break;
            }

            if (!visited.Add(next))
                revisits++;

            if (adjacentApples && !board.IsFruit(next))
                adjacentAppleMisses++;

            if (board.TryMoveCaterpillarTo(next, out var ate) && ate)
                applesCollected++;
        }

        return new AlgorithmEvaluationResult(
            algorithm.Name,
            board.Rows,
            board.Cols,
            initialApples,
            applesCollected,
            stepsTaken,
            invalidMoves,
            revisits,
            adjacentAppleOpportunities,
            adjacentAppleMisses);
    }

    private static IEnumerable<(int x, int y)> Neighbors((int x, int y) position)
    {
        yield return (position.x - 1, position.y);
        yield return (position.x + 1, position.y);
        yield return (position.x, position.y - 1);
        yield return (position.x, position.y + 1);
    }
}
