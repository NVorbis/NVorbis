﻿using System;
using System.IO;

namespace TestApp
{
    class ForwardOnlyStream : Stream
    {
        private Stream _steam;

        public override bool CanRead => _steam.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _steam.Position; set => throw new NotSupportedException(); }

        public ForwardOnlyStream(Stream stream)
        {
            _steam = stream;
        }

        public override void Flush()
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _steam.Read(buffer, offset, count);
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
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _steam?.Dispose();
                _steam = null;
            }

            base.Dispose(disposing);
        }
    }
}
