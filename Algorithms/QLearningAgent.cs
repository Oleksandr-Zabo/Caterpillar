using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Caterpillar.DataStructures;
using System.Threading.Tasks;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    // Minimal Q-Learning agent stub using a Dictionary as hash-table for Q-values
    public class QLearningAgent : IAlgorithm
    {
        public string Name => "Q-Learning";
        // Use custom hash table as requested
        private readonly CustomHash<double[]> _q = new(512);
        private readonly Random _rng = new();
        private readonly string[] _actions = new[] { "U", "D", "L", "R" };

        public async Task PrepareAsync(GameBoard board, int stepsLimit, IProgress<string> progress)
        {
            progress?.Report("QLearning: preparing...");

            // Use a simple cache file per board size+applecount to speed training
            string bestFname = $"best_qtable_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
            string fname = $"qtable_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
            // prefer best file if present
            if (File.Exists(bestFname))
            {
                try
                {
                    LoadFromFile(bestFname);
                    progress?.Report("QLearning: loaded best q-table from file.");
                    return;
                }
                catch { }
            }
            if (File.Exists(fname))
            {
                try
                {
                    LoadFromFile(fname);
                    progress?.Report("QLearning: loaded q-table from file.");
                    return;
                }
                catch { }
            }

            // Perform Q-Learning training using episodes on the provided board
            // Increase episodes proportional to board size to obtain stronger policies
            int episodes = Math.Max(5000, board.Rows * board.Cols * 200);
            double alpha = 0.1; // learning rate
            double gamma = 0.9; // discount
            double epsilon = 0.1; // exploration

            // initialize q entries (include apples remaining in state key)
            for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                var key = Key((r, c), board.ApplesRemaining);
                _q.GetOrAdd(key, () => new double[4]);
            }

            // training episodes
            for (int ep = 0; ep < episodes; ep++)
            {
                // clone board to run episode without affecting UI board
                var env = new GameBoard(board);
                var lastPos = env.LastPosition;
                var head = env.CaterpillarHead;

                for (int step = 0; step < stepsLimit; step++)
                {
                    // collect valid actions
                    var cand = new[] { (head.x - 1, head.y), (head.x + 1, head.y), (head.x, head.y - 1), (head.x, head.y + 1) };
                    var valid = new List<int>();
                    for (int i = 0; i < cand.Length; i++)
                    {
                        var p = cand[i];
                        if (!env.IsValidMove(p)) continue;
                        if (lastPos == p) continue;
                        valid.Add(i);
                    }

                    if (valid.Count == 0) break;

                    var sKey = Key(head, env.ApplesRemaining);
                    var qvals = _q.GetOrAdd(sKey, () => new double[4]);

                    int actionIdx;
                    if (_rng.NextDouble() < epsilon)
                        actionIdx = valid[_rng.Next(valid.Count)];
                    else
                    {
                        // choose best among valid
                        double best = double.NegativeInfinity;
                        var bestActions = new List<int>();
                        foreach (var a in valid)
                        {
                            var v = qvals[a];
                            if (v > best) { best = v; bestActions.Clear(); bestActions.Add(a); }
                            else if (v == best) bestActions.Add(a);
                        }
                        actionIdx = bestActions[_rng.Next(bestActions.Count)];
                    }

                    var nextPos = cand[actionIdx];
                    bool ate = env.MoveCaterpillarTo(nextPos);
                    var reward = ate ? 10.0 : -0.01;

                    var nextKey = Key(env.CaterpillarHead, env.ApplesRemaining);
                    var nextQ = _q.GetOrAdd(nextKey, () => new double[4]);
                    double maxNext = double.NegativeInfinity;
                    foreach (var a in new int[] { 0, 1, 2, 3 }) if (nextQ[a] > maxNext) maxNext = nextQ[a];
                    if (maxNext == double.NegativeInfinity) maxNext = 0;

                    // Q-learning update
                    var curQ = qvals[actionIdx];
                    qvals[actionIdx] = curQ + alpha * (reward + gamma * maxNext - curQ);

                    // prepare for next step
                    lastPos = env.LastPosition;
                    head = env.CaterpillarHead;

                    if (env.ApplesRemaining == 0) break; // episode finished
                }
            }

            // write to file for future runs
            try
            {
                var dict = _q.ToDictionary();
                var txt = JsonSerializer.Serialize(dict);
                File.WriteAllText(fname, txt);
            }
            catch { }

            progress?.Report("QLearning: ready.");
        }

        // Public helpers to save/load Q-table
        public void SaveToFile(string path)
        {
            try
            {
                var dict = _q.ToDictionary();
                var txt = JsonSerializer.Serialize(dict);
                File.WriteAllText(path, txt);
            }
            catch { }
        }

        public void LoadFromFile(string path)
        {
            var txt = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, double[]>>(txt);
            if (dict != null) _q.LoadFromDictionary(dict);
        }

        public (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            // Choose a random valid action weighted by Q (epsilon-greedy simplified)
            var neighbors = new List<((int x, int y) pos, int actionIdx)>();
            var cand = new[] { (currentHead.x - 1, currentHead.y), (currentHead.x + 1, currentHead.y), (currentHead.x, currentHead.y - 1), (currentHead.x, currentHead.y + 1) };
            for (int i = 0; i < cand.Length; i++)
            {
                var p = cand[i];
                var tup = (p.Item1, p.Item2);
                if (!board.IsValidMove(tup)) continue;
                if (lastPos.HasValue && tup == lastPos.Value) continue;
                neighbors.Add((tup, i));
            }

            if (neighbors.Count == 0) return lastPos ?? currentHead;

            var key = Key(currentHead, board.ApplesRemaining);
            if (!_q.TryGetValue(key, out var qvals)) qvals = _q.GetOrAdd(key, () => new double[4]);

            // pick max-q among neighbors (break ties randomly)
            double best = double.NegativeInfinity;
            var bestList = new List<((int x, int y) pos, int idx)>();
            foreach (var n in neighbors)
            {
                var val = qvals[n.actionIdx];
                if (val > best)
                {
                    best = val; bestList.Clear(); bestList.Add(n);
                }
                else if (val == best) bestList.Add(n);
            }

            var choice = bestList[_rng.Next(bestList.Count)];
            return choice.pos;
        }

        private string Key((int x, int y) p, int applesRemaining) => $"{p.x}:{p.y}:{applesRemaining}";
    }
}
