using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core;

public sealed record TodoItem(Guid Id, string Title, bool Done);

public sealed class TodoService
{
    private readonly List<TodoItem> _items = new();
    
    public TodoItem Add(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));

        var item = new TodoItem(Guid.NewGuid(), title.Trim(), false);
        _items.Add(item);
        return item;
    }
    
    public bool Remove(Guid id)
    {
        var idx = _items.FindIndex(x => x.Id == id);
        if (idx < 0) return false;

        _items.RemoveAt(idx);
        return true;
    }
    
    public TodoItem GetById(Guid id)
    {
        var item = _items.FirstOrDefault(x => x.Id == id);
        if (item is null)
            throw new InvalidOperationException("Item not found.");

        return item;
    }
    
    public IReadOnlyList<TodoItem> GetAll() => _items.ToList();
    
    public TodoItem UpdateTitle(Guid id, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle))
            throw new ArgumentException("Title is required.", nameof(newTitle));

        var idx = _items.FindIndex(x => x.Id == id);
        if (idx < 0)
            throw new InvalidOperationException("Item not found.");

        var updated = _items[idx] with { Title = newTitle.Trim() };
        _items[idx] = updated;
        return updated;
    }
    
    public TodoItem MarkDone(Guid id)
    {
        var idx = _items.FindIndex(x => x.Id == id);
        if (idx < 0)
            throw new InvalidOperationException("Item not found.");

        if (_items[idx].Done) return _items[idx];

        var updated = _items[idx] with { Done = true };
        _items[idx] = updated;
        return updated;
    }
    
    public TodoItem ToggleDone(Guid id)
    {
        var idx = _items.FindIndex(x => x.Id == id);
        if (idx < 0)
            throw new InvalidOperationException("Item not found.");

        var updated = _items[idx] with { Done = !_items[idx].Done };
        _items[idx] = updated;
        return updated;
    }
    
    public IReadOnlyList<TodoItem> Search(string? query)
    {
        query ??= "";
        var q = query.Trim();

        if (q.Length == 0) return _items.ToList();

        return _items
            .Where(x => x.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
    }
    
    public int ClearCompleted()
    {
        var before = _items.Count;
        _items.RemoveAll(x => x.Done);
        return before - _items.Count;
    }
    
    public async Task<int> CountDoneAsync(CancellationToken ct = default)
    {
        await Task.Delay(50, ct);
        return _items.Count(x => x.Done);
    }
}
