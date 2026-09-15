using System.Windows.Media;

namespace Caterpillar.Models
{
    public enum CellType
    {
        Empty,
        Apple,
        Head,
        Tail
    }

    public class GridCell
    {
        public int X { get; set; }
        public int Y { get; set; }
        public CellType Type { get; set; } = CellType.Empty;

        public Brush GetBrush()
        {
            // Field is green, apple red, tail darker green, head darkest green
            return Type switch
            {
                CellType.Empty => Brushes.PaleGreen,
                CellType.Apple => Brushes.Red,
                CellType.Head => Brushes.DarkGreen,
                CellType.Tail => Brushes.Green,
                _ => Brushes.PaleGreen,
            };
        }
    }
}
