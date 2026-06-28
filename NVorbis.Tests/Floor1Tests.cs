using System;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    public class Floor1Tests
    {
        static readonly Type _floor1Type = typeof(VorbisReader).Assembly.GetType("NVorbis.Floor1")!;
        static readonly Type _dataType = _floor1Type.GetNestedType("Data", BindingFlags.NonPublic)!;

        static readonly MethodInfo _renderPoint =
            _floor1Type.GetMethod("RenderPoint", BindingFlags.NonPublic | BindingFlags.Instance)!;
        static readonly MethodInfo _unwrapPosts =
            _floor1Type.GetMethod("UnwrapPosts", BindingFlags.NonPublic | BindingFlags.Instance)!;

        static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

        static int RenderPoint(int x0, int y0, int x1, int y1, int x)
        {
            var floor = Activator.CreateInstance(_floor1Type, nonPublic: true)!;
            return (int)_renderPoint.Invoke(floor, new object[] { x0, y0, x1, y1, x })!;
        }

        // ── RenderPoint: integer line interpolation ──────────────────────────

        [Fact]
        public void RenderPoint_Midpoint_RisingLine()
        {
            Assert.Equal(16, RenderPoint(0, 0, 16, 32, 8));
        }

        [Fact]
        public void RenderPoint_QuarterPoint_RisingLine()
        {
            Assert.Equal(8, RenderPoint(0, 0, 16, 32, 4));
        }

        [Fact]
        public void RenderPoint_FallingLine()
        {
            Assert.Equal(16, RenderPoint(0, 32, 16, 0, 8));
        }

        [Fact]
        public void RenderPoint_FlatLine_ReturnsConstant()
        {
            Assert.Equal(10, RenderPoint(0, 10, 16, 10, 8));
        }

        [Fact]
        public void RenderPoint_AtStart_ReturnsY0()
        {
            Assert.Equal(5, RenderPoint(0, 5, 16, 100, 0));
        }

        // ── UnwrapPosts: dequantization of floor posts ───────────────────────

        // build a 3-post floor where post 2 sits at x=8 between posts 0 (x=0) and 1 (x=16)
        static object MakeFloor(int range)
        {
            var floor = Activator.CreateInstance(_floor1Type, nonPublic: true)!;
            SetField(floor, "_range", range);
            SetField(floor, "_xList", new[] { 0, 16, 8 });
            SetField(floor, "_lNeigh", new[] { 0, 0, 0 });
            SetField(floor, "_hNeigh", new[] { 0, 0, 1 });
            return floor;
        }

        static object MakeData(params int[] posts)
        {
            var data = Activator.CreateInstance(_dataType, nonPublic: true)!;
            var arr = (int[])_dataType.GetField("Posts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(data)!;
            Array.Copy(posts, arr, posts.Length);
            _dataType.GetField("PostCount", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(data, posts.Length);
            return data;
        }

        static int[] Posts(object data) =>
            (int[])_dataType.GetField("Posts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(data)!;

        static bool[] UnwrapPosts(object floor, object data) =>
            (bool[])_unwrapPosts.Invoke(floor, new object[] { data })!;

        [Fact]
        public void UnwrapPosts_ZeroPost_KeepsPredictedValue()
        {
            // posts 0 and 1 both 100 → predicted at x=8 is 100; a 0 delta leaves it there
            var floor = MakeFloor(256);
            var data = MakeData(100, 100, 0);
            var flags = UnwrapPosts(floor, data);

            Assert.Equal(100, Posts(data)[2]);
            Assert.False(flags[2]); // no step at a zero post
        }

        [Fact]
        public void UnwrapPosts_EvenDelta_AddsHalf()
        {
            var floor = MakeFloor(256);
            var data = MakeData(100, 100, 4); // predicted 100, even delta 4 → +2
            UnwrapPosts(floor, data);
            Assert.Equal(102, Posts(data)[2]);
        }

        [Fact]
        public void UnwrapPosts_OddDelta_SubtractsHalfRoundedUp()
        {
            var floor = MakeFloor(256);
            var data = MakeData(100, 100, 3); // predicted 100, odd delta 3 → -2
            UnwrapPosts(floor, data);
            Assert.Equal(98, Posts(data)[2]);
        }

        [Fact]
        public void UnwrapPosts_NonZeroPost_SetsStepFlag()
        {
            var floor = MakeFloor(256);
            var data = MakeData(100, 100, 4);
            var flags = UnwrapPosts(floor, data);
            Assert.True(flags[2]);
        }

        [Fact]
        public void UnwrapPosts_EndpointsUnchanged()
        {
            var floor = MakeFloor(256);
            var data = MakeData(40, 70, 4);
            UnwrapPosts(floor, data);
            Assert.Equal(40, Posts(data)[0]);
            Assert.Equal(70, Posts(data)[1]);
        }
    }
}
