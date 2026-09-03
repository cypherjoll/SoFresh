using SoFresh.Core.Domain;

namespace SoFresh.Core.Organization;

public interface IOrganizationPlanner
{
    OrganizationPreview BuildPreview(
        IEnumerable<FileEntry> entries,
        OrganizationPlanRequest request);
}
