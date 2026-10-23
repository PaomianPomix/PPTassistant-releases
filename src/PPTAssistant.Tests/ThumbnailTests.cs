using PPTAssistant.Session;

namespace PPTAssistant.Tests;

public class ThumbnailPlannerTests
{
    [Fact]
    public void PlanCoversCurrentPagePlusMinusRadius()
    {
        var plan = ThumbnailPlanner.Plan(currentIndex: 20, slideCount: 80, radius: 3);

        Assert.Equal([17, 18, 19, 20, 21, 22, 23], plan);
    }

    [Fact]
    public void PlanIsClampedAtBothEnds()
    {
        Assert.Equal([1, 2, 3], ThumbnailPlanner.Plan(currentIndex: 1, slideCount: 80, radius: 2));
        Assert.Equal([78, 79, 80], ThumbnailPlanner.Plan(currentIndex: 80, slideCount: 80, radius: 2));
    }

    [Fact]
    public void PlanLengthNeverExceedsWindow()
    {
        // 关键性质：无论课件多少页、翻到哪一页，计划长度都不会超过 2r+1 —— 导出代价恒定。
        foreach (var count in new[] { 1, 2, 7, 40, 300 })
        {
            for (var current = 1; current <= count; current++)
            {
                var plan = ThumbnailPlanner.Plan(current, count, radius: 6);
                Assert.InRange(plan.Count, 1, 13);
                Assert.Contains(current, plan);
                Assert.Equal(plan.OrderBy(static x => x), plan);
                Assert.All(plan, index => Assert.InRange(index, 1, count));
            }
        }
    }

    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(-5, 10, 1)]
    [InlineData(99, 10, 10)]
    public void PlanClampsOutOfRangeCurrentPage(int current, int count, int expectedAnchor)
    {
        var plan = ThumbnailPlanner.Plan(current, count, radius: 1);

        // 页码不可信时退化为「夹到合法范围」（0/负数 → 第 1 页；超出 → 最后一页），而不是崩掉。
        Assert.All(plan, index => Assert.InRange(index, 1, count));
        Assert.Contains(expectedAnchor, plan);
    }

    [Fact]
    public void PlanIsEmptyWithoutSlides()
        => Assert.Empty(ThumbnailPlanner.Plan(1, 0, 3));

    [Fact]
    public void ZeroRadiusPlansOnlyTheCurrentPage()
        => Assert.Equal([5], ThumbnailPlanner.Plan(5, 10, 0));

    [Fact]
    public void MissingFromKeepsOrderAndSkipsExported()
    {
        var plan = ThumbnailPlanner.Plan(10, 40, 2); // 8..12
        var missing = ThumbnailPlanner.MissingFrom(plan, [9, 10, 12]);

        Assert.Equal([8, 11], missing);
    }

    [Fact]
    public void MissingFromReturnsEmptyWhenEverythingIsCached()
        => Assert.Empty(ThumbnailPlanner.MissingFrom([1, 2, 3], [1, 2, 3]));
}

