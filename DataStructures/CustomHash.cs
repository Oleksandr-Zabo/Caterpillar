using System;
using System.Collections;
using System.Collections.Generic;

namespace Caterpillar.DataStructures
{
    // Simple open-addressing hash map for string keys to TValue
    public class CustomHash<TValue>
    {
        private struct Entry { public string Key; public TValue Value; public bool Used; }

        private Entry[] _entries;
        private int _count;

        public CustomHash(int capacity = 256)
        {
            _entries = new Entry[Math.Max(16, capacity)];
            _count = 0;
        }

        private int IndexFor(string key)
        {
            var h = (uint)key.GetHashCode();
            return (int)(h % (uint)_entries.Length);
        }

        public bool TryGetValue(string key, out TValue value)
        {
            int idx = IndexFor(key);
            int start = idx;
            do
            {
                var e = _entries[idx];
                if (!e.Used) break;
                if (e.Used && e.Key == key)
                {
                    value = e.Value; return true;
                }
                idx = (idx + 1) % _entries.Length;
            } while (idx != start);
            value = default!;
            return false;
        }

        public TValue GetOrAdd(string key, Func<TValue> factory)
        {
            if (TryGetValue(key, out var v)) return v;
            var val = factory();
            Set(key, val);
            return val;
        }

        public void Set(string key, TValue value)
        {
            if (_count * 2 >= _entries.Length) Resize(_entries.Length * 2);
            int idx = IndexFor(key);
            while (_entries[idx].Used)
            {
                if (_entries[idx].Key == key)
                {
                    _entries[idx].Value = value; return;
                }
                idx = (idx + 1) % _entries.Length;
            }
            _entries[idx].Key = key;
            _entries[idx].Value = value;
            _entries[idx].Used = true;
            _count++;
        }

        private void Resize(int newSize)
        {
            var old = _entries;
            _entries = new Entry[newSize];
            _count = 0;
            foreach (var e in old)
            {
                if (e.Used)
                {
                    Set(e.Key, e.Value);
                }
            }
        }

        public Dictionary<string, TValue> ToDictionary()
        {
            var d = new Dictionary<string, TValue>(_count);
            foreach (var e in _entries)
            {
                if (e.Used) d[e.Key] = e.Value;
            }
            return d;
        }

        public void LoadFromDictionary(Dictionary<string, TValue> d)
        {
            if (d == null) return;
            _entries = new Entry[Math.Max(16, d.Count * 2)];
            _count = 0;
            foreach (var kv in d)
            {
                Set(kv.Key, kv.Value);
            }
        }
    }
}
