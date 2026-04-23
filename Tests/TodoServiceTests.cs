using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Framework;

namespace Tests;

[TestClass(Category = "ProjectForTest")]
[UseSharedContext(typeof(TestDbContext))]
public sealed class TodoServiceTests
{
    private readonly TestDbContext _shared;
    private TodoService _svc = null!;

    public TodoServiceTests(TestDbContext shared) => _shared = shared;

    [SetUp]
    public void SetUp()
    {
        _svc = new TodoService();
        _shared.AddLog("SetUp per test");
    }

    [TearDown]
    public void TearDown()
    {
        _shared.AddLog("TearDown per test");
    }

    [Test]
    [Timeout(50)]
    public void Add_TrimsTitle_AndDoneFalse()
    {
        var item = _svc.Add("  hello  ");
        Assert.IsNotNull(item);
        Assert.AreEqual("hello", item.Title);
        Assert.IsFalse(item.Done);
    }

    [Test(Category = "Add", Author = "Antonia")]
    [Timeout(50)]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Add_ThrowsOnNullOrWhitespace(string? title)
    {
        Assert.Throws<ArgumentException>(() => _svc.Add(title!));
    }

    [Test(Category = "Remove", Author = "Antonia")]
    [Timeout(50)]
    [Priority(1)]
    public void Remove_ReturnsTrueWhenRemoved_FalseWhenMissing()
    {
        var item = _svc.Add("a");
        Assert.IsTrue(_svc.Remove(item.Id));
        Assert.IsFalse(_svc.Remove(item.Id));
        Assert.IsFalse(_svc.Remove(Guid.NewGuid()));
    }

    [Test(Category = "Id", Author = "Antonia")]
    [Timeout(50)]
    [Priority(2)]
    public void GetById_ReturnsExisting()
    {
        var item = _svc.Add("a");
        var got = _svc.GetById(item.Id);
        Assert.AreEqual(item.Id, got.Id);
        Assert.AreEqual("a", got.Title);
    }

    [Test(Category = "Id", Author = "Antonia")]
    [Timeout(50)]
    [Priority(3)]
    public void GetById_ThrowsIfMissing()
    {
        Assert.Throws<InvalidOperationException>(() => _svc.GetById(Guid.NewGuid()));
    }

    [Test]
    [Timeout(50)]
    public void GetAll_ReturnsSnapshot()
    {
        _svc.Add("a");
        _svc.Add("b");

        var all = _svc.GetAll();
        Assert.AreEqual(2, all.Count);
        Assert.SequenceEqual(all.Select(x => x.Title).OrderBy(x => x), new[] { "a", "b" });
    }

    [Test(Category = "Update", Author = "Tonya")]
    [Timeout(50)]
    public void UpdateTitle_UpdatesAndTrims()
    {
        var item = _svc.Add("a");
        var updated = _svc.UpdateTitle(item.Id, "  new  ");
        Assert.AreEqual(item.Id, updated.Id);
        Assert.AreEqual("new", updated.Title);
    }

    [Test(Category = "Update", Author = "Antonia")]
    [Timeout(50)]
    public void UpdateTitle_ThrowsOnMissing()
    {
        Assert.Throws<InvalidOperationException>(() => _svc.UpdateTitle(Guid.NewGuid(), "x"));
    }

    [Test(Category = "Update", Author = "Tonya")]
    [Timeout(50)]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void UpdateTitle_ThrowsOnInvalidTitle(string? newTitle)
    {
        var item = _svc.Add("a");
        Assert.Throws<ArgumentException>(() => _svc.UpdateTitle(item.Id, newTitle!));
    }

    [Test]
    [Ignore("Just test")]
    public void MarkDone_SetsDone_AndIsIdempotent()
    {
        var item = _svc.Add("a");

        var d1 = _svc.MarkDone(item.Id);
        Assert.IsTrue(d1.Done);

        var d2 = _svc.MarkDone(item.Id);
        Assert.IsTrue(d2.Done);
    }

