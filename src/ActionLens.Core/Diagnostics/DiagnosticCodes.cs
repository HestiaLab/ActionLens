namespace ActionLens.Core.Diagnostics;

/// <summary>
/// Stable identifiers for every finding ActionLens can produce. Codes are part of the observable
/// output (JSON export, UI problem list), so they are never reused or renumbered.
/// </summary>
public static class DiagnosticCodes
{
    // --- Yaml stage: the text could not be read as one workflow document. ---
    public const string YamlSyntax = "YAML001";
    public const string YamlMultipleDocuments = "YAML002";
    public const string YamlDuplicateKey = "YAML003";
    public const string YamlAliasLimit = "YAML004";
    public const string YamlDepthLimit = "YAML005";
    public const string YamlSizeLimit = "YAML006";
    public const string YamlRootNotMapping = "YAML007";
    public const string YamlUnsupportedConstruct = "YAML008";
    public const string YamlExpansionLimit = "YAML009";

    // --- Schema stage: legal YAML, illegal workflow. ---
    public const string MissingRequired = "VAL001";
    public const string WrongType = "VAL002";
    public const string UnknownField = "VAL003";
    public const string InvalidIdentifier = "VAL004";
    public const string InvalidValue = "VAL005";

    // --- Trigger filtering, reported at the Schema stage: the on: section's own rules, positioned. ---
    public const string TriggerFilterConflict = "TRIG001";
    public const string TriggerFilterUnsupported = "TRIG002";
    public const string TriggerPatternInvalid = "TRIG003";
    public const string TriggerEventMismatch = "TRIG004";

    // --- Expression stage: an ${{ }} could not be parsed. ---
    public const string ExpressionSyntax = "EXPR001";
    public const string ExpressionTooComplex = "EXPR002";
    public const string UnknownFunction = "EXPR003";
    public const string FunctionArity = "EXPR004";
    public const string ContextNotAvailable = "EXPR005";

    // --- Matrix stage. ---
    public const string MatrixCapReached = "MTX001";
    public const string MatrixDynamic = "MTX002";
    public const string MatrixInvalidShape = "MTX003";
    public const string MatrixValueInvalid = "MTX004";
    public const string MatrixSchedulingNotModelled = "MTX005";

    // --- Graph stage. ---
    public const string NeedsUnknownJob = "GRPH001";
    public const string NeedsCycle = "GRPH002";
    public const string NoJobs = "GRPH003";

    // --- Scenario stage: what the run would have to tell us, and what the scenario cannot say. ---
    public const string ScenarioUnknownJob = "SCEN001";
    public const string ScenarioValueUnresolved = "SCEN002";
    public const string ScenarioConflict = "SCEN003";

    // --- Internal stage: ActionLens failed, not the workflow. ---
    public const string AnalysisFailed = "INT001";
}
