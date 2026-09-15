using System;
using System.Collections;
using System.Collections.Generic;

namespace Caterpillar.DataStructures
{
    // Simple generic stack implementation (requested custom stack)
    public class CustomStack<T> : IEnumerable<T>
    {
        private T[] _items;
        private int _count;

        public CustomStack(int capacity = 16)
        {
            _items = new T[capacity];
            _count = 0;
        }

        public int Count => _count;

        public void Push(T item)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }
            _items[_count++] = item;
        }

        public T Pop()
        {
            if (_count == 0) throw new InvalidOperationException("Stack is empty");
            var idx = --_count;
            var item = _items[idx];
            _items[idx] = default!;
            return item;
        }

        public T Peek()
        {
            if (_count == 0) throw new InvalidOperationException("Stack is empty");
            return _items[_count - 1];
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _count);
            _count = 0;
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = _count - 1; i >= 0; i--)
                yield return _items[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
