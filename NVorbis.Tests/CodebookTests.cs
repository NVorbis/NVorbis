using System;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    public class CodebookTests
    {
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
    }
}