    [Test(Category = "Mark", Author = "Tonya")]
    [Timeout(50)]
    public void MarkDone_ThrowsIfMissing()
    {
        Assert.Throws<InvalidOperationException>(() => _svc.MarkDone(Guid.NewGuid()));
    }

    [Test]
    [Ignore("Just test")]
    public void ToggleDone_Toggles()
    {
        var item = _svc.Add("a");

        var t1 = _svc.ToggleDone(item.Id);
        Assert.IsTrue(t1.Done);

        var t2 = _svc.ToggleDone(item.Id);
        Assert.IsFalse(t2.Done);
    }

    [Test(Category = "Search", Author = "Tonya")]
    [Timeout(50)]
    [TestCase("hello", "he", 1)]
    [TestCase("hello", "x", 0)]
    [TestCase("Hello", "he", 1)]
    [TestCase("hello world", "WORLD", 1)]
    public void Search_FindsExpected(string title, string query, int expectedCount)
    {
        _svc.Add(title);

        var found = _svc.Search(query);
        Assert.AreEqual(expectedCount, found.Count);
    }

    [Test(Category = "Search", Author = "Tonya")]
    [Timeout(200)]
    public void Search_EmptyQuery_ReturnsAll()
    {
        _svc.Add("a");
        _svc.Add("b");

        var all = _svc.Search("   ");
        Assert.AreEqual(2, all.Count);
    }

    [Test(Category = "Remove", Author = "Antonia")]
    [Timeout(200)]
    [Priority(10)]
    public void ClearCompleted_RemovesDone_ReturnsRemovedCount()
    {
        var a = _svc.Add("a");
        var b = _svc.Add("b");
        var c = _svc.Add("c");

        _svc.MarkDone(a.Id);
        _svc.MarkDone(c.Id);

        var removed = _svc.ClearCompleted();
        Assert.AreEqual(2, removed);

        var all = _svc.GetAll();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(b.Id, all[0].Id);
    }

    [Test(Category = "Assert", Author = "Antonina")]
    [Timeout(200)]
    public async Task CountDoneAsync_Works()
    {
        var a = _svc.Add("a");
        _svc.Add("b");
        _svc.MarkDone(a.Id);

        var done = await _svc.CountDoneAsync();
        Assert.AreEqual(1, done);
        Assert.Greater(done, 0);
        Assert.InRange(done, 0, 2);
        
        Assert.That(() => done == 1 && done >= 0 && done <= 2);
    }
    
    [Test(Category = "Assert", Author = "Antonina")]
    public void AssertThat_ShowsExpressionTreeDetails_WhenExpressionFails()
    {
        var done = 1;
        var total = 3;

        Assert.That(() => done == 2 && total < 2);
    }

    [Test(Category = "SharedContext", Author = "Antonina")]
    [Timeout(200)]
    public void SharedContext_WasUsed()
    {
        Assert.Contains(_shared.Logs, "SharedContext: SetUp");
        Assert.DoesNotContain(_shared.Logs, "some missing log");
    }

    [Test(Category = "Time", Author = "Antonina")]
    [Timeout(200)]
    public async Task FastTest()
    {
        await Task.Delay(100);
    }
    
    [Test(Category = "Time", Author = "Antonina")]
    [Timeout(3000)]
    [Priority(20)]
    public async Task SlowAsyncTest_800ms()
    {
        _svc.Add("slow-800");
        await Task.Delay(800);

        var all = _svc.GetAll();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("slow-800", all[0].Title);
    }

    [Test(Category = "Time", Author = "Antonina")]
    [Timeout(4000)]
    [Priority(21)]
    public async Task SlowAsyncTest_1200ms()
    {
        var item = _svc.Add("slow-1200");
        await Task.Delay(1200);

        var got = _svc.GetById(item.Id);
        Assert.AreEqual(item.Id, got.Id);
        Assert.AreEqual("slow-1200", got.Title);
    }

