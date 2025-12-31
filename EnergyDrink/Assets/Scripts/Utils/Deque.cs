using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class Deque<T>
{
    private T[] _buffer;
    private int _head;
    private int _count;

    public int Count => _count;
    public int Capacity => _buffer.Length;

    public Deque(int capacity = 8)
    {
        _buffer = new T[Mathf.Max(2, capacity)];
        _head = 0;
        _count = 0;
    }

    private int Mask => _buffer.Length - 1;

    private int Index(int i) => (_head + i) & Mask;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _buffer[Index(index)];
        }
    }

    public void PushFront(T value)
    {
        EnsureCapacity(_count + 1);
        _head = (_head - 1) & Mask;
        _buffer[_head] = value;
        _count++;
    }

    public void PushBack(T value)
    {
        EnsureCapacity(_count + 1);
        _buffer[Index(_count)] = value;
        _count++;
    }

    public T Front()
    {
        if (_count == 0) throw new InvalidOperationException();
        return _buffer[_head];
    }

    public T Back()
    {
        if (_count == 0) throw new InvalidOperationException();
        int idx = Index(_count - 1);
        return _buffer[idx];
    }

    public T PopFront()
    {
        if (_count == 0) throw new InvalidOperationException();
        T value = _buffer[_head];
        _buffer[_head] = default!;
        _head = (_head + 1) & Mask;
        _count--;
        return value;
    }

    public T PopBack()
    {
        if (_count == 0) throw new InvalidOperationException();
        int idx = Index(_count - 1);
        T value = _buffer[idx];
        _buffer[idx] = default!;
        _count--;
        return value;
    }

    public void Clear()
    {
        if (_count == 0) { return; }
        if (!typeof(T).IsValueType)
        {
            for (int i = 0; i < _count; i++)
                _buffer[Index(i)] = default!;
        }
        _head = 0;
        _count = 0;
    }

    public IEnumerable<T> Iter()
    {
        for (int i = 0; i < _count; i++)
            yield return _buffer[Index(i)];
    }


    private void EnsureCapacity(int size)
    {
        if (size <= _buffer.Length) return;

        int newCap = _buffer.Length << 1;
        T[] newBuf = new T[newCap];

        for (int i = 0; i < _count; i++)
            newBuf[i] = this[i];

        _buffer = newBuf;
        _head = 0;
    }
}
