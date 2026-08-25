namespace PSMS.App.Services;

public sealed class ContextMenuItem
{
    public required string Text { get; init; }
    public Func<Task>? Action { get; init; }
    public bool IsDivider { get; init; }
}

public sealed class ContextMenuService
{
    public event Action? Changed;

    public bool IsOpen { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public IReadOnlyList<ContextMenuItem> Items { get; private set; } = [];

    public void Open(double clientX, double clientY, IEnumerable<ContextMenuItem> items)
    {
        X = clientX;
        Y = clientY;
        Items = items.ToList();
        IsOpen = true;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Items = [];
        Changed?.Invoke();
    }
}
