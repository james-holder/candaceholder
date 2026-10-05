namespace CandaceHolder.Filters
{
    /// <summary>
    /// Marks a controller or action as exempt from <see cref="TrialGateFilter"/>.
    /// Use on pages that must remain reachable even after an org's trial has
    /// expired (auth, billing, help, legal, error pages, etc.) to avoid
    /// redirect loops and to keep informational pages accessible.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class SkipTrialGateAttribute : Attribute
    {
    }
}