    [Test(Category = "Time", Author = "Antonina")]
    [Timeout(4000)]
    [Priority(22)]
    public async Task SlowAsyncTest_1500ms()
    {
        var item = _svc.Add("slow-1500");
        await Task.Delay(1500);

        var updated = _svc.UpdateTitle(item.Id, "slow-1500-updated");
        Assert.AreEqual("slow-1500-updated", updated.Title);
    }

    [Test(Category = "Time", Author = "Antonina")]
    [Timeout(4000)]
    [Priority(23)]
    public void SlowSyncTest_1000ms()
    {
        var item = _svc.Add("slow-sync");
        Thread.Sleep(1000);

        var got = _svc.GetById(item.Id);
        Assert.AreEqual(item.Id, got.Id);
        Assert.AreEqual("slow-sync", got.Title);
    }

    [Test(Category = "Time", Author = "Antonina")]
    [Timeout(5000)]
    [Priority(24)]
    public async Task SlowMixedTest_1400ms()
    {
        var a = _svc.Add("a");
        var b = _svc.Add("b");

        _svc.MarkDone(a.Id);

        await Task.Delay(1400);

        var done = await _svc.CountDoneAsync();
        Assert.AreEqual(1, done);

        var all = _svc.GetAll();
        Assert.AreEqual(2, all.Count);
    }

    [Test(Category = "Add", Author = "Antonina")]
    [Timeout(100)]
    [Priority(5)]
    [TestCaseSource(nameof(Add_ValidTitleCases))]
    public void Add_WithSource_TrimsTitle_AndCreatesNotDoneItem(string title, string expectedTitle)
    {
        var item = _svc.Add(title);

        Assert.AreEqual(expectedTitle, item.Title);
        Assert.IsFalse(item.Done);
    }

    private static IEnumerable<object?[]> Add_ValidTitleCases()
    {
        yield return new object?[] { "task", "task" };
        yield return new object?[] { "  task with spaces  ", "task with spaces" };
        yield return new object?[] { "UPPER", "UPPER" };
    }

    [Test(Category = "Search", Author = "Antonina")]
    [Timeout(100)]
    [Priority(6)]
    [TestCaseSource(nameof(Search_SourceCases))]
    public void Search_WithSource_FindsExpectedItems(
        string[] titles,
        string query,
        int expectedCount)
    {
        foreach (var title in titles)
            _svc.Add(title);

        var found = _svc.Search(query);

        Assert.AreEqual(expectedCount, found.Count);
    }

    private static IEnumerable<object?[]> Search_SourceCases()
    {
        yield return new object?[]
        {
            new[] { "Buy milk", "Read book", "Call mom" },
            "milk",
            1
        };

        yield return new object?[]
        {
            new[] { "Buy milk", "Milk chocolate", "Read book" },
            "MILK",
            2
        };

        yield return new object?[]
        {
            new[] { "Buy milk", "Read book", "Call mom" },
            "unknown",
            0
        };

        yield return new object?[]
        {
            new[] { "Buy milk", "Read book", "Call mom" },
            "   ",
            3
        };
    }

    [Test(Category = "Update", Author = "Antonia")]
    [Timeout(100)]
    [Priority(7)]
    [TestCaseSource(nameof(UpdateTitle_SourceCases))]
    public void UpdateTitle_WithSource_UpdatesAndTrims(
        string originalTitle,
        string newTitle,
        string expectedTitle)
    {
        var item = _svc.Add(originalTitle);

        var updated = _svc.UpdateTitle(item.Id, newTitle);

        Assert.AreEqual(expectedTitle, updated.Title);
    }

    private static IEnumerable<object?[]> UpdateTitle_SourceCases()
    {
        yield return new object?[] { "old", "new", "new" };
        yield return new object?[] { "old", "  new with spaces  ", "new with spaces" };
        yield return new object?[] { "first", "SECOND", "SECOND" };
    }
}