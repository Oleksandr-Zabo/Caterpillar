using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using Caterpillar.DataStructures;
using System.Threading.Tasks;
using System.Threading;
using Caterpillar.Models;
using System.Security.Cryptography;
using System.Text;

namespace Caterpillar.Algorithms
{
    public class QLearningAgent : IAlgorithm
    {
        private const int ActionCount = 4;
        private const double TargetAccuracy = 0.915;
        private const int EvaluationInterval = 256;
        private const int MaximumTrainingEpisodes = 100000;
        private const int MaximumTrainingMilliseconds = 300000;
        private const int TrainingYieldInterval = 4;
        private const int MaximumTrainingSteps = 60;
        private const int ParallelWorkerCount = 4;
        private const int EpisodesPerWorker = 96;
        private const int LocalAppleLookahead = 3;
        private const double LocalAppleReward = 35.0;
        private const double DistanceReward = 8.0;
        private const double EmptyMovePenalty = 12.0;
        private const double RevisitPenalty = 80.0;
        private const double ReverseMovePenalty = 35.0;
        private const double LineProgressReward = 30.0;
        private const double LineTargetBonus = 900.0;
        private const double LearningRate = 0.1;
        private const double DiscountFactor = 0.9;
        private const double InitialExploration = 0.8;
        private const double MinimumExploration = 0.01;
        private const double ExplorationDecay = 0.9998;

        public string Name => "Q-Learning";
        private readonly CustomHash<double[]> _q = new(512);
        private readonly Random _rng = new();
        private readonly string[] _actions = new[] { "U", "D", "L", "R" };
        private readonly HashSet<(int x, int y)> _runtimeVisited = new();
        private readonly Dictionary<(int x, int y), int> _runtimeVisitCounts = new();
        private readonly Dictionary<string, int> _runtimeStateVisits = new();
        private (int x, int y)? _runtimeLastHead;
        private readonly bool _workerMode;
        private readonly int _workerEpisodes;

        public QLearningAgent()
        {
        }

        private QLearningAgent(int workerEpisodes)
        {
            _workerMode = true;
            _workerEpisodes = workerEpisodes;
        }

        public async Task PrepareAsync(GameBoard board, int stepsLimit, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            progress?.Report("QLearning: preparing...");
            ResetRuntimeState();
            var evaluationSteps = Math.Max(1, stepsLimit);
            var targetApples = Math.Min(board.Rows * board.Cols, (int)Math.Ceiling(evaluationSteps * TargetAccuracy));

            // Use a simple cache file per board size+applecount to speed training
            var boardKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(board.LayoutKey)))[..16];
            string bestFname = $"best_qtable_v6_{board.Rows}x{board.Cols}_{boardKey}.json";
            string fname = $"qtable_v6_{board.Rows}x{board.Cols}_{boardKey}.json";

            // prefer best file if present
            var loaded = false;
            if (!_workerMode && File.Exists(bestFname))
            {
                try
                {
                    LoadFromFile(bestFname);
                    loaded = true;
                    progress?.Report($"QLearning: loaded cached table; evaluating {evaluationSteps}-step accuracy.");
                }
                catch { }
            }
            if (!_workerMode && !loaded && File.Exists(fname))
            {
                try
                {
                    LoadFromFile(fname);
                    loaded = true;
                    progress?.Report($"QLearning: loaded cached table; evaluating {evaluationSteps}-step accuracy.");
                }
                catch { }
            }

            if (loaded)
            {
                var cachedScore = EvaluatePolicy(board, evaluationSteps, cancellationToken);
                progress?.Report($"QLearning: cached accuracy {cachedScore}/{evaluationSteps} ({AccuracyPercent(cachedScore, evaluationSteps):F1}%), target {TargetAccuracy:P1}.");
                if (cachedScore >= targetApples)
                    return;
            }

