# NVorbis

[![NuGet](https://img.shields.io/nuget/v/NVorbis.svg)](https://www.nuget.org/packages/NVorbis/)
[![NuGet downloads](https://img.shields.io/nuget/dt/NVorbis.svg)](https://www.nuget.org/packages/NVorbis/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

NVorbis is a fully managed .NET decoder for [Xiph.org Ogg Vorbis](https://xiph.org/vorbis/) audio. It reads an `.ogg` stream and hands you interleaved 32-bit float PCM samples, with support for seeking, tags, stream statistics, and chained (concatenated) streams.

- **Fully managed** — no P/Invoke, no native binaries to ship per platform.
- **Broad reach** — targets `netstandard2.0` and `netstandard2.1`, so it runs on .NET 5+, .NET Core 2.0+, .NET Framework 4.6.1+, Mono, Xamarin, and Unity.
- **Streaming-friendly** — decodes seekable *and* forward-only (network/pipe) streams.
- **Complete decode features** — accurate seeking with pre-roll, Vorbis comment tags, per-stream bitrate/statistics, sample clipping, and chained logical streams.
- **Lightweight** — one small runtime dependency (`System.Runtime.CompilerServices.Unsafe`, only on `netstandard2.1`; `netstandard2.0` pulls it and a couple of other polyfills transitively).

The implementation follows the [Vorbis I specification](https://xiph.org/vorbis/doc/Vorbis_I_spec.html). The MDCT and Huffman codeword generator are derived from the public-domain [stb_vorbis](https://github.com/nothings/stb/blob/master/stb_vorbis.c).

> **Scope:** NVorbis is a *decoder only* — it does not encode Vorbis. It handles Ogg-encapsulated Vorbis (`.ogg`); it is not an Opus, FLAC, or Speex decoder, even though those also use the Ogg container.

## Contents

- [Install](#install)
- [Quick start](#quick-start)
- [Understanding the output](#understanding-the-output)
- [Common tasks](#common-tasks)
- [API overview](#api-overview)
- [Requirements](#requirements)
- [Using NVorbis with NAudio](#using-nvorbis-with-naudio)
- [Building from source](#building-from-source)
- [License](#license)
- [Contributing & support](#contributing--support)
- [Acknowledgements](#acknowledgements)

## Install

```
dotnet add package NVorbis
```

Or via the Package Manager Console:

```
Install-Package NVorbis
```

## Quick start

```csharp
using NVorbis;

using var vorbis = new VorbisReader("path/to/file.ogg");

// Stream info
int channels   = vorbis.Channels;
int sampleRate = vorbis.SampleRate;
TimeSpan total = vorbis.TotalTime;

// A ~200ms buffer. Values are interleaved by channel (L, R, L, R, ...).
float[] buffer = new float[channels * sampleRate / 5];

int count;
while ((count = vorbis.ReadSamples(buffer, 0, buffer.Length)) > 0)
{
    // `count` values are ready in buffer[0..count].
    // Each value is in the range [-1.0, 1.0] (unless ClipSamples is false).
    // ... send them to your audio output ...
}
```

`ReadSamples` returns the number of values written, or `0` at end of stream. There is also a `Span<float>` overload:

```csharp
int count = vorbis.ReadSamples(buffer.AsSpan());
```

The constructor throws `ArgumentException` if the input is not a valid Ogg Vorbis container.

## Understanding the output

- **Output is interleaved 32-bit float.** For stereo, `buffer[0]` is left, `buffer[1]` is right, `buffer[2]` is left, and so on.
- **Values are nominally in `[-1.0, 1.0]`.** By default `ClipSamples` is `true`, so out-of-range values are clamped to `[-0.99999994f, 0.99999994f]`. Set `ClipSamples = false` to get the raw (possibly overshooting) values and clip/limit them yourself. `HasClipped` tells you whether any clipping has actually occurred.
- **End of stream** is signalled by `ReadSamples` returning `0`; `IsEndOfStream` reflects the same condition as a property.

### Samples vs. frames

Two units show up in the API, and the member names tell you which is which:

| Member(s) | Unit | Meaning |
|---|---|---|
| `ReadSamples(...)` / `Read(...)` — the buffer, `count`, and the return value | **samples** (interleaved values) | one `float` per channel per frame; always a multiple of `Channels` |
| `TotalFrames`, `FramePosition`, `SeekTo(long)` | **frames** | a position/length on the timeline (samples *per channel*), independent of channel count |

Concretely, for a 44.1 kHz **stereo** stream:

```csharp
long frames = vorbis.TotalFrames;         // e.g. 441000  -> 10 seconds
long floats = frames * vorbis.Channels;   // 882000 total values you will read back
vorbis.SeekTo(44100L);                     // seeks to 1 second in (frame 44100)
int  got    = vorbis.ReadSamples(buf, 0, 480);  // up to 480 values = 240 frames of stereo
```

So `TotalFrames` is the length in frames — multiply by `Channels` to get the number of `float`s `ReadSamples` will hand you in total.

### Convert to 16-bit PCM

Most OS audio APIs and WAV files want interleaved `Int16`. With `ClipSamples` left on (the default), the floats are already in range, so the conversion is a scale-and-round:

```csharp
float[] floatBuf = new float[channels * sampleRate / 5];
short[] pcm      = new short[floatBuf.Length];

int count;
while ((count = vorbis.ReadSamples(floatBuf, 0, floatBuf.Length)) > 0)
{
    for (int i = 0; i < count; i++)
    {
        int s = (int)(floatBuf[i] * 32767f);
        pcm[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s));
    }
    // ... write pcm[0..count] to your device / WAV writer ...
}
```

## Common tasks

### Read from a stream you already own

```csharp
// Pass closeOnDispose: false to keep ownership of the stream.
using var vorbis = new VorbisReader(httpStream, closeOnDispose: false);
```

Non-seekable streams (network, pipes) decode forward-only; seeking requires a seekable stream.

### Open without blocking the UI thread

Opening a reader parses the header packets synchronously. Use `OpenAsync` to do that work on a background thread:

```csharp
var vorbis = await VorbisReader.OpenAsync("path/to/file.ogg");
// or: await VorbisReader.OpenAsync(stream, closeOnDispose: true, cancellationToken);
```

> **Threading:** a `VorbisReader` is not thread-safe. After opening, drive `ReadSamples`/`SeekTo` from a single thread for the object's lifetime. `OpenAsync` only moves the *open* off the calling thread. (`IStreamStats.InstantBitRate` is the one value safe to read concurrently — e.g. from a UI thread for a VU/bitrate display.)

### Seek

```csharp
vorbis.SeekTo(TimeSpan.FromSeconds(30));    // by time
vorbis.SeekTo(441000L);                      // by frame position (10s at 44.1 kHz) — see samples vs. frames

vorbis.TimePosition = TimeSpan.FromSeconds(10);   // property form
long framePos = vorbis.FramePosition;              // in frames
```

Seeking decodes one packet of pre-roll so the seek point is artifact-free. `SeekTo` also accepts a `SeekOrigin` (`Begin` by default) to seek relative to the current position or the end.

### Read tags

Vorbis comments are exposed both as strongly-typed properties and as a raw dictionary.

```csharp
var tags = vorbis.Tags;

string title  = tags.Title;          // TITLE
string artist = tags.Artist;         // ARTIST
string album  = tags.Album;          // ALBUM
string vendor = tags.EncoderVendor;  // encoder vendor string

// Fields that legitimately repeat come back as lists:
IReadOnlyList<string> genres = tags.Genres;      // GENRE (may be several)
IReadOnlyList<string> dates  = tags.Dates;       // DATE

// Arbitrary / non-standard keys:
string gain = tags.GetTagSingle("REPLAYGAIN_TRACK_GAIN");
IReadOnlyList<string> everyComment = tags.GetTagMulti("COMMENT");

// Or enumerate everything:
foreach (var kvp in tags.All)
    Console.WriteLine($"{kvp.Key} = {string.Join(", ", kvp.Value)}");
```

When a key repeats, `GetTagSingle(key)` returns the **last** value by default; pass `concatenate: true` to join all occurrences with newlines.

### Stream statistics

```csharp
var stats = vorbis.StreamStats;
int instant   = stats.InstantBitRate;     // bit rate of the last couple of packets
int effective = stats.EffectiveBitRate;   // averaged over everything decoded so far
long audio    = stats.AudioBits;          // bits that produced audio
long overhead = stats.OverheadBits;       // non-audio bits (excluding container framing)
int packets   = stats.PacketCount;
```

`vorbis.NominalBitrate` / `UpperBitrate` / `LowerBitrate` report the values declared in the stream header (when present).

### Chained / concatenated files

Some `.ogg` files contain several logical streams back to back, possibly with different channel counts or sample rates. NVorbis exposes each as an `IStreamDecoder`.

```csharp
using var reader = new VorbisReader("chained.ogg");

// Inspect / skip streams as they are discovered:
reader.NewStream += (sender, e) =>
{
    // e.StreamDecoder is the decoder for the newly found logical stream.
    if (e.StreamDecoder.Channels > 2)
        e.IgnoreStream = true;   // don't add it to reader.Streams
};

foreach (var stream in reader.Streams)
{
    // stream.Channels, stream.SampleRate, stream.TotalTime, ...
}

// Switch which logical stream ReadSamples/SeekTo operate on:
if (reader.SwitchStreams(1))
{
    // returns true when the new stream's format (channels/sample rate) differs
    // from the previous one — meaning you should reconfigure your output device.
}

// Discover the next stream in a stream that is still being read forward-only:
while (reader.FindNextStream()) { /* ... */ }
```

## API overview

NVorbis has two layers; most callers only need the first.

- **`VorbisReader`** (implements `IVorbisReader`) — the convenience wrapper. Opens a file or `Stream`, selects the first logical stream, and gives you `ReadSamples`, `SeekTo`, `Tags`, `StreamStats`, and chained-stream management. Start here.
- **`IStreamDecoder` + `Ogg.ContainerReader`** — the lower-level pieces `VorbisReader` is built on. An `IStreamDecoder` decodes a single logical stream (`Read(Span<float>)`, `SeekTo`, `Tags`, `Stats`, position/format properties); `ContainerReader` demuxes Ogg pages into per-stream packet providers. Reach for these if you need direct control over demuxing or want to drive individual streams yourself. The decoders in `VorbisReader.Streams` are exactly these `IStreamDecoder` instances.

## Requirements

- A runtime supporting **.NET Standard 2.0** or **2.1**: .NET 5+, .NET Core 2.0+, .NET Framework 4.6.1+, Mono, Xamarin, or Unity (2018.1+).
- Package dependencies are resolved automatically by NuGet:
  - **netstandard2.0**: `Microsoft.Bcl.Numerics`, `System.Memory`, `System.ValueTuple` (the latter two provide `Span<T>` and friends; `System.Memory` transitively supplies `System.Runtime.CompilerServices.Unsafe`).
  - **netstandard2.1**: `System.Runtime.CompilerServices.Unsafe` only (everything else is in-box).

## Using NVorbis with NAudio

If you use [NAudio](https://github.com/naudio/NAudio), the [NAudio.Vorbis](https://github.com/naudio/Vorbis) package provides a ready-made `WaveStream` wrapper around NVorbis.

## Building from source

```
git clone https://github.com/NVorbis/NVorbis.git
cd NVorbis
dotnet build -c Release
dotnet test
```

The library builds for both target frameworks; the test suite runs against the `.ogg` fixtures in the test project.

## License

NVorbis is released under the [MIT License](LICENSE). Copyright © Andrew Ward.

## Contributing & support

Issues and feature requests are welcome in the [issue tracker](https://github.com/NVorbis/NVorbis/issues). When reporting a decode problem, please attach a sample `.ogg` file that reproduces it — most decode bugs are specific to how a particular encoder laid out the stream.

## Acknowledgements

- The [Xiph.Org Foundation](https://xiph.org/) for Vorbis and its specification.
- Sean Barrett's public-domain [stb_vorbis](https://github.com/nothings/stb), the basis for the MDCT and Huffman codeword generation.
