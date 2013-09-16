using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using System.IO;
using TestHarness;

namespace UnitTests
{
    class StreamReadBufferTests
    {
        Stream GetStandardStream()
        {
            var buf = new byte[4096];
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = (byte)((i / 2 >> ((i % 2) * 8)) & 0xFF);
            }
            return new MemoryStream(buf);
        }

        StreamReadBuffer GetStandardWrapper(Stream testStream)
        {
            return new StreamReadBuffer(testStream, 512, 2048, false);
        }

        #region .ctor

        // technically, we should check to make sure the stream wrapper logic works...
        // but we won't here

        // base constructor
        [Test]
        public void Constructor0()
        {
            using (var testingStream = GetStandardStream())
            {
                var wrapper = new StreamReadBuffer(testingStream, 47, 84, false);

                Assert.AreEqual(64, wrapper.TestingInitialSize);
                Assert.AreEqual(64, wrapper.MaxSize);
                Assert.IsTrue(wrapper.TestingBuffer.Length == 64);
            }
        }

        #endregion

        #region ReadStream

        //   readCount == 0
        [Test]
        public void ReadStream0()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                wrapper.TestingReadStream(0, 0, 0L);

                Assert.AreEqual(0, testStream.Position);
                Assert.AreEqual(0, wrapper.TestingBufferEndIndex);
            }
        }
        //   readCount > 0 && readOffset + readCount < _wrapper.EofOffset
        [Test]
        public void ReadStream1()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = testStream.Length - 11;
                wrapper.TestingReadStream(0, 10, testStream.Position);

                Assert.AreEqual(testStream.Length - 1, testStream.Position);
                Assert.AreEqual(10, wrapper.TestingBufferEndIndex);
                Assert.That(wrapper.TestingBuffer.Take(10).SequenceEqual(new byte[] { 7, 251, 7, 252, 7, 253, 7, 254, 7, 255 }));
            }
        }
        //   readCount > 0 && readOffset + readCount == _wrapper.EofOffset
        [Test]
        public void ReadStream2()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = testStream.Length - 10;
                wrapper.TestingReadStream(0, 10, testStream.Position);

                Assert.AreEqual(testStream.Length, testStream.Position);
                Assert.AreEqual(10, wrapper.TestingBufferEndIndex);
                Assert.That(wrapper.TestingBuffer.Take(10).SequenceEqual(new byte[] { 251, 7, 252, 7, 253, 7, 254, 7, 255, 7 }));
            }
        }
        //   readCount > 0 && readOffset + readCount > _wrapper.EofOffset
        [Test]
        public void ReadStream3()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = testStream.Length - 9;
                wrapper.TestingReadStream(0, 10, testStream.Position);

                Assert.AreEqual(testStream.Length, testStream.Position);
                Assert.AreEqual(9, wrapper.TestingBufferEndIndex);
                // check that the read data matches our expectation
                Assert.That(wrapper.TestingBuffer.Take(9).SequenceEqual(new byte[] { 7, 252, 7, 253, 7, 254, 7, 255, 7 }));
            }
        }
        //   readCount > 0 && readOffset + readCount > _wrapper.EofOffset (unknown value)
        [Test]
        public void ReadStream3a()
        {
            using (var testStream = GetStandardStream())
            using (var foStream = new ForwardOnlyStream(testStream))
            {
                var wrapper = GetStandardWrapper(foStream);
                testStream.Position = testStream.Length - 9;
                wrapper.TestingReadStream(0, 10, testStream.Position);

                Assert.AreEqual(testStream.Length, testStream.Position);
                Assert.AreEqual(9, wrapper.TestingBufferEndIndex);
                Assert.That(wrapper.TestingBuffer.Take(9).SequenceEqual(new byte[] { 7, 252, 7, 253, 7, 254, 7, 255, 7 }));
            }
        }
        //   readOffset == _wrapper.EofOffset
        [Test]
        public void ReadStream4()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = testStream.Length;
                wrapper.TestingReadStream(0, 10, testStream.Position);

                Assert.AreEqual(testStream.Length, testStream.Position);
                Assert.AreEqual(0, wrapper.TestingBufferEndIndex);
            }
        }
        //   readOffset > _wrapper.EofOffset
        [Test]
        public void ReadStream5()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = testStream.Length;
                wrapper.TestingReadStream(0, 10, testStream.Position + 1);

                Assert.AreEqual(testStream.Length, testStream.Position);
                Assert.AreEqual(0, wrapper.TestingBufferEndIndex);
            }
        }

        #endregion

        #region PrepareStreamForRead

        //   readCount == 0
        [Test]
        public void PrepareStreamForRead0()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = 50;

                int readCount = wrapper.TestingPrepareStreamForRead(0, 55);

                Assert.AreEqual(0, readCount);
                Assert.AreEqual(50, testStream.Position);
            }
        }
        //   _wrapper.Source.Position == readOffset
        [Test]
        public void PrepareStreamForRead1()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = 50;

                int readCount = wrapper.TestingPrepareStreamForRead(1, 0);

                Assert.AreEqual(1, readCount);
                Assert.AreEqual(0, testStream.Position);
            }
        }
        //   _wrapper.Source.Position != readOffset && readoffset < _wrapper.EofOffset
        //     _wrapper.Source.CanSeek == false
        //       past end of stream
        [Test]
        public void PrepareStreamForRead2a()
        {
            using (var testStream = GetStandardStream())
            using (var foStream = new ForwardOnlyStream(testStream))
            {
                var wrapper = GetStandardWrapper(foStream);

                int readCount = wrapper.TestingPrepareStreamForRead(1, 5000);

                Assert.AreEqual(0, readCount);
                Assert.AreEqual(4096, testStream.Position);
            }
        }
        //       to place in stream
        [Test]
        public void PrepareStreamForRead2b()
        {
            using (var testStream = GetStandardStream())
            using (var foStream = new ForwardOnlyStream(testStream))
            {
                var wrapper = GetStandardWrapper(foStream);

                int readCount = wrapper.TestingPrepareStreamForRead(1, 55);

                Assert.AreEqual(1, readCount);
                Assert.AreEqual(55, testStream.Position);
            }
        }
        //       before current position
        [Test]
        public void PrepareStreamForRead2c()
        {
            using (var testStream = GetStandardStream())
            using (var foStream = new ForwardOnlyStream(testStream))
            {
                var wrapper = GetStandardWrapper(foStream);
                testStream.Position = 50;

                int readCount = wrapper.TestingPrepareStreamForRead(1, 0);

                Assert.AreEqual(0, readCount);
                Assert.AreEqual(50, testStream.Position);
            }
        }
        //     _wrapper.Source.CanSeek == true
        //       readOffset > _wrapper.Source.Position
        [Test]
        public void PrepareStreamForRead3a()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                int readCount = wrapper.TestingPrepareStreamForRead(1, 55);

                Assert.AreEqual(1, readCount);
                Assert.AreEqual(55, testStream.Position);
            }
        }
        //       readOffset < _wrapper.Source.Position
        [Test]
        public void PrepareStreamForRead3b()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                testStream.Position = 50;

                int readCount = wrapper.TestingPrepareStreamForRead(1, 0);

                Assert.AreEqual(1, readCount);
                Assert.AreEqual(0, testStream.Position);
            }
        }
        //   _wrapper.Source.Position != readOffset && readoffset == _wrapper.EofOffset
        [Test]
        public void PrepareStreamForRead4a()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                int readCount = wrapper.TestingPrepareStreamForRead(1, 4096);

                Assert.AreEqual(0, readCount);
                Assert.AreEqual(0, testStream.Position);
            }
        }
        //   _wrapper.Source.Position != readOffset && readoffset > _wrapper.EofOffset
        [Test]
        public void PrepareStreamForRead4b()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                int readCount = wrapper.TestingPrepareStreamForRead(1, 4097);

                Assert.AreEqual(0, readCount);
                Assert.AreEqual(0, testStream.Position);
            }
        }

        #endregion

        #region FillBuffer

        // standard
        [Test]
        public void FillBuffer0()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                wrapper.MinimalRead = true;

                var count = wrapper.TestingFillBuffer(0, 10, 0, 10, 0L);

                Assert.AreEqual(10, count);
                Assert.AreEqual(10, testStream.Position);
                Assert.AreEqual(10, wrapper.TestingBufferEndIndex);
                Assert.That(wrapper.TestingBuffer.Take(10).SequenceEqual(new byte[] { 0, 0, 1, 0, 2, 0, 3, 0, 4, 0 }));
            }
        }
        // _end < startIdx + count
        [Test]
        public void FillBuffer1()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                var count = wrapper.TestingFillBuffer(0, 10, 0, 10, 4090L);

                Assert.AreEqual(6, count);
                Assert.AreEqual(4096, testStream.Position);
                Assert.AreEqual(6, wrapper.TestingBufferEndIndex);
                Assert.That(wrapper.TestingBuffer.Take(6).SequenceEqual(new byte[] { 253, 7, 254, 7, 255, 7 }));
            }
        }
        // !_minimalRead && _end < _data.Length
        [Test]
        public void FillBuffer2()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                var count = wrapper.TestingFillBuffer(0, 10, 0, 10, 0L);

                Assert.AreEqual(10, count);
                Assert.AreEqual(wrapper.TestingInitialSize, testStream.Position);
                Assert.AreEqual(wrapper.TestingInitialSize, wrapper.TestingBufferEndIndex);
                Assert.That(wrapper.TestingBuffer.Take(10).SequenceEqual(new byte[] { 0, 0, 1, 0, 2, 0, 3, 0, 4, 0 }));
            }
        }

        #endregion

        #region PrepareBufferForRead

        // reqSize < _data.Length
        //   moveCount == 0
        [Test]
        public void PrepareBufferForRead0()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);
                
                int startIdx = 0;
                int endIdx = 1;
                int readStart = 0;
                int readCount = 1;
                int moveCount = 0;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 0L);

                Assert.AreSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(originalBase, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(originalDiscardCount, wrapper.TestingDiscardCount);
                Assert.AreEqual(0, startIdx);
                Assert.AreEqual(1, endIdx);
                Assert.AreEqual(0, readStart);
            }
        }
        //   moveCount > 0
        [Test]
        public void PrepareBufferForRead1()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 5;

                int startIdx = 1;
                int endIdx = 2;
                int readStart = 1;
                int readCount = 2;
                int moveCount = 1;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 5; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 1L);

                Assert.AreSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(originalBase + 1, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd - 1, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(originalDiscardCount, wrapper.TestingDiscardCount);
                Assert.AreEqual(0, startIdx);
                Assert.AreEqual(1, endIdx);
                Assert.AreEqual(0, readStart);
                Assert.That(originalBuffer.Take(5).SequenceEqual(new byte[] { 1, 2, 3, 4, 4 }));
            }
        }
        //   moveCount < 0
        [Test]
        public void PrepareBufferForRead2()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 5;
                wrapper.TestingBufferEndIndex = 5;
                wrapper.TestingDiscardCount = 2;

                int startIdx = 0;
                int endIdx = 1;
                int readStart = 0;
                int readCount = 1;
                int moveCount = -5;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 5; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 10L);

                Assert.AreSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(originalBase - 5, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd + 5, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(0, wrapper.TestingDiscardCount);
                Assert.AreEqual(5, startIdx);
                Assert.AreEqual(6, endIdx);
                Assert.AreEqual(5, readStart);
                Assert.That(originalBuffer.Skip(5).Take(5).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 }));
            }
        }
        // reqSize > _data.Length
        //   newSize <= _maxSize
        [Test]
        public void PrepareBufferForRead3()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 5;

                int startIdx = 5;
                int endIdx = 700;
                int readStart = 5;
                int readCount = 700;
                int moveCount = 0;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 5; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 5L);

                Assert.AreNotSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(1024, wrapper.TestingBuffer.Length);
                Assert.AreEqual(originalBase, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(originalDiscardCount, wrapper.TestingDiscardCount);
                Assert.AreEqual(5, startIdx);
                Assert.AreEqual(700, endIdx);
                Assert.AreEqual(5, readStart);
                Assert.That(wrapper.TestingBuffer.Take(5).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 }));
            }
        }
        //   make sure doubling works correctly
        [Test]
        public void PrepareBufferForRead4()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 5;

                int startIdx = 5;
                int endIdx = 1200;
                int readStart = 5;
                int readCount = 1200;
                int moveCount = 0;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 5; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 5L);

                Assert.AreNotSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(2048, wrapper.TestingBuffer.Length);
                Assert.AreEqual(originalBase, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(originalDiscardCount, wrapper.TestingDiscardCount);
                Assert.AreEqual(5, startIdx);
                Assert.AreEqual(1200, endIdx);
                Assert.AreEqual(5, readStart);
                Assert.That(wrapper.TestingBuffer.Take(5).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 }));
            }
        }
        //   newSize > _maxSize
        [Test]
        public void PrepareBufferForRead6()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 5;

                int startIdx = 5;
                int endIdx = 3000;
                int readStart = 5;
                int readCount = 3000;
                int moveCount = 0;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                Assert.Throws<InvalidOperationException>(() => wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 5L));
            }
        }
        //   newSize < _maxSize AFTER discard
        [Test]
        public void PrepareBufferForRead7()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 10;
                wrapper.TestingDiscardCount = 5;

                int startIdx = 5;
                int endIdx = 2049;
                int readStart = 5;
                int readCount = 2044;
                int moveCount = 0;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 10; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 5L);

                Assert.AreNotSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(2048, wrapper.TestingBuffer.Length);
                Assert.AreEqual(originalBase + 5, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd - 5, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(0, wrapper.TestingDiscardCount);
                Assert.AreEqual(0, startIdx);
                Assert.AreEqual(2044, endIdx);
                Assert.AreEqual(0, readStart);
                Assert.That(wrapper.TestingBuffer.Take(5).SequenceEqual(new byte[] { 5, 6, 7, 8, 9 }));
            }
        }
        //   moveCount >= newBuf.Length
        [Test]
        public void PrepareBufferForRead8()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 15;
                wrapper.TestingBufferEndIndex = 2048;

                int startIdx = 2048;
                int endIdx = 2748;
                int readStart = 2048;
                int readCount = 700;
                int moveCount = 2048;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 5; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 5L);

                Assert.AreNotSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(1024, wrapper.TestingBuffer.Length);
                Assert.AreEqual(5, wrapper.TestingBaseOffset);
                Assert.AreEqual(0, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(0, wrapper.TestingDiscardCount);
                Assert.AreEqual(0, startIdx);
                Assert.AreEqual(700, endIdx);
                Assert.AreEqual(0, readStart);
                Assert.That(wrapper.TestingBuffer.Take(5).SequenceEqual(new byte[] { 0, 0, 0, 0, 0 }));
            }
        }
        //   moveCount >= newBuf.Length && startIdx != 0
        [Test]
        public void PrepareBufferForRead8a()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 15;
                wrapper.TestingBufferEndIndex = 2048;

                int startIdx = 2049;
                int endIdx = 2749;
                int readStart = 2049;
                int readCount = 700;
                int moveCount = 2048;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 5; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 5L);

                Assert.AreNotSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(1024, wrapper.TestingBuffer.Length);
                Assert.AreEqual(6, wrapper.TestingBaseOffset);
                Assert.AreEqual(0, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(0, wrapper.TestingDiscardCount);
                Assert.AreEqual(0, startIdx);
                Assert.AreEqual(700, endIdx);
                Assert.AreEqual(0, readStart);
                Assert.That(wrapper.TestingBuffer.Take(5).SequenceEqual(new byte[] { 0, 0, 0, 0, 0 }));
            }
        }
        //   moveCount > 0
        [Test]
        public void PrepareBufferForRead9()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 10;

                int startIdx = 5;
                int endIdx = 518;
                int readStart = 10;
                int readCount = 508;
                int moveCount = 5;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 10; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 15L);

                Assert.AreNotSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(1024, wrapper.TestingBuffer.Length);
                Assert.AreEqual(originalBase + 5, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd - 5, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(0, wrapper.TestingDiscardCount);
                Assert.AreEqual(0, startIdx);
                Assert.AreEqual(513, endIdx);
                Assert.AreEqual(5, readStart);
                Assert.That(wrapper.TestingBuffer.Take(5).SequenceEqual(new byte[] { 5, 6, 7, 8, 9 }));
            }
        }
        //   moveCount < 0
        [Test]
        public void PrepareBufferForRead10()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 10;

                int startIdx = 5;
                int endIdx = 518;
                int readStart = 10;
                int readCount = 508;
                int moveCount = -5;
                var originalBuffer = wrapper.TestingBuffer;
                var originalBase = wrapper.TestingBaseOffset;
                var originalEnd = wrapper.TestingBufferEndIndex;
                var originalDiscardCount = wrapper.TestingDiscardCount;

                for (int i = 0; i < 10; i++)
                {
                    originalBuffer[i] = (byte)i;
                }

                wrapper.TestingPrepareBufferForRead(ref startIdx, ref endIdx, ref readStart, readCount, moveCount, 5L);

                Assert.AreNotSame(originalBuffer, wrapper.TestingBuffer);
                Assert.AreEqual(1024, wrapper.TestingBuffer.Length);
                Assert.AreEqual(originalBase - 5, wrapper.TestingBaseOffset);
                Assert.AreEqual(originalEnd + 5, wrapper.TestingBufferEndIndex);
                Assert.AreEqual(0, wrapper.TestingDiscardCount);
                Assert.AreEqual(10, startIdx);
                Assert.AreEqual(523, endIdx);
                Assert.AreEqual(15, readStart);
                Assert.That(wrapper.TestingBuffer.Skip(10).Take(5).SequenceEqual(new byte[] { 5, 6, 7, 8, 9 }));
            }
        }

        #endregion

        #region CalculateRead

        // startIdx < 0
        //   can't seek
        [Test]
        public void CalculateRead0()
        {
            using (var testStream = GetStandardStream())
            using (var foStream = new ForwardOnlyStream(testStream))
            {
                var wrapper = GetStandardWrapper(foStream);

                int startIdx, endIdx, readStart, readCount, moveCount;
                long readOffset;

                startIdx = -10;
                endIdx = -1;

                Assert.Throws<InvalidOperationException>(() => wrapper.TestingCalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset));
            }
        }
        //   _end - startIdx <= _maxSize
        [Test]
        public void CalculateRead1()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 15;
                wrapper.TestingBufferEndIndex = 10;

                int startIdx, endIdx, readStart, readCount, moveCount;
                long readOffset;

                startIdx = -10;
                endIdx = -1;

                wrapper.TestingCalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset);

                Assert.AreEqual(-10, startIdx);
                Assert.AreEqual(-1, endIdx);
                Assert.AreEqual(-10, readStart);
                Assert.AreEqual(10, readCount);
                Assert.AreEqual(-10, moveCount);
                Assert.AreEqual(5, readOffset);
            }
        }
        //   !(_end - startIdx <= _maxSize)
        [Test]
        public void CalculateRead2()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 15;
                wrapper.TestingBufferEndIndex = 2048;

                int startIdx, endIdx, readStart, readCount, moveCount;
                long readOffset;

                startIdx = -10;
                endIdx = -1;

                wrapper.TestingCalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset);

                Assert.AreEqual(2048, startIdx);
                Assert.AreEqual(2057, endIdx);
                Assert.AreEqual(2048, readStart);
                Assert.AreEqual(9, readCount);
                Assert.AreEqual(2048, moveCount);
                Assert.AreEqual(5, readOffset);
            }
        }
        // startIdx >= 0
        //   endIdx < _data.Length
        [Test]
        public void CalculateRead3()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 15;
                wrapper.TestingBufferEndIndex = 10;

                int startIdx, endIdx, readStart, readCount, moveCount;
                long readOffset;

                startIdx = 10;
                endIdx = 11;

                wrapper.TestingCalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset);

                Assert.AreEqual(10, startIdx);
                Assert.AreEqual(11, endIdx);
                Assert.AreEqual(10, readStart);
                Assert.AreEqual(1, readCount);
                Assert.AreEqual(0, moveCount);
                Assert.AreEqual(25, readOffset);
            }
        }
        //   endIdx - _discardCount < _maxSize
        [Test]
        public void CalculateRead4()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 15;
                wrapper.TestingBufferEndIndex = 10;

                int startIdx, endIdx, readStart, readCount, moveCount;
                long readOffset;

                startIdx = 50;
                endIdx = 1024;

                wrapper.TestingCalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset);

                Assert.AreEqual(50, startIdx);
                Assert.AreEqual(1024, endIdx);
                Assert.AreEqual(10, readStart);
                Assert.AreEqual(1014, readCount);
                Assert.AreEqual(0, moveCount);
                Assert.AreEqual(25, readOffset);
            }
        }
        //   none of the above
        [Test]
        public void CalculateRead5()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBaseOffset = 15;
                wrapper.TestingBufferEndIndex = 10;

                int startIdx, endIdx, readStart, readCount, moveCount;
                long readOffset;

                startIdx = 1024;
                endIdx = 2048;

                wrapper.TestingCalculateRead(ref startIdx, ref endIdx, out readStart, out readCount, out moveCount, out readOffset);

                Assert.AreEqual(2048, startIdx);
                Assert.AreEqual(3072, endIdx);
                Assert.AreEqual(2048, readStart);
                Assert.AreEqual(1024, readCount);
                Assert.AreEqual(2048, moveCount);
                Assert.AreEqual(1039, readOffset);
            }
        }
        
        #endregion

        #region EnsureAvailable

        // startIdx >= 0 && endIdx <= _end
        [Test]
        public void EnsureAvailable0()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                wrapper.TestingBufferEndIndex = 10;

                int count = 2;

                var index = wrapper.TestingEnsureAvailable(5L, ref count);

                Assert.AreEqual(5, index);
                Assert.AreEqual(2, count);
            }
        }
        // !(startIdx >= 0 && endIdx <= _end)
        [Test]
        public void EnsureAvailable1()
        {
            using (var testStream = GetStandardStream())
            {
                var wrapper = GetStandardWrapper(testStream);

                int count = 2;

                var index = wrapper.TestingEnsureAvailable(5L, ref count);

                Assert.AreEqual(5, index);
                Assert.AreEqual(2, count);
                Assert.IsTrue(wrapper.TestingBufferEndIndex + wrapper.TestingBaseOffset >= 7);
                Assert.AreEqual(0, wrapper.TestingBuffer[index]);
                Assert.AreEqual(3, wrapper.TestingBuffer[index + 1]);
            }
        }

        #endregion
    }
}
