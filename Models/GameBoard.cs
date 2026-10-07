using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Caterpillar.Models
{
    public sealed class GameBoard
    {
        public const int MinBoardDimension = 1;
        public const int MaxBoardDimension = 200;
        public const int StartX = 0;
        public const int StartY = 0;
        public const int MinBoardSize = MinBoardDimension;
        public const int MaxBoardSize = MaxBoardDimension;
        public const int InitialCaterpillarLength = 1;

        private readonly GridCell[,] _cells;
        private readonly Caterpillar _caterpillar;
        private readonly int _rows;
        private readonly int _cols;

        public GameBoard(int rows, int cols, int apples = 0, FruitType fruitType = FruitType.Apple)
        {
            _rows = ValidateDimension(rows, nameof(rows));
            _cols = ValidateDimension(cols, nameof(cols));
            _cells = CreateCells(_rows, _cols);
            _caterpillar = new Caterpillar(StartX, StartY);
            FruitType = fruitType;
            LastPosition = null;
            _cells[StartX, StartY].Type = CellType.Head;
            ApplesRemaining = 0;
        }

        public GameBoard(string filePath)
        {
            var rows = File.ReadAllLines(filePath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

            if (rows.Length == 0)
                throw new InvalidDataException("The board file does not contain any rows.");

            if (rows.Any(row => row.Any(character => character != '0' && character != '1')))
                throw new InvalidDataException("The board file may contain only 0 and 1 characters.");

            if (rows.Any(row => row.Length != rows[0].Length))
                throw new InvalidDataException("All board rows must have the same number of columns.");

            _rows = ValidateDimension(rows.Length, "rows");
            _cols = ValidateDimension(rows[0].Length, "columns");
            _cells = CreateCells(_rows, _cols);
            _caterpillar = new Caterpillar(StartX, StartY);
            FruitType = FruitType.Apple;
            LastPosition = null;

            for (var x = 0; x < _rows; x++)
            {
                for (var y = 0; y < _cols; y++)
                {
                    if (rows[x][y] == '1')
                    {
                        _cells[x, y].Type = CellType.Apple;
                        ApplesRemaining++;
                    }
                }
            }

            _cells[StartX, StartY].Type = CellType.Head;
            if (rows[StartX][StartY] == '1')
                ApplesRemaining--;
        }

        public GameBoard(GameBoard other)
        {
            _rows = other._rows;
            _cols = other._cols;
            _cells = CreateCells(_rows, _cols);
            for (var x = 0; x < _rows; x++)
            for (var y = 0; y < _cols; y++)
                _cells[x, y].Type = other._cells[x, y].Type;

            _caterpillar = new Caterpillar(other.CaterpillarHead.x, other.CaterpillarHead.y);
            FruitType = other.FruitType;
            ApplesRemaining = other.ApplesRemaining;
            LastPosition = other.LastPosition;
        }

        public int Rows => _rows;
        public int Cols => _cols;
        public int BoardSize => Math.Max(_rows, _cols);
        public GridCell[,] Cells => _cells;
        public FruitType FruitType { get; }
        public int ApplesRemaining { get; private set; }
        public int FruitsRemaining => ApplesRemaining;
        public (int x, int y) CaterpillarHead => _caterpillar.Head;
        public IEnumerable<(int x, int y)> CaterpillarSegments => new[] { CaterpillarHead };
        public (int x, int y)? LastPosition { get; private set; }

        public string LayoutKey
        {
            get
            {
                var layout = new System.Text.StringBuilder(_rows * (_cols + 1));
                layout.Append(_rows).Append('x').Append(_cols).Append(':');
                for (var x = 0; x < _rows; x++)
                {
                    for (var y = 0; y < _cols; y++)
                        layout.Append(_cells[x, y].Type == CellType.Apple ? '1' : '0');
                    layout.Append('/');
                }
                return layout.ToString();
            }
        }

        public static bool IsValidPosition(int x, int y, int boardSize, List<(int x, int y)>? body = null) =>
            x >= 0 && x < boardSize && y >= 0 && y < boardSize;

        public bool IsValidPosition((int x, int y) position) =>
            position.x >= 0 && position.x < _rows && position.y >= 0 && position.y < _cols;

        public bool IsValidMove((int x, int y) position)
        {
            if (!IsValidPosition(position))
                return false;

            var head = CaterpillarHead;
            return Math.Abs(head.x - position.x) + Math.Abs(head.y - position.y) == 1;
        }

        public bool MoveCaterpillarTo((int x, int y) position)
        {
            if (!IsValidMove(position))
                return false;

            var ate = IsFruit(position);
            var previous = CaterpillarHead;
            _cells[previous.x, previous.y].Type = CellType.Empty;
            _caterpillar.MoveTo(position);
            _cells[position.x, position.y].Type = CellType.Head;
            LastPosition = previous;

            if (ate)
                ApplesRemaining--;

            return ate;
        }

        public bool TryMoveCaterpillarTo((int x, int y) position, out bool ateApple)
        {
            ateApple = false;
            if (!IsValidMove(position))
                return false;

            ateApple = MoveCaterpillarTo(position);
            return true;
        }

        public bool IsFruit((int x, int y) position) =>
            IsValidPosition(position) && _cells[position.x, position.y].Type == CellType.Apple;

        private static GridCell[,] CreateCells(int rows, int cols)
        {
            var cells = new GridCell[rows, cols];
            for (var x = 0; x < rows; x++)
            for (var y = 0; y < cols; y++)
                cells[x, y] = new GridCell { X = x, Y = y, Type = CellType.Empty };
            return cells;
        }

        private static int ValidateDimension(int value, string name)
        {
            if (value < MinBoardDimension || value > MaxBoardDimension)
                throw new ArgumentOutOfRangeException(name, value, $"Board dimensions must be between {MinBoardDimension} and {MaxBoardDimension}.");
            return value;
        }
    }
}