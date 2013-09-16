/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2013, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis
{
    partial class StreamReadBuffer
    {
        class StreamWrapper
        {
            internal Stream Source;
            internal object LockObject = new object();
            internal long EofOffset = long.MaxValue;
        }

        static Dictionary<Stream, StreamWrapper> _lockObjects = new Dictionary<Stream, StreamWrapper>();

        internal StreamReadBuffer(Stream source, int initialSize, int maxSize, bool minimalRead)
        {
            StreamWrapper wrapper;
            if (!_lockObjects.TryGetValue(source, out wrapper))
            {
                _lockObjects.Add(source, new StreamWrapper { Source = source });
                wrapper = _lockObjects[source];

                if (source.CanSeek)
                {
                    // assume that this is a quick operation
                    wrapper.EofOffset = source.Length;
                }
            }

            // make sure our initial size is a power of 2 (this makes resizing simpler to understand)
            initialSize = 2 << (int)Math.Log(initialSize - 1, 2);

            // make sure our max size is a power of 2 (in this case, just so we report a "real" number)
            maxSize = 1 << (int)Math.Log(maxSize, 2);

            _wrapper = wrapper;
            _data = new byte[initialSize];
            _maxSize = maxSize;
            _minimalRead = minimalRead;
        }

        StreamWrapper _wrapper;
        int _maxSize;

        byte[] _data;
        long _baseOffset;
        int _end;
        int _discardCount;

        bool _minimalRead;

        /// <summary>
        /// Gets or Sets whether to limit reads to the smallest size possible.
        /// </summary>
        public bool MinimalRead
        {
            get { return _minimalRead; }
            set { _minimalRead = value; }
        }

        /// <summary>
        /// Gets or Sets the maximum size of the buffer.  This is not a hard limit.
        /// </summary>
        public int MaxSize
        {
            get { return _maxSize; }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException("Must be greater than zero.");

                var newMaxSize = 1 << (int)Math.Ceiling(Math.Log(value, 2));

                if (newMaxSize < _end)
                {
                    if (newMaxSize < _end - _discardCount)
                    {
                        // we can't discard enough bytes to satisfy the buffer request...
                        throw new ArgumentOutOfRangeException("Must be greater than or equal to the number of bytes currently buffered.");
                    }

                    CommitDiscard();
                    var newBuf = new byte[newMaxSize];
                    Buffer.BlockCopy(_data, 0, newBuf, 0, _end);
                    _data = newBuf;
                }
                _maxSize = newMaxSize;
            }
        }

        /// <summary>
        /// Gets the offset of the start of the buffered data.  Reads to offsets before this are likely to require a seek.
        /// </summary>
        public long BaseOffset
        {
            get { return _baseOffset + _discardCount; }
        }

        /// <summary>
        /// Gets the number of bytes currently buffered.
        /// </summary>
        public int BytesFilled
        {
            get { return _end - _discardCount; }
        }

        /// <summary>
        /// Gets the number of bytes the buffer can hold.
        /// </summary>
        public int Length
        {
            get { return _data.Length; }
        }

        internal long BufferEndOffset
        {
            get
            {
                if (_end - _discardCount > 0)
                {
                    // this is the base offset + discard bytes + buffer max length (though technically we could go a little further...)
                    return _baseOffset + _discardCount + _maxSize;
                }
                // if there aren't any bytes in the buffer, we can seek wherever we want
                return _wrapper.Source.Length;
            }
        }

        /// <summary>
        /// Reads the number of bytes specified into the buffer given, starting with the offset indicated.
        /// </summary>
        /// <param name="offset">The offset into the stream to start reading.</param>
        /// <param name="buffer">The buffer to read to.</param>
        /// <param name="index">The index into the buffer to start writing to.</param>
        /// <param name="count">The number of bytes to read.</param>
        /// <returns>The number of bytes read.</returns>
        public int Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0L || offset >= _wrapper.EofOffset) throw new ArgumentOutOfRangeException("offset");
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException("index");
            if (count < 0) throw new ArgumentOutOfRangeException("count");

            var startIdx = EnsureAvailable(offset, ref count);

            Buffer.BlockCopy(_data, startIdx, buffer, index, count);

            return count;
        }

        internal int ReadByte(long offset)
        {
            if (offset < 0L || offset >= _wrapper.EofOffset) throw new ArgumentOutOfRangeException("offset");

            int count = 1;
            var startIdx = EnsureAvailable(offset, ref count);
            if (count == 1)
            {
                return _data[startIdx];
            }

            return -1;
        }

        int EnsureAvailable(long offset, ref int count)
        {
            // short-circuit if the offset & count are inside our buffer's range
            // if we're not doing minimal read, this should be the typical path
            var startIdx = (int)(offset - _baseOffset);
            int endIdx = startIdx + count;
            if (startIdx >= 0 && endIdx <= _end)
            {
                return startIdx;
            }

            // we don't already have enough data in the buffer to satisfy the request...  Go get it

            // declare some variables...
            int readStart, readCount, moveCount;
            long readOffset;

            // go figure out our read parameters
            CalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset);

            // prepare the buffer for the read
            PrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, readOffset);

            // fill the buffer appropriately
            count = FillBuffer(startIdx, count, readStart, readCount, readOffset);

            return startIdx;
        }

        void CalculateRead(ref int startIdx, ref int endIdx, out int readStart, out int readCount, out int moveCount, out long readOffset)
        {
            if (startIdx < 0)
            {
                // if we can't seek, there's nothing we can do
                if (!_wrapper.Source.CanSeek) throw new InvalidOperationException("Cannot seek backwards on a forward-only stream!");

                // we know we'll have to start reading here
                readOffset = _baseOffset + startIdx;

                // if there's data in the buffer, try to keep it (up to the maximum buffer size)
                if (_end - startIdx <= _maxSize)
                {
                    // we have to move the data, so do so
                    moveCount = startIdx;
                    readStart = startIdx;
                    readCount = -startIdx;
                }
                // if the end of the request is before the start of our buffer...
                else
                {
                    // ... just truncate and move on
                    moveCount = _maxSize;
                    readStart = moveCount;
                    readCount = endIdx - startIdx;
                    startIdx = moveCount;
                    endIdx = startIdx + readCount;
                }
            }
            else // i.e., startIdx >= 0
            {
                // we only get to here if at least one byte of the request is past the end of the read data
                // start with the simplest scenario and work our way up

                // 1) We just need to fill the buffer a bit more
                if (endIdx < _data.Length)
                {
                    moveCount = 0;
                    readStart = _end;
                    readCount = endIdx - readStart;
                    readOffset = _baseOffset + readStart;
                }
                // 2) There's enough room to save some data without discarding
                else if (endIdx < _maxSize)
                {
                    moveCount = 0;
                    readStart = _end;
                    readCount = endIdx - readStart;
                    readOffset = _baseOffset + readStart;
                }
                // 3) There's enough room to save some data with discarding
                else if (endIdx - _discardCount < _maxSize)
                {
                    moveCount = _discardCount;
                    readStart = _end;
                    readCount = endIdx - readStart;
                    readOffset = _baseOffset + readStart;
                }
                // 4) We have to throw away some data that hasn't been discarded
                else
                {
                    // just truncate
                    moveCount = _maxSize;
                    readStart = moveCount;
                    readCount = endIdx - startIdx;
                    readOffset = _baseOffset + startIdx;
                    startIdx = moveCount;
                    endIdx = startIdx + readCount;
                }
            }
        }

        void PrepareBufferForRead(ref int startIdx, ref int endIdx, ref int readStart, int readCount, int moveCount, long readOffset)
        {
            if (Math.Abs(moveCount) >= _maxSize)
            {
                // make sure our counts come in right
                _discardCount = 0;
                _end = 0;

                // adjust the numbers back to reality
                startIdx -= moveCount;
                endIdx -= moveCount;
                readStart -= moveCount;
                moveCount = 0;

                // update our base offset to reflect the real base
                _baseOffset = readOffset + startIdx;

                if (startIdx != 0)
                {
                    // adjust so startIdx = 0
                    endIdx -= startIdx;
                    readStart -= startIdx;
                    startIdx = 0;
                }
            }

            // figure out the minimum index we'll actually touch (including non-discarded bytes)
            var firstIndex = Math.Min(Math.Min(startIdx, readStart) - moveCount, _discardCount);
            // figure out the maximum index we'll actually touch
            var lastIndex = Math.Max(Math.Max(endIdx, readStart + readCount), _end) - moveCount;
            // if they are more the _data.Length apart, resize; otherwise try to move the data
            var reqSize = Math.Abs(lastIndex - firstIndex);

            // figure out if we need to resize the buffer
            byte[] newBuf = _data;
            if (reqSize > _data.Length)
            {
                var newSize = _data.Length * 2;
                while (newSize < reqSize)
                {
                    newSize *= 2;
                }
                
                if (newSize <= _maxSize)
                {
                    newBuf = new byte[newSize];

                    if (_discardCount > 0 && newSize == _maxSize)
                    {
                        // we need to discard everything... move it out
                        moveCount += _discardCount;
                    }
                }
                else
                {
                    throw new InvalidOperationException("Not enough room in the buffer!  Increase the maximum size and try again.");
                }
            }
            else if (lastIndex > _data.Length)
            {
                // if we discard everything and still can't hold all the data, something is wrong
                if (lastIndex - _discardCount > _data.Length)
                {
                    // this shouldn't happen
                    throw new InvalidOperationException("PrepareBufferForRead got confused!");
                }

                // only discard as much as we absolutely have to
                moveCount += (lastIndex - _data.Length);
            }

            // if moveCount is non-zero, we have to move some data around
            if (moveCount != 0)
            {
                // forward copy, reverse copy, or truncate?
                if (moveCount > 0)
                {
                    // forward move
                    Buffer.BlockCopy(_data, moveCount, newBuf, 0, _end - moveCount);

                    if ((_discardCount -= moveCount) < 0) _discardCount = 0;
                }
                else
                {
                    // reverse move
                    for (int srcIdx = _data.Length - 1 + moveCount, destIdx = _data.Length - 1; srcIdx >= 0; srcIdx--, destIdx--)
                    {
                        newBuf[destIdx] = _data[srcIdx];
                    }

                    _discardCount = 0;
                }

                // remove moveCount from the indexes
                _baseOffset += moveCount;
                readStart -= moveCount;
                startIdx -= moveCount;
                endIdx -= moveCount;
                _end -= moveCount;
            }
            else if (_end > 0 && !object.ReferenceEquals(_data, newBuf))
            {
                // just do a straight copy
                Buffer.BlockCopy(_data, 0, newBuf, 0, _data.Length);
            }
            _data = newBuf;
        }

        int FillBuffer(int startIdx, int count, int readStart, int readCount, long readOffset)
        {
            // This lock is for sitations where more than one BufferedReadStream instance are wrapping the same stream.
            //    Otherwise it is redundant.
            lock (_wrapper.LockObject)
            {
                readCount = PrepareStreamForRead(readCount, readOffset);

                ReadStream(readStart, readCount, readOffset);

                if (_end < startIdx + count)
                {
                    // we didn't get a full read...
                    count = Math.Max(0, _end - startIdx);
                }
                else if (!_minimalRead && _end < _data.Length)
                {
                    // try to finish filling the buffer
                    _end += _wrapper.Source.Read(_data, _end, _data.Length - _end);
                }
            }

            return count;
        }

        int PrepareStreamForRead(int readCount, long readOffset)
        {
            if (readCount > 0 && _wrapper.Source.Position != readOffset)
            {
                if (readOffset < _wrapper.EofOffset)
                {
                    if (_wrapper.Source.CanSeek)
                    {
                        _wrapper.Source.Position = readOffset;
                    }
                    else
                    {
                        // ugh, gotta read bytes until we've reached the desired offset
                        var seekCount = readOffset - _wrapper.Source.Position;
                        if (seekCount < 0)
                        {
                            // not so fast... we can't seek backwards.  This technically shouldn't happen, but just in case...
                            readCount = 0;
                        }
                        else
                        {
                            while (--seekCount >= 0)
                            {
                                if (_wrapper.Source.ReadByte() == -1)
                                {
                                    // crap... we just threw away a bunch of bytes for no reason
                                    _wrapper.EofOffset = _wrapper.Source.Position;
                                    readCount = 0;
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    readCount = 0;
                }
            }
            return readCount;
        }

        void ReadStream(int readStart, int readCount, long readOffset)
        {
            while (readCount > 0 && readOffset < _wrapper.EofOffset)
            {
                var temp = _wrapper.Source.Read(_data, readStart, readCount);
                if (temp == 0)
                {
                    break;
                }
                readStart += temp;
                readOffset += temp;
                readCount -= temp;
            }

            if (readStart > _end)
            {
                _end = readStart;
            }
        }

        /// <summary>
        /// Tells the buffer that it no longer needs to maintain any bytes before the indicated offset.
        /// </summary>
        /// <param name="offset">The offset to discard through.</param>
        public void DiscardThrough(long offset)
        {
            var count = (int)(offset - _baseOffset);
            _discardCount = Math.Max(count, _discardCount);

            if (_discardCount >= _data.Length) CommitDiscard();
        }

        void CommitDiscard()
        {
            if (_discardCount >= _data.Length || _discardCount >= _end)
            {
                // we have been told to discard the entire buffer
                _baseOffset += _discardCount;
                _end = 0;
            }
            else
            {
                // just discard the first part...
                Buffer.BlockCopy(_data, _discardCount, _data, 0, _end - _discardCount);
                _baseOffset += _discardCount;
                _end -= _discardCount;
            }
            _discardCount = 0;
        }
    }
}
