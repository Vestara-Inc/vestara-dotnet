using Vestara.Models;

namespace Vestara.Queue;

public sealed class BreadcrumbBuffer
{
    private const int MaxBreadcrumbs = 100;
    private readonly LinkedList<Breadcrumb> _buffer = new();
    private readonly object _lock = new();

    public void Add(Breadcrumb breadcrumb)
    {
        ArgumentNullException.ThrowIfNull(breadcrumb);

        lock (_lock)
        {
            if (_buffer.Count >= MaxBreadcrumbs)
            {
                _buffer.RemoveFirst();
            }

            _buffer.AddLast(breadcrumb);
        }
    }

    public List<Breadcrumb> Snapshot()
    {
        lock (_lock)
        {
            return new List<Breadcrumb>(_buffer);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Count;
            }
        }
    }
}
