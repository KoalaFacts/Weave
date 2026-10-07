using Microsoft.Data.Sqlite;

namespace Weave.Mailboxes.Sqlite;

/// <summary>Preserved-file SQLite mailbox store; every mutation is a short, serialized transaction.</summary>
public sealed partial class SqliteMailboxStore : IExpiringMailboxStore
{
    private readonly MailboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;

    public SqliteMailboxStore(MailboxOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);
        _options = options;
        _timeProvider = timeProvider;
        var path = Path.GetFullPath(options.DatabasePath);
        if (options.RequireExistingStorage && !File.Exists(path))
            throw new InvalidOperationException("The retained mailbox database is missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5
        }.ToString();
        using var connection = Open(allowCreate: !options.RequireExistingStorage);
        InitializeSchema(connection, options.RequireExistingStorage);
    }

    private SqliteConnection Open(bool allowCreate = false)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (allowCreate) builder.Mode = SqliteOpenMode.ReadWriteCreate;
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            throw;
        }
    }

    private MailboxDatabaseSession Session(bool write, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = Open();
        try { return new(connection, write, ct); }
        catch (SqliteException) { connection.Dispose(); throw; }
    }

    private static void ValidateOptions(MailboxOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DatabasePath)
            || options.DatabasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || options.DatabasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Mailbox storage requires an unambiguous on-disk path.", nameof(options));
        if (options.MaximumMailboxes <= 0 || options.MaximumPendingMessagesPerMailbox <= 0
            || options.MaximumPendingBytes <= 0 || options.MaximumMessageRows <= 0
            || options.MaximumPendingRequestsPerRecipient <= 0 || options.MaximumRequestRows <= 0
            || options.MaximumChannels <= 0 || options.MaximumCards <= 0
            || options.MaximumMessageLifetime <= TimeSpan.Zero || options.MaximumMessageLifetime > TimeSpan.FromHours(24)
            || options.FirstAdmissionWindow <= TimeSpan.Zero || options.FutureClockSkew < TimeSpan.Zero
            || options.TerminalRetention <= options.FirstAdmissionWindow + options.FutureClockSkew)
            throw new ArgumentException("Mailbox bounds and replay-retention windows must be positive and consistent.", nameof(options));
    }

    private bool EnsureOwner(MailboxDatabaseSession db, string owner)
    {
        if (db.Scalar("SELECT count(*) FROM mailbox_owners WHERE mailbox = $owner", ("$owner", owner)) != 0) return true;
        if (db.Scalar("SELECT count(*) FROM mailbox_owners") >= _options.MaximumMailboxes) return false;
        db.Execute("INSERT INTO mailbox_owners(mailbox) VALUES($owner)", ("$owner", owner));
        return true;
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128;
    private static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
}
