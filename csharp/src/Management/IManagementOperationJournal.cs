namespace Weave.Management;

public interface IManagementOperationJournal
{
    bool TryAdmit(ManagementOperationRecord operation, CancellationToken cancellationToken);
    bool Complete(string id, ManagementOperationOutcome outcome, DateTimeOffset completedAt);
    ManagementOperationRecord? Find(string id, CancellationToken cancellationToken);
}