public class ThumbnailServiceSafetyTests
{
    [Fact]
    public void SessionDirectoryLivesUnderTheGivenRoot()
    {
        var root = CreateTempRoot();
        try
        {
            using var service = new ThumbnailService(root);

            var fullRoot = Path.GetFullPath(root);
            var fullSession = Path.GetFullPath(service.SessionDirectory);
            Assert.StartsWith(fullRoot, fullSession, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(fullRoot, fullSession);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void DisposeRemovesItsOwnSessionDirectoryWhenEmpty()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new ThumbnailService(root);

            // 手工造出「本会话目录」（正常运行由 EnsureThumbnail 创建）。
            Directory.CreateDirectory(service.SessionDirectory);
            var sessionDirectory = service.SessionDirectory;

            service.Dispose();

            Assert.False(Directory.Exists(sessionDirectory), "空的会话目录应当被收掉，不留垃圾");
            Assert.True(Directory.Exists(root), "上层根目录必须保留");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void DisposeNeverDeletesForeignFilesOrNonEmptyDirectories()
    {
        var root = CreateTempRoot();
        try
        {
            // 模拟「目录里混进了不属于本会话的东西」——递归删除的实现会把它一起干掉。
            var foreignDirectory = Path.Combine(root, "user-stuff");
            Directory.CreateDirectory(foreignDirectory);
            var foreignFile = Path.Combine(foreignDirectory, "别删我.txt");
            File.WriteAllText(foreignFile, "用户的文件");

            var service = new ThumbnailService(root);

            // 往「本会话目录」里也放一个不是我们创建的文件：非空 → 目录不该被删。
            Directory.CreateDirectory(service.SessionDirectory);
            var untracked = Path.Combine(service.SessionDirectory, "untracked.png");
            File.WriteAllText(untracked, "x");

            service.Dispose();

            Assert.True(File.Exists(foreignFile), "缩略图清理绝不能碰不属于自己的文件");
            Assert.True(Directory.Exists(foreignDirectory));
            Assert.True(File.Exists(untracked), "只删自己记录过的文件；没记录过的一律不动");
            Assert.True(Directory.Exists(service.SessionDirectory), "非空目录不删（绝不递归删除）");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void EnsureThumbnailRequiresAHost()
    {
        var root = CreateTempRoot();
        try
        {
            using var service = new ThumbnailService(root);

            // 契约：宿主为空是调用方的编程错误（而不是「安静地什么都不做」）。
            Assert.Throws<ArgumentNullException>(() => service.EnsureThumbnail(null!, 1, 320, 180));

            // 失败路径不应当在磁盘上留下任何东西。
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>强杀留下的残骸要被收掉（否则教室里攒垃圾）。</summary>
    [Fact]
    public void StaleSessionDirectoriesAreCleanedUp()
    {
        var root = CreateTempRoot();
        try
        {
            var stale = Path.Combine(root, "session-1234-20260101-101010");
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, "slide-001.png"), "x");
            File.WriteAllText(Path.Combine(stale, "slide-012.png"), "x");

            var removed = ThumbnailService.CleanupStaleSessions(root);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>目录名不像我们的 / 内容不像缩略图的，一律不碰。</summary>
    [Fact]
    public void CleanupOnlyTouchesItsOwnArtifacts()
    {
        var root = CreateTempRoot();
        try
        {
            // ① 别人的目录（名字不匹配）→ 完全不动。
            var foreign = Path.Combine(root, "user-notes");
            Directory.CreateDirectory(foreign);
            var foreignFile = Path.Combine(foreign, "别删我.txt");
            File.WriteAllText(foreignFile, "x");

            // ② 像我们的目录，但混进了非缩略图文件 → 只删缩略图，目录与外来文件保留。
            var mixed = Path.Combine(root, "session-99-20260101-101010");
            Directory.CreateDirectory(mixed);
            File.WriteAllText(Path.Combine(mixed, "slide-001.png"), "x");
            File.WriteAllText(Path.Combine(mixed, "重要.txt"), "x");

            // ③ 像我们的目录，但有个子目录 → 不递归，保留了子目录就整个目录不删。
            var withSub = Path.Combine(root, "session-98-20260101-101010");
            Directory.CreateDirectory(withSub);
            Directory.CreateDirectory(Path.Combine(withSub, "nested"));

            // ④ 名字形状对但内容不是缩略图（slide-abc.png）→ 不删。
            var odd = Path.Combine(root, "session-97-20260101-101010");
            Directory.CreateDirectory(odd);
            var oddFile = Path.Combine(odd, "slide-abc.png");
            File.WriteAllText(oddFile, "x");

            var removed = ThumbnailService.CleanupStaleSessions(root);

            Assert.Equal(0, removed);
            Assert.True(File.Exists(foreignFile));
            Assert.True(File.Exists(Path.Combine(mixed, "重要.txt")), "非缩略图文件绝不能删");
            Assert.False(File.Exists(Path.Combine(mixed, "slide-001.png")), "缩略图文件应当被收掉");
            Assert.True(Directory.Exists(Path.Combine(withSub, "nested")), "不递归删子目录");
            Assert.True(File.Exists(oddFile), "文件名形状不符的一律不动");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ConstructingAServiceCleansPreviousRunsLeftovers()
    {
        var root = CreateTempRoot();
        try
        {
            var stale = Path.Combine(root, "session-1-20260101-000000");
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, "slide-003.png"), "x");

            // 构造即清理（模拟下一次启动时的行为）。
            using var service = new ThumbnailService(root);

            Assert.False(Directory.Exists(stale));
            Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(service.SessionDirectory), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pptassistant-thumbs-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
