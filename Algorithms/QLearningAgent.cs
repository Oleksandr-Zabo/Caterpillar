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
using System.Diagnostics;

namespace Caterpillar.Algorithms
{
    

    public class QLearningAgent : IAlgorithm
    {
        private const int ActionCount = 4;
        private const double TargetAccuracy = 0.915;
        private const double AcceptanceRatio = 0.95;   
        private const int EvaluationInterval = 256;
        private const int MaximumTrainingEpisodes = 100000;
        private const int MaximumPreparationMilliseconds = 600000; 
        private const int MaximumTrainingMilliseconds = MaximumPreparationMilliseconds;
        private const int TrainingYieldInterval = 4;
        private const int ParallelWorkerCount = 2;
        private const int EpisodesPerWorker = 8;
        private const int LocalAppleLookahead = 3;
        private const double LocalAppleReward = 35.0;
        private const double EmptyMovePenalty = 12.0;
        private const double RevisitPenalty = 80.0;
        private const double ReverseMovePenalty = 35.0;
        private const int RuntimeHistorySize = 8;
        private const double RuntimeCyclePenalty = 2500.0;
        private const double LearningRate = 0.1;
        private const double DiscountFactor = 0.9;
        private const double InitialExploration = 0.8;
        private const double MinimumExploration = 0.01;
        private const double ExplorationDecay = 0.9998;

        private static readonly double[] _zeroQ = new double[ActionCount];

        private readonly List<(int x, int y)> _cachedPath = new();
        private int _replayIndex;

        public string Name => _mode == QLearningMode.Train
            ? "Q-Learning (Train)"
            : "Q-Learning (Run)";

        private readonly CustomHash<double[]> _q = new(512);
        private readonly Random _rng = new();
        private readonly Dictionary<(int x, int y), int> _runtimeVisitCounts = new();
        private readonly Queue<(int x, int y)> _runtimePositionHistory = new();
        private readonly Queue<(int x, int y)> _committedRoute = new();
        private (int x, int y)? _runtimeLastHead;
        private int _stepsLimit;
        private int _stepsTaken;
        private readonly bool _workerMode;
        private readonly int _workerEpisodes;
        private readonly QLearningMode _mode;
        private double _epsilon;
        private bool _updatesEnabled;

        public QLearningAgent() : this(QLearningMode.Train)
        {
        }

        public QLearningAgent(QLearningMode mode)
        {
            _mode = mode;
        }

        private QLearningAgent(int workerEpisodes) : this(QLearningMode.Train)
        {
            _workerMode = true;
            _workerEpisodes = workerEpisodes;
        }

       

        private static string SafeAbs(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static string BestTableName(GameBoard board, string key)
            => $"best_qtable_v7_{board.Rows}x{board.Cols}_{key}.json";

        private static string PlainTableName(GameBoard board, string key)
            => $"qtable_v7_{board.Rows}x{board.Cols}_{key}.json";

        private static string BestPathName(GameBoard board, string key)
            => Path.ChangeExtension(BestTableName(board, key), ".path.json");

        private static string PlainPathName(GameBoard board, string key)
            => Path.ChangeExtension(PlainTableName(board, key), ".path.json");

      

        public async Task PrepareAsync(GameBoard board, int stepsLimit, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            using var preparationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preparationDeadline.CancelAfter(MaximumPreparationMilliseconds);
            var preparationToken = preparationDeadline.Token;
            progress?.Report("QLearning: preparing...");
            ResetRuntimeState();
            var evaluationSteps = Math.Max(1, stepsLimit);
            _stepsLimit = evaluationSteps;
            var targetApples = Math.Min(board.Rows * board.Cols, (int)Math.Ceiling(evaluationSteps * TargetAccuracy));
            var acceptanceScore = (int)Math.Floor(targetApples * AcceptanceRatio);

            var boardKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(board.LayoutKey)))[..16];
            var bestTable = BestTableName(board, boardKey);
            var plainTable = PlainTableName(board, boardKey);
            var bestPath = BestPathName(board, boardKey);
            var plainPath = PlainPathName(board, boardKey);

            
            if (_mode == QLearningMode.Run)
            {
                string tableToLoad;
                if (File.Exists(bestTable)) tableToLoad = bestTable;
                else if (File.Exists(plainTable)) tableToLoad = plainTable;
                else throw new InvalidOperationException(
                    $"No trained Q-table found for {board.Rows}x{board.Cols}. Run Training mode first. " +
                    $"Looked for '{SafeAbs(bestTable)}' and '{SafeAbs(plainTable)}'.");

             
                LoadFromFile(tableToLoad);

                if (File.Exists(bestPath))
                {
                    LoadPathFromFile(bestPath);
                }
                else if (File.Exists(plainPath))
                {
                    LoadPathFromFile(plainPath);
                }
                else
                {
                    Console.WriteLine("[Run] NO path file found — Run will fall back to planner/greedy.");
                }

                _epsilon = 0.0;
                _updatesEnabled = false;
                _replayIndex = 0;
                progress?.Report($"QLearning: run mode; path entries = {_cachedPath.Count}.");
                return;
            }

