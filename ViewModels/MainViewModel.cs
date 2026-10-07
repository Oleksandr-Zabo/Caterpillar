using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows.Input;
using Caterpillar.Algorithms;
using Caterpillar.Helpers;
using Caterpillar.Models;

namespace Caterpillar.ViewModels
{
    public sealed class MainViewModel : BaseViewModel
    {
        private const int DefaultStepsLimit = 500;
        private const int DefaultAnimationSpeed = 50;
        private const int MinimumAnimationSpeed = 0;
        private const int MaximumAnimationSpeed = 2000;
        private const string BoardFileName = "data.txt";

        private GameBoard? _initialBoard;
        private GameBoard? _activeBoard;
        private CancellationTokenSource _operationCts = new();
        private int _stepsLimit = DefaultStepsLimit;
        private int _animationSpeed = DefaultAnimationSpeed;
        private string _status = "Load data.txt to begin.";
        private string _algorithmName = "No algorithm selected";
        private string _decisionTime = "Decision time: 0 ms";
        private string _executingTime = "Executing time: 0 ms";
        private int _applesEaten;
        private int _stepsTaken;
        private double _headRotation;
        private readonly Dictionary<string, QLearningAgent> _qLearningCache = new();
        private readonly Dictionary<string, long> _decisionTimeCache = new();
        private int _runId;
        private readonly object _runGate = new();

        public MainViewModel()
        {
            ReadFileCommand = new RelayCommand(_ => ReadFile());
            GenerateBoardCommand = ReadFileCommand;
            StartBacktrackingCommand = new RelayCommand(_ => StartAlgorithm(new BacktrackingSolver()));
            StartQLearningCommand = new RelayCommand(_ => StartAlgorithm(new QLearningAgent()));
            ReadFile();
        }

        public ObservableCollection<CellViewModel> Cells { get; } = new();
        public ICommand ReadFileCommand { get; }
        public ICommand GenerateBoardCommand { get; }
        public ICommand StartBacktrackingCommand { get; }
        public ICommand StartQLearningCommand { get; }

        public int Rows => _activeBoard?.Rows ?? 1;
        public int Cols => _activeBoard?.Cols ?? 1;
        public double CellSize => Math.Max(8, Math.Min(760.0 / Math.Max(1, Cols), 560.0 / Math.Max(1, Rows)));
        public int StepsLimit { get => _stepsLimit; set => Set(ref _stepsLimit, Math.Clamp(value, 1, 100000)); }
        public int AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, Math.Clamp(value, MinimumAnimationSpeed, MaximumAnimationSpeed)); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string AlgorithmName { get => _algorithmName; private set => Set(ref _algorithmName, value); }
        public string DecisionTime { get => _decisionTime; private set => Set(ref _decisionTime, value); }
        public string ExecutingTime { get => _executingTime; private set => Set(ref _executingTime, value); }
        public int ApplesEaten { get => _applesEaten; private set => Set(ref _applesEaten, value); }
        public int StepsTaken { get => _stepsTaken; private set => Set(ref _stepsTaken, value); }
        public double HeadRotation { get => _headRotation; private set => Set(ref _headRotation, value); }

        private void ReadFile()
        {
            try
            {
                CancelCurrentRun();
                var path = Path.Combine(AppContext.BaseDirectory, BoardFileName);
                _initialBoard = new GameBoard(path);
                ShowBoard(new GameBoard(_initialBoard));
                AlgorithmName = "No algorithm selected";
                Status = $"Loaded {Rows} x {Cols} field.";
                ResetStatistics();
            }
            catch (Exception ex)
            {
                Status = $"Cannot load {BoardFileName}: {ex.Message}";
            }
        }

