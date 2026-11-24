# CHAR column read back after INSERT: empty value for one row, ORA-50029 for two rows (Oracle.EntityFrameworkCore 10.23.26400)

## Versions

- Oracle.EntityFrameworkCore 10.23.26400 (assembly version 10.0.23.1), Microsoft.EntityFrameworkCore.Relational 10.0.0,
  Oracle.ManagedDataAccess.Core 23.26.400
- .NET 10.0.15
- Oracle Database 23.26.3.0.0

Regression: the same program works with Oracle.EntityFrameworkCore 8.23.90 (assembly version 8.0.23.1),
Microsoft.EntityFrameworkCore.Relational 8.0.11, Oracle.ManagedDataAccess.Core 23.9.0, .NET 8.0.34, on the same database.

## Description

A `CHAR(1)` column has a database default and is configured as `dotnet ef dbcontext scaffold` generates it
(`HasMaxLength(1)`, `IsUnicode(false)`, `IsFixedLength()`, `HasDefaultValueSql`, `ValueGeneratedOnAdd`).
The application does not set the property, so EF Core reads the value back with `RETURNING` after the insert.

1. **One row**: the insert succeeds and the table has the default `'N'`, but the entity gets an empty string `''`
   (not null). The output parameter is `OracleDbType.Char` with `Size = 0`.
2. **Two rows in the same `SaveChanges`**: the command is not sent; ODP.NET throws
   `ORA-50029: OracleParameter.ArrayBindSize is invalid`. The provider uses `FORALL ... RETURNING ... BULK COLLECT`
   with output PL/SQL associative arrays: the `OracleDbType.Char` output parameter has `ArrayBindSize = null`,
   while the `OracleDbType.Varchar2` one has `ArrayBindSize = [4000,4000]`.

The same `CHAR(1)` column mapped without `IsFixedLength()` gets an `OracleDbType.Varchar2` output parameter with a size,
and works in both cases. Both failures look like the same cause: the output parameter for a fixed-length string gets
no size. The 8.23.90 provider reads the generated values with a `REF CURSOR` per row instead, and works.

