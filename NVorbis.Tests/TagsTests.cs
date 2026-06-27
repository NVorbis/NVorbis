using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace NVorbis.Tests
{
    public class TagsTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        // Tags.Value must be non-null after successful stream open.
        [Fact]
        public void Tags_AfterOpen_IsNotNull()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.NotNull(reader.Tags);
        }

        // Lazy<T> must return the same instance on every access (no re-creation).
        [Fact]
        public void Tags_AccessedMultipleTimes_ReturnsSameInstance()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var t1 = reader.Tags;
            var t2 = reader.Tags;
            Assert.Same(t1, t2);
        }

        // Concurrent reads must all get the same instance — Lazy<T> with the
        // default ExecutionAndPublication mode guarantees exactly one construction.
        // The old "?? (_tags = new TagData(...))" pattern was not thread-safe:
        // two threads could both observe _tags == null and create separate objects.
        [Fact]
        public void Tags_ConcurrentAccess_AllReturnSameInstance()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));

            const int threadCount = 16;
            var results = new NVorbis.Contracts.ITagData[threadCount];
            var barrier = new Barrier(threadCount);

            var threads = Enumerable.Range(0, threadCount).Select(i => new Thread(() =>
            {
                barrier.SignalAndWait(); // race all threads to the first Tags access
                results[i] = reader.Tags;
            })).ToArray();

            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            for (int i = 1; i < threadCount; i++)
                Assert.Same(results[0], results[i]);
        }

        // EncoderVendor must be a non-null string (may be empty for files with no vendor tag).
        [Fact]
        public void Tags_EncoderVendor_IsNotNull()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.NotNull(reader.Tags.EncoderVendor);
        }

        // All returns a non-null dictionary (may be empty for files with no comment tags).
        [Fact]
        public void Tags_All_IsNotNull()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.NotNull(reader.Tags.All);
        }
    }
}
