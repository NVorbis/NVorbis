using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace NVorbis.Ogg
{
    class PageReader : PageReaderBase, IPageData
    {
        internal static Func<IPageData, int, IStreamPageReader> CreateStreamPageReader { get; set; } = (pr, ss) => new StreamPageReader(pr, ss);

        private readonly Dictionary<int, IStreamPageReader> _streamReaders = new Dictionary<int, IStreamPageReader>();
        private readonly Func<IPacketProvider, bool> _newStreamCallback;
        private readonly object _readLock = new object();

        private long _nextPageOffset;
        private ValueTuple<long, int>[] _packetList;

        public PageReader(Stream stream, bool closeOnDispose, Func<IPacketProvider, bool> newStreamCallback)
            : base(stream, closeOnDispose)
        {
            _newStreamCallback = newStreamCallback;
        }

        private void ParsePageHeader(byte[] pageBuf, bool? isResync)
        {
            var isContinued = false;
            var segCnt = pageBuf[26];
            var dataOffset = PageOffset + 27 + segCnt;
            var packets = new (long, int)[segCnt];
            var pktIdx = 0;

            if (segCnt > 0)
            {
                var size = 0;
                for (int i = 0, idx = 27; i < segCnt; i++, idx++)
                {
                    size += pageBuf[idx];
                    if (pageBuf[idx] < 255)
                    {
                        if (size > 0)
                        {
                            packets[pktIdx].Item1 = dataOffset;
                            packets[pktIdx++].Item2 = size;
                            dataOffset += size;
                        }
                        size = 0;
                        isContinued = false;
                    }
                    else
                    {
                        isContinued = true;
                    }
                }
                if (size > 0)
                {
                    packets[pktIdx].Item1 = dataOffset;
                    packets[pktIdx++].Item2 = size;
                }
                else
                {
                    isContinued = false;
                }
            }
            _packetList = new (long, int)[pktIdx];
            Array.Copy(packets, 0, _packetList, 0, pktIdx);

            StreamSerial = BitConverter.ToInt32(pageBuf, 14);
            SequenceNumber = BitConverter.ToInt32(pageBuf, 18);
            PageFlags = (PageFlags)pageBuf[5];
            GranulePosition = BitConverter.ToInt64(pageBuf, 6);
            PacketCount = (short)pktIdx;
            IsResync = isResync;
            IsContinued = isContinued;
            PageOverhead = 27 + segCnt;
        }

        public override void Lock()
        {
            Monitor.Enter(_readLock);
        }

        protected override bool CheckLock()
        {
            return Monitor.IsEntered(_readLock);
        }

        public override bool Release()
        {
            if (Monitor.IsEntered(_readLock))
            {
                Monitor.Exit(_readLock);
                return true;
            }
            return false;
        }

        protected override void SaveNextPageSearch()
        {
            _nextPageOffset = StreamPosition;
        }

        protected override void PrepareStreamForNextPage()
        {
            SeekStream(_nextPageOffset, SeekOrigin.Begin);
        }

        protected override bool AddPage(int streamSerial, byte[] pageBuf, bool isResync)
        {
            PageOffset = StreamPosition - pageBuf.Length;
            ParsePageHeader(pageBuf, isResync);

            if (_streamReaders.TryGetValue(streamSerial, out var spr))
            {
                spr.AddPage();

                // if we've read the last page, remove from our list so cleanup can happen.
                // this is safe because the instance still has access to us for reading.
                if ((PageFlags & PageFlags.EndOfStream) == PageFlags.EndOfStream)
                {
                    _streamReaders.Remove(StreamSerial);
                }
            }
            else
            {
                var streamReader = CreateStreamPageReader(this, StreamSerial);
                streamReader.AddPage();
                _streamReaders.Add(StreamSerial, streamReader);
                if (!_newStreamCallback(streamReader.PacketProvider))
                {
                    _streamReaders.Remove(StreamSerial);
                    return false;
                }
            }
            return true;
        }

        public override bool ReadPageAt(long offset)
        {
            // make sure we're locked; no sense reading if we aren't
            if (!CheckLock()) throw new InvalidOperationException("Must be locked prior to reading!");

            // this should be safe; we've already checked the page by now

            if (offset == PageOffset)
            {
                // short circuit for when we've already loaded the page
                return true;
            }

            var hdrBuf = new byte[282];

            SeekStream(offset, SeekOrigin.Begin);
            var cnt = EnsureRead(hdrBuf, 0, 27);

            PageOffset = offset;
            if (VerifyHeader(hdrBuf, 0, ref cnt))
            {
                ParsePageHeader(hdrBuf, null);
                return true;
            }
            return false;
        }

        protected override void SetEndOfStreams()
        {
            foreach (var kvp in _streamReaders)
            {
                kvp.Value.SetEndOfStream();
            }
            _streamReaders.Clear();
        }


        #region IPacketData

        public long PageOffset { get; private set; }

        public int StreamSerial { get; private set; }

        public int SequenceNumber { get; private set; }

        public PageFlags PageFlags { get; private set; }

        public long GranulePosition { get; private set; }

        public short PacketCount { get; private set; }

        public bool? IsResync { get; private set; }

        public bool IsContinued { get; private set; }

        public int PageOverhead { get; private set; }

        public (long, int)[] GetPackets()
        {
            if (!CheckLock()) throw new InvalidOperationException("Must be locked!");

            return _packetList;
        }

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            lock (_readLock)
            {
                SeekStream(offset, SeekOrigin.Begin);
                return EnsureRead(buffer, index, count);
            }
        }

        #endregion
    }
}