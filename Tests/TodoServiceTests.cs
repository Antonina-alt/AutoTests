using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Framework;

namespace Tests;

//Priority:
//ClearCompleted_RemovesDone_ReturnsRemovedCount - 10
//GetById_ThrowsIfMissing - 3
//GetById_ReturnsExisting - 2
//Remove_ReturnsTrueWhenRemoved_FalseWhenMissing - 1

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
        _shared.Logs.Add("SetUp per test");
    }

    [TearDown]
    public void TearDown()
    {
        _shared.Logs.Add("TearDown per test");
    }

    [Test]
    public void Add_TrimsTitle_AndDoneFalse()
    {
        var item = _svc.Add("  hello  ");
        Assert.IsNotNull(item);
        Assert.AreEqual("hello", item.Title);
        Assert.IsFalse(item.Done);
    }

    [Test]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Add_ThrowsOnNullOrWhitespace(string? title)
    {
        Assert.Throws<ArgumentException>(() => _svc.Add(title!));

    }
    
    [Test]
    [Priority(1)]
    public void Remove_ReturnsTrueWhenRemoved_FalseWhenMissing()
    {
        var item = _svc.Add("a");
        Assert.IsTrue(_svc.Remove(item.Id));
        Assert.IsFalse(_svc.Remove(item.Id)); 
        Assert.IsFalse(_svc.Remove(Guid.NewGuid())); 
    }
    
    [Test]
    [Priority(2)]
    public void GetById_ReturnsExisting()
    {
        var item = _svc.Add("a");
        var got = _svc.GetById(item.Id);
        Assert.AreEqual(item.Id, got.Id);
        Assert.AreEqual("a", got.Title);
    }

    [Test]
    [Priority(3)]
    public void GetById_ThrowsIfMissing()
    {
        Assert.Throws<InvalidOperationException>(() => _svc.GetById(Guid.NewGuid()));
    }
    
    [Test]
    public void GetAll_ReturnsSnapshot()
    {
        _svc.Add("a");
        _svc.Add("b");

        var all = _svc.GetAll();
        Assert.AreEqual(2, all.Count);
        Assert.SequenceEqual(all.Select(x => x.Title).OrderBy(x => x), new[] { "a", "b" });
    }
    
    [Test]
    public void UpdateTitle_UpdatesAndTrims()
    {
        var item = _svc.Add("a");
        var updated = _svc.UpdateTitle(item.Id, "  new  ");
        Assert.AreEqual(item.Id, updated.Id);
        Assert.AreEqual("new", updated.Title);
    }

    [Test]
    public void UpdateTitle_ThrowsOnMissing()
    {
        Assert.Throws<InvalidOperationException>(() => _svc.UpdateTitle(Guid.NewGuid(), "x"));
    }

    [Test]
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

    [Test]
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
    
    [Test]
    [TestCase("hello", "he", 1)]
    [TestCase("hello", "x", 0)]
    [TestCase("Hello", "he", 1)] 
    [TestCase("hello world", "WORLD", 1)] 
    [TestCase("hello world", "q", 5)] 
    [TestCase("hello", "hell", 0)] 
    [TestCase("hello", "hell", 10)] 
    public void Search_FindsExpected(string title, string query, int expectedCount)
    {
        _svc.Add(title);

        var found = _svc.Search(query);
        Assert.AreEqual(expectedCount, found.Count);
    }

    [Test]
    public void Search_EmptyQuery_ReturnsAll()
    {
        _svc.Add("a");
        _svc.Add("b");

        var all = _svc.Search("   ");
        Assert.AreEqual(2, all.Count);
    }
    
    [Test]
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
    
    [Test]
    public async Task CountDoneAsync_Works()
    {
        var a = _svc.Add("a");
        _svc.Add("b");
        _svc.MarkDone(a.Id);

        var done = await _svc.CountDoneAsync();
        Assert.AreEqual(1, done);
        Assert.Greater(done, 0);
        Assert.InRange(done, 0, 2);
    }
    
    [Test]
    public void SharedContext_WasUsed()
    {
        Assert.Contains(_shared.Logs, "SharedContext: SetUp");
        Assert.DoesNotContain(_shared.Logs, "some missing log");
    }
}
