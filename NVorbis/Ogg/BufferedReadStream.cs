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

namespace NVorbis.Ogg
{
    /// <summary>
    /// A thread-safe, read-only, buffering stream wrapper.
    /// </summary>
    class BufferedReadStream : Stream
    {
        const int DEFAULT_INITIAL_SIZE = 32768; // 32KB  (1/2 full page)
        const int DEFAULT_MAX_SIZE = 262144;    // 256KB (4 full pages)

        class StreamReadBuffer
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

                initialSize = 2 << (int)Math.Log(initialSize, 2);

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
            object _localLock = new object();

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

                    lock (_localLock)
                    {
                        if (_maxSize < _end)
                        {
                            if (_maxSize < _end - _discardCount)
                            {
                                // we can't discard enough bytes to satisfy the buffer request...
                                throw new ArgumentOutOfRangeException("Must be greater than or equal to the number of bytes currently buffered.");
                            }

                            CommitDiscard();
                            var newBuf = new byte[value];
                            Buffer.BlockCopy(_data, 0, newBuf, 0, _end);
                            _data = newBuf;
                        }
                        _maxSize = value;
                    }
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

                lock (_localLock)
                {
                    var startIdx = EnsureAvailable(offset, ref count);

                    Buffer.BlockCopy(_data, startIdx, buffer, index, count);
                }
                return count;
            }

            internal int ReadByte(long offset)
            {
                if (offset < 0L || offset >= _wrapper.EofOffset) throw new ArgumentOutOfRangeException("offset");

                lock (_localLock)
                {
                    int count = 1;
                    var startIdx = EnsureAvailable(offset, ref count);
                    if (count == 1)
                    {
                        return _data[startIdx];
                    }
                }

                return -1;
            }

            int EnsureAvailable(long offset, ref int count)
            {
                // if the offset & count are inside our buffer's range, just return the appropriate index
                var startIdx = (int)(offset - _baseOffset);
                int endIdx = startIdx + count;
                if (startIdx >= 0 && endIdx <= _end)
                {
                    return startIdx;
                }

                int readStart = 0, readCount = 0, moveCount = 0;
                long readOffset = 0;

                #region Decision-Making

                if (startIdx < 0)
                {
                    // if we can't seek, there's nothing we can do
                    if (!_wrapper.Source.CanSeek) throw new InvalidOperationException("Cannot seek backwards on a forward-only stream!");

                    // if there's data in the buffer, try to keep it (up to doubling the buffer size)
                    if (_end > 0)
                    {
                        // if doubling the buffer would push it past the max size, don't check it
                        if ((startIdx + _data.Length > 0) || (_data.Length * 2 <= _maxSize && startIdx + _data.Length * 2 > 0))
                        {
                            endIdx = _end;
                        }
                    }

                    // we know we'll have to start reading here
                    readOffset = offset;

                    // if the end of the request is before the start of our buffer...
                    if (endIdx < 0)
                    {
                        // ... just truncate and move on
                        Truncate();

                        // set up our read parameters
                        _baseOffset = offset;
                        startIdx = 0;
                        endIdx = count;

                        // how much do we need to read?
                        readCount = count;
                    }
                    else // i.e., endIdx >= 0
                    {
                        // we have overlap with existing data...  save as much as possible
                        moveCount = -endIdx;
                        readCount = -startIdx;
                    }
                }
                else // i.e., startIdx >= 0
                {
                    // we only get to here if at least one byte of the request is past the end of the read data
                    // start with the simplest scenario and work our way up

                    // 1) We just need to fill the buffer a bit more
                    if (endIdx < _data.Length)
                    {
                        readCount = endIdx - _end;
                        readStart = _end;
                        readOffset = _baseOffset + readStart;
                    }
                    // 2) We need to discard some bytes, then fill the buffer
                    else if (endIdx - _discardCount < _data.Length)
                    {
                        moveCount = _discardCount;
                        readStart = _data.Length;
                        readCount = endIdx - readStart;
                        readOffset = _baseOffset + readStart;
                    }
                    // 3) We need to expand the buffer to hold all the existing & requested data
                    else if (_data.Length * 2 <= _maxSize)
                    {
                        // by definition, we discard
                        moveCount = _discardCount;
                        readStart = _data.Length;
                        readCount = endIdx - _data.Length;
                        readOffset = _baseOffset + readStart;
                    }
                    // 4) We have to throw away some data that hasn't been discarded
                    else
                    {
                        // just truncate
                        Truncate();

                        // set up our read parameters
                        _baseOffset = offset;
                        startIdx = 0;
                        endIdx = count;

                        // how much do we have to read?
                        readCount = count;
                    }
                }

                #endregion

                #region Buffer Resizing & Data Moving

                if (endIdx - moveCount > _data.Length || readStart + readCount - moveCount > _data.Length)
                {
                    var newBuf = new byte[_data.Length * 2];
                    if (moveCount < 0)
                    {
                        // reverse copy
                        Buffer.BlockCopy(_data, 0, newBuf, -moveCount, _end);

                        _discardCount = 0;
                    }
                    else
                    {
                        // forward or neutral copy
                        Buffer.BlockCopy(_data, moveCount, newBuf, 0, _end - moveCount);

                        _discardCount -= moveCount;
                    }
                    _data = newBuf;
                }
                else if (moveCount != 0)
                {
                    if (moveCount > 0)
                    {
                        // forward move
                        Buffer.BlockCopy(_data, moveCount, _data, 0, _end - moveCount);

                        _discardCount -= moveCount;
                    }
                    else
                    {
                        // backward move
                        for (int i = 0, srcIdx = _data.Length - 1, destIdx = _data.Length - 1 - moveCount; i < moveCount; i++, srcIdx--, destIdx--)
                        {
                            _data[destIdx] = _data[srcIdx];
                        }

                        _discardCount = 0;
                    }
                }

                _baseOffset += moveCount;
                readStart -= moveCount;
                startIdx -= moveCount;
                endIdx -= moveCount;
                _end -= moveCount;

                #endregion

                #region Buffer Filling

                lock (_wrapper.LockObject)
                {
                    if (readCount > 0 && _wrapper.Source.Position != readOffset && readOffset < _wrapper.EofOffset)
                    {
                        if (_wrapper.Source.CanSeek)
                        {
                            try
                            {
                                _wrapper.Source.Position = readOffset;
                            }
                            catch (EndOfStreamException)
                            {
                                _wrapper.EofOffset = _wrapper.Source.Length;
                                readCount = 0;
                            }
                        }
                        else
                        {
                            // ugh, gotta read bytes until we've reached the desired offset
                            var seekCount = readOffset - _wrapper.Source.Position;
                            while (--seekCount >= 0)
                            {
                                if (_wrapper.Source.ReadByte() == -1)
                                {
                                    _wrapper.EofOffset = _wrapper.Source.Position;
                                    readCount = 0;
                                    break;
                                }
                            }
                        }
                    }

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

                    if (_end < endIdx)
                    {
                        // we didn't get a full read...
                        count = Math.Max(0, _end - startIdx);
                    }
                    else if (!_minimalRead && _end < _data.Length)
                    {
                        // try to finish filling the buffer
                        var temp = _wrapper.Source.Read(_data, _end, _data.Length - _end);
                        _end += temp;
                    }
                }

                #endregion

                return startIdx;
            }

