using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace UnitTests
{
    [TestFixture]
    class BufferedReadStreamTests
    {
        #region Forward Only Stream
        
        class FOStream : Stream
        {
            Stream _baseStream;

            public FOStream(Stream baseStream)
            {
                _baseStream = baseStream;
            }

            public override bool CanRead
            {
                get { return _baseStream.CanRead; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return _baseStream.CanWrite; }
            }

            public override void Flush()
            {
                _baseStream.Flush();
            }

            public override long Length
            {
                get { return _baseStream.Length; }
            }

            public override long Position
            {
                get
                {
                    return _baseStream.Position;
                }
                set
                {
                    throw new NotSupportedException();
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _baseStream.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _baseStream.Write(buffer, offset, count);
            }
        }
        
        #endregion

        #region Unreadable Stream

        class UnreadableStream : Stream
        {
            Stream _baseStream;

            public UnreadableStream(Stream baseStream)
            {
                _baseStream = baseStream;
            }

            public override bool CanRead
            {
                get { return false; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return _baseStream.CanWrite; }
            }

            public override void Flush()
            {
                _baseStream.Flush();
            }

            public override long Length
            {
                get { return _baseStream.Length; }
            }

            public override long Position
            {
                get
                {
                    return _baseStream.Position;
                }
                set
                {
                    throw new NotSupportedException();
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _baseStream.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _baseStream.Write(buffer, offset, count);
            }
        }
        
        #endregion

        #region Setup & Teardown

        MemoryStream _baseStream;
        TestHarness.BufferedReadStream _readBuffer;

        [TestFixtureSetUp]
        public void Init()
        {
            _baseStream = new MemoryStream(4096);

            for (int i = 0; i < 2048; i++)
            {
                _baseStream.WriteByte((byte)(i / 256));
                _baseStream.WriteByte((byte)(i % 256));
            }
        }

        [SetUp]
        public void TestInit()
        {
            _baseStream.Position = 0;
        }

        TestHarness.BufferedReadStream GetStandardWrapper()
        {
            return new TestHarness.BufferedReadStream(_baseStream, 256, 1024, false);
        }

        [TearDown]
        public void TestTearDown()
        {
            if (_readBuffer != null)
            {
                _readBuffer.Dispose();
                _readBuffer = null;
            }
        }

        [TestFixtureTearDown]
        public void TearDown()
        {
            _baseStream.Dispose();
        }

        #endregion

        #region Constructors

        [Test]
        public void Constructor1()
        {
            _readBuffer = new TestHarness.BufferedReadStream(_baseStream);
        }

        [Test]
        public void Constructor2()
        {
            _readBuffer = new TestHarness.BufferedReadStream(_baseStream, true);

            Assert.AreEqual(true, _readBuffer.MinimalRead);
        }

        [Test]
        public void Constructor3()
        {
            _readBuffer = new TestHarness.BufferedReadStream(_baseStream, 64, 1024);

            Assert.AreEqual(1024, _readBuffer.MaxBufferSize);
        }

        [Test]
        public void Constructor4()
        {
            _readBuffer = new TestHarness.BufferedReadStream(_baseStream, 64, 1024, true);

            Assert.AreEqual(true, _readBuffer.MinimalRead);
            Assert.AreEqual(1024, _readBuffer.MaxBufferSize);
        }

        [Test]
        public void ConstructorErrors()
        {
            Assert.Throws<ArgumentNullException>(() => _readBuffer = new TestHarness.BufferedReadStream(null));
            using (var urs = new UnreadableStream(_baseStream))
            {
                Assert.Throws<ArgumentException>(() => new TestHarness.BufferedReadStream(urs));
            }
        }

        [Test]
        public void ConstructorFixMaxSize()
        {
            _readBuffer = new TestHarness.BufferedReadStream(_baseStream, 0, 0);
            Assert.AreEqual(1, _readBuffer.MaxBufferSize);
        }

        #endregion

        #region CloseBaseStream

        [Test]
        public void CloseBaseStream()
        {
            var stream = new MemoryStream(new byte[6]);

            using (var buf = new TestHarness.BufferedReadStream(stream))
            {
                buf.CloseBaseStream = true;
            }

            Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        }

        #endregion

        #region MinimalRead

        [Test]
        public void MinimalRead()
        {
            _readBuffer = new TestHarness.BufferedReadStream(_baseStream, true);
            var val = _readBuffer.ReadByte();
            
            Assert.AreNotEqual(-1, val);
            Assert.AreEqual(1, _readBuffer.BufferBytesFilled);
        }

        #endregion

        #region Discard
        #endregion

        #region DiscardThrough
        #endregion

        #region CanRead

        [Test]
        public void CanRead()
        {
            _readBuffer = GetStandardWrapper();
            Assert.That(_readBuffer.CanRead);
        }

        #endregion

        #region CanSeek

        [Test]
        public void CanSeek()
        {
            _readBuffer = GetStandardWrapper();
            Assert.That(_readBuffer.CanSeek);
        }

        #endregion

        #region CanWrite

        // this is hard-coded as false; no testing required

        #endregion

        #region Flush

        // this is a no-op

        #endregion

        #region Length

        [Test]
        public void Length()
        {
            _readBuffer = GetStandardWrapper();

            Assert.AreEqual(_baseStream.Length, _readBuffer.Length);
        }

        #endregion

        #region Position

        // we're not going to mess with this here...

        #endregion

        #region ReadByte

        // We're putting all our "real" testing here...

        [Test]
        public void ReadByteFromBuffer()
        {
            _readBuffer = GetStandardWrapper();
            _readBuffer.ReadByte();

            _readBuffer.Position = 128;
            var val = _readBuffer.ReadByte();

            Assert.AreEqual(129, _readBuffer.Position);
            Assert.AreEqual(0, val);
        }

        [Test]
        public void ReadByteFromPastBuffer()
        {
            _readBuffer = GetStandardWrapper();
            _readBuffer.ReadByte();

            _readBuffer.Position = 257;
            var val = _readBuffer.ReadByte();

            Assert.AreEqual(258, _readBuffer.Position);
            Assert.AreEqual(128, val);
        }

        [Test]
        public void ReadByteFromPastBufferWithDiscard()
        {
            _readBuffer = GetStandardWrapper();
            for (int i = 0; i < 128; i++)
            {
                _readBuffer.ReadByte();
            }

            _readBuffer.Discard(128);

            _readBuffer.Position = 1024;
            var val = _readBuffer.ReadByte();

            Assert.AreEqual(1025, _readBuffer.Position);
            Assert.AreEqual(2, val);
        }

        #endregion

        #region Read
        #endregion

        #region Seek
        #endregion

        #region SetLength
        #endregion

        #region Write
        #endregion
    }
}
