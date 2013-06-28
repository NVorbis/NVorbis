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
        const int DEFAULT_INITIAL_SIZE = 2048;
        const int DEFAULT_MAX_SIZE = 16384;

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
                    var startIdx = offset - _baseOffset;
                    if (startIdx < 0 || startIdx + count > _end)
                    {
                        startIdx = EnsureAvailable(offset, ref count);
                    }

                    Buffer.BlockCopy(_data, (int)startIdx, buffer, index, count);
                }
                return count;
            }

            internal int ReadByte(long offset)
            {
                if (offset < 0L || offset >= _wrapper.EofOffset) throw new ArgumentOutOfRangeException("offset");

                lock (_localLock)
                {
                    // short-circuit the buffer management code if we already have the byte available
                    if (offset >= _baseOffset && offset < _baseOffset + _end)
                    {
                        return _data[offset - _baseOffset];
                    }

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
                // check that we can seek to the correct location
                if (!_wrapper.Source.CanSeek && offset < _baseOffset) throw new InvalidOperationException("Cannot seek backwards on a forward-only stream!");

                // figure up how many bytes we need for the request
                var bufSize = (int)(offset + count - _baseOffset - _discardCount);

                // don't resize if it makes more sense to just truncate...
                if (_minimalRead && bufSize > _data.Length)
                {
                    // if the end is nowhere near the buffer size necessary...
                    if (_end < bufSize - bufSize / 10)
                    {
                        // ... just truncate
                        Truncate();
                        bufSize = count;
                    }
                    // ... otherwise allow the read to resize and fill the buffer
                }

                // don't let the buffer grow to more than 16KB or the size of the request (whichever is larger)
                if (bufSize > Math.Max(16 * 1024, count) || bufSize < count)
                {
                    bufSize = count;
                }

                // actually resize the buffer
                byte[] newBuf = null;
                if (bufSize > _data.Length)  // if bufSize is bigger than the buffer, we have to resize...
                {
                    bufSize = 2 << (int)Math.Log(bufSize, 2);
                    newBuf = new byte[bufSize];
                }
                else
                {
                    // otherwise, we should be able to accomodate...
                    bufSize = _data.Length;
                    newBuf = _data;

                    // if we have enough room, but the discard count is too high, dump the discard bytes
                    if (_data.Length - _discardCount < count)
                    {
                        CommitDiscard();
                    }
                }

                // if offset < base || offset >= base + bufSize, truncate
                // else
                //   if offset + count <= bufEnd, do nothing (we already have the correct data)
                //   if offset + count > bufEnd
                //     if offset < bufEnd && offset > base, move

                // do we truncate, move, or leave the data alone?
                if (offset >= _baseOffset && offset < _baseOffset + bufSize)
                {
                    // if we get here...
                    //  ...offset cannot be less than base
                    //  ...offset cannot be greater than or equal to base + bufSize
                    // in other words, we are within the size of the new buffer for at least one byte

                    // if the end of the request is after the last read byte...
                    if (offset + count > _baseOffset + _end)
                    {
                        // ... and the start of the request is before the last read byte...
                        // ... and the start of the request is after the first read byte...
                        if (offset - _baseOffset + count < _end && offset > _baseOffset)
                        {
                            // ... move the read data to the beginning of the buffer
                            var idx = (int)(offset - _baseOffset);
                            var cnt = (int)(_end - idx);
                            Buffer.BlockCopy(_data, idx, newBuf, 0, cnt);
                            _baseOffset = offset;
                            _end = cnt;
                            _discardCount = 0;

                            // swap to the new buffer (or just make sure we don't recopy below)
                            _data = newBuf;
                        }
                    }

                    // if we resized, make sure the new buffer has the correct data in it
                    if (!object.ReferenceEquals(_data, newBuf))
                    {
                        // just copy the data as-is
                        Buffer.BlockCopy(_data, 0, newBuf, 0, _end);

                        // swap to the new buffer (or just make sure we don't recopy below)
                        _data = newBuf;
                    }
                }
                else
                {
                    // truncate
                    _baseOffset = offset;
                    Truncate();

                    // swap to the new buffer (or just make sure we don't recopy below)
                    _data = newBuf;
                }

                // seek to the correct location for the read
                var startIdx = (int)(offset - _baseOffset);
                var endIdx = startIdx + count;
                var readOffset = _baseOffset + _end;
                int readCount;

                if (_minimalRead)
                {
                    // only read as much as we need to satisfy the request
                    readCount = Math.Max(endIdx - _end, 0);
                }
                else
                {
                    // go ahead and fill the buffer
                    readCount = bufSize - _end;
                }

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
                        var temp = _wrapper.Source.Read(_data, _end, readCount);
                        if (temp == 0)
                        {
                            if (_end < endIdx)
                            {
                                count = (int)Math.Max(0, _baseOffset + _end - offset);
                            }
                            break;
                        }
                        _end += temp;
                        readOffset += temp;
                        readCount -= temp;
                    }
                }

                if (_end < endIdx)
                {
                    // we didn't get a full read...
                    count = Math.Max(0, _end - startIdx);
                }
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
