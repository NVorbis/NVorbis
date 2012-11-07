using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;

namespace NVorbis.Ogg
{
    internal class ThreadSafeStream : Stream
    {
        class Node
        {
            public Thread Thread;
            public long Position;
            public int HitCount;
        }

        Stream _baseStream;
        LinkedList<Node> _positions;
        long _length;

        object _streamLock;
        ReaderWriterLockSlim _rwls;

        internal ThreadSafeStream(Stream baseStream)
        {
            if (!baseStream.CanSeek)
            {
                throw new ArgumentException("The stream must be seekable.", "baseStream");
            }

            _baseStream = baseStream;
            _positions = new LinkedList<Node>();

            _streamLock = new object();
            _rwls = new ReaderWriterLockSlim();

            _length = baseStream.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _positions.Clear();

                if (_rwls != null)
                {
                    _rwls.Dispose();
                    _rwls = null;
                }
            }

            base.Dispose(disposing);
        }

        public override bool CanRead
        {
            get { return _baseStream.CanRead; }
        }

        public override bool CanSeek
        {
            get { return _baseStream.CanSeek; }
        }

        public override bool CanWrite
        {
            get { return _baseStream.CanWrite; }
        }

        public override void Flush()
        {
            lock (_streamLock)
            {
                _baseStream.Flush();
            }
        }

        public override long Length
        {
            get { return _length; }
        }

        int _totalHitCounter;

        Node GetNode()
        {
            if (Interlocked.Increment(ref _totalHitCounter) % 50 == 0)
            {
                // try to sort...
                var upgraded = false;
                _rwls.EnterUpgradeableReadLock();
                try
                {
                    var curNode = _positions.First;
                    while (curNode != null)
                    {
                        var chkNode = curNode;
                        while (chkNode.Previous != null && chkNode.Previous.Value.HitCount < curNode.Value.HitCount)
                        {
                            chkNode = chkNode.Previous;
                        }
                        if (chkNode != curNode)
                        {
                            var temp = curNode.Next;
                            if (!upgraded)
                            {
                                _rwls.EnterWriteLock();
                                upgraded = true;
                            }
                            _positions.Remove(curNode);
                            _positions.AddBefore(chkNode, curNode);
                            curNode = temp;
                        }
                        else
                        {
                            curNode = curNode.Next;
                        }
                    }
                }
                finally
                {
                    if (upgraded)
                    {
                        _rwls.ExitWriteLock();
                    }

                    _rwls.ExitUpgradeableReadLock();
                }
            }

            LinkedListNode<Node> node;

            var thread = Thread.CurrentThread;
            _rwls.EnterReadLock();
            try
            {
                node = _positions.First;
                while (node != null)
                {
                    if (node.Value.Thread == thread)
                    {
                        // found it!
                        ++node.Value.HitCount;
                        return node.Value;
                    }
                    node = node.Next;
                }
            }
            finally
            {
                _rwls.ExitReadLock();
            }

            // not found, create a new node and add it
            node = new LinkedListNode<Node>(
                new Node
                {
                    Thread = thread,
                    Position = 0L,
                    HitCount = 1,
                }
            );

            _rwls.EnterWriteLock();
            try
            {
                _positions.AddLast(node);
            }
            finally
            {
                _rwls.ExitWriteLock();
            }

            return node.Value;
        }

        public override long Position
        {
            get
            {
                return GetNode().Position;
            }
            set
            {
                Seek(value, SeekOrigin.Begin);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var node = GetNode();
            int cnt;

            lock (_streamLock)
            {
                if (_baseStream.Position != node.Position) _baseStream.Position = node.Position;
                cnt = _baseStream.Read(buffer, offset, count);
            }

            node.Position += cnt;

            return cnt;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek) throw new InvalidOperationException();

            var node = GetNode();
            long pos = 0L;

            switch (origin)
            {
                case SeekOrigin.Begin:
                    pos = offset;
                    break;
                case SeekOrigin.End:
                    pos = Length + offset;
                    break;
                case SeekOrigin.Current:
                    pos = node.Position + offset;
                    break;
            }

            if (pos < 0L || pos > Length) throw new ArgumentOutOfRangeException("offset");

            return (node.Position = pos);
        }

        public override void SetLength(long value)
        {
            lock (_streamLock)
            {
                _baseStream.SetLength(value);
                _length = value;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var node = GetNode();

            lock (_streamLock)
            {
                if (_baseStream.Position != node.Position) _baseStream.Position = node.Position;
                _baseStream.Write(buffer, offset, count);
                _length = _baseStream.Length;
            }

            node.Position += count;
        }
    }
}
