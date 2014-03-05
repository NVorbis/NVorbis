/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2013, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace NVorbis.Ogg
{
    [System.Diagnostics.DebuggerTypeProxy(typeof(PacketReader.DebugView))]
    class PacketReader : IPacketProvider
    {
        class DebugView
        {
            PacketReader _reader;

            public DebugView(PacketReader reader)
            {
                if (reader == null) throw new ArgumentNullException("reader");
                _reader = reader;
            }

            public ContainerReader Container { get { return _reader._container; } }
            public int StreamSerial { get { return _reader._streamSerial; } }
            public bool EndOfStreamFound { get { return _reader._eosFound; } }

            public int CurrentPacketIndex
            {
                get
                {
                    if (_reader._current == null) return -1;
                    return Array.IndexOf(Packets, _reader._current);
                }
            }

            Packet _last, _first;
            Packet[] _packetList = new Packet[0];
            public Packet[] Packets
            {
                get
                {
                    if (_reader._last == _last && _reader._first == _first)
                    {
                        return _packetList;
                    }

                    _last = _reader._last;
                    _first = _reader._first;

                    var packets = new List<Packet>();
                    var node = _first;
                    while (node != null)
                    {
                        packets.Add(node);
                        node = node.Next;
                    }
                    _packetList = packets.ToArray();
                    return _packetList;
                }
            }
        }

        ContainerReader _container;
        int _streamSerial;
        bool _eosFound;

        Packet _first, _current, _last;

        object _packetLock = new object();

        internal PacketReader(ContainerReader container, int streamSerial)
        {
            _container = container;
            _streamSerial = streamSerial;
        }

        public void Dispose()
        {
            _eosFound = true;

            _container.DisposePacketReader(this);
            _container = null;

            _current = null;

            if (_first != null)
            {
                var node = _first;
                _first = null;
                while (node.Next != null)
                {
                    var temp = node.Next;
                    node.Next = null;
                    node = temp;
                    node.Prev = null;
                }
                node = null;
            }

            _last = null;
        }

        internal void AddPacket(Packet packet)
        {
            lock (_packetLock)
            {
                // if the packet is a resync, it cannot be a continuation...
                if (packet.IsResync)
                {
                    packet.IsContinuation = false;
                    if (_last != null) _last.IsContinued = false;
                }

                if (packet.IsContinuation)
                {
                    // if we get here, the stream is invalid if there isn't a previous packet
                    if (_last == null) throw new InvalidDataException();

                    // if the last packet isn't continued, something is wrong
                    if (!_last.IsContinued) throw new InvalidDataException();

                    _last.MergeWith(packet);
                    _last.IsContinued = packet.IsContinued;
                }
                else
                {
                    var p = packet as Packet;
                    if (p == null) throw new ArgumentException("Wrong packet datatype", "packet");

                    if (_first == null)
                    {
                        // this is the first packet to add, so just set first & last to point at it
                        _first = p;
                        _last = p;
                    }
                    else
                    {
                        // swap the new packet in to the last position (remember, we're doubly-linked)
                        _last = ((p.Prev = _last).Next = p);
                    }
                }

                _eosFound |= packet.IsEndOfStream;
            }
        }

        internal bool HasEndOfStream
        {
            get { return _eosFound; }
        }

        public int StreamSerial
        {
            get { return _streamSerial; }
        }

        public long ContainerBits
        {
            get;
            set;
        }

        public bool CanSeek
        {
            get { return _container.CanSeek; }
        }

        bool EnsurePackets()
        {
            do
            {
                lock (_packetLock)
                {
                    // don't bother reading more packets unless we actually need them
                    if (_last != null && !_last.IsContinued && _current != _last) return true;
                }

                if (!_eosFound && !_container.GatherNextPage(_streamSerial))
                {
                    // not technically true, but because the container doesn't have any more pages, it might as well be
                    _eosFound = true;
                    return false;
                }

                lock (_packetLock)
                {
                    // if we've read the entire stream, do some further checking...
                    if (_eosFound)
                    {
                        // make sure the last packet read isn't continued... (per the spec, if the last packet is a partial, ignore it)
                        // if _last is null, something has gone horribly wrong (i.e., that shouldn't happen)
                        if (_last.IsContinued)
                        {
                            _last = _last.Prev;
                            _last.Next.Prev = null;
                            _last.Next = null;
                        }

                        // if our "current" packet is the same as the "last" packet, we're done
                        // _last won't be null here
                        if (_current == _last) return false;
                    }
                }
            } while (true);
        }

        // This is fast path... don't make the caller wait if we can help it...
        public DataPacket GetNextPacket()
        {
            // make sure we have enough packets... if we're at the end of the stream, return null
            if (!EnsurePackets()) return null;

            Packet packet;
            lock (_packetLock)
            {
                // "current" is always set to the packet previous to the one about to be returned...
                if (_current == null)
                {
                    packet = (_current = _first);
                }
                else
                {
                    packet = (_current = _current.Next);
                }
            }

            if (packet.IsContinued) throw new InvalidDataException();

            // make sure the packet is ready for "playback"
            packet.Reset();

            return packet;
        }

        public DataPacket PeekNextPacket()
        {
            Packet curPacket;
            lock (_packetLock)
            {
                // get the current packet
                curPacket = (_current ?? _first);

                // if we don't have one, we can't do anything...
                if (curPacket == null) return null;

                // if we have a next packet, go ahead and return it
                if (curPacket.Next != null)
                {
                    return curPacket.Next;
                }

                // if we've hit the end of the stream, we're done
                if (_eosFound) return null;
            }

            // finally, try to load more packets and just return the next one
            EnsurePackets();
            return curPacket.Next;
        }

        public void SeekToPacket(int index)
        {
            if (!CanSeek) throw new InvalidOperationException();

            // we won't worry about locking here since the only atomic operation is the assignment to _current
            _current = GetPacketByIndex(index).Prev;
        }

        Packet GetPacketByIndex(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");

            // don't lock since we're obviously not even started yet...
            while (_first == null)
            {
                if (_eosFound) throw new InvalidDataException();

                if (!_container.GatherNextPage(_streamSerial)) _eosFound = true;
            }

            var packet = _first;
            while (--index >= 0)
            {
                lock (_packetLock)
                {
                    if (packet.Next != null)
                    {
                        packet = packet.Next;
                        continue;
                    }

                    if (_eosFound) throw new ArgumentOutOfRangeException("index");
                }

                do
                {
                    if (!_container.GatherNextPage(_streamSerial))
                    {
                        _eosFound = true;
                        throw new ArgumentOutOfRangeException("index");
                    }
                } while (packet.Next == null);

                // go ahead and loop back to the locked section above...
                ++index;
            }

            return packet;
        }

        internal void ReadAllPages()
        {
            if (!CanSeek) throw new InvalidOperationException();

            // don't hold the lock any longer than we have to
            while (!_eosFound)
            {
                if (!_container.GatherNextPage(_streamSerial)) _eosFound = true;
            }
        }

        internal DataPacket GetLastPacket()
        {
            ReadAllPages();

            return _last;
        }

        public int GetTotalPageCount()
        {
            ReadAllPages();

            // here we just count the number of times the page sequence number changes
            var cnt = 0;
            var lastPageSeqNo = 0;
            var packet = _first;
            while (packet != null)
            {
                if (packet.PageSequenceNumber != lastPageSeqNo)
                {
                    ++cnt;
                    lastPageSeqNo = packet.PageSequenceNumber;
                }
                packet = packet.Next;
            }
            return cnt;
        }

        public DataPacket GetPacket(int packetIndex)
        {
            var packet = GetPacketByIndex(packetIndex);
            packet.Reset();
            return packet;
        }

        Packet GetLastPacketInPage(Packet packet)
        {
            if (packet != null)
            {
                var pageSeqNumber = packet.PageSequenceNumber;
                while (packet.Next != null && packet.Next.PageSequenceNumber == pageSeqNumber)
                {
                    packet = packet.Next;
                }

                while (packet != null && packet.IsContinued)
                {
                    // gotta go grab the next page
                    if (_eosFound)
                    {
                        packet = null;
                    }
                    else if (!_container.GatherNextPage(_streamSerial))
                    {
                        _eosFound = true;
                        packet = null;
                    }
                }
            }
            return packet;
        }

        Packet FindPacketInPage(Packet pagePacket, long targetGranulePos, Func<DataPacket, DataPacket, int> packetGranuleCountCallback)
        {
            var lastPacketInPage = GetLastPacketInPage(pagePacket);
            if (lastPacketInPage == null)
            {
                return null;
            }

            // return the packet the granule position is in
            var packet = lastPacketInPage;
            do
            {
                if (!packet.GranuleCount.HasValue)
                {
                    // we don't know its length or position...

                    // if it's the last packet in the page, it gets the page's granule position. Otherwise, calc it.
                    if (packet == lastPacketInPage)
                    {
                        packet.GranulePosition = packet.PageGranulePosition;
                    }
                    else
                    {
                        packet.GranulePosition = packet.Next.GranulePosition - packet.Next.GranuleCount.Value;
                    }

                    // if it's the last packet in the stream, it might be a partial.  The spec says the last packet has to be on its own page, so if it is not assume the stream was truncated.
                    if (packet == _last && _eosFound && packet.Prev.PageSequenceNumber < packet.PageSequenceNumber)
                    {
                        packet.GranuleCount = (int)(packet.GranulePosition - packet.Prev.PageGranulePosition);
                    }
                    else if (packet.Prev != null)
                    {
                        packet.Prev.Reset();
                        packet.Reset();

                        packet.GranuleCount = packetGranuleCountCallback(packet, packet.Prev);
                    }
                    else
                    {
                        // probably the first data packet...
                        if (packet.GranulePosition > packet.Next.GranulePosition - packet.Next.GranuleCount)
                        {
                            throw new InvalidOperationException("First data packet size mismatch");
                        }
                        packet.GranuleCount = (int)packet.GranulePosition;
                    }
                }

                // we now know the granule position and count of the packet... is the target within that range?
                if (targetGranulePos <= packet.GranulePosition && targetGranulePos > packet.GranulePosition - packet.GranuleCount)
                {
                    // make sure the previous packet has a position too
                    if (packet.Prev != null && !packet.Prev.GranuleCount.HasValue)
                    {
                        packet.Prev.GranulePosition = packet.GranulePosition - packet.GranuleCount.Value;
                    }
                    return packet;
                }

                packet = packet.Prev;
            } while (packet != null && packet.PageSequenceNumber == lastPacketInPage.PageSequenceNumber);

            // couldn't find it, but maybe that's because something glitched in the file...
            // we're doing this in case there's a dicontinuity in the file...  It's not perfect, but it'll work
            if (packet != null && packet.PageGranulePosition < targetGranulePos)
            {
                packet.GranulePosition = packet.PageGranulePosition;
                return packet.Next;
            }
            return null;
        }

        public int FindPacket(long granulePos, Func<DataPacket, DataPacket, int> packetGranuleCountCallback)
        {
            // This will find which packet contains the granule position being requested.  It is basically a linear search.
            // Please note, the spec actually calls for a bisection search, but the result here should be the same.

            // don't look for any position before 0!
            if (granulePos < 0) throw new ArgumentOutOfRangeException("granulePos");

            Packet foundPacket = null;

            // determine which direction to search from...
            var packet = _current ?? _first;
            if (granulePos > packet.PageGranulePosition)
            {
                // forward search

                // find the first packet in the page the requested granule is on
                while (granulePos > packet.PageGranulePosition)
                {
                    if ((packet.Next == null || packet.IsContinued) && !_eosFound)
                    {
                        if (!_container.GatherNextPage(_streamSerial))
                        {
                            _eosFound = true;
                            packet = null;
                            break;
                        }
                    }
                    packet = packet.Next;
                }

                foundPacket = FindPacketInPage(packet, granulePos, packetGranuleCountCallback);
            }
            else
            {
                // reverse search (or we're looking at the same page)
                while (packet.Prev != null && (granulePos < packet.Prev.PageGranulePosition || packet.Prev.PageGranulePosition == -1))
                {
                    packet = packet.Prev;
                }

                foundPacket = FindPacketInPage(packet, granulePos, packetGranuleCountCallback);
            }

            // if we didn't find a packet, just return "not found"
            if (foundPacket == null)
            {
                return -1;
            }

            // we found the packet, so now we just have to count back to the beginning and see what its index is...
            int idx = 0;
            while (foundPacket.Prev != null)
            {
                foundPacket = foundPacket.Prev;
                ++idx;
            }
            return idx;
        }

        public long GetGranuleCount()
        {
            return GetLastPacket().PageGranulePosition;
        }
    }
}
