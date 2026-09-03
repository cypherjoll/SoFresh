using SoFresh.Core.Domain;

namespace SoFresh.Core.Safety;

public interface IFileSafetyPolicy
{
    SafetyAssessment Evaluate(string path, FileAttributes? attributes = null);

    SafetyAssessment Evaluate(FileEntry entry);
}