        private async Task RunAsync(IAlgorithm algorithm)
        {
            if (_initialBoard is null)
                return;

            CancellationToken token;
            int runId;
            lock (_runGate)
            {
                CancelCurrentRun();
                _operationCts = new CancellationTokenSource();
                token = _operationCts.Token;
                runId = ++_runId;
                _activeBoard = new GameBoard(_initialBoard);
                ShowBoard(_activeBoard);
                ResetStatistics();
            }
            AlgorithmName = algorithm.Name;
            Status = $"{algorithm.Name}: preparing...";

            var cacheKey = $"{algorithm.Name}:{_initialBoard.LayoutKey}";
            if (algorithm is QLearningAgent && _qLearningCache.TryGetValue(cacheKey, out var preparedAgent))
            {
                algorithm = preparedAgent;
                preparedAgent.ResetRuntime();
            }

            var decisionTimer = Stopwatch.StartNew();
            try
            {
                if (_decisionTimeCache.TryGetValue(cacheKey, out var cachedDecisionTime))
                {
                    DecisionTime = $"Decision time: {cachedDecisionTime} ms (cached)";
                }
                else
                {
                    await algorithm.PrepareAsync(_activeBoard, StepsLimit, null!, token);
                    decisionTimer.Stop();
                    var elapsed = decisionTimer.ElapsedMilliseconds;
                    _decisionTimeCache[cacheKey] = elapsed;
                    if (algorithm is QLearningAgent prepared)
                        _qLearningCache[cacheKey] = prepared;
                    DecisionTime = $"Decision time: {elapsed} ms";
                }
                Status = $"{algorithm.Name}: running";

                var executionTimer = Stopwatch.StartNew();
                for (var step = 0; step < StepsLimit && _activeBoard.ApplesRemaining > 0 && runId == _runId; step++)
                {
                    token.ThrowIfCancellationRequested();
                    var current = _activeBoard.CaterpillarHead;
                    var next = algorithm.GetNextMove(_activeBoard, current, _activeBoard.LastPosition);
                    if (!_activeBoard.TryMoveCaterpillarTo(next, out var ate))
                        break;

                    UpdateDirection(current, next);
                    StepsTaken++;
                    if (ate)
                        ApplesEaten++;
                    if (runId != _runId)
                        break;
                    RefreshCells();
                    if (AnimationSpeed > 0)
                        await Task.Delay(AnimationSpeed, token);
                    else
                        await Task.Yield();
                }
                executionTimer.Stop();
                ExecutingTime = $"Executing time: {executionTimer.ElapsedMilliseconds} ms";
                if (runId == _runId)
                    Status = _activeBoard.ApplesRemaining == 0
                        ? $"{algorithm.Name}: all apples collected"
                        : $"{algorithm.Name}: finished at step limit";
            }
            catch (OperationCanceledException)
            {
                if (runId == _runId)
                    Status = "Run cancelled.";
            }
            catch (Exception ex)
            {
                if (runId == _runId)
                    Status = $"{algorithm.Name}: {ex.Message}";
            }
        }

        private void StartAlgorithm(IAlgorithm algorithm)
        {
            _ = RunAsync(algorithm);
        }

        private void CancelCurrentRun()
        {
            _operationCts.Cancel();
            _runId++;
        }

        private void ShowBoard(GameBoard board)
        {
            _activeBoard = board;
            Cells.Clear();
            for (var x = 0; x < board.Rows; x++)
            for (var y = 0; y < board.Cols; y++)
                Cells.Add(new CellViewModel(board.Cells[x, y]));
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(Cols));
            OnPropertyChanged(nameof(CellSize));
        }

        private void RefreshCells()
        {
            foreach (var cell in Cells)
                cell.Refresh();
        }

        private void UpdateDirection((int x, int y) from, (int x, int y) to)
        {
            HeadRotation = to.y > from.y ? 90 : to.y < from.y ? 270 : to.x > from.x ? 180 : 0;
        }

        private void ResetStatistics()
        {
            DecisionTime = "Decision time: 0 ms";
            ExecutingTime = "Executing time: 0 ms";
            ApplesEaten = 0;
            StepsTaken = 0;
            HeadRotation = 0;
        }
    }
}