            // ---------- TRAIN MODE ----------
            _updatesEnabled = true;

            var loaded = false;
            string loadedFrom = null;
            if (!_workerMode)
            {
                foreach (var candidate in new[] { bestTable, plainTable })
                {
                    if (loaded || !File.Exists(candidate)) continue;
                    try
                    {
                        LoadFromFile(candidate);
                        loaded = true;
                        loadedFrom = candidate;
                        progress?.Report($"QLearning: loaded cached table '{Path.GetFileName(candidate)}' ({_q.Count} states); evaluating {evaluationSteps}-step accuracy.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Train] failed to load '{candidate}'", ex);
                    }
                }
            }

            int cachedScore = -1;
            if (loaded)
            {
                try
                {
                    cachedScore = EvaluatePolicy(board, evaluationSteps, preparationToken);
                }
                catch (OperationCanceledException) when (
                    preparationDeadline.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    SavePathFromCurrentTable(board, evaluationSteps, bestPath, plainPath);
                    progress?.Report("QLearning: cached evaluation timed out; kept cached table + path.");
                    return;
                }

                progress?.Report($"QLearning: cached accuracy {cachedScore}/{evaluationSteps} ({AccuracyPercent(cachedScore, evaluationSteps):F1}%), target {TargetAccuracy:P1}.");

                // KEY FIX: accept the cached table if it's at least 95% of target.
                // Previously 547 < 549 (target) caused us to fall through and abort.
                if (cachedScore >= acceptanceScore)
                {

                    if (!File.Exists(bestTable))
                    {
                        SaveToFile(bestTable);
                    }

                    if (!File.Exists(bestPath) || !File.Exists(plainPath))
                    {
                        BuildCachedPath(board, evaluationSteps);
                        SavePathToFile(bestPath);
                        SavePathToFile(plainPath);
                    }
                    else
                    {
                        Console.WriteLine("[Train] path files already present — reusing.");
                    }
                    return;
                }
                else
                {
                    Console.WriteLine($"[Train] cached score BELOW acceptance ({cachedScore} < {acceptanceScore}) — will retrain.");
                }
            }

            if (!_workerMode)
            {
                progress?.Report($"QLearning: training {ParallelWorkerCount} candidates in parallel...");
                var candidates = Enumerable.Range(0, ParallelWorkerCount)
                    .Select(index => Task.Run(async () =>
                    {
                        var worker = new QLearningAgent(EpisodesPerWorker);
                        try
                        {
                            await worker.PrepareAsync(board, stepsLimit, null!, preparationToken);
                            var score = worker.EvaluatePolicy(board, evaluationSteps, preparationToken);
                            return ((QLearningAgent worker, int score)?)((worker, score));
                        }
                        catch (OperationCanceledException) when (
                            preparationDeadline.IsCancellationRequested &&
                            !cancellationToken.IsCancellationRequested)
                        {
                            return null;
                        }
                    }))
                    .ToArray();

                var results = (await Task.WhenAll(candidates))
                    .Where(result => result.HasValue)
                    .Select(result => result!.Value)
                    .ToList();


                if (results.Count == 0)
                {
                    if (loaded)
                    {
                        SavePathFromCurrentTable(board, evaluationSteps, bestPath, plainPath);
                        progress?.Report("QLearning: parallel training timed out; kept cached table + path.");
                    }
                    else
                    {
                        progress?.Report("QLearning: no candidate completed and no cached table available.");
                    }
                    return;
                }

                var bestWorker = results.OrderByDescending(r => r.score).First();

                // Only swap in the new table if it actually beats the cached one.
                if (cachedScore >= 0 && bestWorker.score <= cachedScore)
                {
                    SavePathFromCurrentTable(board, evaluationSteps, bestPath, plainPath);
                    progress?.Report($"QLearning: cached table ({cachedScore}) beat retrained ({bestWorker.score}); path saved.");
                    return;
                }

                _q.LoadFromDictionary(bestWorker.worker._q.ToDictionary());
                SaveToFile(plainTable);
                SaveToFile(bestTable);

                BuildCachedPath(board, evaluationSteps);
                SavePathToFile(plainPath);
                SavePathToFile(bestPath);

                progress?.Report($"QLearning: selected parallel candidate {bestWorker.score}/{evaluationSteps} ({AccuracyPercent(bestWorker.score, evaluationSteps):F1}%); saved {_q.Count} states + {_cachedPath.Count}-move path.");
                return;
            }

