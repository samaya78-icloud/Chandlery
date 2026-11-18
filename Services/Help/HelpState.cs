namespace Chandlery.Services;

/// <summary>Whether the help panel is open and whether the assistant is available for this person on this page. Shared by the top-bar button and the drawer within one browser session.</summary>
public class HelpState
{
    private bool _open, _available;

    public event Action? Changed;

    public bool Open => _open;

    public bool Available
    {
        get => _available;
        set { if (_available == value) return; _available = value; Changed?.Invoke(); }
    }

    public void Toggle() { _open = !_open; Changed?.Invoke(); }

    public void Close() { if (!_open) return; _open = false; Changed?.Invoke(); }
}
