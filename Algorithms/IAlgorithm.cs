using System.Threading.Tasks;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    public interface IAlgorithm
    {
        string Name { get; }

        // Return next move position
        (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos);

        // Optional prepare/train step
        Task PrepareAsync(GameBoard board, int stepsLimit, System.IProgress<string> progress);
    }
}