            int episodes = _workerMode ? _workerEpisodes : MaximumTrainingEpisodes;
            double alpha = LearningRate;
            double gamma = DiscountFactor;
            double epsilon = InitialExploration;
            double epsilonMin = MinimumExploration;
            double epsilonDecay = ExplorationDecay;
            int maxStepsPerEpisode = Math.Max(30, stepsLimit);
            _epsilon = epsilon;
            var trainingTimer = Stopwatch.StartNew();

            for (int ep = 0; ep < episodes; ep++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (trainingTimer.ElapsedMilliseconds >= MaximumTrainingMilliseconds)
                {
                    break;
                }

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
                    {
                        break;
                    }
                }

                var env = new GameBoard(board);
                var head = env.CaterpillarHead;
                var visited = new HashSet<(int x, int y)> { head };
                int previousAction = -1;

                for (int step = 0; step < maxStepsPerEpisode; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cand = new[] { (head.x - 1, head.y), (head.x + 1, head.y), (head.x, head.y - 1), (head.x, head.y + 1) };
                    var valid = LegalActions(env, head, null);
                    if (valid.Count == 0)
                    {
                        var terminalKey = StateKey(env, head, previousAction);
                        var terminalQ = GetQValues(terminalKey);
                        for (int i = 0; i < ActionCount; i++) terminalQ[i] -= 100.0;
                        break;
                    }
                    var preferredValid = valid;

                    var prevDist = ManhattanDistanceToNearestFruit(env, head);
                    var sKey = StateKey(env, head, previousAction);
                    var qvals = GetQValues(sKey);

                    int actionIdx;
                    if (_updatesEnabled && _rng.NextDouble() < _epsilon)
                    {
                        actionIdx = ChooseExploratoryAction(preferredValid, cand, visited, head, previousAction);
                        if (actionIdx < 0)
                            actionIdx = preferredValid.Count > 0 ? preferredValid[0] : -1;
                    }
                    else
                    {
                        double best = double.NegativeInfinity;
                        var bestActions = new List<int>();
                        foreach (var a in preferredValid)
                        {
                            var v = qvals[a];
                            if (v > best) { best = v; bestActions.Clear(); bestActions.Add(a); }
                            else if (v == best) bestActions.Add(a);
                        }
                        if (bestActions.Count == 0) bestActions.AddRange(preferredValid);
                        if (bestActions.Count == 0) break;
                        actionIdx = bestActions[_rng.Next(bestActions.Count)];
                    }

                    if (actionIdx < 0 || actionIdx >= cand.Length) break;
                    var nextPos = cand[actionIdx];
                    bool revisited = visited.Contains(nextPos);
                    bool ate = env.MoveCaterpillarTo(nextPos);
                    var newDist = ManhattanDistanceToNearestFruit(env, env.CaterpillarHead);
                    double reward = ate ? 180.0 : -EmptyMovePenalty;
                    if (revisited) reward -= RevisitPenalty;
                    if (newDist < prevDist) reward += 5.0;
                    else if (newDist > prevDist) reward -= 5.0;
                    if (ate) reward += LocalAppleReward;
                    if (actionIdx == Opposite(previousAction)) reward -= ReverseMovePenalty;
                    if (!ate && newDist >= prevDist) reward -= EmptyMovePenalty;
                    var nextKey = StateKey(env, env.CaterpillarHead, actionIdx);
                    var nextQ = GetQValues(nextKey);
                    double maxNext = double.NegativeInfinity;
                    foreach (var a in LegalActions(env, env.CaterpillarHead, null))
                        if (nextQ[a] > maxNext) maxNext = nextQ[a];
                    if (maxNext == double.NegativeInfinity) maxNext = 0;

                    var curQ = qvals[actionIdx];
                    if (_updatesEnabled) qvals[actionIdx] = curQ + alpha * (reward + gamma * maxNext - curQ);
                    epsilon = Math.Max(epsilonMin, epsilon * epsilonDecay);
                    _epsilon = epsilon;

                    head = env.CaterpillarHead;
                    visited.Add(head);
                    previousAction = actionIdx;
                    if (env.ApplesRemaining == 0) break;
                }
            }

