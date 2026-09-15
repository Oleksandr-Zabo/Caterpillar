using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
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

        public ObservableCollection<CellViewModel> CellsBack { get; } = new();
        public ObservableCollection<CellViewModel> CellsRight { get; } = new();

        private int _boardSize = 10;
        private int _applesCount = 10;
        private int _stepsLimit = 100;
        private int _animationSpeed = 100;

        private readonly Stopwatch _timerBack = new();
        private readonly Stopwatch _timerRight = new();

        // Metrics (algorithm-only timing)
        private string _algTimeLeft = "";   // planning/training time
        private string _decTimeLeft = "";   // decision time (sum of GetNextMove)
        private string _algTimeRight = "";
        private string _decTimeRight = "";

        public MainViewModel()
        {
            GenerateBoardCommand = new RelayCommand(_ => GenerateBoards());
            StartBacktrackingCommand = new RelayCommand(_ => _ = StartBacktrackingAsync());
            StartQLearningCommand = new RelayCommand(_ => _ = StartQLearningAsync());
            TrainQLearningCommand = new RelayCommand(_ => _ = TrainQLearningAsync());
            StartBothCommand = new RelayCommand(_ => _ = StartBothAsync());

            GenerateBoards();
            // Start Q-Learning training in background so right-side agent is ready at startup
            var trainTask = TrainQLearningAsync();
            trainTask.ContinueWith(t =>
            {
                if (t.Exception != null)
                    AlgorithmTimeRight = $"Error: {t.Exception.GetBaseException().Message}";
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
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
        public ICommand TrainQLearningCommand { get; }
        public ICommand StartBothCommand { get; }

        public int BoardSize { get => _boardSize; set { if (Set(ref _boardSize, value)) OnPropertyChanged(nameof(CellSize)); } }
        public int ApplesCount { get => _applesCount; set => Set(ref _applesCount, value); }
        public int StepsLimit { get => _stepsLimit; set => Set(ref _stepsLimit, value); }
        public int AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, value); }

        public double CellSize => Math.Max(8.0, 400.0 / Math.Max(1, BoardSize));

        public string AlgorithmTimeLeft { get => _algTimeLeft; private set => Set(ref _algTimeLeft, value); }
        public string DecisionTimeLeft { get => _decTimeLeft; private set => Set(ref _decTimeLeft, value); }
        public string AlgorithmTimeRight { get => _algTimeRight; private set => Set(ref _algTimeRight, value); }
        public string DecisionTimeRight { get => _decTimeRight; private set => Set(ref _decTimeRight, value); }
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
            var baseBoard = new GameBoard(BoardSize, BoardSize, ApplesCount);
            _boardBack = new GameBoard(baseBoard);
            _boardQL = new GameBoard(baseBoard);

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
            AlgorithmTimeLeft = DecisionTimeLeft = AlgorithmTimeRight = DecisionTimeRight = "";

            OnPropertyChanged(nameof(CellSize));

            // Retrain Q-Learning for the new board configuration (size/apples)
            AlgorithmTimeRight = "Training: started...";
            var trainTask = TrainQLearningAsync();
            trainTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    AlgorithmTimeRight = $"Training error: {t.Exception?.GetBaseException().Message}";
                }
                else
                {
                    AlgorithmTimeRight = AlgorithmTimeRight; // leave the training message set by TrainQLearningAsync
                }
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private async Task TrainQLearningAsync()
        {
            // Use trainer to run several trainings and save best q-table
            var trainer = new QLearningTrainer();
            var sw = Stopwatch.StartNew();
            var best = await trainer.RunAndSaveBestAsync(_boardQL, StepsLimit, runs: 5, progress: new System.Progress<string>(s => { }));
            sw.Stop();
            AlgorithmTimeRight = $"Training: {sw.ElapsedMilliseconds} ms (best saved: {best})";
        }

        private async Task StartBacktrackingAsync()
        {
            var solver = new BacktrackingSolver();
            // Run planner on threadpool to avoid UI freeze and measure planning time
            var swPlan = Stopwatch.StartNew();
            await Task.Run(() => solver.PrepareAsync(_boardBack, StepsLimit, new System.Progress<string>(s => { })));
            swPlan.Stop();

            // Now execute plan and measure decision time
            long decisionTicks = 0;
            int stepsRemaining = StepsLimit;
            int applesEaten = 0;
            int stepsTaken = 0;

            _timerBack.Restart();
            while (stepsRemaining > 0)
            {
                var t0 = Stopwatch.GetTimestamp();
                var next = solver.GetNextMove(_boardBack, _boardBack.CaterpillarHead, _boardBack.LastPosition);
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
                    PlannedApplesLeft = solver.PlannedApples;
                    _timerBack.Stop();
                    return;
                }

                if (ate) applesEaten++;
                stepsTaken++;
                // stop if no apples remain
                if (_boardBack.ApplesRemaining == 0)
                {
                    stepsRemaining = 0;
                    UpdateCells(CellsBack);
                    break;
                }
                stepsRemaining--;
                UpdateCells(CellsBack);
                await Task.Delay(AnimationSpeed);
            }
            _timerBack.Stop();

            AlgorithmTimeLeft = $"Planning: {swPlan.ElapsedMilliseconds} ms";
            DecisionTimeLeft = $"Decision time: { (decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
            ApplesEatenLeft = applesEaten;
            StepsTakenLeft = stepsTaken;
            PlannedApplesLeft = solver.PlannedApples;
        }

        private async Task StartQLearningAsync()
        {
            try
            {
                var agent = new QLearningAgent();
                // Run training/loading on background thread to avoid UI freeze
                var swTrain = Stopwatch.StartNew();
                await Task.Run(() => agent.PrepareAsync(_boardQL, StepsLimit, new System.Progress<string>(s => { })));
                swTrain.Stop();

                long decisionTicks = 0;
                int stepsRemaining = StepsLimit;
                int applesEaten = 0;
                int stepsTaken = 0;

                _timerRight.Restart();
                while (stepsRemaining > 0)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var next = agent.GetNextMove(_boardQL, _boardQL.CaterpillarHead, _boardQL.LastPosition);
                    var t1 = Stopwatch.GetTimestamp();
                    decisionTicks += (t1 - t0);

                    if (!_boardQL.IsValidMove(next)) break;
                    var ate = _boardQL.MoveCaterpillarTo(next);
                    if (ate) applesEaten++;
                    stepsTaken++;
                // stop if no apples remain
                if (_boardQL.ApplesRemaining == 0)
                {
                    stepsRemaining = 0;
                    UpdateCells(CellsRight);
                    break;
                }
                stepsRemaining--;
                    UpdateCells(CellsRight);
                    await Task.Delay(AnimationSpeed);
                }

                _timerRight.Stop();
                AlgorithmTimeRight = $"Train: {swTrain.ElapsedMilliseconds} ms, Exec: {_timerRight.ElapsedMilliseconds} ms";
                DecisionTimeRight = $"Decision time: { (decisionTicks * 1000.0 / Stopwatch.Frequency):F2} ms";
            ApplesEatenRight = applesEaten;
            StepsTakenRight = stepsTaken;
            }
            catch (Exception ex)
            {
                AlgorithmTimeRight = $"Error: {ex.Message}";
            }
        }

        private void UpdateCells(ObservableCollection<CellViewModel> coll)
        {
            foreach (var c in coll) c.Refresh();
        }
    }
}