Possibly related, not verified: the [10.23.26400 release notes](https://www.nuget.org/packages/Oracle.EntityFrameworkCore/10.23.26400)
list "IMPROVE PERFORMANCE OF SAVECHANGES()" (bugs 38663906, 33050516, 39563807).

## Repro

Repro repository: https://github.com/mcauzzi/oracle-efcore-char-returning-repro (`CharRepro.cs`, a single file, no project file). It needs:

- the .NET 10 SDK, plus the .NET 8 runtime for `-f net8.0`;
- a database user that can create and drop tables: the program creates `CHAR_REPRO` and
  `CHAR_REPRO_NOT_FIXED_LENGTH` and runs each case in a transaction that is rolled back.

```
dotnet run CharRepro.cs -f net10.0 -- "User Id=...;Password=...;Data Source=..."
dotnet run CharRepro.cs -f net8.0  -- "User Id=...;Password=...;Data Source=..."
```

`-f net10.0` uses Oracle.EntityFrameworkCore 10.23.26400, `-f net8.0` uses 8.23.90.

Both columns of `CHAR_REPRO` are `CHAR(1) DEFAULT 'N'`: `FLAG_FIXED_LENGTH` is mapped with `IsFixedLength()`,
`FLAG_NOT_FIXED_LENGTH` without it. `CHAR_REPRO_NOT_FIXED_LENGTH` shows that two rows work when no column is mapped
with `IsFixedLength()` (case 2 cannot show it, because the whole command fails). For each column the program prints the
output parameter EF Core hands to ODP.NET (recorded by a `DbCommandInterceptor` before the command runs), the value
read back into the entity, the value in the table, and `OK` or `WRONG`.

Output with `-f net10.0`:

```
Oracle Database 23.26.3.0.0, .NET 10.0.15, Oracle.EntityFrameworkCore assembly 10.0.23.1-20260923, Microsoft.EntityFrameworkCore.Relational 10.0.0, Oracle.ManagedDataAccess.Core 23.26.400
Every column is CHAR(1) DEFAULT 'N' and the program does not set it: EF Core must read back 'N'.
FLAG_FIXED_LENGTH is mapped with IsFixedLength(), as scaffolded; FLAG_NOT_FIXED_LENGTH is mapped without it.

1) 1 row into CHAR_REPRO
   FLAG_FIXED_LENGTH      output Char, Size=0                                  read back ''       (table 'N')   WRONG
   FLAG_NOT_FIXED_LENGTH  output Varchar2, Size=1                              read back 'N'      (table 'N')   OK

2) 2 rows into CHAR_REPRO
   FLAG_FIXED_LENGTH      output Char, array, ArrayBindSize=null
   FLAG_NOT_FIXED_LENGTH  output Varchar2, array, ArrayBindSize=[4000,4000]
   SaveChanges failed: ORA-50029: OracleParameter.ArrayBindSize is invalid   WRONG

3) 2 rows into CHAR_REPRO_NOT_FIXED_LENGTH
   FLAG_NOT_FIXED_LENGTH  output Varchar2, array, ArrayBindSize=[4000,4000]    read back 'N','N'  (table 'N','N')   OK
```

With `-f net8.0` every line is `OK`; the generated values are read with one `REF CURSOR` per row.

With `-f net10.0` and `#:package Oracle.ManagedDataAccess.Core@23.26.401` added after the other `#:package` line,
the output is the same.

Stack trace of case 2 (shortened):

```
System.InvalidOperationException: ORA-50029: OracleParameter.ArrayBindSize is invalid
   at Oracle.ManagedDataAccess.Client.OracleParameter.SetArrayContext(Int32 arrayBindCount)
   at Oracle.ManagedDataAccess.Client.OracleParameter.PreBind(...)
   ...
   at Oracle.ManagedDataAccess.Client.OracleCommand.ExecuteNonQueryInternalAsync(Boolean bAsync, CancellationToken cancellationToken)
   at Oracle.EntityFrameworkCore.Update.Internal.OracleModificationCommandBatch.ExecuteNonQueryWithLoggingAsync(IRelationalConnection connection, DbCommand dbCommand, Guid commandId, CancellationToken cancellationToken)
```

With `REPRO_LOG=1` the program also prints the SQL of each command. The command of case 2:

```sql
DECLARE
  TYPE t_o0_0 IS TABLE OF CHAR(1) INDEX BY PLS_INTEGER;
  TYPE t_o0_1 IS TABLE OF VARCHAR2(1) INDEX BY PLS_INTEGER;
  tmp_o0_0 t_o0_0;
  tmp_o0_1 t_o0_1;
  v_ptr_0 PLS_INTEGER := 1;
BEGIN
  FORALL i IN 1 .. :n0_cnt
  INSERT INTO "CHAR_REPRO"
  ("ID")
  VALUES (:p0_0(i))
  RETURNING "FLAG_FIXED_LENGTH", "FLAG_NOT_FIXED_LENGTH"
  BULK COLLECT INTO tmp_o0_0, tmp_o0_1;
  FOR i IN 1 .. :n0_cnt LOOP
  IF SQL%BULK_ROWCOUNT(i) > 0 THEN
  :o0_0(i) := tmp_o0_0(v_ptr_0);
  :o0_1(i) := tmp_o0_1(v_ptr_0);
  v_ptr_0 := v_ptr_0 + 1;
  ELSE
  :o0_0(i) := NULL;
  :o0_1(i) := NULL;
  END IF;
  END LOOP;
END;
```

## Expected

The default value of the `CHAR` column is read back (`'N'`) for one row and for two rows, as it is when the column is
mapped without `IsFixedLength()`.

## Workaround

- Map the `CHAR(1)` columns without `IsFixedLength()`. Safe only for `CHAR(1)` (or columns whose values always fill
  the declared length): the parameters become `VARCHAR2`, and Oracle compares `CHAR` with `VARCHAR2` without blank
  padding, so a `CHAR(n)` value shorter than `n` no longer matches in a `WHERE`.
- For the case with two or more rows only: a `DbCommandInterceptor` that sets `ArrayBindSize` on output
  `OracleDbType.Char` associative-array parameters that have none.

