using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    // Simple trainer that runs multiple training runs and selects the best Q-table
    public class QLearningTrainer
    {
        public async Task<string> RunAndSaveBestAsync(GameBoard board, int stepsLimit, int runs = 10, IProgress<string>? progress = null)
        {
            string bestPath = null;
            double bestScore = double.NegativeInfinity;

            for (int i = 0; i < runs; i++)
            {
                try
                {
                    progress?.Report($"Trainer: run {i + 1}/{runs}");
                    var agent = new QLearningAgent();
                    var sw = Stopwatch.StartNew();
                    await agent.PrepareAsync(board, stepsLimit, progress ?? new Progress<string>(s => { }));
                    sw.Stop();

                    // evaluate agent on multiple episodes and average score
                    int evalEpisodes = 3;
                    double totalApples = 0;
                    for (int e = 0; e < evalEpisodes; e++)
                    {
                        var env = new GameBoard(board);
                        int apples = 0;
                        int steps = stepsLimit;
                        var lastPos = env.LastPosition;
                        while (steps-- > 0)
                        {
                            var next = agent.GetNextMove(env, env.CaterpillarHead, lastPos);
                            if (!env.IsValidMove(next)) break;
                            if (env.MoveCaterpillarTo(next)) apples++;
                            lastPos = env.LastPosition;
                            if (env.ApplesRemaining == 0) break;
                        }
                        totalApples += apples;
                    }
                    double avgApples = totalApples / evalEpisodes;

                    progress?.Report($"Trainer: run {i + 1} avg apples {avgApples:F2} (time {sw.ElapsedMilliseconds} ms)");

                    if (avgApples > bestScore)
                    {
                        bestScore = avgApples;
                        bestPath = $"best_qtable_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
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
                await fallback.PrepareAsync(board, stepsLimit, progress ?? new Progress<string>(s => { }));
                bestPath = $"best_qtable_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
                fallback.SaveToFile(bestPath);
            }

            return bestPath;
        }
    }
}
