using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BooklyHub.IntegrationTests.Infrastructure;

public class QueryCountInterceptor : DbCommandInterceptor
{
    private int _queryCount;
    private readonly List<string> _executedCommands = [];
    private readonly object _lock = new();

    public int QueryCount
    {
        get
        {
            lock (_lock)
            {
                return _queryCount;
            }
        }
    }

    public IReadOnlyList<string> ExecutedCommands
    {
        get
        {
            lock (_lock)
            {
                return _executedCommands.ToList();
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _queryCount = 0;
            _executedCommands.Clear();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        RecordCommand(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        RecordCommand(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        RecordCommand(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        RecordCommand(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void RecordCommand(DbCommand command)
    {
        lock (_lock)
        {
            _queryCount++;
            _executedCommands.Add(command.CommandText);
        }
    }
}
