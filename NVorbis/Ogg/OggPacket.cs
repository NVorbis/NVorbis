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
using System.Text;
using System.IO;

namespace NVorbis.Ogg
{
    class Packet : DataPacket
    {
        BufferedReadStream _stream;

        long _offset;
        long _length;
        Packet _mergedPacket;

        internal Packet Next { get; set; }
        internal Packet Prev { get; set; }
        internal bool IsContinued { get; set; }
        internal bool IsContinuation { get; set; }

        int _curOfs;

        internal Packet(BufferedReadStream stream, long streamOffset, int length)
            : base(length)
        {
            _stream = stream;

            _offset = streamOffset;
            _length = length;
            _curOfs = 0;
        }

        internal void MergeWith(NVorbis.DataPacket continuation)
        {
            var op = continuation as Packet;

            if (op == null) throw new ArgumentException("Incorrect packet type!");

            Length += continuation.Length;

            if (_mergedPacket == null)
            {
                _mergedPacket = op;
            }
            else
            {
                _mergedPacket.MergeWith(continuation);
            }

            // per the spec, a partial packet goes with the next page's granulepos.  we'll go ahead and assign it to the next page as well
            PageGranulePosition = continuation.PageGranulePosition;
            PageSequenceNumber = continuation.PageSequenceNumber;
        }

        internal void Reset()
        {
            _curOfs = 0;
            ResetBitReader();

            if (_mergedPacket != null)
            {
                _mergedPacket.Reset();
            }
        }

        protected override int ReadNextByte()
        {
            if (_curOfs == _length)
            {
                if (_mergedPacket == null) return -1;

                return _mergedPacket.ReadNextByte();
            }

            _stream.Seek(_curOfs + _offset, SeekOrigin.Begin);

            var b = _stream.ReadByte();
            ++_curOfs;
            return b;
        }

        public override void Done()
        {
            if (_mergedPacket != null)
            {
                _mergedPacket.Done();
            }
            else
            {
                _stream.DiscardThrough(_offset + _length);
            }
        }
    }
}
