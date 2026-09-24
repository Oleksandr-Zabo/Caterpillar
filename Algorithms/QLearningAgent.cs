using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using Caterpillar.DataStructures;
using System.Threading.Tasks;
using System.Threading;
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
        private readonly HashSet<(int x, int y)> _runtimeVisited = new();
        private readonly Dictionary<(int x, int y), int> _runtimeVisitCounts = new();
        private readonly Dictionary<string, int> _runtimeStateVisits = new();
        private (int x, int y)? _runtimeLastHead;

        public async Task PrepareAsync(GameBoard board, int stepsLimit, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            progress?.Report("QLearning: preparing...");
            ResetRuntimeState();

            // Use a simple cache file per board size+applecount to speed training
            string bestFname = $"best_qtable_v3_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
            string fname = $"qtable_v3_{board.Rows}x{board.Cols}_{board.ApplesRemaining}.json";
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
            int episodes = Math.Clamp(board.Rows * board.Cols * 10, 800, 4000);
            double alpha = 0.1; // learning rate
            double gamma = 0.9; // discount
            double epsilon = 0.8;
            double epsilonMin = 0.01;
            double epsilonDecay = 0.9998;
            int maxStepsPerEpisode = Math.Clamp(board.Rows * board.Cols / 2, 30, 200);

            // q entries are created on demand during training

            // training episodes
            for (int ep = 0; ep < episodes; ep++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // clone board to run episode without affecting UI board
                var env = new GameBoard(board);
                var lastPos = env.LastPosition;
                var head = env.CaterpillarHead;

                var visited = new HashSet<(int x, int y)> { head };
                int previousAction = -1;

                for (int step = 0; step < maxStepsPerEpisode; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // collect valid actions
                    var cand = new[] { (head.x - 1, head.y), (head.x + 1, head.y), (head.x, head.y - 1), (head.x, head.y + 1) };
                    var dangers = GetDangerMap(env, head);
                    var valid = LegalActions(env, head, lastPos);
                    if (valid.Count == 0)
                    {
                        var terminalKey = StateKey(env, head, previousAction);
                        var terminalQ = GetQValues(terminalKey);
                        for (int i = 0; i < 4; i++) terminalQ[i] -= 100.0;
                        break;
                    }
                    var preferredValid = SafeActions(env, head, lastPos, dangers);
                    if (preferredValid.Count == 0)
                        preferredValid = valid;

                    var prevDist = ManhattanDistanceToNearestFruit(env, head);
                    var sKey = StateKey(env, head, previousAction);
                    var qvals = GetQValues(sKey);

                    int actionIdx;
                    if (_rng.NextDouble() < epsilon)
                    {
                        actionIdx = ChooseExploratoryAction(preferredValid, cand, visited, head, previousAction);
                        if (actionIdx < 0)
                            actionIdx = preferredValid.Count > 0 ? preferredValid[0] : -1;
                    }
                    else
                    {
                        // choose best among valid
                        double best = double.NegativeInfinity;
                        var bestActions = new List<int>();
                        foreach (var a in preferredValid)
                        {
                            var v = qvals[a];
                            if (v > best) { best = v; bestActions.Clear(); bestActions.Add(a); }
                            else if (v == best) bestActions.Add(a);
                        }
                        if (bestActions.Count == 0)
                            bestActions.AddRange(preferredValid);
                        if (bestActions.Count == 0)
                            break;
                        actionIdx = bestActions[_rng.Next(bestActions.Count)];
                    }

                    if (actionIdx < 0 || actionIdx >= cand.Length)
                        break;
                    var nextPos = cand[actionIdx];
                    bool revisited = visited.Contains(nextPos);
                    bool ate = env.MoveCaterpillarTo(nextPos);
                    // reward shaping: base reward and distance-based bonus
                    var newDist = ManhattanDistanceToNearestFruit(env, env.CaterpillarHead);
                    double reward = ate ? 100.0 : -1.0;
                    if (revisited) reward -= 15.0;
                    if (newDist < prevDist)
                    {
                        reward += 5.0;
                    }
                    else if (newDist > prevDist)
                        reward -= 5.0;
                    var nextKey = StateKey(env, env.CaterpillarHead, actionIdx);
                    var nextQ = GetQValues(nextKey);
                    double maxNext = double.NegativeInfinity;
                    foreach (var a in new int[] { 0, 1, 2, 3 }) if (nextQ[a] > maxNext) maxNext = nextQ[a];
                    if (maxNext == double.NegativeInfinity) maxNext = 0;

                    // Q-learning update
                    var curQ = qvals[actionIdx];
                    qvals[actionIdx] = curQ + alpha * (reward + gamma * maxNext - curQ);

                    // prepare for next step
                    lastPos = env.LastPosition;
                    head = env.CaterpillarHead;
                    visited.Add(head);
                    previousAction = actionIdx;

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

        // Helper: find nearest apple and return delta and manhattan distance
        private (int adx, int ady, int dist) NearestAppleDelta(GameBoard board, (int x, int y) head)
        {
            int bestDist = int.MaxValue;
            (int ax, int ay) best = (-1, -1);
            for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                if (!board.IsFruit((r, c))) continue;
                int d = Math.Abs(head.x - r) + Math.Abs(head.y - c);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = (r, c);
                }
            }
            if (bestDist == int.MaxValue) return (0, 0, int.MaxValue);
            return (best.ax - head.x, best.ay - head.y, bestDist);
        }

        private int GetAction((int x, int y) from, (int x, int y) to)
        {
            if (to.x < from.x) return 0;
            if (to.x > from.x) return 1;
            if (to.y < from.y) return 2;
            return 3;
        }

        private int Opposite(int action) => action switch
        {
            0 => 1,
            1 => 0,
            2 => 3,
            3 => 2,
            _ => -1
        };

        private int ChooseExploratoryAction(List<int> valid, (int x, int y)[] candidates,
            HashSet<(int x, int y)> visited, (int x, int y) head, int previousAction)
        {
            var bounded = valid.FindAll(action => action >= 0 && action < candidates.Length);
            if (bounded.Count == 0)
                return -1;

            var preferred = bounded.FindAll(action =>
            {
                var p = candidates[action];
                return !visited.Contains(p) && action != Opposite(previousAction);
            });
            var choices = preferred.Count > 0 ? preferred : bounded;
            return choices[_rng.Next(choices.Count)];
        }

        private static List<int> LegalActions(GameBoard board, (int x, int y) head, (int x, int y)? lastPos)
        {
            var result = new List<int>();
            foreach (var action in Enumerable.Range(0, 4))
            {
                var next = ApplyAction(head, action);
                if (board.IsValidMove(next) && (!lastPos.HasValue || next != lastPos.Value))
                    result.Add(action);
            }
            return result;
        }

        private static List<int> SafeActions(GameBoard board, (int x, int y) head,
            (int x, int y)? lastPos, bool[] dangers)
        {
            var result = new List<int>();
            foreach (var action in Enumerable.Range(0, 4))
            {
                if (dangers[action]) continue;
                var next = ApplyAction(head, action);
                if (board.IsValidMove(next) && (!lastPos.HasValue || next != lastPos.Value))
                    result.Add(action);
            }
            return result;
        }

        private static bool[] GetDangerMap(GameBoard board, (int x, int y) head)
        {
            return Enumerable.Range(0, 4)
                .Select(action => !IsFreeCell(board, ApplyAction(head, action)))
                .ToArray();
        }

        private static bool IsFreeCell(GameBoard board, (int x, int y) position)
        {
            return position.x >= 0 && position.x < board.Rows &&
                   position.y >= 0 && position.y < board.Cols &&
                   !board.CaterpillarSegments.Contains(position);
        }

        private static (int x, int y) ApplyAction((int x, int y) head, int action) => action switch
        {
            0 => (head.x - 1, head.y),
            1 => (head.x + 1, head.y),
            2 => (head.x, head.y - 1),
            _ => (head.x, head.y + 1)
        };

        private static (int x, int y)? NearestFruit(GameBoard board, (int x, int y) head)
        {
            return board.Cells.Cast<GridCell>()
                .Where(cell => cell.Type == CellType.Apple || cell.Type == CellType.Grape)
                .OrderBy(cell => Math.Abs(cell.X - head.x) + Math.Abs(cell.Y - head.y))
                .Select(cell => ((int x, int y)?)(cell.X, cell.Y))
                .FirstOrDefault();
        }

        private static int ManhattanDistanceToNearestFruit(GameBoard board, (int x, int y) head)
        {
            var fruit = NearestFruit(board, head);
            return fruit.HasValue ? Math.Abs(fruit.Value.x - head.x) + Math.Abs(fruit.Value.y - head.y) : int.MaxValue;
        }

        private string StateKey(GameBoard board, (int x, int y) head, int previousAction)
        {
            var fruit = NearestFruit(board, head);
            int relX = fruit.HasValue ? Math.Sign(fruit.Value.x - head.x) : 0;
            int relY = fruit.HasValue ? Math.Sign(fruit.Value.y - head.y) : 0;
            var dangers = GetDangerMap(board, head);
            return $"AppleDir:{relX},{relY}|Danger:{(dangers[0] ? 1 : 0)}{(dangers[1] ? 1 : 0)}{(dangers[2] ? 1 : 0)}{(dangers[3] ? 1 : 0)}|LastDir:{previousAction + 1}";
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
            if (dict == null)
                return;

            var normalized = new Dictionary<string, double[]>(dict.Count);
            foreach (var pair in dict)
                normalized[pair.Key] = EnsureQValues(pair.Value);
            _q.LoadFromDictionary(normalized);
        }

        public (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            if (_runtimeLastHead == null)
            {
                _runtimeVisited.Clear();
                _runtimeVisitCounts.Clear();
            _runtimeStateVisits.Clear();
            }
            _runtimeVisited.Add(currentHead);
            _runtimeVisitCounts[currentHead] = _runtimeVisitCounts.GetValueOrDefault(currentHead) + 1;
            _runtimeLastHead = currentHead;

            var runtimeState = RuntimeStateKey(board, currentHead);
            _runtimeStateVisits[runtimeState] = _runtimeStateVisits.GetValueOrDefault(runtimeState) + 1;

            var dangers = GetDangerMap(board, currentHead);
            var legalNeighbors = new List<((int x, int y) pos, int actionIdx)>();
            var safeNeighbors = new List<((int x, int y) pos, int actionIdx)>();
            var cand = new[] { (currentHead.x - 1, currentHead.y), (currentHead.x + 1, currentHead.y), (currentHead.x, currentHead.y - 1), (currentHead.x, currentHead.y + 1) };
            for (int i = 0; i < cand.Length; i++)
            {
                var p = cand[i];
                var tup = (p.Item1, p.Item2);
                if (!board.IsValidMove(tup)) continue;
                if (lastPos.HasValue && tup == lastPos.Value) continue;
                var candidate = (tup, i);
                legalNeighbors.Add(candidate);
                if (!dangers[i])
                    safeNeighbors.Add(candidate);
            }

            if (legalNeighbors.Count == 0) return currentHead;

            // The forward-space check is a heuristic, not a second collision
            // rule. If it rejects every legal move, keep moving safely rather
            // than returning the current head and ending the simulation.
            var neighbors = safeNeighbors.Count > 0 ? safeNeighbors : legalNeighbors;

            if (neighbors.Count == 0 && legalNeighbors.Count > 0)
                neighbors = legalNeighbors;

            if (_runtimeStateVisits[runtimeState] > 1)
            {
                foreach (var candidate in neighbors)
                {
                    var simulated = new GameBoard(board);
                    if (!simulated.MoveCaterpillarTo(candidate.pos))
                        continue;

                    var nextState = RuntimeStateKey(simulated, simulated.CaterpillarHead);
                    if (!_runtimeStateVisits.ContainsKey(nextState))
                        return candidate.pos;
                }

                return currentHead;
            }

            var fruitMove = FindNextFruitMove(board, currentHead);
            if (fruitMove.HasValue)
            {
                var routeCandidate = neighbors.FirstOrDefault(n => n.pos == fruitMove.Value);
                if (routeCandidate != default)
                    return routeCandidate.pos;
            }

            var currentFruitDistance = FindNearestFruitDistance(board);
            // The persisted Q-table is only a tie-breaker. Runtime geometry must
            // decide first, otherwise an old policy can walk away from reachable fruit.
            int previousAction = lastPos.HasValue ? GetAction(lastPos.Value, currentHead) : -1;
            var key = StateKey(board, currentHead, previousAction);
            var qvals = GetQValues(key);

            double best = double.NegativeInfinity;
            var bestList = new List<((int x, int y) pos, int idx)>();
            foreach (var n in neighbors)
            {
                var simulated = new GameBoard(board);
                if (!simulated.MoveCaterpillarTo(n.pos))
                    continue;

                var fruitDistance = FindNearestFruitDistance(simulated);
                var val = fruitDistance == int.MaxValue ? -100000.0 : 0.0;
                if (currentFruitDistance != int.MaxValue && fruitDistance != int.MaxValue)
                    val += (currentFruitDistance - fruitDistance) * 5000.0;
                if (fruitDistance != int.MaxValue)
                    val -= fruitDistance * 100.0;
                val += Math.Clamp(qvals[n.actionIdx], -5.0, 5.0);
                if (_runtimeVisitCounts.TryGetValue(n.pos, out var visits)) val -= 50.0 * visits;
                if (n.actionIdx == Opposite(previousAction)) val -= 10.0;
                if (val > best)
                {
                    best = val; bestList.Clear(); bestList.Add(n);
                }
                else if (val == best) bestList.Add(n);
            }

            if (bestList.Count == 0)
            {
                bestList.AddRange(neighbors
                    .Where(n => n.actionIdx >= 0 && n.actionIdx < 4)
                    .OrderByDescending(n => CountFreeAreaAfterMove(board, n.pos)));
            }

            if (bestList.Count == 0)
                return currentHead;

            var choice = bestList[_rng.Next(bestList.Count)];
            return choice.pos;
        }

        private static string RuntimeStateKey(GameBoard board, (int x, int y) head)
        {
            return $"{head.x},{head.y}|{string.Join(';', board.CaterpillarSegments.Select(s => $"{s.x},{s.y}"))}|{board.ApplesRemaining}";
        }

        private double[] GetQValues(string key)
        {
            if (!_q.TryGetValue(key, out var values))
            {
                values = new double[4];
                _q.Set(key, values);
                return values;
            }

            var normalized = EnsureQValues(values);
            if (!ReferenceEquals(values, normalized))
                _q.Set(key, normalized);
            return normalized;
        }

        private static double[] EnsureQValues(double[]? values)
        {
            var normalized = new double[4];
            if (values != null)
                Array.Copy(values, normalized, Math.Min(values.Length, normalized.Length));
            return normalized;
        }

        private static int FindNearestFruitDistance(GameBoard board)
        {
            var start = board.CaterpillarHead;
            var queue = new Queue<((int x, int y) position, int distance)>();
            var visited = new HashSet<(int x, int y)> { start };
            queue.Enqueue((start, 0));

            while (queue.Count > 0)
            {
                var (current, distance) = queue.Dequeue();
                if (board.IsFruit(current))
                    return distance;

                foreach (var next in Neighbors(current))
                {
                    if (!IsFree(board, next) || !visited.Add(next))
                        continue;
                    queue.Enqueue((next, distance + 1));
                }
            }

            return int.MaxValue;
        }

        private void ResetRuntimeState()
        {
            _runtimeVisited.Clear();
            _runtimeVisitCounts.Clear();
            _runtimeLastHead = null;
        }

        public void ResetRuntime()
        {
            ResetRuntimeState();
        }

        private static bool HasSafeContinuation(GameBoard board, (int x, int y) candidate, int depth)
        {
            var simulated = new GameBoard(board);
            if (!simulated.MoveCaterpillarTo(candidate))
                return false;

            if (simulated.ApplesRemaining == 0)
                return true;

            if (depth <= 0)
                return true;

            var head = simulated.CaterpillarHead;
            foreach (var next in Neighbors(head))
            {
                if (!simulated.IsValidMove(next))
                    continue;

                if (HasSafeContinuation(simulated, next, depth - 1))
                    return true;
            }

            return false;
        }

        private static int CountFreeAreaAfterMove(GameBoard board, (int x, int y) candidate)
        {
            var simulated = new GameBoard(board);
            if (!simulated.MoveCaterpillarTo(candidate))
                return 0;

            return CountFreeArea(simulated, simulated.CaterpillarHead);
        }

        private static int SafetyScore(GameBoard board, (int x, int y) candidate)
        {
            var simulated = new GameBoard(board);
            if (!simulated.MoveCaterpillarTo(candidate))
                return int.MinValue / 4;

            return SafetyDepth(simulated, 4, new HashSet<(int x, int y)>());
        }

        private static int SafetyDepth(GameBoard board, int depth, HashSet<(int x, int y)> path)
        {
            if (depth == 0)
                return 0;

            var head = board.CaterpillarHead;
            if (!path.Add(head))
                return -1;

            var best = -1;
            foreach (var next in Neighbors(head))
            {
                if (!board.IsValidMove(next))
                    continue;

                var simulated = new GameBoard(board);
                simulated.MoveCaterpillarTo(next);
                best = Math.Max(best, 1 + SafetyDepth(simulated, depth - 1, new HashSet<(int x, int y)>(path)));
            }

            return best;
        }

        private static int CountFreeArea(GameBoard board, (int x, int y) start)
        {
            var queue = new Queue<(int x, int y)>();
            var visited = new HashSet<(int x, int y)> { start };
            queue.Enqueue(start);
            var count = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                count++;
                foreach (var next in Neighbors(current))
                {
                    if (board.IsValidPosition(next) && visited.Add(next))
                        queue.Enqueue(next);
                }
            }
            return count;
        }

        private static IEnumerable<(int x, int y)> Neighbors((int x, int y) position)
        {
            yield return (position.x - 1, position.y);
            yield return (position.x + 1, position.y);
            yield return (position.x, position.y - 1);
            yield return (position.x, position.y + 1);
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
                var neighbors = new[]
                {
                    (current.x - 1, current.y),
                    (current.x + 1, current.y),
                    (current.x, current.y - 1),
                    (current.x, current.y + 1)
                };

                foreach (var next in neighbors)
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
                   !board.CaterpillarSegments.Contains(position);
        }

    }
}
