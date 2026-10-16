using System.Collections;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace NotificationWorkflowService.Repository;

// SQL errors can arrive while streaming rows, after ExecuteReaderAsync has succeeded.
internal sealed class LoggingDbDataReader(DbDataReader reader, ILogger logger, string operation) : DbDataReader
{
    public override bool Read() => DatabaseDiagnostics.Execute(logger, "connstr", operation + ":Read", reader.Read);
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => DatabaseDiagnostics.ExecuteAsync(logger, "connstr", operation + ":Read", cancellationToken,
            () => reader.ReadAsync(cancellationToken));
    public override bool NextResult() => DatabaseDiagnostics.Execute(logger, "connstr", operation + ":NextResult", reader.NextResult);
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => DatabaseDiagnostics.ExecuteAsync(logger, "connstr", operation + ":NextResult", cancellationToken,
            () => reader.NextResultAsync(cancellationToken));
    public override void Close() => DatabaseDiagnostics.Execute(logger, "connstr", operation + ":CloseReader", () =>
    {
        reader.Close();
        return true;
    });
    public override Task CloseAsync()
        => DatabaseDiagnostics.ExecuteAsync(logger, "connstr", operation + ":CloseReader", CancellationToken.None, reader.CloseAsync);
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DatabaseDiagnostics.Execute(logger, "connstr", operation + ":DisposeReader", () =>
            {
                reader.Dispose();
                return true;
            });
    }
    public override async ValueTask DisposeAsync()
        => await DatabaseDiagnostics.ExecuteAsync(logger, "connstr", operation + ":DisposeReader",
            CancellationToken.None, () => reader.DisposeAsync().AsTask()).ConfigureAwait(false);
    public override object this[int ordinal] => reader[ordinal];
    public override object this[string name] => reader[name];
    public override int Depth => reader.Depth;
    public override int FieldCount => reader.FieldCount;
    public override int VisibleFieldCount => reader.VisibleFieldCount;
    public override bool HasRows => reader.HasRows;
    public override bool IsClosed => reader.IsClosed;
    public override int RecordsAffected => reader.RecordsAffected;
    public override bool GetBoolean(int ordinal) => reader.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => reader.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => reader.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => reader.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => reader.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => reader.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => reader.GetDouble(ordinal);
    public override Type GetFieldType(int ordinal) => reader.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => reader.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => reader.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => reader.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => reader.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => reader.GetInt64(ordinal);
    public override string GetName(int ordinal) => reader.GetName(ordinal);
    public override int GetOrdinal(string name) => reader.GetOrdinal(name);
    public override string GetString(int ordinal) => reader.GetString(ordinal);
    public override object GetValue(int ordinal) => reader.GetValue(ordinal);
    public override int GetValues(object[] values) => reader.GetValues(values);
    public override bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => reader.IsDBNullAsync(ordinal, cancellationToken);
    public override T GetFieldValue<T>(int ordinal) => reader.GetFieldValue<T>(ordinal);
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
        => reader.GetFieldValueAsync<T>(ordinal, cancellationToken);
    public override Stream GetStream(int ordinal) => reader.GetStream(ordinal);
    public override TextReader GetTextReader(int ordinal) => reader.GetTextReader(ordinal);
    public override object GetProviderSpecificValue(int ordinal) => reader.GetProviderSpecificValue(ordinal);
    public override int GetProviderSpecificValues(object[] values) => reader.GetProviderSpecificValues(values);
    public override Type GetProviderSpecificFieldType(int ordinal) => reader.GetProviderSpecificFieldType(ordinal);
    public override DataTable? GetSchemaTable() => reader.GetSchemaTable();
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
}
