using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Caterpillar.DataStructures;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    // Simple backtracking / greedy solver that tries to eat nearby apples using DFS with custom stack
    public class BacktrackingSolver : IAlgorithm
    {
        public string Name => "Backtracking";
        private Queue<(int x, int y)> _plan = new();
        private int _plannedApples = 0;

        public int PlannedApples => _plannedApples;

        public Task PrepareAsync(GameBoard board, int stepsLimit, System.IProgress<string> progress)
        {
            // Replace expensive DFS with greedy shortest-path planning to nearest apples.
            _plan.Clear();

            int rows = board.Rows;
            int cols = board.Cols;

            // copy apple positions
            var apples = new bool[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    apples[r, c] = board.Cells[r, c].Type == CellType.Apple;

            var body = new List<(int x, int y)>(board.CaterpillarSegments);
            var head = board.CaterpillarHead;
            int stepsLeft = stepsLimit;
            int applesPlanned = 0;

            progress?.Report("Backtracking: planning (greedy BFS to nearest apples)...");

            // helper: BFS to nearest apple avoiding current body positions
            List<(int x, int y)> FindPathToNearestApple((int x, int y) start)
            {
                var q = new Queue<((int x, int y) p, (int x, int y)? parent)>();
                var visited = new bool[rows, cols];
                var parent = new Dictionary<(int x, int y), (int x, int y)>();
                q.Enqueue((start, null));
                visited[start.x, start.y] = true;

                while (q.Count > 0)
                {
                    var cur = q.Dequeue().p;
                    if (apples[cur.x, cur.y])
                    {
                        // reconstruct path from start (exclusive) to cur (inclusive)
                        var path = new List<(int x, int y)>();
                        var p = cur;
                        while (!p.Equals(start))
                        {
                            path.Add(p);
                            p = parent[p];
                        }
                        path.Reverse();
                        return path;
                    }

                    foreach (var nb in new (int dx, int dy)[] { (-1,0),(1,0),(0,-1),(0,1) })
                    {
                        var nx = cur.x + nb.dx;
                        var ny = cur.y + nb.dy;
                        if (nx < 0 || nx >= rows || ny < 0 || ny >= cols) continue;
                        if (visited[nx, ny]) continue;
                        // avoid moving into body positions (except tail which will move if not eating)
                        if (body.Contains((nx, ny))) continue;
                        visited[nx, ny] = true;
                        parent[(nx, ny)] = cur;
                        q.Enqueue(((nx, ny), null));
                    }
                }
                return null;
            }

            while (stepsLeft > 0)
            {
                var path = FindPathToNearestApple(head);
                if (path == null || path.Count == 0) break;

                foreach (var stepPos in path)
                {
                    if (stepsLeft == 0) break;
                    _plan.Enqueue(stepPos);

                    bool cellHasApple = apples[stepPos.x, stepPos.y];
                    // simulate body movement
                    body.Insert(0, stepPos);
                    if (!cellHasApple)
                        body.RemoveAt(body.Count - 1);
                    else
                    {
                        apples[stepPos.x, stepPos.y] = false;
                        applesPlanned++;
                    }

                    head = stepPos;
                    stepsLeft--;
                }
            }

            _plannedApples = applesPlanned;
            progress?.Report($"Backtracking: plan length {_plan.Count}, apples planned: {_plannedApples}");
            return Task.CompletedTask;
        }

        public (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos)
        {
            if (_plan.Count > 0)
            {
                // return next valid planned move; skip any that became invalid
                while (_plan.Count > 0)
                {
                    var p = _plan.Dequeue();
                    if (p.x < 0 || p.x >= board.Rows || p.y < 0 || p.y >= board.Cols) continue;
                    if (!board.IsValidMove(p)) continue;
                    return p;
                }
            }

            // Fallback: choose any valid neighbor (prefer apple)
            var neighbors = GetNeighbors(currentHead, board);
            var apple = neighbors.FirstOrDefault(n => board.Cells[n.x, n.y].Type == CellType.Apple && board.IsValidMove(n));
            if (apple != default) return apple;

            foreach (var n in neighbors)
            {
                if (lastPos.HasValue && n == lastPos.Value) continue;
                if (board.IsValidMove(n)) return n;
            }

            return lastPos ?? currentHead;
        }

        private IEnumerable<(int x, int y)> GetNeighbors((int x, int y) p, GameBoard board)
        {
            yield return (p.x - 1, p.y);
            yield return (p.x + 1, p.y);
            yield return (p.x, p.y - 1);
            yield return (p.x, p.y + 1);
        }
    }
}
