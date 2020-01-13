using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    class PacketProvider : IPacketProvider, IPacketReader
    {
        private IStreamPageReader _reader;

        private int _pageIndex;
        private int _packetIndex;

        private int _lastPacketPageIndex;
        private int _lastPacketPacketIndex;
        private Packet _lastPacket;

        public bool CanSeek => true;

        public int StreamSerial { get; }

        internal PacketProvider(IStreamPageReader reader, int streamSerial)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));

            StreamSerial = streamSerial;
        }

        public long GetGranuleCount()
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            if (!_reader.HasAllPages)
            {
                // this will force the reader to attempt to read all pages
                _reader.GetPage(int.MaxValue, out _, out _, out _, out _, out _, out _);
            }
            return _reader.MaxGranulePosition.Value;
        }

        public IPacket GetNextPacket()
        {
            var pkt = PeekNextPacket();
            if (pkt != null)
            {
                ++_packetIndex;
            }
            return pkt;
        }

        public IPacket PeekNextPacket()
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            if (_lastPacketPacketIndex != _packetIndex || _lastPacketPageIndex != _pageIndex || _lastPacket == null)
            {
                _lastPacket = null;

                var packetIndex = _packetIndex;
                while (_reader.GetPage(_pageIndex, out var granulePos, out var isResync, out var isContinuation, out var isContinued, out var packetCount, out var pageOverhead))
                {
                    if (isContinuation)
                    {
                        ++packetIndex;
                    }

                    if (packetIndex >= packetCount)
                    {
                        ++_pageIndex;
                        packetIndex = _packetIndex = 0;
                        continue;
                    }

                    _lastPacket = CreatePacket(_pageIndex, packetIndex, granulePos, isResync, isContinued, packetCount, pageOverhead);
                    _lastPacketPageIndex = _pageIndex;
                    _lastPacketPacketIndex = _packetIndex;
                    break;
                }
            }
            return _lastPacket;
        }

        public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            var pageIndex = _reader.FindPage(granulePos);

            int packetIndex;
            if (granulePos == 0)
            {
                // for this, we can generically say the first packet on the first page having a non-zero granule
                packetIndex = 0;
            }
            else
            {
                packetIndex = FindPacket(pageIndex, ref granulePos, getPacketGranuleCount);
                packetIndex -= preRoll;
            }

            if (!NormalizePacketIndex(ref pageIndex, ref packetIndex))
            {
                throw new ArgumentOutOfRangeException(nameof(granulePos));
            }

            _lastPacket = null;
            _pageIndex = pageIndex;
            _packetIndex = packetIndex;
            return granulePos;
        }

        private int FindPacket(int pageIndex, ref long granulePos, GetPacketGranuleCount getPacketGranuleCount)
        {
            // we will only look through the current page...
            // if the granule isn't on the page, behavior is:
            //   - if before: return 0 (this shouldn't happen because we already know we'er on the correct page...)
            //   - if after: return packetCount - (isContinued ? 2 : 1)

            if (_reader.GetPage(pageIndex, out var pageGranulePos, out var isResync, out _, out var isContinued, out var packetCount, out var pageOverhead))
            {
                var gp = pageGranulePos;
                var packetIndex = packetCount - (isContinued ? 2 : 1);
                while (packetIndex > 0)
                {
                    if (gp >= granulePos)
                    {
                        Packet packet = CreatePacket(pageIndex, packetIndex, pageGranulePos, isResync, isContinued, packetCount, pageOverhead);
                        if (packet == null)
                        {
                            break;
                        }
                        try
                        {
                            gp -= getPacketGranuleCount(packet, false);
                        }
                        finally
                        {
                            packet.Done();
                        }
                    }

                    if (gp < granulePos)
                    {
                        granulePos = gp;
                        return packetIndex;
                    }

                    --packetIndex;
                }
                if (packetIndex == 0)
                {
                    // we're likely in the right spot for the granulePos...
                    if (pageIndex > 0 && _reader.GetPage(pageIndex - 1, out var prevGranulePos, out _, out _, out _, out _, out _))
                    {
                        granulePos = prevGranulePos;
                        return 0;
                    }
                }
            }
            return -1;
        }

        // this method calc's the appropriate page and packet prior to the one specified, honoring continuations and handling negative packetIndex values
        // if packet index is larger than the current page allows, we just return it as-is
        private bool NormalizePacketIndex(ref int pageIndex, ref int packetIndex)
        {
            if (!_reader.GetPage(pageIndex, out _, out var isResync, out var isContinuation, out _, out _, out _))
            {
                return false;
            }

            var pgIdx = pageIndex;
            var pktIdx = packetIndex;

            while (pktIdx < (isContinuation ? 1: 0))
            {
                // can't merge across resync
                if (isContinuation && isResync) return false;

                // get the previous packet
                var wasContinuation = isContinuation;
                if (!_reader.GetPage(--pgIdx, out _, out isResync, out isContinuation, out var isContinued, out var packetCount, out _))
                {
                    return false;
                }

                // can't merge if continuation flags don't match
                if (wasContinuation && !isContinued) return false;

                // add the previous packet's packetCount
                pktIdx += packetCount - (wasContinuation ? 1 : 0);
            }

            pageIndex = pgIdx;
            packetIndex = pktIdx;
            return true;
        }

        private Packet CreatePacket(int pageIndex, int packetIndex, long granulePos, bool isResync, bool isContinued, int packetCount, int pageOverhead)
        {
            // create the packet list and add the item to it
            var pktList = new List<Tuple<long, int>> { _reader.GetPagePackets(pageIndex)[packetIndex] };

            // make sure we handle continuations
            bool isLastPacket;
            bool isFirstPacket;
            var finalPage = pageIndex;
            if (isContinued && packetIndex == packetCount - 1)
            {
                // by definition, it's the first packet in the page it ends on
                isFirstPacket = true;

                // but we don't want to include the current page's overhead if we didn't start the page
                if (packetIndex > 0)
                {
                    pageOverhead = 0;
                }

                // go read the next page(s) that include this packet
                var contPageIdx = pageIndex;
                while (isContinued)
                {
                    if (!_reader.GetPage(++contPageIdx, out granulePos, out isResync, out var isContinuation, out isContinued, out packetCount, out var contPageOverhead))
                    {
                        // no more pages?  In any case, we can't satify the request
                        return null;
                    }
                    pageOverhead += contPageOverhead;

                    // if the next page isn't a continuation or is a resync, the stream is broken so we'll just return what we could get
                    if (!isContinuation || isResync)
                    {
                        break;
                    }

                    // if the next page is continued, only keep reading if there are more packets in the page
                    if (isContinued && packetCount > 1)
                    {
                        isContinued = false;
                    }

                    // add the packet to the list
                    pktList.Add(_reader.GetPagePackets(contPageIdx)[0]);
                }

                // we're now the first packet in the final page, so we'll act like it...
                isLastPacket = packetCount == 1;

                // track the final page read
                finalPage = contPageIdx;
            }
            else
            {
                isFirstPacket = packetIndex == 0;
                isLastPacket = packetIndex == packetCount - 1;
            }

            // create the packet instance and populate it with the appropriate initial data
            var packet = new Packet(pktList, this)
            {
                PageGranulePosition = granulePos,
                IsResync = isResync,
            };

            // if it's the first packet, associate the container overhead with it
            if (isFirstPacket)
            {
                packet.ContainerOverheadBits = pageOverhead * 8;
            }

            // if we're the last packet completed in the page, set the .GranulePosition
            if (isLastPacket)
            {
                packet.GranulePosition = packet.PageGranulePosition;

                // if we're the last packet completed in the page, no more pages are available, and _hasAllPages is set, set .IsEndOfStream
                if (_reader.HasAllPages && finalPage == _reader.PageCount - 1)
                {
                    packet.IsEndOfStream = true;
                }
            }

            // done!
            return packet;
        }

        void IPacketReader.InvalidatePacketCache(IPacket packet)
        {
            if (ReferenceEquals(_lastPacket, packet))
            {
                _lastPacket = null;
            }
        }

        int IPacketReader.FillBuffer(long offset, byte[] buffer, int index, int count)
        {
            return _reader.FillBuffer(offset, buffer, index, count);
        }
    }
}
