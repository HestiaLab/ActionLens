namespace ActionLens.Core.Diagnostics;

/// <summary>
/// How much attention a finding deserves. Shared by parser/validator diagnostics and lint rules so
/// that the UI has one severity vocabulary.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Worth knowing; the workflow is fine as written.</summary>
    Info,

    /// <summary>Likely a mistake or a fragility, but the workflow still does what it says.</summary>
    Warning,

    /// <summary>GitHub will reject the workflow, or the analysis cannot be trusted.</summary>
    Error,

    /// <summary>The workflow can be manipulated into doing something its author did not intend.</summary>
    Critical,
}
