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

namespace NVorbis
{
    public class VorbisStreamStatus
    {
        OggContainerReader _container;
        VorbisReader _reader;

        internal VorbisStreamStatus(OggContainerReader container, VorbisReader reader)
        {
            _container = container;
            _reader = reader;

            // _container._containerBits
            // _reader._bookBits
            // _reader._floorBits
            // _reader._floorHdrBits
            // _reader._glueBits
            // _reader._mapHdrBits
            // _reader._metaBits
            // _reader._modeBits
            // _reader._modeHdrBits
            // _reader._resBits
            // _reader._resHdrBits
            // _reader._timeHdrBits
            // _reader._wasteBits
            // _reader._wasteHdrBits

            // EffectiveBitRate // only good through current playback, defaults to nominal
        }
    }
}
