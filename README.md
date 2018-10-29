NVorbis    [![Gitter](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/ioctlLR/NVorbis?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)
-------

NVorbis is a .Net library for decoding Xiph.org Vorbis files. It is designed to run in partial trust environments and does not require P/Invoke or unsafe code. It is built for .Net Standard 2.0 and .Net Framework 3.5.

This implementation is based on the Vorbis specification found on xiph.org. The MDCT and Huffman codeword generator were borrowed from public domain implementations in https://github.com/nothings/stb/blob/master/stb_vorbis.c.

Currently the only container supported is Xiph.org Ogg. There are no plans for the Ogg Skeleton and Matroska / WebM containers or RTP support at this time.

To use:

```cs
// add a reference to NVorbis.dll

using (var vorbis = new NVorbis.VorbisReader("path/to/file.ogg"))
{
	// get the channels & sample rate
    var channels = vorbis.Channels;
    var sampleRate = vorbis.SampleRate;

    // OPTIONALLY: get a TimeSpan indicating the total length of the Vorbis stream
    var totalTime = vorbis.TotalTime;

	// create a buffer for reading samples
    var readBuffer = new float[channels * sampleRate / 5];	// 200ms

	// get the initial position (obviously the start)
    var position = TimeSpan.Zero;

    // go grab samples
    int cnt;
    while ((cnt = vorbis.ReadSamples(readBuffer, 0, readBuffer.Length)) > 0)
    {
    	// do stuff with the buffer
    	// samples are interleaved (chan0, chan1, chan0, chan1, etc.)
    	// sample value range is -0.99999994f to 0.99999994f unless vorbis.ClipSamples == false
    
    	// OPTIONALLY: get the position we just read through to...
        position = vorbis.DecodedTime;
    }
}
```

NVorbis can be downloaded on [NuGet](https://www.nuget.org/packages/NVorbis/).

If you are using [NAudio](https://github.com/naudio/NAudio), support is available via [NAudio.Vorbis](https://github.com/NAudio/Vorbis).

Support for [OpenTK](https://github.com/opentk/opentk) also exists and can be downloaded on [NuGet](https://www.nuget.org/packages/NVorbis.OpenTKSupport/).

If you have any questions or comments, feel free to join us on Gitter.  If you have any issues or feature requests, please submit them in the issue tracker.
