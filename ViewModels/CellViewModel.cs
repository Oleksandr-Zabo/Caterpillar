using System.Windows.Media;
using Caterpillar.Models;

namespace Caterpillar.ViewModels
{
    public class CellViewModel : BaseViewModel
    {
        private GridCell _cell;

        public CellViewModel(GridCell cell)
        {
            _cell = cell;
        }

        public Brush CellColor => _cell.GetBrush();
        public bool IsHead => _cell.Type == CellType.Head;
        public bool IsApple => _cell.Type == CellType.Apple;
        public bool IsGrape => _cell.Type == CellType.Grape;

        public void Refresh()
        {
            OnPropertyChanged(nameof(CellColor));
            OnPropertyChanged(nameof(IsHead));
            OnPropertyChanged(nameof(IsApple));
            OnPropertyChanged(nameof(IsGrape));
        }
    }
}
