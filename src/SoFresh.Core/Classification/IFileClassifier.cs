using SoFresh.Core.Domain;

namespace SoFresh.Core.Classification;

public interface IFileClassifier
{
    FileClassification Classify(string fullPath, FileAttributes attributes, bool isDirectory);
}
