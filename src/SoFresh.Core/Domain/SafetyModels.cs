namespace SoFresh.Core.Domain;

public enum SafetyLevel
{
    SafeToClean,
    ProbablySafe,
    ReviewRequired,
    Protected
}

public sealed record SafetyAssessment(
    string Path,
    SafetyLevel Level,
    string Reason,
    string RecommendedAction,
    bool DirectOperationAllowed);