            /// <summary>
            /// Tells the buffer that it no longer needs to maintain any bytes before the indicated offset.
            /// </summary>
            /// <param name="offset">The offset to discard through.</param>
            public void DiscardThrough(long offset)
            {
                lock (_localLock)
                {
                    var count = (int)(offset - _baseOffset);
                    _discardCount = Math.Max(count, _discardCount);

                    if (_discardCount >= _data.Length) CommitDiscard();
                }
            }

            void Truncate()
            {
                _end = 0;
                _discardCount = 0;
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
                    //Array.Copy(_readBuf, _readBufDiscardCount, _readBuf, 0, _readBufEnd - _readBufDiscardCount);
                    Buffer.BlockCopy(_data, _discardCount, _data, 0, _end - _discardCount);
                    _baseOffset += _discardCount;
                    _end -= _discardCount;
                }
                _discardCount = 0;
            }
        }

        Stream _baseStream;
        StreamReadBuffer _buffer;
        long _readPosition;
        object _localLock = new object();

        public BufferedReadStream(Stream baseStream)
            : this(baseStream, DEFAULT_INITIAL_SIZE, DEFAULT_MAX_SIZE, false)
        {
        }

        public BufferedReadStream(Stream baseStream, bool minimalRead)
            : this(baseStream, DEFAULT_INITIAL_SIZE, DEFAULT_MAX_SIZE, minimalRead)
        {
        }

        public BufferedReadStream(Stream baseStream, int initialSize, int maxSize)
            : this(baseStream, initialSize, maxSize, false)
        {
        }

        public BufferedReadStream(Stream baseStream, int initialSize, int maxBufferSize, bool minimalRead)
        {
            if (baseStream == null) throw new ArgumentNullException("baseStream");
            if (!baseStream.CanRead) throw new ArgumentException("baseStream");

            if (maxBufferSize < 1) maxBufferSize = 1;
            if (initialSize > maxBufferSize) initialSize = maxBufferSize;

            _baseStream = baseStream;
            _buffer = new StreamReadBuffer(baseStream, initialSize, maxBufferSize, minimalRead);
            _buffer.MaxSize = maxBufferSize;
            _buffer.MinimalRead = minimalRead;
        }

        public bool MinimalRead
        {
            get { return _buffer.MinimalRead; }
            set { _buffer.MinimalRead = value; }
        }

        public int MaxBufferSize
        {
            get { return _buffer.MaxSize; }
            set { _buffer.MaxSize = value; }
        }

        public long BufferBaseOffset
        {
            get { return _buffer.BaseOffset; }
        }

        public int BufferBytesFilled
        {
            get { return _buffer.BytesFilled; }
        }

        public void Discard(int bytes)
        {
            _buffer.DiscardThrough(_buffer.BaseOffset + bytes);
        }

        public void DiscardThrough(long offset)
        {
            _buffer.DiscardThrough(offset);
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return true; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override void Flush()
        {
            // no-op
        }

        public override long Length
        {
            get { return _baseStream.Length; }
        }

        public override long Position
        {
            get { return _readPosition; }
            set
            {
                lock (_localLock)
                {
                    if (!_baseStream.CanSeek)
                    {
                        if (value < _buffer.BaseOffset) throw new InvalidOperationException("Cannot seek to before the start of the buffer!");
                        if (value >= _buffer.BufferEndOffset) throw new InvalidOperationException("Cannot seek to beyond the end of the buffer!  Discard some bytes.");
                    }

                    _readPosition = value;
                }
            }
        }

        public override int ReadByte()
        {
            lock (_localLock)
            {
                var val = _buffer.ReadByte(_readPosition);
                if (val > -1)
                {
                    ++_readPosition;
                }
                return val;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_localLock)
            {
                var cnt = _buffer.Read(_readPosition, buffer, offset, count);
                _readPosition += cnt;
                return cnt;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    // no-op
                    break;
                case SeekOrigin.Current:
                    offset += _readPosition;
                    break;
                case SeekOrigin.End:
                    offset += _baseStream.Length;
                    break;
            }

            Position = offset;
            return _readPosition;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
