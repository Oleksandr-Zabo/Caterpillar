using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    // Simple trainer that runs multiple training runs and selects the best Q-table
    public class QLearningTrainer
    {
        public async Task<string> RunAndSaveBestAsync(GameBoard board, int stepsLimit, int runs = 6, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            string bestPath = null;
            double bestScore = double.NegativeInfinity;

            var stats = new List<object>();

            for (int i = 0; i < runs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report($"Trainer: run {i + 1}/{runs}");
                    var agent = new QLearningAgent();
                    var sw = Stopwatch.StartNew();
                    await agent.PrepareAsync(board, stepsLimit, progress ?? new Progress<string>(s => { }), cancellationToken);
                    sw.Stop();

                    // evaluate agent on multiple episodes and average score + steps
                    int evalEpisodes = 6;
                    double totalApples = 0;
                    double totalSteps = 0;
                    for (int e = 0; e < evalEpisodes; e++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var env = new GameBoard(board);
                        agent.ResetRuntime();
                        int apples = 0;
                        int steps = Math.Clamp(board.Rows * board.Cols * 2, 40, 600);
                        var lastPos = env.LastPosition;
                        int taken = 0;
                        var visited = new HashSet<(int x, int y)> { env.CaterpillarHead };
                        while (steps-- > 0 && env.ApplesRemaining > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var next = agent.GetNextMove(env, env.CaterpillarHead, lastPos);
                            if (!env.IsValidMove(next)) break;
                            if (env.MoveCaterpillarTo(next)) apples++;
                            lastPos = env.LastPosition;
                            taken++;
                            if (!visited.Add(env.CaterpillarHead) && taken > board.Rows * board.Cols)
                                break;
                            if (env.ApplesRemaining == 0) break;
                        }
                        totalApples += apples;
                        totalSteps += taken;
                    }
                    double avgApples = totalApples / evalEpisodes;
                    double avgSteps = totalSteps / evalEpisodes;

                    // combine apples and steps into a score (apples have priority)
                    double score = avgApples + 0.01 * (avgSteps / Math.Max(1, board.Rows * board.Cols * 2));

                    progress?.Report($"Trainer: run {i + 1} avg apples {avgApples:F2}, avg steps {avgSteps:F1} (time {sw.ElapsedMilliseconds} ms)");

                    stats.Add(new { run = i + 1, avgApples, avgSteps, time = sw.ElapsedMilliseconds });

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPath = $"best_qtable_v3_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
                        agent.SaveToFile(bestPath);
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Trainer: run {i + 1} failed: {ex.Message}");
                }
            }

            if (bestPath == null)
            {
                // fallback: train one agent and save
                var fallback = new QLearningAgent();
                await fallback.PrepareAsync(board, stepsLimit, progress ?? new Progress<string>(s => { }), cancellationToken);
                bestPath = $"best_qtable_v3_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
                fallback.SaveToFile(bestPath);
            }

            // save stats to a JSON file for later inspection
            try
            {
                var statsFile = $"training_stats_v2_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
                var txt = System.Text.Json.JsonSerializer.Serialize(stats);
                File.WriteAllText(statsFile, txt);
            }
            catch { }

            return bestPath;
        }
    }
}
