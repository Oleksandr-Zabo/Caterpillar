using System.Threading.Tasks;
using System.Threading;
using Caterpillar.Models;

namespace Caterpillar.Algorithms
{
    public interface IAlgorithm
    {
        string Name { get; }

        (int x, int y) GetNextMove(GameBoard board, (int x, int y) currentHead, (int x, int y)? lastPos);

        Task PrepareAsync(GameBoard board, int stepsLimit, System.IProgress<string> progress, CancellationToken cancellationToken = default);
    }
}