            if (!_workerMode)
            {
                var finalScore = EvaluatePolicy(board, evaluationSteps, cancellationToken);
                progress?.Report($"QLearning: final accuracy {finalScore}/{evaluationSteps} ({AccuracyPercent(finalScore, evaluationSteps):F1}%).");

                try
                {
                    SaveToFile(plainTable);
                    SaveToFile(bestTable);
                    
                    BuildCachedPath(board, evaluationSteps);

                    SavePathToFile(plainPath);
                    SavePathToFile(bestPath);
                    progress?.Report($"QLearning: saved {_q.Count} states + {_cachedPath.Count}-move path.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Train] save", ex);
                }
            }

            progress?.Report("QLearning: ready.");
        }


        private void SavePathFromCurrentTable(GameBoard board, int steps, string bestPath, string plainPath)
        {
            BuildCachedPath(board, steps);
            SavePathToFile(bestPath);
            SavePathToFile(plainPath);
        }

        private void BuildCachedPath(GameBoard source, int steps)
        {
            _cachedPath.Clear();
            var board = new GameBoard(source);
            ResetRuntimeState();
            var movesAttempted = 0;
            var movesApplied = 0;
            var applesEaten = 0;

            for (var step = 0; step < steps && board.ApplesRemaining > 0; step++)
            {
                var current = board.CaterpillarHead;
                var next = GetNextMove(board, current, board.LastPosition);
                movesAttempted++;
                _cachedPath.Add(next);
                if (!board.TryMoveCaterpillarTo(next, out var ate))
                {
                   _cachedPath.RemoveAt(_cachedPath.Count - 1);
                    break;
                }
                movesApplied++;
                if (ate) applesEaten++;
            }

             ResetRuntimeState();
        }

        private sealed class PathEntry
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        private void SavePathToFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                var entries = _cachedPath.Select(p => new PathEntry { X = p.x, Y = p.y }).ToList();
                var json = JsonSerializer.Serialize(entries);
                File.WriteAllText(path, json);
                var exists = File.Exists(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SavePath] FAILED '{path}'", ex);
            }
        }

        private void LoadPathFromFile(string path)
        {
            _cachedPath.Clear();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var entries = JsonSerializer.Deserialize<List<PathEntry>>(json);
                if (entries != null && entries.Count > 0)
                {
                    foreach (var e in entries) _cachedPath.Add((e.X, e.Y));
                    return;
                }

                var legacy = JsonSerializer.Deserialize<List<Dictionary<string, int>>>(json);
                if (legacy != null)
                {
                    foreach (var d in legacy)
                        if (d.TryGetValue("Item1", out var x) && d.TryGetValue("Item2", out var y))
                            _cachedPath.Add((x, y));
                }
                else
                {
                    Console.WriteLine("[LoadPath] legacy deserialization returned null.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoadPath] FAILED '{path}'", ex);
            }
        }


        public (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            if (_runtimeLastHead == null)
            {
                _runtimeVisitCounts.Clear();
                _runtimePositionHistory.Clear();
                _replayIndex = 0;
            }
            _runtimeVisitCounts[currentHead] = _runtimeVisitCounts.GetValueOrDefault(currentHead) + 1;
            if (_runtimePositionHistory.Count == 0 || _runtimePositionHistory.Last() != currentHead)
            {
                _runtimePositionHistory.Enqueue(currentHead);
                while (_runtimePositionHistory.Count > RuntimeHistorySize)
                    _runtimePositionHistory.Dequeue();
            }
            _runtimeLastHead = currentHead;

            if (_mode == QLearningMode.Run)
                return GetRunMove(board, currentHead, lastPos);

            return GetTrainMove(board, currentHead, lastPos);
        }

        private (int x, int y) GetRunMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            if (_replayIndex == 0)
                Console.WriteLine($"[RunMove] first step: path={_cachedPath.Count}, head={currentHead}.");

            while (_replayIndex < _cachedPath.Count)
            {
                var expectedCurrent = _replayIndex == 0 ? currentHead : _cachedPath[_replayIndex - 1];
                if (expectedCurrent != currentHead)
                {
                    _replayIndex++;
                    continue;
                }

                var target = _cachedPath[_replayIndex];
                if (board.IsValidMove(target))
                {
                    _replayIndex++;
                    _stepsTaken++;
                    return target;
                }

                Console.WriteLine($"[RunMove] invalid replay target {target} at idx={_replayIndex} (head={currentHead}); skipping.");
                _replayIndex++;
            }
                Console.WriteLine($"[RunMove] path exhausted at idx={_replayIndex}; falling back to greedy.");

            return GetGreedyRunMove(board, currentHead, lastPos);
        }

        private (int x, int y) GetGreedyRunMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            var previousAction = lastPos.HasValue ? GetAction(lastPos.Value, currentHead) : -1;
            var qvals = GetQValues(StateKey(board, currentHead, previousAction));
            var legalActions = LegalActions(board, currentHead, lastPos);
            if (legalActions.Count == 0) return currentHead;

            var nonReverse = legalActions.Where(a => a != Opposite(previousAction)).ToList();
            var pool = nonReverse.Count > 0 ? nonReverse : legalActions;

            var bestAction = pool
                .OrderByDescending(action => qvals[action])
                .ThenBy(action => action == Opposite(previousAction) ? 1 : 0)
                .ThenBy(action => action)
                .First();
            _stepsTaken++;
            return ApplyAction(currentHead, bestAction);
        }

        private (int x, int y) GetTrainMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            var beamMove = AppleRoutePlanner.FindBestMove(board, currentHead,
                _runtimePositionHistory.ToHashSet(), lastPos, preferGlobalFrontier: true);
            if (beamMove.HasValue && board.IsValidMove(beamMove.Value))
            {
                _committedRoute.Clear();
                _stepsTaken++;
                return beamMove.Value;
            }

            if (_committedRoute.Count > 0 &&
                Neighbors(currentHead).Any(board.IsFruit) &&
                !board.IsFruit(_committedRoute.Peek()))
            {
                _committedRoute.Clear();
            }

            if (_committedRoute.Count == 0 || !board.IsValidMove(_committedRoute.Peek()))
            {
                _committedRoute.Clear();
                var route = GraphRoutePlanner.FindBestRoute(board, currentHead,
                    Math.Max(0, _stepsLimit - _stepsTaken), lastPos,
                    _runtimePositionHistory.ToHashSet());
                if (route is not null)
                    foreach (var move in route.Moves.Take(1))
                        _committedRoute.Enqueue(move);
            }

            if (_committedRoute.Count > 0 && board.IsValidMove(_committedRoute.Peek()))
            {
                _stepsTaken++;
                return _committedRoute.Dequeue();
            }

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
                if (!dangers[i]) safeNeighbors.Add(candidate);
            }

            if (legalNeighbors.Count == 0) return currentHead;
            var neighbors = safeNeighbors.Count > 0 ? safeNeighbors : legalNeighbors;

            int previousAction = lastPos.HasValue ? GetAction(lastPos.Value, currentHead) : -1;
            var nonReverseNeighbors = neighbors.Where(n => n.actionIdx != Opposite(previousAction)).ToList();
            if (nonReverseNeighbors.Count > 0) neighbors = nonReverseNeighbors;

            var currentFruitDistance = FindNearestFruitDistance(board);
            var nearestAppleDistance = currentFruitDistance;
            var key = StateKey(board, currentHead, previousAction);
            var qvals = GetQValues(key);
            var graphMove = GraphRoutePlanner.FindBestMove(board, currentHead,
                Math.Max(0, _stepsLimit - _stepsTaken), lastPos,
                _runtimePositionHistory.ToHashSet());
            var qMin = qvals.Min();
            var qMax = qvals.Max();
            var qRange = qMax - qMin;

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
                if (fruitDistance != int.MaxValue) val -= fruitDistance * 100.0;
                if (localDistance != int.MaxValue) val -= localDistance * 250.0;
                if (nearestAppleDistance != int.MaxValue && fruitDistance < nearestAppleDistance) val += 1200.0;
                var normalizedQ = qRange > 0.000001 ? (qvals[n.actionIdx] - qMin) / qRange : 0.0;
                val += normalizedQ * 1800.0;
                if (graphMove.HasValue && n.pos == graphMove.Value) val += 6000.0;
                if (_runtimeVisitCounts.TryGetValue(n.pos, out var visits)) val -= RevisitPenalty * visits;
                if (_runtimePositionHistory.Contains(n.pos)) val -= RuntimeCyclePenalty;
                if (n.actionIdx == Opposite(previousAction)) val -= ReverseMovePenalty;
                val += _rng.NextDouble() * 0.05;
                if (val > best) { best = val; bestList.Clear(); bestList.Add(n); }
                else if (val == best) bestList.Add(n);
            }

            if (bestList.Count == 0)
            {
                bestList.AddRange(neighbors
                    .Where(n => n.actionIdx >= 0 && n.actionIdx < ActionCount)
                    .OrderByDescending(n => BestAppleLineScoreAfterMove(board, n.pos))
                    .ThenBy(n => FindNearestFruitDistanceAfterMove(board, n.pos)));
            }

            if (bestList.Count == 0) return neighbors[0].pos;
            var choice = bestList[_rng.Next(bestList.Count)];
            _stepsTaken++;
            return choice.pos;
        }


        public void SaveToFile(string path)
        {
            try
            {
                var dict = _q.ToDictionary();
                var txt = JsonSerializer.Serialize(dict);
                File.WriteAllText(path, txt);
                }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaveTable] FAILED '{path}'", ex);
            }
        }

        public void LoadFromFile(string path)
        {
            var txt = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, double[]>>(txt);
            if (dict == null)
            {
                return;
            }

            var normalized = new Dictionary<string, double[]>(dict.Count);
            foreach (var pair in dict) normalized[pair.Key] = EnsureQValues(pair.Value);
            _q.LoadFromDictionary(normalized);
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
            if (bounded.Count == 0) return -1;
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
            foreach (var action in Enumerable.Range(0, ActionCount))
            {
                var next = ApplyAction(head, action);
                if (board.IsValidMove(next)) result.Add(action);
            }
            return result;
        }

        private static bool[] GetDangerMap(GameBoard board, (int x, int y) head)
        {
            return Enumerable.Range(0, ActionCount)
                .Select(action => !board.IsValidPosition(ApplyAction(head, action)))
                .ToArray();
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
            var localFruitCount = CountNearbyFruit(board, head, 3);
            return $"AppleDir:{relX},{relY}|Distance:{ManhattanDistanceToNearestFruit(board, head)}|Local:{localFruitCount}|Danger:{(dangers[0] ? 1 : 0)}{(dangers[1] ? 1 : 0)}{(dangers[2] ? 1 : 0)}{(dangers[3] ? 1 : 0)}|LastDir:{previousAction + 1}";
        }

        private static double BestAppleLineScoreAfterMove(GameBoard board, (int x, int y) position)
        {
            var simulated = new GameBoard(board);
            if (!simulated.IsValidMove(position)) return double.MinValue;
            simulated.MoveCaterpillarTo(position);
            return BestAppleLineScore(simulated, simulated.CaterpillarHead);
        }

        private static int FindNearestFruitDistanceAfterMove(GameBoard board, (int x, int y) position)
        {
            var simulated = new GameBoard(board);
            if (!simulated.IsValidMove(position)) return int.MaxValue;
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

        private static List<List<(int x, int y)>> FindAppleComponents(GameBoard board)
        {
            var components = new List<List<(int x, int y)>>();
            var visited = new HashSet<(int x, int y)>();
            for (var x = 0; x < board.Rows; x++)
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

        private double[] GetQValues(string key)
        {
            if (!_q.TryGetValue(key, out var values))
            {
                if (!_updatesEnabled) return _zeroQ;
                values = new double[ActionCount];
                _q.Set(key, values);
                return values;
            }
            var normalized = EnsureQValues(values);
            if (!ReferenceEquals(values, normalized)) _q.Set(key, normalized);
            return normalized;
        }

        private static double[] EnsureQValues(double[]? values)
        {
            var normalized = new double[ActionCount];
            if (values != null) Array.Copy(values, normalized, Math.Min(values.Length, normalized.Length));
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
                if (board.IsFruit(current)) return distance;
                foreach (var next in Neighbors(current))
                {
                    if (!IsFree(board, next) || !visited.Add(next)) continue;
                    queue.Enqueue((next, distance + 1));
                }
            }
            return int.MaxValue;
        }

        private void ResetRuntimeState()
        {
            _runtimeVisitCounts.Clear();
            _runtimePositionHistory.Clear();
            _committedRoute.Clear();
            _runtimeLastHead = null;
            _stepsTaken = 0;
            _replayIndex = 0;
        }

        private static int CountNearbyFruit(GameBoard board, (int x, int y) center, int radius)
        {
            var count = 0;
            for (var x = Math.Max(0, center.x - radius); x <= Math.Min(board.Rows - 1, center.x + radius); x++)
                for (var y = Math.Max(0, center.y - radius); y <= Math.Min(board.Cols - 1, center.y + radius); y++)
                    if (board.IsFruit((x, y))) count++;
            return count;
        }

        private int EvaluatePolicy(GameBoard source, int steps, CancellationToken cancellationToken)
        {
            var board = new GameBoard(source);
            ResetRuntimeState();
            var eaten = 0;
            for (var step = 0; step < steps && board.ApplesRemaining > 0; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = board.CaterpillarHead;
                var next = GetNextMove(board, current, board.LastPosition);
                if (!board.TryMoveCaterpillarTo(next, out var ate)) break;
                if (ate) eaten++;
            }
            ResetRuntimeState();
            return eaten;
        }

        private static double AccuracyPercent(int apples, int steps)
            => steps <= 0 ? 0.0 : apples * 100.0 / steps;

        public void ResetRuntime() => ResetRuntimeState();

        private static bool HasSafeContinuation(GameBoard board, (int x, int y) candidate, int depth)
        {
            var simulated = new GameBoard(board);
            if (!simulated.IsValidMove(candidate)) return false;
            if (simulated.ApplesRemaining == 0) return true;
            if (depth <= 0) return true;
            var head = simulated.CaterpillarHead;
            foreach (var next in Neighbors(head))
            {
                if (!simulated.IsValidMove(next)) continue;
                if (HasSafeContinuation(simulated, next, depth - 1)) return true;
            }
            return false;
        }

        //private static int CountFreeAreaAfterMove(GameBoard board, (int x, int y) candidate)
        //{
        //    var simulated = new GameBoard(board);
        //    if (!simulated.IsValidMove(candidate)) return 0;
        //    simulated.MoveCaterpillarTo(candidate);
        //    return CountFreeArea(simulated, simulated.CaterpillarHead);
        //}

        //private static int SafetyScore(GameBoard board, (int x, int y) candidate)
        //{
        //    var simulated = new GameBoard(board);
        //    if (!simulated.IsValidMove(candidate)) return int.MinValue / 4;
        //    simulated.MoveCaterpillarTo(candidate);
        //    return SafetyDepth(simulated, 4, new HashSet<(int x, int y)>());
        //}

        private static int SafetyDepth(GameBoard board, int depth, HashSet<(int x, int y)> path)
        {
            if (depth == 0) return 0;
            var head = board.CaterpillarHead;
            if (!path.Add(head)) return -1;
            var best = -1;
            foreach (var next in Neighbors(head))
            {
                if (!board.IsValidMove(next)) continue;
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
                    if (board.IsValidPosition(next) && visited.Add(next))
                        queue.Enqueue(next);
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

        //private (int x, int y)? FindNextFruitMove(GameBoard board, (int x, int y) start)
        //{
        //    var queue = new Queue<(int x, int y)>();
        //    var visited = new HashSet<(int x, int y)> { start };
        //    var parent = new Dictionary<(int x, int y), (int x, int y)>();
        //    queue.Enqueue(start);
        //    while (queue.Count > 0)
        //    {
        //        var current = queue.Dequeue();
        //        var neighbors = new[]
        //        {
        //            (current.x - 1, current.y),
        //            (current.x + 1, current.y),
        //            (current.x, current.y - 1),
        //            (current.x, current.y + 1)
        //        };
        //        foreach (var next in neighbors)
        //        {
        //            if (!IsFree(board, next) || !visited.Add(next)) continue;
        //            parent[next] = current;
        //            if (board.IsFruit(next))
        //            {
        //                var step = next;
        //                while (parent[step] != start) step = parent[step];
        //                return step;
        //            }
        //            queue.Enqueue(next);
        //        }
        //    }
        //    return null;
        //}

        private static bool IsFree(GameBoard board, (int x, int y) position)
        {
            return position.x >= 0 && position.x < board.Rows &&
                   position.y >= 0 && position.y < board.Cols &&
                   !board.CaterpillarSegments.Contains(position);
        }
    }
}