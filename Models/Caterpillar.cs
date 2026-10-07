namespace Caterpillar.Models
{
    public sealed class Caterpillar
    {
        public Caterpillar(int startX = 0, int startY = 0)
        {
            Head = (startX, startY);
        }

        public (int x, int y) Head { get; private set; }

        public void MoveTo((int x, int y) position)
        {
            Head = position;
        }
    }
}