using System.Collections;
using System.Collections.ObjectModel;
using System.Data.Common;

namespace TriasDev.Tabular.WriteComparison.Writers;

/// <summary>
/// A scenario's dataset as a forward-only <see cref="DbDataReader"/>: the form Sylvan's <c>CsvDataWriter</c> and
/// MiniExcel's <c>SaveAs</c> take their rows in. Typed getters read the row's cells without boxing; only
/// <see cref="GetValue"/> boxes.
/// </summary>
internal sealed class DatasetReader : DbDataReader, IDbColumnSchemaGenerator
{
    private readonly IDataset _dataset;
    private readonly DataColumn[] _columns;
    private readonly Datum[] _cells;
    private readonly long _rows;
    private long _next;

    public DatasetReader(Scenario scenario)
    {
        _dataset = scenario.Dataset;
        _columns = _dataset.Columns;
        _cells = new Datum[_columns.Length];
        _rows = scenario.Rows;
    }

    /// <summary>The columns' names and types, which Sylvan reads once before it writes.</summary>
    public ReadOnlyCollection<DbColumn> GetColumnSchema() =>
        new([.. _columns.Select((c, i) => (DbColumn)new SchemaColumn(c.Header, i, GetFieldType(i)))]);

    public override int FieldCount => _columns.Length;

    public override bool HasRows => _rows > 0;

    public override bool IsClosed => false;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_next >= _rows)
        {
            return false;
        }

        _dataset.Fill(_next++, _cells);
        return true;
    }

    public override bool NextResult() => false;

    public override string GetName(int ordinal) => _columns[ordinal].Header;

    public override int GetOrdinal(string name) => Array.FindIndex(_columns, c => c.Header == name);

    public override Type GetFieldType(int ordinal) => _columns[ordinal].Kind switch
    {
        ValueKind.Text => typeof(string),
        ValueKind.Long => typeof(long),
        ValueKind.Double => typeof(double),
        ValueKind.Decimal => typeof(decimal),
        ValueKind.Date or ValueKind.DateTime => typeof(DateTime),
        ValueKind.Boolean => typeof(bool),
        _ => typeof(object),
    };

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override bool IsDBNull(int ordinal) => _cells[ordinal].Kind == ValueKind.Empty;

    public override string GetString(int ordinal) => _cells[ordinal].Text!;

    public override long GetInt64(int ordinal) => _cells[ordinal].Long;

    public override int GetInt32(int ordinal) => (int)_cells[ordinal].Long;

    public override short GetInt16(int ordinal) => (short)_cells[ordinal].Long;

    public override byte GetByte(int ordinal) => (byte)_cells[ordinal].Long;

    public override double GetDouble(int ordinal) => _cells[ordinal].Double;

    public override float GetFloat(int ordinal) => (float)_cells[ordinal].Double;

    public override decimal GetDecimal(int ordinal) => _cells[ordinal].Decimal;

    public override DateTime GetDateTime(int ordinal) => _cells[ordinal].Date;

    public override bool GetBoolean(int ordinal) => _cells[ordinal].Boolean;

    public override char GetChar(int ordinal) => throw new NotSupportedException();

    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override object GetValue(int ordinal)
    {
        ref readonly Datum cell = ref _cells[ordinal];

        return cell.Kind switch
        {
            ValueKind.Text => cell.Text!,
            ValueKind.Long => cell.Long,
            ValueKind.Double => cell.Double,
            ValueKind.Decimal => cell.Decimal,
            ValueKind.Date or ValueKind.DateTime => cell.Date,
            ValueKind.Boolean => cell.Boolean,
            _ => DBNull.Value,
        };
    }

    public override int GetValues(object[] values)
    {
        int count = Math.Min(values.Length, _columns.Length);

        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}

/// <summary>One column of <see cref="DatasetReader"/>'s schema.</summary>
internal sealed class SchemaColumn : DbColumn
{
    public SchemaColumn(string name, int ordinal, Type type)
    {
        ColumnName = name;
        ColumnOrdinal = ordinal;
        DataType = type;
        DataTypeName = type.Name;
        AllowDBNull = true;
    }
}
