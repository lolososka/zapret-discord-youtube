namespace ZapretGui.Core;

public sealed partial class AppState
{
    private ConnectionAssistant? _assistant;
    public ConnectionAssistant Assistant => _assistant ??= new ConnectionAssistant(this);

    internal bool CanRunAssistant => CanStartBypassOperation && !IsBusy && !IsApplyingStrategy
                                     && !Tester.IsRunning && !IsProbing;
    internal bool IsAssistantShuttingDown => _isShuttingDown;
    internal CancellationToken AssistantShutdownToken => _shutdownCancellation.Token;
    internal Task RunAssistantOperationAsync(Func<CancellationToken, Task> operation)
        => RunBypassOperationAsync(operation);

    internal async Task<bool> PrepareAssistantAsync(CancellationToken ct)
    {
        _bypass.RefreshState();
        return await EnsureManualStartAllowedAsync(ct);
    }

    internal async Task<bool> ApplyAssistantStrategyAsync(Strategy strategy, GameFilterMode mode,
                                                         CancellationToken ct)
    {
        var previousSelected = SelectedStrategy;
        var previousMode = GameFilter;
        SelectedStrategy = strategy;
        SetGameFilter(mode, notifyRunning: false);
        try
        {
            await ApplySelectedStrategyAsync(ct);
            return IsRunning && SameStrategy(strategy, _bypass.ActiveStrategy)
                             && mode == _bypass.ActiveGameFilterMode;
        }
        finally
        {
            if (!(IsRunning && SameStrategy(strategy, _bypass.ActiveStrategy) && mode == _bypass.ActiveGameFilterMode))
            {
                SelectedStrategy = IsRunning ? _bypass.ActiveStrategy ?? previousSelected : previousSelected;
                SetGameFilter(IsRunning && _bypass.ActiveStrategy is not null
                    ? _bypass.ActiveGameFilterMode : previousMode, notifyRunning: false);
            }
        }
    }
}
