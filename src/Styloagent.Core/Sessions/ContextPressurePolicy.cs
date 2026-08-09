namespace Styloagent.Core.Sessions;

public enum ContextPressure { Unknown, Normal, Elevated, High, Critical }

/// <summary>Maps the shared Claude/Codex context budget to a conservative adaptive pressure level.</summary>
public static class ContextPressurePolicy
{
    public static ContextPressure For(double usedFraction)
    {
        if (usedFraction <= 0) return ContextPressure.Unknown;
        if (usedFraction >= 0.90) return ContextPressure.Critical;
        if (usedFraction >= 0.80) return ContextPressure.High;
        if (usedFraction >= 0.65) return ContextPressure.Elevated;
        return ContextPressure.Normal;
    }

    public static string Guidance(ContextPressure pressure) => pressure switch
    {
        ContextPressure.Critical => "Context is nearly full. Switch to reasoning effort 'low' immediately, finish the smallest safe unit, summarize decisions, and checkpoint. Do not spawn new agents or take new scope.",
        ContextPressure.High => "Context is under heavy pressure. Lower your reasoning effort to 'medium' or 'low', keep replies and tool output compact, avoid rereading files, and checkpoint before taking new scope.",
        ContextPressure.Elevated => "Context is filling. Consider lowering reasoning effort to 'medium', prefer concise replies, targeted reads, and small bounded actions; avoid expanding scope.",
        _ => "",
    };
}
