using System;
using System.Collections.Generic;
using System.Linq;

namespace Caterpillar.Models
{
    public class GameBoard
    {
        private readonly Random _rng = new();
        private readonly GridCell[,] _cells;
        private readonly int _rows;
        private readonly int _cols;
        private readonly Caterpillar _caterpillar;

        public int ApplesRemaining { get; private set; }

        public (int x, int y) LastPosition { get; private set; }

        public GameBoard(int rows, int cols, int apples)
        {
            _rows = rows;
            _cols = cols;
            _cells = new GridCell[rows, cols];

            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                _cells[r, c] = new GridCell { X = r, Y = c };

            // place caterpillar in center with initial length 3
            int sx = rows / 2;
            int sy = cols / 2;
            _caterpillar = new Caterpillar(sx, sy, 3);
            // mark caterpillar cells (head + tail)
            foreach (var seg in _caterpillar.Segments)
            {
                _cells[seg.x, seg.y].Type = seg.Equals(_caterpillar.Head) ? CellType.Head : CellType.Tail;
            }
            LastPosition = _caterpillar.Head;

            PlaceApples(apples);
        }

        // Clone constructor: create a new board with the same cell types and caterpillar head
        public GameBoard(GameBoard other)
        {
            _rows = other._rows;
            _cols = other._cols;
            _cells = new GridCell[_rows, _cols];

            for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                _cells[r, c] = new GridCell { X = r, Y = c, Type = other._cells[r, c].Type };

            var head = other.CaterpillarHead;
            _caterpillar = new Caterpillar(head.x, head.y);
            _cells[head.x, head.y].Type = CellType.Head;
            LastPosition = other.LastPosition;
            ApplesRemaining = other.ApplesRemaining;
        }

        public int Rows => _rows;
        public int Cols => _cols;

        private void PlaceApples(int apples)
        {
            ApplesRemaining = apples;
            var positions = new List<(int x, int y)>();

            for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                if (_cells[r, c].Type == CellType.Empty)
                    positions.Add((r, c));

            for (int i = 0; i < apples && positions.Count > 0; i++)
            {
                int idx = _rng.Next(positions.Count);
                var p = positions[idx];
                positions.RemoveAt(idx);
                _cells[p.x, p.y].Type = CellType.Apple;
            }
        }

        public GridCell[,] Cells => _cells;

        public (int x, int y) CaterpillarHead => _caterpillar.Head;

        public IEnumerable<(int x, int y)> CaterpillarSegments => _caterpillar.Segments;

        public bool IsValidMove((int x, int y) pos)
        {
            if (pos.x < 0 || pos.x >= _rows || pos.y < 0 || pos.y >= _cols)
                return false;

            // cannot move back to last position
            if (pos == LastPosition)
                return false;

            // cannot move into caterpillar tail (self-collision)
            if (_cells[pos.x, pos.y].Type == CellType.Tail)
                return false;

            // must be adjacent (manhattan distance 1)
            var head = CaterpillarHead;
            int md = Math.Abs(head.x - pos.x) + Math.Abs(head.y - pos.y);
            return md == 1;
        }

        // Move caterpillar's head to position. Returns true if ate apple.
        public bool MoveCaterpillarTo((int x, int y) pos)
        {
            var head = CaterpillarHead;
            bool ate = _cells[pos.x, pos.y].Type == CellType.Apple;

            // set previous head to tail
            _cells[head.x, head.y].Type = CellType.Tail;

            // Move caterpillar and get removed tail position (if any)
            var removed = _caterpillar.MoveTo(pos, ate);

            // update new head cell
            _cells[pos.x, pos.y].Type = CellType.Head;

            // if tail was removed (didn't grow), clear that cell
            if (removed.HasValue)
            {
                var t = removed.Value;
                if (t.x >= 0 && t.x < _rows && t.y >= 0 && t.y < _cols)
                {
                    // only clear if not occupied by head or apple
                    if (_cells[t.x, t.y].Type != CellType.Head && _cells[t.x, t.y].Type != CellType.Apple)
                        _cells[t.x, t.y].Type = CellType.Empty;
                }
            }

            if (ate)
            {
                ApplesRemaining = Math.Max(0, ApplesRemaining - 1);
            }

            LastPosition = head;
            return ate;
        }
    }
}
