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
    /// <summary>
    /// A thread-safe, read-only, buffering stream wrapper.
    /// </summary>
    partial class BufferedReadStream : Stream
    {
        const int DEFAULT_INITIAL_SIZE = 32768; // 32KB  (1/2 full page)
        const int DEFAULT_MAX_SIZE = 262144;    // 256KB (4 full pages)

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
            if (initialSize < 1) initialSize = 1;
            if (initialSize > maxBufferSize) initialSize = maxBufferSize;

            _baseStream = baseStream;
            _buffer = new StreamReadBuffer(baseStream, initialSize, maxBufferSize, minimalRead);
            _buffer.MaxSize = maxBufferSize;
            _buffer.MinimalRead = minimalRead;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                if (CloseBaseStream)
                {
                    _baseStream.Close();
                }
            }
        }

        public bool CloseBaseStream
        {
            get;
            set;
        }

        public bool MinimalRead
        {
            get { return _buffer.MinimalRead; }
            set { _buffer.MinimalRead = value; }
        }

        public int MaxBufferSize
        {
            get { return _buffer.MaxSize; }
            set
            {
                lock (_localLock)
                {
                    _buffer.MaxSize = value;
                }
            }
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
            lock (_localLock)
            {
                _buffer.DiscardThrough(_buffer.BaseOffset + bytes);
            }
        }

        public void DiscardThrough(long offset)
        {
            lock (_localLock)
            {
                _buffer.DiscardThrough(offset);
            }
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
            set { Seek(value, SeekOrigin.Begin); }
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
            lock (_localLock)
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

                if (!_baseStream.CanSeek)
                {
                    if (offset < _buffer.BaseOffset) throw new InvalidOperationException("Cannot seek to before the start of the buffer!");
                    if (offset >= _buffer.BufferEndOffset) throw new InvalidOperationException("Cannot seek to beyond the end of the buffer!  Discard some bytes.");
                }

                return (_readPosition = offset);
            }
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
