using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BooklyHub.IntegrationTests.Infrastructure;

public class QueryCountInterceptor : DbCommandInterceptor
{
    private int _queryCount;
    private readonly List<string> _executedCommands = [];
    private readonly object _lock = new();

    private string? _armSignature;
    private Func<IReadOnlyCollection<Guid>, Task>? _armAction;
    private bool _armRepeats;
    private DbCommand? _armedCommand;
    private Guid[] _armedIdentifiers = [];
    private int _armFired;

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

    /// <summary>
    /// Runs <paramref name="action"/> the moment the reader for a query whose text contains
    /// <paramref name="signature"/> closes: once, or on every matching read while <paramref name="repeat"/>
    /// holds. This is how a test lands a second writer inside a code path's own read-then-write window
    /// instead of racing it with Task.WhenAll, which proves nothing about interleaving.
    /// <see cref="ArmHits"/> tells the test whether the window was ever reached, so a query that changes
    /// shape fails loudly instead of silently testing nothing. The action is handed every identifier the
    /// matched query was parameterised with, so a race can be pinned to the rows that read belongs to
    /// instead of disturbing every tenant the sweep visits in the same tick.
    /// </summary>
    public void ArmAfterRead(string signature, Func<IReadOnlyCollection<Guid>, Task> action, bool repeat = false)
    {
        lock (_lock)
        {
            _armSignature = signature;
            _armAction = action;
            _armRepeats = repeat;
            _armedCommand = null;
            _armFired = 0;
        }
    }

    public void DisarmAfterRead()
    {
        lock (_lock)
        {
            _armAction = null;
            _armedCommand = null;
        }
    }

    public int ArmHits
    {
        get
        {
            lock (_lock)
            {
                return _armFired;
            }
        }
    }

    private Func<Task>? TakeArmedAction(DbCommand command)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_armedCommand, command))
            {
                return null;
            }

            var action = _armAction;
            var identifiers = _armedIdentifiers;
            _armedCommand = null;
            _armedIdentifiers = [];
            if (!_armRepeats)
            {
                _armAction = null;
            }

            _armFired++;
            return action == null ? null : () => action(identifiers);
        }
    }

    private void MarkArmedCommand(DbCommand command)
    {
        lock (_lock)
        {
            if (_armAction != null && _armedCommand == null && command.CommandText.Contains(_armSignature!))
            {
                _armedCommand = command;
                _armedIdentifiers = ReadGuidParameters(command);
            }
        }
    }

    /// <summary>
    /// Every identifier the matched query carries. Read here rather than at reader close, because the
    /// provider clears the parameter collection once the reader is done, and which of them scopes the query
    /// is the test's business, not this hook's.
    /// </summary>
    private static Guid[] ReadGuidParameters(DbCommand command)
    {
        var values = new List<Guid>();
        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.Value is Guid value)
            {
                values.Add(value);
            }
        }

        return values.ToArray();
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
        MarkArmedCommand(command);
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

    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result)
    {
        var armed = TakeArmedAction(command);
        var closeResult = await base.DataReaderClosingAsync(command, eventData, result);
        if (armed != null)
        {
            await armed();
        }

        return closeResult;
    }
}
