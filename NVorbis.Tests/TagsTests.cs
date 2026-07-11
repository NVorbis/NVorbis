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

        // ── API correctness (key case-insensitivity, GetTagSingle/Multi) ─────

        [Fact]
        public void Tags_GetTagSingle_MissingKey_ReturnsEmpty()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.Equal(string.Empty, reader.Tags.GetTagSingle("NONEXISTENT_KEY_XYZ"));
        }

        [Fact]
        public void Tags_GetTagMulti_MissingKey_ReturnsEmptyList()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.Empty(reader.Tags.GetTagMulti("NONEXISTENT_KEY_XYZ"));
        }

        [Fact]
        public void Tags_GetTagSingle_CaseInsensitive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            // Both forms must agree regardless of case
            var upper = reader.Tags.GetTagSingle("TITLE");
            var lower = reader.Tags.GetTagSingle("title");
            Assert.Equal(upper, lower);
        }

        [Fact]
        public void Tags_StandardProperties_NotNull()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var tags = reader.Tags;
            // All standard properties must return non-null (empty string / empty list when absent)
            Assert.NotNull(tags.Title);
            Assert.NotNull(tags.Album);
            Assert.NotNull(tags.Artist);
            Assert.NotNull(tags.TrackNumber);
            Assert.NotNull(tags.Copyright);
            Assert.NotNull(tags.License);
            Assert.NotNull(tags.Organization);
            Assert.NotNull(tags.Description);
            Assert.NotNull(tags.Contact);
            Assert.NotNull(tags.Isrc);
            Assert.NotNull(tags.Version);
            Assert.NotNull(tags.Performers);
            Assert.NotNull(tags.Genres);
            Assert.NotNull(tags.Dates);
            Assert.NotNull(tags.Locations);
        }

        // ── Comment-parsing internals (constructed directly, no fixture needed) ─

        [Fact]
        public void TagData_CommentWithoutEquals_TreatsWholeCommentAsKeyWithEmptyValue()
        {
            var tags = new TagData("vendor", new[] { "NOEQUALSIGN" });
            Assert.Equal(string.Empty, tags.GetTagSingle("NOEQUALSIGN"));
            Assert.Equal(new[] { string.Empty }, tags.GetTagMulti("NOEQUALSIGN"));
        }

        [Fact]
        public void TagData_DuplicateKey_GetTagMulti_ReturnsAllValuesInOrder()
        {
            var tags = new TagData("vendor", new[] { "GENRE=Rock", "GENRE=Jazz", "GENRE=Blues" });
            Assert.Equal(new[] { "Rock", "Jazz", "Blues" }, tags.GetTagMulti("GENRE"));
        }

        [Fact]
        public void TagData_DuplicateKey_GetTagSingle_ReturnsLastValue()
        {
            var tags = new TagData("vendor", new[] { "GENRE=Rock", "GENRE=Jazz" });
            Assert.Equal("Jazz", tags.GetTagSingle("GENRE"));
        }

        [Fact]
        public void TagData_DuplicateKey_GetTagSingleConcatenate_JoinsWithNewline()
        {
            var tags = new TagData("vendor", new[] { "GENRE=Rock", "GENRE=Jazz" });
            Assert.Equal("Rock" + Environment.NewLine + "Jazz", tags.GetTagSingle("GENRE", concatenate: true));
        }

        [Fact]
        public void TagData_BracketSyntax_PrefixesValueWithUppercasedBracketContent()
        {
            // "PERFORMER[vocals]=Alice" -> key "PERFORMER", value "VOCALS: Alice"
            var tags = new TagData("vendor", new[] { "PERFORMER[vocals]=Alice" });
            Assert.Equal("VOCALS: Alice", tags.GetTagSingle("PERFORMER"));
        }

        [Fact]
        public void TagData_EmptyCommentsArray_AllIsEmpty()
        {
            var tags = new TagData("vendor", Array.Empty<string>());
            Assert.Empty(tags.All);
        }

        [Fact]
        public void TagData_All_ExposesRawDictionary()
        {
            var tags = new TagData("vendor", new[] { "TITLE=Song", "ARTIST=Band" });
            Assert.Equal(2, tags.All.Count);
            Assert.Equal("Song", tags.All["TITLE"][0]);
            Assert.Equal("Band", tags.All["ARTIST"][0]);
        }
    }
}
