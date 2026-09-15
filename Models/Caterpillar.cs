using System.Collections.Generic;

namespace Caterpillar.Models
{
    public class Caterpillar
    {
        private readonly LinkedList<(int x, int y)> _segments = new();
        public Caterpillar(int startX, int startY, int initialLength = 3)
        {
            // place head at start position and extend to the left (decreasing x)
            _segments.AddFirst((startX, startY));
            for (int i = 1; i < initialLength; i++)
            {
                _segments.AddLast((startX - i, startY));
            }
        }

        public (int x, int y) Head => _segments.First.Value;

        public IEnumerable<(int x, int y)> Segments => _segments;

        // Move head to new position. If grow is true (ate fruit), don't remove tail.
        // Returns the removed tail position when not growing; otherwise null.
        public (int x, int y)? MoveTo((int x, int y) pos, bool grow = false)
        {
            _segments.AddFirst(pos);
            if (!grow)
            {
                var last = _segments.Last.Value;
                _segments.RemoveLast();
                return last;
            }
            return null;
        }
    }
}
