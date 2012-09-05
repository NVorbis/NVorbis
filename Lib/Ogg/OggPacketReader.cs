/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
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
    class PacketReader
    {
        ContainerReader _container;
        int _streamSerial;
        bool _eosFound;

        int _dataStartPacketIndex;

        Queue<DataPacket> _packetQueue;
        List<DataPacket> _packetList;
        List<int> _pageList;
        DataPacket _lastAddedPacket;

        internal PacketReader(ContainerReader container, int streamSerial)
        {
            _container = container;
            _streamSerial = streamSerial;

            _packetQueue = new Queue<DataPacket>();
            _packetList = new List<DataPacket>();
            _pageList = new List<int>();
        }

        internal void AddPacket(DataPacket packet)
        {
            // if the packet is a resync, it cannot be a continuation...
            if (packet.IsResync)
            {
                packet.IsContinuation = false;
                if (_lastAddedPacket != null) packet.IsContinued = false;
            }

            if (packet.IsContinuation)
            {
                // if we get here, the stream is invalid if there isn't a previous packet
                if (_lastAddedPacket == null) throw new InvalidDataException();

                // if the last packet isn't continued, something is wrong
                if (!_lastAddedPacket.IsContinued) throw new InvalidDataException();

                _lastAddedPacket.MergeWith(packet);
                _lastAddedPacket.IsContinued = packet.IsContinued;
            }
            else
            {
                _packetQueue.Enqueue(packet);
                _lastAddedPacket = packet;
            }

            if (!_pageList.Contains(packet.PageSequenceNumber)) _pageList.Add(packet.PageSequenceNumber);

            _eosFound |= packet.IsEndOfStream;
        }

        internal void SetDataStart()
        {
            _dataStartPacketIndex = _packetList.Count;
        }

        void GetMorePackets()
        {
            // tell our container we need another page...  unless we've found the end of our stream.
            if (!_eosFound) _container.GatherNextPage(_streamSerial);
        }

        internal DataPacket GetNextPacket()
        {
            // make sure we have some packets in the queue...
            while (_packetQueue.Count == 0 || _packetQueue.Peek().IsContinued) // make sure we have at least 1 full packet available
            {
                GetMorePackets();

                if (_packetQueue.Count == 1)
                {
                    // per the spec, if the last packet is a partial, ignore it.
                    if (_eosFound && _packetQueue.Peek().IsContinued)
                    {
                        _packetQueue.Dequeue();
                    }
                }

                // no packets, we must have a problem...
                if (_packetQueue.Count == 0) throw new EndOfStreamException();
            }

            // get the next packet in the queue...
            var packet = _packetQueue.Dequeue();

            if (packet.IsContinued) throw new InvalidDataException();

            // save off the packet for later (in case we start seeking around)
            _packetList.Add(packet);

            return packet;
        }

        internal void SeekToPacket(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");

            if (index >= _packetList.Count + _packetQueue.Count) throw new ArgumentOutOfRangeException("index");

            // add the queued packets to the list...
            _packetList.AddRange(_packetQueue);

            // clear the queue (for now)
            _packetQueue.Clear();

            // re-queue the packets...
            for (; index < _packetList.Count; index++)
            {
                _packetQueue.Enqueue(_packetList[index]);
            }
        }

        internal void SeekToGranule(long position)
        {
            if (position < 0L) throw new ArgumentOutOfRangeException("position");

            // go through the list until we find a GranulePosition higher than the requested one...
            // if GranulePosition == 0, look for the first PageGranulePosition higher than the requested one, then back up 1 page...

            // first, make sure we have the ability to satisfy the request from the cached list
            while (position > _packetList[_packetList.Count - 1].PageGranulePosition)
            {
                if (_eosFound) throw new ArgumentOutOfRangeException("position");

                GetMorePackets();

                if (_packetQueue.Count == 0) throw new EndOfStreamException();

                _packetList.AddRange(_packetQueue);
                _packetQueue.Clear();
            }

            // second, find the last packet where the page granule position is less than or equal to the position
            int idx = _packetList.Count;
            while (_packetList[--idx].PageGranulePosition > position)
            {
                if (idx == 0) throw new InvalidOperationException("Could not find requested position.");
            }

            // _packetList[idx + 1].PageGranulePosition > position
            // _packetList[idx].PageGranulePosition <= position
            // if ==, the packet *ends* on the requested granule position
            // if <, the packet ends before the granule position

            if (_packetList[idx].PageGranulePosition == position)
            {
                ++idx;
            }
            else
            {
                // third, try to find the exact packet where the position exists...
                while (idx < _packetList.Count)
                {
                    var gp = _packetList[idx + 1].GranulePosition;
                    if (gp == 0) break; // we can't go any further
                    if (gp > position) break;   // we've found the right packet

                    ++idx;  // try again...
                }
            }

            if (idx == _packetList.Count) throw new InvalidOperationException("Could not find requested position.");

            SeekToPacket(idx);
        }

        internal void ReadAllPages()
        {
            while (!_eosFound)
            {
                _container.GatherNextPage(_streamSerial);
            }
        }

        internal DataPacket GetLastPacket()
        {
            ReadAllPages();

            return _packetQueue.LastOrDefault() ?? _packetList.Last();
        }

        internal int GetTotalPageCount()
        {
            ReadAllPages();

            return _pageList.Count;
        }
    }
}
