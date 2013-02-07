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
        Stream _stream;

        long _offset;
        long _length;
        Packet _mergedPacket;

        byte[] _savedBuffer;
        int _bufOffset;

        internal Packet Next { get; set; }
        internal Packet Prev { get; set; }

        int _curOfs;

        internal Packet(Stream stream, long streamOffset, int length)
            : base(length)
        {
            _stream = stream;

            _offset = streamOffset;
            _length = length;
            _curOfs = 0;
        }

        internal void SetBuffer(byte[] savedBuf, int offset)
        {
            _savedBuffer = savedBuf;
            _bufOffset = offset;
        }

        protected override void DoMergeWith(NVorbis.DataPacket continuation)
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
                _mergedPacket.DoMergeWith(continuation);
            }

            // per the spec, a partial packet goes with the next page's granulepos.  we'll go ahead and assign it to the next page as well
            PageGranulePosition = continuation.PageGranulePosition;
            PageSequenceNumber = continuation.PageSequenceNumber;
        }

        protected override bool CanReset
        {
            get { return true; }
        }

        protected override void DoReset()
        {
            _curOfs = 0;

            if (_mergedPacket != null)
            {
                _mergedPacket.DoReset();
            }
        }

        protected override int ReadNextByte()
        {
            if (_curOfs == _length)
            {
                if (_mergedPacket == null) return -1;

                return _mergedPacket.ReadNextByte();
            }

            if (_savedBuffer != null)
            {
                return _savedBuffer[_bufOffset + _curOfs++];
            }

            _stream.Seek(_curOfs + _offset, SeekOrigin.Begin);

            var b = _stream.ReadByte();
            ++_curOfs;
            return b;
        }

        public override void Done()
        {
            if (_savedBuffer != null)
            {
                _savedBuffer = null;
            }
        }
    }
}