            if (!_workerMode)
            {
                progress?.Report($"QLearning: training {ParallelWorkerCount} candidates in parallel...");
                var candidates = Enumerable.Range(0, ParallelWorkerCount)
                    .Select(index => Task.Run(async () =>
                    {
                        var worker = new QLearningAgent(EpisodesPerWorker);
                        await worker.PrepareAsync(board, stepsLimit, null, cancellationToken);
                        var score = worker.EvaluatePolicy(board, evaluationSteps, cancellationToken);
                        return (worker, score);
                    }, cancellationToken))
                    .ToArray();

                var results = await Task.WhenAll(candidates);
                var bestWorker = results.OrderByDescending(result => result.score).First();
                _q.LoadFromDictionary(bestWorker.worker._q.ToDictionary());
                SaveToFile(fname);
                progress?.Report($"QLearning: selected parallel candidate {bestWorker.score}/{evaluationSteps} ({AccuracyPercent(bestWorker.score, evaluationSteps):F1}%).");
                return;
            }

            // Perform Q-Learning training using episodes on the provided board
            // Increase episodes proportional to board size to obtain stronger policies
            int episodes = _workerMode ? _workerEpisodes : MaximumTrainingEpisodes;
            double alpha = LearningRate;
            double gamma = DiscountFactor;
            double epsilon = InitialExploration;
            double epsilonMin = MinimumExploration;
            double epsilonDecay = ExplorationDecay;
            int maxStepsPerEpisode = Math.Clamp(board.Rows * board.Cols / 35, 30, MaximumTrainingSteps);
            var trainingTimer = System.Diagnostics.Stopwatch.StartNew();

            // q entries are created on demand during training

