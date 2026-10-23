using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class StrokeInputFilterTests
{
    // ---------------------------------------------------------------- 守恒

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var result = StrokeInputFilter.Binomial3(Array.Empty<DipPoint>());
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ShortInput_ReturnsAsIs(int count)
    {
        var points = Enumerable.Range(0, count)
            .Select(i => new DipPoint(100 + (i * 5.3), 700 + (i * 0.7)))
            .ToList();

        var result = StrokeInputFilter.Binomial3(points);

        Assert.Equal(count, result.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(points[i].X, result[i].X, 9);
            Assert.Equal(points[i].Y, result[i].Y, 9);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(100)]
    [InlineData(2048)]
    public void Output_LengthPreserved_AndEndpointsKept(int count)
    {
        // 带抖动的伪随机输入（固定种子，确定性）。
        var seed = 20260926;
        var points = new List<DipPoint>(count);
        for (var i = 0; i < count; i++)
        {
            seed = (seed * 48271) % int.MaxValue;
            var jitter = ((seed % 1000) / 1000.0 - 0.5) * 28.5;
            points.Add(new DipPoint(300 + (i * 5.3), 700 + jitter));
        }

        var result = StrokeInputFilter.Binomial3(points);

        Assert.Equal(count, result.Count);
        Assert.Equal(points[0].X, result[0].X, 9);
        Assert.Equal(points[0].Y, result[0].Y, 9);
        Assert.Equal(points[^1].X, result[^1].X, 9);
        Assert.Equal(points[^1].Y, result[^1].Y, 9);
    }

    // ---------------------------------------------------------------- 数学性质

    /// <summary>核心判据①：交替抖动被精确消除（输出恒为真实中心，误差 = 浮点舍入）。</summary>
    [Fact]
    public void AlternatingJitter_EliminatedExactly()
    {

        const int count = 260;
        var points = new List<DipPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var jitter = (i % 2 == 0 ? 1.0 : -1.0) * 14.25;
            points.Add(new DipPoint(300 + (i * 5.3), 700 + jitter));
        }

        var result = StrokeInputFilter.Binomial3(points);

        for (var i = 1; i < count - 1; i++)
        {
            Assert.Equal(700, result[i].Y, 9);
        }
    }

    /// <summary>核心判据②a：常数输入逐点不变。</summary>
    [Fact]
    public void ConstantInput_Unchanged()
    {
        var points = Enumerable.Repeat(new DipPoint(123.4, 567.8), 17).ToList();

        var result = StrokeInputFilter.Binomial3(points);

        for (var i = 0; i < points.Count; i++)
        {
            Assert.Equal(points[i].X, result[i].X, 9);
            Assert.Equal(points[i].Y, result[i].Y, 9);
        }
    }

    /// <summary>核心判据②b：线性输入逐点不变（匀速直线完全不透过损耗——零相位）。</summary>
    [Fact]
    public void LinearInput_UnchangedExactly()
    {
        var points = new List<DipPoint>();
        for (var i = 0; i < 50; i++)
        {
            points.Add(new DipPoint(100 + (i * 5.3), 700 + (i * -0.7)));
        }

        var result = StrokeInputFilter.Binomial3(points);

        for (var i = 0; i < points.Count; i++)
        {
            Assert.Equal(points[i].X, result[i].X, 9);
            Assert.Equal(points[i].Y, result[i].Y, 9);
        }
    }

    /// <summary>核心判据③：共线输入仍共线（变速度写的直线不会被滤弯——凸组合保持在凸包内）。</summary>
    [Fact]
    public void CollinearInput_StaysCollinear()
    {
        // 采样间距不均匀（模拟变速度的直线笔画）。
        var spacings = new[] { 1.1, 5.3, 0.8, 12.7, 3.3, 5.3, 0.9, 20.0, 4.4, 2.2 };
        var points = new List<DipPoint> { new(400, 500) };
        foreach (var spacing in spacings)
        {
            // 沿 45° 方向前进。
            var last = points[^1];
            points.Add(new DipPoint(last.X + spacing, last.Y + spacing));
        }

        var result = StrokeInputFilter.Binomial3(points);

        foreach (var point in result)
        {
            // 共线判定：到首末点连线的距离为零（45° 线上 x − y 恒等于起点差）。
            Assert.Equal(points[0].X - points[0].Y, point.X - point.Y, 6);
        }
    }

    /// <summary>核心判据④：尖角点的偏移符合凸组合公式（确定性、无额外损伤）。</summary>
    [Fact]
    public void CornerPoint_DeviationMatchesConvexCombination()
    {
        // 直角折角：(0,0) → (10,0) → (10,10)。折角点是被「拉向邻点中点」的
        // 二分之一：filtered = (prev + 2·cur + next) / 4 = (7.5, 2.5)。
        var points = new List<DipPoint> { new(0, 0), new(10, 0), new(10, 10) };

        var result = StrokeInputFilter.Binomial3(points);

        Assert.Equal(7.5, result[1].X, 9);
        Assert.Equal(2.5, result[1].Y, 9);
    }

    /// <summary>尖角偏移量 ≤ 邻点中点距离的一半（一般界的抽查：折角不会被过度拉直）。</summary>
    [Theory]
    [InlineData(0, 0, 10, 0, 10, 10)]
    [InlineData(0, 0, 20, 0, 0, 20)]
    [InlineData(-15, 3, 0, 0, 15, -3)]
    public void CornerPoint_PullBoundedByHalfMidpointDistance(
        double ax, double ay, double bx, double by, double cx, double cy)
    {
        var points = new List<DipPoint> { new(ax, ay), new(bx, by), new(cx, cy) };

        var result = StrokeInputFilter.Binomial3(points);

        var midX = (ax + cx) / 2;
        var midY = (ay + cy) / 2;
        var midpointDistance = Math.Sqrt(((bx - midX) * (bx - midX)) + ((by - midY) * (by - midY)));
        var deviation = Math.Sqrt(
            ((result[1].X - bx) * (result[1].X - bx)) + ((result[1].Y - by) * (result[1].Y - by)));

        Assert.True(deviation <= (midpointDistance / 2) + 1e-9,
            $"折角偏移 {deviation:F3} 超过邻点中点距离的一半 {midpointDistance / 2:F3}");
    }

    [Fact]
    public void AlternatingJitter_AfterFilter_DenseInteriorStaysInTightBand()
    {
        const int count = 100;
        var raw = new List<DipPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var jitter = (i % 2 == 0 ? 1.0 : -1.0) * 14.25;
            raw.Add(new DipPoint(300 + (i * 5.3), 700 + jitter));
        }

        var filtered = StrokeInputFilter.Binomial3(raw);
        var dense = StrokeSmoothing.Flatten(filtered);

        var interior = dense.Where(p => p.X > 320 && p.X < 800).ToList();
        Assert.NotEmpty(interior);
        foreach (var point in interior)
        {
            Assert.True(Math.Abs(point.Y - 700) < 0.5,
                $"中段点 ({point.X:F1}, {point.Y:F1}) 偏离真实中心线超过 0.5px");
        }
    }

    /// <summary>对照：同样的交替抖动<b>不滤</b>时，dense 中段锯齿明显（峰峰量级）。</summary>
    [Fact]
    public void AlternatingJitter_WithoutFilter_DenseInteriorZigzags()
    {
        const int count = 100;
        var raw = new List<DipPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var jitter = (i % 2 == 0 ? 1.0 : -1.0) * 14.25;
            raw.Add(new DipPoint(300 + (i * 5.3), 700 + jitter));
        }

        var dense = StrokeSmoothing.Flatten(raw);

        Assert.True(dense.Count > 2, $"不滤时不应走直线快路径（实际 {dense.Count} 点）");
        var interior = dense.Where(p => p.X > 320 && p.X < 800).ToList();
        Assert.NotEmpty(interior);
        Assert.True(interior.Max(p => Math.Abs(p.Y - 700)) > 10,
            "不滤时中段应保留峰峰量级（>10px）的锯齿");
    }

    /// <summary>组合判据：交替抖动叠加缓慢正弦漂移，滤后残留远小于原始峰峰值。</summary>
    /// <remarks>
    /// 漂移透过（核对称零相位）、交替分量被消除——只有随机噪声会残留，
    /// 而它被核能量压缩到 ~0.61 倍。断言留足裕量：滤后峰峰 &lt; 原始的 1/4。
    /// </remarks>
    [Fact]
    public void AlternatingPlusDrift_ResidualFarBelowRaw()
    {
        const int count = 260;
        var raw = new List<DipPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var drift = 22 * Math.Sin((2 * Math.PI * 1.7 * i / count) + 0.3);
            var jitter = (i % 2 == 0 ? 1.0 : -1.0) * 14.25;
            raw.Add(new DipPoint(300 + (i * 5.3), 700 + drift + jitter));
        }

        double RawPeakToPeak()
        {
            double max = double.MinValue, min = double.MaxValue;
            foreach (var point in raw)
            {
                max = Math.Max(max, point.Y);
                min = Math.Min(min, point.Y);
            }

            return max - min;
        }

        double FilteredPeakToPeakAroundDrift()
        {
            // 残留 = 滤波输出 − 同样的漂移信号（漂移应透过）。
            // 只统计中段：首末点按边界语义保留原值（起笔/收笔位置不漂），
            // 它们携带的 ±14.25 抖动是设计取舍，不算「残留」。
            double max = double.MinValue, min = double.MaxValue;
            var filtered = StrokeInputFilter.Binomial3(raw);
            for (var i = 1; i < count - 1; i++)
            {
                var drift = 22 * Math.Sin((2 * Math.PI * 1.7 * i / count) + 0.3);
                var residual = filtered[i].Y - (700 + drift);
                max = Math.Max(max, residual);
                min = Math.Min(min, residual);
            }

            return max - min;
        }

        Assert.True(FilteredPeakToPeakAroundDrift() < RawPeakToPeak() / 4,
            $"滤后残留 {FilteredPeakToPeakAroundDrift():F2}px 应远小于原始峰峰 {RawPeakToPeak():F2}px");
    }
}
