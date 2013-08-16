using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace NVorbis
{
    public partial class StreamReadBuffer
    {
        public long TestingBaseOffset
        {
            get { return _baseOffset; }
            set { _baseOffset = value; }
        }
        
        public int TestingDiscardCount
        {
            get { return _discardCount; }
            set { _discardCount = value; }
        }

        public int TestingBufferEndIndex
        {
            get { return _end; }
            set { _end = value; }
        }

        public byte[] TestingBuffer
        {
            get { return _data; }
            set { _data = value; }
        }

        public int TestingMaxSize
        {
            get { return _maxSize; }
            set { _maxSize = value; }
        }

        public long TestingBufferEndOffset
        {
            get { return BufferEndOffset; }
        }

        public int TestingReadByte(long offset)
        {
            return ReadByte(offset);
        }

        public int TestingEnsureAvailable(long offset, ref int count)
        {
            return EnsureAvailable(offset, ref count);
        }

        public void TestingCalculateRead(ref int startIdx, ref int endIdx, out int readStart, out int readCount, out int moveCount, out long readOffset)
        {
            CalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset);
        }

        public void TestingPrepareBufferForRead(ref int startIdx, ref int endIdx, ref int readStart, int readCount, int moveCount, long readOffset)
        {
            PrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, readOffset);
        }

        public int TestingFillBuffer(int startIdx, int count, int readStart, int readCount, long readOffset)
        {
            return FillBuffer(startIdx, count, readStart, readCount, readOffset);
        }

        public int TestingPrepareStreamForRead(int readCount, long readOffset)
        {
            return PrepareStreamForRead(readCount, readOffset);
        }

        public void TestingReadStream(int readStart, int readCount, long readOffset)
        {
            ReadStream(readStart, readCount, readOffset);
        }
    }
}
namespace TestHarness
{
    public class StreamReadBuffer : NVorbis.StreamReadBuffer
    {
        public StreamReadBuffer(Stream source, int initialSize, int maxSize, bool minimalRead)
            : base(source, initialSize, maxSize, minimalRead)
        {
            TestingInitialSize = base.Length;
        }

        public int TestingInitialSize { get; private set; }
    }
}