            // training episodes
            for (int ep = 0; ep < episodes; ep++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (trainingTimer.ElapsedMilliseconds >= MaximumTrainingMilliseconds)
                    break;

                if (ep % TrainingYieldInterval == 0)
                {
                    progress?.Report($"QLearning: training {ep + 1}/{episodes}");
                    await Task.Yield();
                }

                if (!_workerMode && ep > 0 && ep % EvaluationInterval == 0)
                {
                    var score = EvaluatePolicy(board, evaluationSteps, cancellationToken);
                    progress?.Report($"QLearning: accuracy {score}/{evaluationSteps} ({AccuracyPercent(score, evaluationSteps):F1}%) after {ep} episodes.");
                    if (score >= targetApples)
                        break;
                }

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
                    var valid = LegalActions(env, head, null);
                    if (valid.Count == 0)
                    {
                        var terminalKey = StateKey(env, head, previousAction);
                        var terminalQ = GetQValues(terminalKey);
                        for (int i = 0; i < 4; i++) terminalQ[i] -= 100.0;
                        break;
                    }
                    var preferredValid = valid;

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
                    double reward = ate ? 180.0 : -EmptyMovePenalty;
                    if (revisited) reward -= RevisitPenalty;
                    if (newDist < prevDist)
                    {
                        reward += 5.0;
                    }
                    else if (newDist > prevDist)
                        reward -= 5.0;
                    if (ate)
                        reward += LocalAppleReward;
                    if (actionIdx == Opposite(previousAction))
                        reward -= ReverseMovePenalty;
                    if (!ate && newDist >= prevDist)
                        reward -= EmptyMovePenalty;
                    var nextKey = StateKey(env, env.CaterpillarHead, actionIdx);
                    var nextQ = GetQValues(nextKey);
                    double maxNext = double.NegativeInfinity;
                    foreach (var a in new int[] { 0, 1, 2, 3 }) if (nextQ[a] > maxNext) maxNext = nextQ[a];
                    if (maxNext == double.NegativeInfinity) maxNext = 0;

                    // Q-learning update
                    var curQ = qvals[actionIdx];
                    qvals[actionIdx] = curQ + alpha * (reward + gamma * maxNext - curQ);

                    // prepare for next step
                    lastPos = null;
                    head = env.CaterpillarHead;
                    visited.Add(head);
                    previousAction = actionIdx;

                    if (env.ApplesRemaining == 0) break; // episode finished
                }
            }

            if (!_workerMode)
            {
                var finalScore = EvaluatePolicy(board, evaluationSteps, cancellationToken);
                progress?.Report($"QLearning: final accuracy {finalScore}/{evaluationSteps} ({AccuracyPercent(finalScore, evaluationSteps):F1}%).");
            }

            // Write the table for future runs. It will be evaluated again before reuse.
            try
            {
                var dict = _q.ToDictionary();
                var txt = JsonSerializer.Serialize(dict);
                if (!_workerMode)
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
                if (board.IsValidMove(next))
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
                var next = ApplyAction(head, action);
                if (board.IsValidMove(next))
                    result.Add(action);
            }
            return result;
        }

        private static bool[] GetDangerMap(GameBoard board, (int x, int y) head)
        {
            return Enumerable.Range(0, ActionCount)
                .Select(action => !board.IsValidPosition(ApplyAction(head, action)))
                .ToArray();
        }

        private static bool IsFreeCell(GameBoard board, (int x, int y) position)
        {
            return board.IsValidPosition(position);
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

            var plannedMove = AppleRoutePlanner.FindBestMove(
                board, currentHead, _runtimeVisited, lastPos, preferGlobalFrontier: false);
            if (plannedMove.HasValue && board.IsValidMove(plannedMove.Value))
                return plannedMove.Value;

            var currentFruitDistance = FindNearestFruitDistance(board);

            var nearestAppleDistance = FindNearestFruitDistance(board);
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
                simulated.MoveCaterpillarTo(n.pos);

                var fruitDistance = FindNearestFruitDistance(simulated);
                var localDistance = ManhattanDistanceToLocalFruit(simulated, simulated.CaterpillarHead);
                var val = fruitDistance == int.MaxValue ? -100000.0 : 0.0;
                if (currentFruitDistance != int.MaxValue && fruitDistance != int.MaxValue)
                    val += (currentFruitDistance - fruitDistance) * 5000.0;
                if (fruitDistance != int.MaxValue)
                    val -= fruitDistance * 100.0;
                if (localDistance != int.MaxValue)
                    val -= localDistance * 250.0;
                if (nearestAppleDistance != int.MaxValue && fruitDistance < nearestAppleDistance)
                    val += 1200.0;
                val += Math.Clamp(qvals[n.actionIdx], -5.0, 5.0);
                if (_runtimeVisitCounts.TryGetValue(n.pos, out var visits)) val -= RevisitPenalty * visits;
                if (n.actionIdx == Opposite(previousAction)) val -= ReverseMovePenalty;
                val += _rng.NextDouble() * 0.05;
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
                    .OrderByDescending(n => BestAppleLineScoreAfterMove(board, n.pos))
                    .ThenBy(n => FindNearestFruitDistanceAfterMove(board, n.pos)));
            }

            if (bestList.Count == 0)
                return neighbors[0].pos;

            var choice = bestList[_rng.Next(bestList.Count)];
            return choice.pos;
        }

        private static double BestAppleLineScoreAfterMove(GameBoard board, (int x, int y) position)
        {
            var simulated = new GameBoard(board);
            if (!simulated.IsValidMove(position))
                return double.MinValue;
            simulated.MoveCaterpillarTo(position);
            return BestAppleLineScore(simulated, simulated.CaterpillarHead);
        }

        private static int FindNearestFruitDistanceAfterMove(GameBoard board, (int x, int y) position)
        {
            var simulated = new GameBoard(board);
            if (!simulated.IsValidMove(position))
                return int.MaxValue;
            simulated.MoveCaterpillarTo(position);
            return FindNearestFruitDistance(simulated);
        }

        private static double BestAppleLineScore(GameBoard board, (int x, int y) head)
        {
            var best = 0.0;
            foreach (var line in FindAppleComponents(board))
            {
                var distance = line.Min(cell => Math.Abs(cell.x - head.x) + Math.Abs(cell.y - head.y));
                best = Math.Max(best, line.Count * 100.0 - distance * 4.0);
            }
            return best;
        }

        private static (int x, int y)? FindBestAppleComponentMove(GameBoard board, (int x, int y) start)
        {
            var components = FindAppleComponents(board);
            if (components.Count == 0)
                return null;

            var selected = components
                .OrderByDescending(line => line.Count * 100 -
                    line.Min(cell => Math.Abs(cell.x - start.x) + Math.Abs(cell.y - start.y)) * 4)
                .First();
            var targets = selected.ToHashSet();
            var queue = new Queue<((int x, int y) position, (int x, int y) first)>();
            var visited = new HashSet<(int x, int y)> { start };
            queue.Enqueue((start, start));
            while (queue.Count > 0)
            {
                var (current, first) = queue.Dequeue();
                foreach (var next in Neighbors(current))
                {
                    if (!board.IsValidPosition(next) || !visited.Add(next)) continue;
                    var firstStep = current == start ? next : first;
                    if (targets.Contains(next)) return firstStep;
                    queue.Enqueue((next, firstStep));
                }
            }
            return null;
        }

        private static List<List<(int x, int y)>> FindAppleComponents(GameBoard board)
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
                        foreach (var next in Neighbors(current))
                            if (board.IsFruit(next) && visited.Add(next)) queue.Enqueue(next);
                    }
                    components.Add(component);
                }
            }
            return components;
        }

        private static int ManhattanDistanceToLocalFruit(GameBoard board, (int x, int y) head)
        {
            return board.Cells.Cast<GridCell>()
                .Where(cell => (cell.Type == CellType.Apple || cell.Type == CellType.Grape) &&
                               Math.Abs(cell.X - head.x) + Math.Abs(cell.Y - head.y) <= LocalAppleLookahead)
                .Select(cell => Math.Abs(cell.X - head.x) + Math.Abs(cell.Y - head.y))
                .DefaultIfEmpty(int.MaxValue)
                .Min();
        }

        private static (int x, int y)? FindLocalFruitMove(GameBoard board, (int x, int y) start)
        {
            var queue = new Queue<((int x, int y) position, (int x, int y) first, int distance)>();
            var visited = new HashSet<(int x, int y)> { start };
            queue.Enqueue((start, start, 0));
            var candidates = new List<((int x, int y) first, int distance)>();

            while (queue.Count > 0)
            {
                var (current, first, distance) = queue.Dequeue();
                if (distance >= LocalAppleLookahead)
                    continue;

                foreach (var next in Neighbors(current))
                {
                    if (!board.IsValidPosition(next) || !visited.Add(next))
                        continue;

                    var firstStep = current == start ? next : first;
                    var nextDistance = distance + 1;
                    if (board.IsFruit(next))
                        candidates.Add((firstStep, nextDistance));
                    queue.Enqueue((next, firstStep, nextDistance));
                }
            }

            return candidates
                .OrderBy(candidate => candidate.distance)
                .Select(candidate => ((int x, int y)?)candidate.first)
                .FirstOrDefault();
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
            _runtimeStateVisits.Clear();
            _runtimeLastHead = null;
        }

        private int EvaluatePolicy(
            GameBoard source,
            int steps,
            CancellationToken cancellationToken)
        {
            var board = new GameBoard(source);
            ResetRuntimeState();
            var eaten = 0;

            for (var step = 0; step < steps && board.ApplesRemaining > 0; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = board.CaterpillarHead;
                var next = GetNextMove(board, current, board.LastPosition);
                if (!board.TryMoveCaterpillarTo(next, out var ate))
                    break;
                if (ate)
                    eaten++;
            }

            ResetRuntimeState();
            return eaten;
        }

        private static double AccuracyPercent(int apples, int steps)
        {
            return steps <= 0 ? 0.0 : apples * 100.0 / steps;
        }

        public void ResetRuntime()
        {
            ResetRuntimeState();
        }

        private static bool HasSafeContinuation(GameBoard board, (int x, int y) candidate, int depth)
        {
            var simulated = new GameBoard(board);
            if (!simulated.IsValidMove(candidate))
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
            if (!simulated.IsValidMove(candidate))
                return 0;
            simulated.MoveCaterpillarTo(candidate);

            return CountFreeArea(simulated, simulated.CaterpillarHead);
        }

        private static int SafetyScore(GameBoard board, (int x, int y) candidate)
        {
            var simulated = new GameBoard(board);
            if (!simulated.IsValidMove(candidate))
                return int.MinValue / 4;
            simulated.MoveCaterpillarTo(candidate);

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
