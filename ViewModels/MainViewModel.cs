using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Input;
using Caterpillar.Algorithms;
using Caterpillar.Helpers;
using Caterpillar.Models;

namespace Caterpillar.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private GameBoard _boardBack;
        private GameBoard _boardQL;
        private GameBoard _initialBoard;

        public ObservableCollection<CellViewModel> CellsBack { get; } = new();
        public ObservableCollection<CellViewModel> CellsRight { get; } = new();

        private int _boardSize = 20;
        private int _applesCount = 20;
        private int _stepsLimit = 100;
        private int _animationSpeed = 100;

        private readonly Stopwatch _timerBack = new();
        private readonly Stopwatch _timerRight = new();
        private CancellationTokenSource _operationCts = new();

        // Metrics (algorithm-only timing)
        private string _algTimeLeft = "";   // planning/training time
        private string _decTimeLeft = "";   // decision time (sum of GetNextMove)
        private string _algTimeRight = "";
        private string _decTimeRight = "";
        private string _execTimeLeft = "";
        private string _execTimeRight = "";

        public MainViewModel()
        {
            GenerateBoardCommand = new RelayCommand(_ => GenerateBoards());
            StartBacktrackingCommand = new RelayCommand(_ => _ = StartBacktrackingAsync());
            StartQLearningCommand = new RelayCommand(_ => _ = StartQLearningAsync());
            StartBothCommand = new RelayCommand(_ => _ = StartBothAsync());

            GenerateBoards();
        }

        private async Task StartBothAsync()
        {
            try
            {
                var t1 = StartBacktrackingAsync();
                var t2 = StartQLearningAsync();
                await Task.WhenAll(t1, t2);
            }
            catch (Exception ex)
            {
                // Surface errors to UI timing fields to avoid unhandled exceptions
                AlgorithmTimeLeft = $"Error: {ex.Message}";
            }
        }

        public ICommand GenerateBoardCommand { get; }
        public ICommand StartBacktrackingCommand { get; }
        public ICommand StartQLearningCommand { get; }
        public ICommand StartBothCommand { get; }

        public int BoardSize
        {
            get => _boardSize;
            set
            {
                var clamped = Math.Clamp(value, GameBoard.MinBoardSize, GameBoard.MaxBoardSize);
                if (Set(ref _boardSize, clamped)) OnPropertyChanged(nameof(CellSize));
            }
        }
        public int ApplesCount
        {
            get => _applesCount;
            set => Set(ref _applesCount, Math.Clamp(value, 0, Math.Max(0, BoardSize * BoardSize - GameBoard.InitialCaterpillarLength)));
        }
        public int StepsLimit { get => _stepsLimit; set => Set(ref _stepsLimit, Math.Clamp(value, 1, 10000)); }
        public int AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, Math.Clamp(value, 0, 2000)); }
        public FruitType SelectedFruit { get; set; } = FruitType.Apple;

        public double CellSize => Math.Max(8.0, 400.0 / Math.Max(1, BoardSize));
        public double FruitDiameter => CellSize * 0.8;
        public double FruitMargin => CellSize * 0.1;
        public double EyeDiameter => CellSize * 0.3;

        public string AlgorithmTimeLeft { get => _algTimeLeft; private set => Set(ref _algTimeLeft, value); }
        public string DecisionTimeLeft { get => _decTimeLeft; private set => Set(ref _decTimeLeft, value); }
        public string AlgorithmTimeRight { get => _algTimeRight; private set => Set(ref _algTimeRight, value); }
        public string DecisionTimeRight { get => _decTimeRight; private set => Set(ref _decTimeRight, value); }
        public string ExecutionTimeLeft { get => _execTimeLeft; private set => Set(ref _execTimeLeft, value); }
        public string ExecutionTimeRight { get => _execTimeRight; private set => Set(ref _execTimeRight, value); }
        private int _applesEatenLeft;
        private int _plannedApplesLeft;
        private int _applesEatenRight;
        private int _stepsTakenLeft;
        private int _stepsTakenRight;

        public int ApplesEatenLeft { get => _applesEatenLeft; private set => Set(ref _applesEatenLeft, value); }
        public int PlannedApplesLeft { get => _plannedApplesLeft; private set => Set(ref _plannedApplesLeft, value); }
        public int ApplesEatenRight { get => _applesEatenRight; private set => Set(ref _applesEatenRight, value); }
        public int StepsTakenLeft { get => _stepsTakenLeft; private set => Set(ref _stepsTakenLeft, value); }
        public int StepsTakenRight { get => _stepsTakenRight; private set => Set(ref _stepsTakenRight, value); }

        private void GenerateBoards()
        {
            _operationCts.Cancel();
            _operationCts.Dispose();
            _operationCts = new CancellationTokenSource();

            ApplesCount = Math.Clamp(ApplesCount, 0, Math.Max(0, BoardSize * BoardSize - GameBoard.InitialCaterpillarLength));
            _initialBoard = new GameBoard(BoardSize, BoardSize, ApplesCount, SelectedFruit);
            _boardBack = new GameBoard(_initialBoard);
            _boardQL = new GameBoard(_initialBoard);

            CellsBack.Clear();
            CellsRight.Clear();

            var cellsB = _boardBack.Cells;
            var cellsR = _boardQL.Cells;

            for (int r = 0; r < BoardSize; r++)
            for (int c = 0; c < BoardSize; c++)
            {
                CellsBack.Add(new CellViewModel(cellsB[r, c]));
                CellsRight.Add(new CellViewModel(cellsR[r, c]));
            }
            AlgorithmTimeLeft = "Train: 0 ms";
            DecisionTimeLeft = "Decision time: 0 ms";
            AlgorithmTimeRight = "Train: 0 ms";
            DecisionTimeRight = "Decision time: 0 ms";
            ExecutionTimeLeft = "Executing: 0 ms";
            ExecutionTimeRight = "Executing: 0 ms";
            ApplesEatenLeft = PlannedApplesLeft = ApplesEatenRight = 0;
            StepsTakenLeft = StepsTakenRight = 0;

            OnPropertyChanged(nameof(CellSize));
            OnPropertyChanged(nameof(FruitDiameter));
            OnPropertyChanged(nameof(FruitMargin));
            OnPropertyChanged(nameof(EyeDiameter));

            // Retrain Q-Learning for the new board configuration (size/apples)
            AlgorithmTimeRight = "Train: started...";
            var trainTask = TrainQLearningAsync(_operationCts.Token);
            trainTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    AlgorithmTimeRight = $"Training error: {t.Exception?.GetBaseException().Message}";
                }
                else
                {
                    // TrainQLearningAsync publishes the final training time.
                }
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ResetBacktrackingBoard()
        {
            _boardBack = new GameBoard(_initialBoard);
            CellsBack.Clear();
            AddCells(CellsBack, _boardBack);
            ApplesEatenLeft = 0;
            PlannedApplesLeft = 0;
            StepsTakenLeft = 0;
            DecisionTimeLeft = "Decision time: 0 ms";
            ExecutionTimeLeft = "Executing: 0 ms";
        }

        private void ResetQLearningBoard()
        {
            _boardQL = new GameBoard(_initialBoard);
            CellsRight.Clear();
            AddCells(CellsRight, _boardQL);
            ApplesEatenRight = 0;
            StepsTakenRight = 0;
            DecisionTimeRight = "Decision time: 0 ms";
            ExecutionTimeRight = "Executing: 0 ms";
        }

        private static void AddCells(ObservableCollection<CellViewModel> target, GameBoard board)
        {
            for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
                target.Add(new CellViewModel(board.Cells[r, c]));
        }

        private async Task TrainQLearningAsync(CancellationToken cancellationToken)
        {
            // Use trainer to run several trainings and save best q-table
            var trainer = new QLearningTrainer();
            var sw = Stopwatch.StartNew();
            await Task.Run(() => trainer.RunAndSaveBestAsync(_boardQL, StepsLimit, runs: 3,
                progress: new System.Progress<string>(s => { }), cancellationToken), cancellationToken);
            sw.Stop();
            AlgorithmTimeRight = $"Train: {sw.ElapsedMilliseconds} ms";
        }

        private async Task StartBacktrackingAsync()
        {
            try
            {
            ResetBacktrackingBoard();
            var cancellationToken = _operationCts.Token;
            var solver = new BacktrackingSolver();
            // Run planner on threadpool to avoid UI freeze and measure planning time
            var swPlan = Stopwatch.StartNew();
            await Task.Run(() => solver.PrepareAsync(_boardBack, StepsLimit,
                new System.Progress<string>(s => { }), cancellationToken), cancellationToken);
            swPlan.Stop();
            AlgorithmTimeLeft = $"Train: {swPlan.ElapsedMilliseconds} ms";
            PlannedApplesLeft = solver.PlannedApples;

            // Now execute plan and measure decision time
            long decisionTicks = 0;
            int applesEaten = 0;
            int stepsTaken = 0;
            int stepsRemaining = StepsLimit;
            DecisionTimeLeft = "Decision time: 0 ms";

            _timerBack.Restart();
            while (_boardBack.ApplesRemaining > 0 && stepsRemaining > 0)
            {
                var currentHead = _boardBack.CaterpillarHead;
                var t0 = Stopwatch.GetTimestamp();
                var next = solver.GetNextMove(_boardBack, currentHead, _boardBack.LastPosition);
                var t1 = Stopwatch.GetTimestamp();
                decisionTicks += (t1 - t0);

                if (!_boardBack.IsValidMove(next)) break;
                var ate = false;
                try
                {
                    ate = _boardBack.MoveCaterpillarTo(next);
                }
                catch (Exception ex)
                {
                    // If MoveCaterpillarTo throws due to finished apples or similar, stop gracefully
                    AlgorithmTimeLeft = $"Planning: {swPlan.ElapsedMilliseconds} ms";
                    DecisionTimeLeft = $"Decision time: { (decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
                    ApplesEatenLeft = applesEaten;
                    StepsTakenLeft = stepsTaken;
                    PlannedApplesLeft = solver.PlannedApples;
                    _timerBack.Stop();
                    ExecutionTimeLeft = $"Executing: {_timerBack.ElapsedMilliseconds} ms";
                    return;
                }

                if (ate) applesEaten++;
                stepsTaken++;
                stepsRemaining--;
                ApplesEatenLeft = applesEaten;
                StepsTakenLeft = stepsTaken;
                DecisionTimeLeft = $"Decision time: {(decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
                // stop if no apples remain
                if (_boardBack.ApplesRemaining == 0)
                {
                    UpdateCells(CellsBack);
                    break;
                }
                UpdateCells(CellsBack);
                await Task.Delay(AnimationSpeed, cancellationToken);
            }
            _timerBack.Stop();

            AlgorithmTimeLeft = $"Train: {swPlan.ElapsedMilliseconds} ms";
            DecisionTimeLeft = $"Decision time: { (decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
            ExecutionTimeLeft = $"Executing: {_timerBack.ElapsedMilliseconds} ms";
            ApplesEatenLeft = applesEaten;
            StepsTakenLeft = stepsTaken;
            PlannedApplesLeft = solver.PlannedApples;
            }
            catch (OperationCanceledException)
            {
                // A new board generation cancels the previous operation.
            }
            catch (Exception ex)
            {
                AlgorithmTimeLeft = $"Error: {ex.Message}";
            }
        }

        private async Task StartQLearningAsync()
        {
            try
            {
                ResetQLearningBoard();
                var agent = new QLearningAgent();
                // Run training/loading on background thread to avoid UI freeze
                var swTrain = Stopwatch.StartNew();
                var cancellationToken = _operationCts.Token;
                await Task.Run(() => agent.PrepareAsync(_boardQL, StepsLimit,
                    new System.Progress<string>(s => { }), cancellationToken), cancellationToken);
                swTrain.Stop();
                AlgorithmTimeRight = $"Train: {swTrain.ElapsedMilliseconds} ms";

                long decisionTicks = 0;
                int applesEaten = 0;
                int stepsTaken = 0;
                int stepsRemaining = StepsLimit;
                DecisionTimeRight = "Decision time: 0 ms";

                _timerRight.Restart();
                while (_boardQL.ApplesRemaining > 0 && stepsRemaining > 0)
                {
                    var currentHead = _boardQL.CaterpillarHead;
                    var t0 = Stopwatch.GetTimestamp();
                    var next = agent.GetNextMove(_boardQL, currentHead, _boardQL.LastPosition);
                    var t1 = Stopwatch.GetTimestamp();
                    decisionTicks += (t1 - t0);

                    if (next == currentHead || !_boardQL.IsValidMove(next))
                    {
                        _timerRight.Stop();
                        AlgorithmTimeRight = $"Train: {swTrain.ElapsedMilliseconds} ms";
                        DecisionTimeRight = $"Decision time: {(decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
                        ExecutionTimeRight = $"Executing: {_timerRight.ElapsedMilliseconds} ms";
                        ApplesEatenRight = applesEaten;
                        StepsTakenRight = stepsTaken;
                        UpdateCells(CellsRight);
                        return;
                    }

                    var bodyBeforeMove = new System.Collections.Generic.HashSet<(int x, int y)>(_boardQL.CaterpillarSegments);
                    if (bodyBeforeMove.Contains(next))
                    {
                        _timerRight.Stop();
                        AlgorithmTimeRight = $"Train: {swTrain.ElapsedMilliseconds} ms";
                        DecisionTimeRight = $"Decision time: {(decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
                        ExecutionTimeRight = $"Executing: {_timerRight.ElapsedMilliseconds} ms";
                        ApplesEatenRight = applesEaten;
                        StepsTakenRight = stepsTaken;
                        UpdateCells(CellsRight);
                        return;
                    }

                    var ate = _boardQL.MoveCaterpillarTo(next);
                    if (ate) applesEaten++;
                    stepsTaken++;
                    stepsRemaining--;
                    ApplesEatenRight = applesEaten;
                    StepsTakenRight = stepsTaken;
                    DecisionTimeRight = $"Decision time: {(decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
                // stop if no apples remain
                if (_boardQL.ApplesRemaining == 0)
                {
                    UpdateCells(CellsRight);
                    break;
                }
                    UpdateCells(CellsRight);
                    await Task.Delay(AnimationSpeed, cancellationToken);
                }

                _timerRight.Stop();
                AlgorithmTimeRight = $"Train: {swTrain.ElapsedMilliseconds} ms";
                DecisionTimeRight = $"Decision time: { (decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
                ExecutionTimeRight = $"Executing: {_timerRight.ElapsedMilliseconds} ms";
                ApplesEatenRight = applesEaten;
                StepsTakenRight = stepsTaken;
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    AlgorithmTimeRight = $"Error: {ex.Message}";
            }
        }

        private void UpdateCells(ObservableCollection<CellViewModel> coll)
        {
            foreach (var c in coll) c.Refresh();
        }
    }
}
