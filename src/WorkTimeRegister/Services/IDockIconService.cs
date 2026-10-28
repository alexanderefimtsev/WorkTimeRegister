namespace WorkTimeRegister.Services;

public interface IDockIconService : IDisposable
{
    void Update(DockIconPresentation? presentation);
}

internal sealed class NoOpDockIconService : IDockIconService
{
    public void Update(DockIconPresentation? presentation)
    {
    }

    public void Dispose()
    {
    }
}
