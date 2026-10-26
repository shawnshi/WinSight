namespace WinSight.Bridge;

public enum McpBridgeState
{
    Disabled,
    Starting,
    Running,
    Stopping,
    Faulted
}

public sealed class McpBridgeStateMachine
{
    private readonly object gate = new();
    private McpBridgeState state = McpBridgeState.Disabled;

    public McpBridgeState CurrentState
    {
        get
        {
            lock (gate) return state;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (gate) return state == McpBridgeState.Running;
        }
    }

    public void TransitionTo(McpBridgeState next)
    {
        lock (gate)
        {
            if (state == next) return;

            // Valid transitions:
            // Disabled -> Starting
            // Starting -> Running
            // Starting -> Stopping | Faulted
            // Running -> Stopping | Faulted
            // Stopping -> Disabled | Faulted
            // Faulted -> Stopping | Disabled
            bool valid = (state, next) switch
            {
                (McpBridgeState.Disabled, McpBridgeState.Starting) => true,
                (McpBridgeState.Starting, McpBridgeState.Running) => true,
                (McpBridgeState.Starting, McpBridgeState.Stopping) => true,
                (McpBridgeState.Starting, McpBridgeState.Faulted) => true,
                (McpBridgeState.Running, McpBridgeState.Stopping) => true,
                (McpBridgeState.Running, McpBridgeState.Faulted) => true,
                (McpBridgeState.Stopping, McpBridgeState.Disabled) => true,
                (McpBridgeState.Stopping, McpBridgeState.Faulted) => true,
                (McpBridgeState.Faulted, McpBridgeState.Stopping) => true,
                (McpBridgeState.Faulted, McpBridgeState.Disabled) => true,
                _ => false
            };

            if (!valid)
            {
                throw new InvalidOperationException($"Invalid state transition from {state} to {next}.");
            }

            state = next;
        }
    }
}
