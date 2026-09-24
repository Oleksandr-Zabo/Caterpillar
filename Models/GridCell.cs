using System.Windows.Media;

namespace Caterpillar.Models
{
    public enum CellType
    {
        Empty,
        Apple,
        Grape,
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
            // Fruit cells keep a green field background; fruit is drawn separately in XAML.
            return Type switch
            {
                CellType.Empty => Brushes.PaleGreen,
                CellType.Apple => Brushes.PaleGreen,
                CellType.Grape => Brushes.PaleGreen,
                CellType.Head => Brushes.DarkGreen,
                CellType.Tail => Brushes.Green,
                _ => Brushes.PaleGreen,
            };
        }
    }
}
