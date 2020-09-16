using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestApp
{
    sealed class WaveWriter : IDisposable
    {
        const string BLANK_HEADER = "RIFF\0\0\0\0WAVEfmt ";
        const string BLANK_DATA_HEADER = "data\0\0\0\0";

        Stream _stream;
        BinaryWriter _writer;

        public WaveWriter(string fileName, int sampleRate, int channels)
        {
            _stream = File.Create(fileName);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, true);

            // basic header
            _writer.Write(Encoding.UTF8.GetBytes(BLANK_HEADER));
            // fmt header size
            _writer.Write(18);
            // encoding (IeeeFloat)
            _writer.Write((short)3);
            // channels
            _writer.Write((short)channels);
            // samplerate
            _writer.Write(sampleRate);
            // averagebytespersecond
            var blockAlign = channels * sizeof(float);
            _writer.Write(blockAlign * sampleRate);
            // blockalign
            _writer.Write((short)blockAlign);
            // bitspersample (32)
            _writer.Write((short)32);
            // extrasize
            _writer.Write((short)0);
            // "data\0\0\0\0"
            _writer.Write(Encoding.UTF8.GetBytes(BLANK_DATA_HEADER));
        }

        public void WriteSamples(float[] buf, int offset, int count)
        {
            for (var i = 0; i < count; i++, offset++)
            {
                _writer.Write(buf[offset]);
            }
        }

        public void Dispose()
        {
            // RIFF chunk size
            _writer.Seek(4, SeekOrigin.Begin);
            _writer.Write((uint)(_stream.Length - 8));

            // data chunk size
            _writer.Seek(44, SeekOrigin.Begin);
            _writer.Write((uint)(_stream.Length - 48));

            _writer?.Dispose();
            _writer = null;

            _stream?.Dispose();
            _stream = null;
        }
    }
}
