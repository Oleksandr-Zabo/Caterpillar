# Caterpillar

A .NET 10 WPF application for collecting apples from a grid using Beam Search and tabular Q-learning.

## Board

The default board is loaded from `data.txt` at application startup. The current file contains:

- 35 rows
- 60 columns
- 1,050 apples, including the start-cell check
- Start position: `(0, 0)`

The step limit is configured in the UI. The default is 600 steps.

## Algorithms

### Beam Search

Beam Search performs bounded lookahead over the grid and selects the highest-scoring route candidate. The current measured configuration reaches 547 apples on the supplied board in 600 steps.

### Q-learning

Q-learning uses the required tabular architecture:

```text
CustomHash<string, double[]>
```

The state table stores four action values for each state. Q-learning has two explicit modes.

## Q-learning modes

### Training mode

Use **Train Q-Learning** in the application.

Training mode:

1. Loads an existing table when available.
2. Trains parallel candidate agents with exploration enabled.
3. Updates Q-values using the configured learning rate and discount factor.
4. Evaluates completed candidates on the configured step limit.
5. Selects the candidate with the highest measured apple score.
6. Saves the selected table for later Run mode execution.

Training preparation is bounded by approximately three minutes. If the deadline is reached, completed candidates are retained and the best completed policy is selected.

Generated files are board-specific:

```text
qtable_v7_<rows>x<columns>_<board-hash>.json
best_qtable_v7_<rows>x<columns>_<board-hash>.json
```

The `best_qtable` file is the preferred file for Run mode.

### Run mode

Use **Run Q-Learning** in the application after training has completed.

Run mode:

1. Loads `best_qtable_v7_...json`, or the regular `qtable_v7_...json` as a fallback.
2. Does not train.
3. Does not update Q-values.
4. Uses `epsilon = 0`.
5. Selects the highest Q-value among legal actions.
6. Uses deterministic tie-breaking.

If no trained table exists for the current board, Run mode stops with an instruction to train first.

Run mode never overwrites the trained table.

## Build and run

From the repository directory:

```powershell
dotnet build .\Caterpillar.csproj
dotnet run --project .\Caterpillar.csproj
```

The project targets `net10.0-windows` and uses WPF.

## Typical workflow

1. Start the application.
2. Confirm that `data.txt` is loaded.
3. Set the desired step limit.
4. Click **Train Q-Learning**.
5. Wait for training and candidate evaluation to finish.
6. Click **Run Q-Learning** to execute the saved greedy table without further learning.
7. Use **Start Beam Search** for the deterministic search policy.

The table is board-specific. Retrain after changing `data.txt` or the board layout.

## Project structure

```text
Algorithms/
  AppleRoutePlanner.cs       Shared bounded route search
  BeamSearchSolver.cs       Beam Search policy
  GraphRoutePlanner.cs       Grid-graph route utilities
  IAlgorithm.cs              Algorithm contract
  QLearningAgent.cs          Training and Run mode Q-learning
  QLearningMode.cs           Train/Run mode enum
DataStructures/
  CustomHash.cs              Tabular Q-learning storage
Models/
  GameBoard.cs               Board loading and movement rules
ViewModels/
  MainViewModel.cs           WPF commands and execution
MainWindow.xaml              WPF user interface
data.txt                    Default apple board
```
