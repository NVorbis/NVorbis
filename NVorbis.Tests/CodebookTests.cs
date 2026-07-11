using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    public class CodebookTests
    {
        // Minimal DataPacket backed by a fixed byte array (bits packed LSB-first).
        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;
            public ByteArrayPacket(byte[] data) => _data = data;
            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;
        }

        // FastRange is a private nested class inside Codebook — access via reflection.
        private static readonly Type _fastRangeType =
            typeof(Codebook).GetNestedType("FastRange", BindingFlags.NonPublic)!;

        private static readonly MethodInfo _getMethod =
            _fastRangeType.GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly PropertyInfo _indexer =
            _fastRangeType.GetProperty("Item")!;

        private static object MakeRange(int start, int count) =>
            _getMethod.Invoke(null, new object[] { start, count })!;

        private static int GetItem(object range, int index) =>
            (int)_indexer.GetValue(range, new object[] { index })!;

        [Fact]
        public void Indexer_ValidLastIndex_ReturnsCorrectValue()
        {
            var range = MakeRange(start: 5, count: 10);
            Assert.Equal(14, GetItem(range, 9)); // start + (count-1) = 5+9
        }

        [Fact]
        public void Indexer_IndexEqualToCount_ThrowsArgumentOutOfRangeException()
        {
            // index == count is one-past-end: must throw, not return start+count
            var range = MakeRange(start: 5, count: 10);

            var ex = Assert.Throws<TargetInvocationException>(() => GetItem(range, 10));
            Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
        }

        [Fact]
        public void Indexer_IndexBeyondCount_ThrowsArgumentOutOfRangeException()
        {
            var range = MakeRange(start: 0, count: 5);

            var ex = Assert.Throws<TargetInvocationException>(() => GetItem(range, 6));
            Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
        }

        // FastRange is private, but it implements the public IReadOnlyList<int> (and thus
        // IEnumerable<int>/IEnumerable), so a boxed instance can be used through those
        // interfaces directly without further reflection.

        [Fact]
        public void Count_ReturnsConfiguredCount()
        {
            var range = (IReadOnlyList<int>)MakeRange(start: 3, count: 7);
            Assert.Equal(7, range.Count);
        }

        [Fact]
        public void GenericGetEnumerator_ThrowsNotSupportedException()
        {
            var range = (IEnumerable<int>)MakeRange(start: 0, count: 3);
            Assert.Throws<NotSupportedException>(() => range.GetEnumerator());
        }

        [Fact]
        public void NonGenericGetEnumerator_DelegatesAndThrowsNotSupportedException()
        {
            var range = (IEnumerable)MakeRange(start: 0, count: 3);
            Assert.Throws<NotSupportedException>(() => range.GetEnumerator());
        }

        [Fact]
        public void Get_ReusesThreadStaticInstance_AndMutatesInPlace()
        {
            // Get() caches one instance per thread and rewrites its fields on each call --
            // callers must not hold a FastRange across a later Get() call and expect stable
            // values. This is the behavior Codebook.Init relies on (use-immediately, don't cache).
            var first = MakeRange(start: 1, count: 3);
            var second = MakeRange(start: 10, count: 3);

            Assert.Same(first, second);
            Assert.Equal(10, GetItem(first, 0)); // `first` now reflects the second Get() call
        }

        // Codebook.Init validation: Dimensions == 0 must be rejected at load time.
        // A zero-Dimensions codebook causes an infinite loop in Residue1.WriteVectors
        // because the inner loop never runs and the outer counter never advances.

        // Header byte layout (LSB-first, as Vorbis packs bits):
        //   bytes 0-2: sync word 0x564342 → { 0x42, 0x43, 0x56 }
        //   bytes 3-4: Dimensions (16-bit) → { 0x00, 0x00 } for 0, { 0x01, 0x00 } for 1

        [Fact]
        public void Init_DimensionsZero_ThrowsInvalidDataException()
        {
            var packet = new ByteArrayPacket(new byte[] { 0x42, 0x43, 0x56, 0x00, 0x00 });
            var codebook = new Codebook();

            var ex = Assert.Throws<InvalidDataException>(() => codebook.Init(packet, null));
            Assert.Contains("dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Init_DimensionsNonZero_DoesNotThrowDimensionsError()
        {
            // Dimensions=1 passes the new check; packet truncates after that so Init
            // will throw later (bad codebook data), but not with a dimensions message.
            var packet = new ByteArrayPacket(new byte[] { 0x42, 0x43, 0x56, 0x01, 0x00 });
            var codebook = new Codebook();

            var ex = Record.Exception(() => codebook.Init(packet, null));
            Assert.False(
                ex is InvalidDataException ide &&
                ide.Message.Contains("dimension", StringComparison.OrdinalIgnoreCase),
                "Should not throw a dimensions error for Dimensions=1");
        }
    }
}
