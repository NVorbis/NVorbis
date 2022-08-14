NVorbis    [![Gitter](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/ioctlLR/NVorbis?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)
-------

NVorbis is a .Net library for decoding Xiph.org Vorbis files. It is designed to run in partial trust environments and does not require P/Invoke or unsafe code. It is built for .Net Standard 2.0 and .Net Framework 4.5.

This implementation is based on the Vorbis specification found on xiph.org. The MDCT and Huffman codeword generator were borrowed from public domain implementations in https://github.com/nothings/stb/blob/master/stb_vorbis.c.

To use:

```cs
// add a package reference to NVorbis.dll
// https://www.nuget.org/packages/NVorbis

using NVorbis;

// this is the simplest usage; see the public classes and constructors for other options
string path = "path/to/file.ogg";
using (VorbisReader vorbis = new VorbisReader(path))
{
	// get the channels & sample rate
	int channels = vorbis.Channels;
	int sampleRate = vorbis.SampleRate;
	long samples = vorbis.TotalSamples;

	// get a TimeSpan indicating the total length of the Vorbis stream
	TimeSpan totalTime = vorbis.TotalTime;
	
	// grab samples
	float[] readBuffer = new float[(int) samples * channels];
	vorbis.ReadSamples(readBuffer, 0, (int) samples * channels);
	
	Console.WriteLine($"File `{path}` has {channels} channels, sample rate of {sampleRate}, and it will take {totalTime.ToString()} to play.");
	// push the audio data to your favorite audio API
}
```

NVorbis can be downloaded on [NuGet](https://www.nuget.org/packages/NVorbis/).

If you are using [NAudio](https://github.com/naudio/NAudio), support is available via [NAudio.Vorbis](https://github.com/NAudio/Vorbis).

Support for [OpenTK](https://github.com/opentk/opentk) also exists and can be downloaded on [NuGet](https://www.nuget.org/packages/NVorbis.OpenTKSupport/).

If you have any questions or comments, feel free to join us on Gitter.  If you have any issues or feature requests, please submit them in the issue tracker.
