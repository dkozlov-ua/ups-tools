namespace UpsTools.Core;

/// <summary>What the built-in actions of an event do to the PC (they need no script).</summary>
public enum PowerAction
{
    Sleep,
    Hibernate,
    ShutDown,
}

public static class PowerActionExtensions
{
    /// <summary>The action's name in the list of an event's scripts, in notifications and in the log.</summary>
    public static string Title(this PowerAction action) => action switch
    {
        PowerAction.Sleep => "Sleep",
        PowerAction.Hibernate => "Hibernate",
        PowerAction.ShutDown => "Shut down",
        _ => action.ToString(),
    };

    /// <summary>What is happening, e.g. "Hibernating the PC".</summary>
    public static string Doing(this PowerAction action) => action switch
    {
        PowerAction.Sleep => "Putting the PC to sleep",
        PowerAction.Hibernate => "Hibernating the PC",
        PowerAction.ShutDown => "Shutting down the PC",
        _ => action.ToString(),
    };

    public static string Description(this PowerAction action) => action switch
    {
        PowerAction.Sleep => "Puts the PC to sleep. The scripts after it run when it wakes up.",
        PowerAction.Hibernate => "Hibernates the PC: it saves everything and turns off. The scripts after it run when it is turned on again.",
        PowerAction.ShutDown => "Shuts down the PC. Apps are closed even if they have unsaved work.",
        _ => "",
    };
}
