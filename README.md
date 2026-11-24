# Oracle.EntityFrameworkCore 10.23.26400: CHAR column read back after INSERT or UPDATE

Repro for an Oracle.EntityFrameworkCore issue: a `CHAR(1)` column with a database default, mapped with
`IsFixedLength()` as `dotnet ef dbcontext scaffold` generates it, is read back as `''` after inserting one row, and
inserting two rows in the same `SaveChanges` fails with `ORA-50029: OracleParameter.ArrayBindSize is invalid`.
A computed `CHAR(1)` column behaves the same after `INSERT` and after `UPDATE`. It works with
Oracle.EntityFrameworkCore 8.23.90. Details in [ISSUE.md](ISSUE.md).

## Run

The program is `CharRepro.cs`, a single file with no project file. It needs:

- the .NET 10 SDK, plus the .NET 8 runtime for `-f net8.0`;
- a database user that can create and drop tables: the program creates `CHAR_REPRO`,
  `CHAR_REPRO_NOT_FIXED_LENGTH` and `CHAR_REPRO_COMPUTED` and runs each case in a transaction that is rolled back.

```
dotnet run CharRepro.cs -f net10.0 -- "User Id=...;Password=...;Data Source=..."
dotnet run CharRepro.cs -f net8.0  -- "User Id=...;Password=...;Data Source=..."
```

`-f net10.0` uses Oracle.EntityFrameworkCore 10.23.26400, `-f net8.0` uses 8.23.90.

Both columns of `CHAR_REPRO` are `CHAR(1) DEFAULT 'N'`: `FLAG_FIXED_LENGTH` is mapped with `IsFixedLength()`,
`FLAG_NOT_FIXED_LENGTH` without it. `CHAR_REPRO_NOT_FIXED_LENGTH` shows that two rows work when no column is mapped
with `IsFixedLength()` (case 2 cannot show it, because the whole command fails). Cases 4 to 7 use
`CHAR_REPRO_COMPUTED`, whose `FLAG_COMPUTED CHAR(1)` is the first letter of `CODE`; for the `UPDATE` cases the rows are
inserted with plain SQL first. For each column the program prints the
output parameter EF Core hands to ODP.NET (recorded by a `DbCommandInterceptor` before the command runs), the value
read back into the entity, the value in the table, and `OK` or `WRONG`.

Output with `-f net10.0`:

```
D:\ArtifactLavoro\OracleEfCharArrayBind\CharRepro.cs(72,32): warning EF1002: Method 'ExecuteSqlRawAsync' inserts interpolated strings directly into the SQL, without any protection against SQL injection. Consider using 'ExecuteSqlAsync' instead, which protects against SQL injection, or make sure that the value is sanitized and suppress the warning.
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

FLAG_COMPUTED is a virtual CHAR(1) column, the first letter of CODE, mapped with IsFixedLength() and HasComputedColumnSql().

4) INSERT 1 row into CHAR_REPRO_COMPUTED
   FLAG_COMPUTED          output Char, Size=0                                  read back ''       (expected 'A')   WRONG

5) INSERT 2 rows into CHAR_REPRO_COMPUTED
   FLAG_COMPUTED          output Char, array, ArrayBindSize=null
   SaveChanges failed: ORA-50029: OracleParameter.ArrayBindSize is invalid   WRONG

6) UPDATE 1 row of CHAR_REPRO_COMPUTED
   FLAG_COMPUTED          output Char, Size=0                                  read back ''       (expected 'B')   WRONG

7) UPDATE 2 rows of CHAR_REPRO_COMPUTED
   FLAG_COMPUTED          output Char, array, ArrayBindSize=null
   SaveChanges failed: ORA-50029: OracleParameter.ArrayBindSize is invalid   WRONG
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
