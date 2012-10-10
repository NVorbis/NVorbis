/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NVorbis
{
    class RingBuffer<T>
    {
        T[] _buffer;
        int _start;
        int _end;
        int _bufLen;

        internal RingBuffer(int size)
        {
            _buffer = new T[size];
            _start = _end = 0;
            _bufLen = size;
        }

        internal void EnsureSize(int size)
        {
            // because _end == _start signifies no data, and _end is always 1 more than the data we have, we must make the buffer {channels} entries bigger than requested
            if (_bufLen < size + Channels)
            {
                size += Channels;

                var temp = new T[size];
                Array.Copy(_buffer, _start, temp, 0, _bufLen - _start);
                if (_end < _start)
                {
                    Array.Copy(_buffer, 0, temp, _bufLen - _start, _end);
                }
                var end = Length;
                _start = 0;
                _end = end;
                _buffer = temp;

                _bufLen = size;
            }
        }

        internal int Channels;

        internal T this[int rawIndex]
        {
            get { return this[rawIndex % Channels, rawIndex / Channels]; }
            set { this[rawIndex % Channels, rawIndex / Channels] = value; }
        }

        internal T this[int channel, int index]
        {
            get
            {
                if (index >= 0)
                {
                    var idx = index * Channels + channel;
                    if (idx >= _bufLen) throw new IndexOutOfRangeException();

                    return _buffer[(idx + _start) % _bufLen];
                }
                return default(T);
            }
            set
            {
                if (index >= 0)
                {
                    var idx = index * Channels + channel;
                    if (idx >= _bufLen) throw new IndexOutOfRangeException();

                    idx = (idx + _start) % _bufLen;


                    if (idx >= _end)    // this will catch wrap-around naturally (doesn't matter where start is...)
                    {
                        _end = idx + 1;
                        if (_end == _bufLen) _end = 0;
                    }
                    else if (idx < _start)  // catch the case where the idx has wrapped, but _end hasn't...
                    {
                        _end = idx + 1;
                    }

                    _buffer[idx] = value;
                }
            }
        }

        internal void CopyTo(T[] buffer, int index, int count)
        {
            if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException("index");

            var start = _start;
            RemoveItems(count);

            // this is used to pull data out of the buffer, so we'll update the start position too...
            var len = (_end - start + _bufLen) % _bufLen;
            if (count > len) throw new ArgumentOutOfRangeException("count");

            var cnt = Math.Min(count, _bufLen - start);
            Array.Copy(_buffer, start, buffer, index, cnt);

            if (cnt < count)
            {
                Array.Copy(_buffer, 0, buffer, index + cnt, count - cnt);
            }
        }

        internal void RemoveItems(int count)
        {
            var cnt = (count + _start) % _bufLen;
            if (_end > _start)
            {
                if (cnt > _end || cnt < _start) throw new ArgumentOutOfRangeException();
            }
            else
            {
                // wrap-around
                if (cnt < _start && cnt > _end) throw new ArgumentOutOfRangeException();
            }

            _start = cnt;
        }

        internal void Clear()
        {
            _start = _end;
        }

        internal int Length
        {
            get
            {
                var temp = _end - _start;
                if (temp < 0) temp += _bufLen;
                return temp;
            }
        }
    }
}
