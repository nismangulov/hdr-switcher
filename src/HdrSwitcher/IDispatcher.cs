namespace HdrSwitcher;

/// <summary>
/// Abstracts "run this on the UI thread" so business logic stays
/// independent of the UI framework (WinForms, WinUI 3, etc.).
/// </summary>
public interface IDispatcher
{
    void Post(Action action);
}

/// <summary>WinForms implementation — wraps <see cref="SynchronizationContext"/>.</summary>
internal sealed class WinFormsDispatcher : IDispatcher
{
    private readonly SynchronizationContext _ctx;
    public WinFormsDispatcher(SynchronizationContext ctx) => _ctx = ctx;
    public void Post(Action action) => _ctx.Post(_ => action(), null);
}
