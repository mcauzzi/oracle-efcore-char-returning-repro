#:property TargetFrameworks=net8.0;net10.0
#:property PublishAot=false
#:property OracleEfVersion=$(TargetFramework.Replace('net10.0','10.23.26400').Replace('net8.0','8.23.90'))
#:package Oracle.EntityFrameworkCore@$(OracleEfVersion)

// Repro: CHAR column read back after INSERT with Oracle.EntityFrameworkCore 10.23.26400 (works with 8.23.90).
// Usage: dotnet run CharRepro.cs -f net10.0 -- "User Id=...;Password=...;Data Source=..."   (-f net8.0 for the 8.23.90 provider)
// The connection string can also be set in ORA_CS; REPRO_LOG=1 also prints the SQL of each command.
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Oracle.ManagedDataAccess.Client;

var cs = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("ORA_CS")
         ?? throw new ArgumentException("Pass the connection string as first argument or in ORA_CS");

await using (var conn = new OracleConnection(cs))
{
    await conn.OpenAsync();
    Console.WriteLine($"Oracle Database {conn.ServerVersion}, .NET {Environment.Version}, " +
                      $"Oracle.EntityFrameworkCore assembly {Version(typeof(OracleDbContextOptionsExtensions))}, " +
                      $"Microsoft.EntityFrameworkCore.Relational {Version(typeof(RelationalDatabaseFacadeExtensions))}, " +
                      $"Oracle.ManagedDataAccess.Core {Version(typeof(OracleConnection)).Split(':')[3]}");
    await using var cmd = conn.CreateCommand();
    foreach (var sql in new[]
             {
                 DropIfExists("CHAR_REPRO"),
                 DropIfExists("CHAR_REPRO_NOT_FIXED_LENGTH"),
                 DropIfExists("CHAR_REPRO_COMPUTED"),
                 "CREATE TABLE CHAR_REPRO (ID NUMBER(10) PRIMARY KEY, FLAG_FIXED_LENGTH CHAR(1) DEFAULT 'N' NOT NULL, FLAG_NOT_FIXED_LENGTH CHAR(1) DEFAULT 'N' NOT NULL)",
                 "CREATE TABLE CHAR_REPRO_NOT_FIXED_LENGTH (ID NUMBER(10) PRIMARY KEY, FLAG_NOT_FIXED_LENGTH CHAR(1) DEFAULT 'N' NOT NULL)",
                 "CREATE TABLE CHAR_REPRO_COMPUTED (ID NUMBER(10) PRIMARY KEY, CODE VARCHAR2(10) NOT NULL, " +
                 "FLAG_COMPUTED CHAR(1) GENERATED ALWAYS AS (CAST(SUBSTR(CODE, 1, 1) AS CHAR(1))) VIRTUAL)",
             })
    {
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
Console.WriteLine("Every column is CHAR(1) DEFAULT 'N' and the program does not set it: EF Core must read back 'N'.");
Console.WriteLine("FLAG_FIXED_LENGTH is mapped with IsFixedLength(), as scaffolded; FLAG_NOT_FIXED_LENGTH is mapped without it.");

await Insert("1) 1 row into CHAR_REPRO", 1, ctx => ctx.Rows, id => new CharRepro { Id = id },
             ("FLAG_FIXED_LENGTH", r => r.FlagFixedLength), ("FLAG_NOT_FIXED_LENGTH", r => r.FlagNotFixedLength));
await Insert("2) 2 rows into CHAR_REPRO", 2, ctx => ctx.Rows, id => new CharRepro { Id = id },
             ("FLAG_FIXED_LENGTH", r => r.FlagFixedLength), ("FLAG_NOT_FIXED_LENGTH", r => r.FlagNotFixedLength));
await Insert("3) 2 rows into CHAR_REPRO_NOT_FIXED_LENGTH", 2, ctx => ctx.NotFixedLengthRows, id => new CharReproNotFixedLength { Id = id },
             ("FLAG_NOT_FIXED_LENGTH", r => r.FlagNotFixedLength));

// Inserts the rows in one SaveChanges, in a transaction that is never committed
Console.WriteLine();
Console.WriteLine("FLAG_COMPUTED is a virtual CHAR(1) column, the first letter of CODE, mapped with IsFixedLength() and HasComputedColumnSql().");
await Computed("4) INSERT 1 row into CHAR_REPRO_COMPUTED", 1, update: false);
await Computed("5) INSERT 2 rows into CHAR_REPRO_COMPUTED", 2, update: false);
await Computed("6) UPDATE 1 row of CHAR_REPRO_COMPUTED", 1, update: true);
await Computed("7) UPDATE 2 rows of CHAR_REPRO_COMPUTED", 2, update: true);

// For the UPDATE cases the rows are inserted with plain SQL, then loaded and changed with EF Core
async Task Computed(string title, int rows, bool update)
{
    Console.WriteLine();
    Console.WriteLine(title);
    await using var ctx = new ReproContext(cs);
    await using var tx = await ctx.Database.BeginTransactionAsync();
    List<CharReproComputed> entities;
    if (update)
    {
        for (var id = 1; id <= rows; id++)
            await ctx.Database.ExecuteSqlRawAsync($"INSERT INTO CHAR_REPRO_COMPUTED (ID, CODE) VALUES ({id}, 'A{id}')");
        entities = await ctx.ComputedRows.OrderBy(r => r.Id).ToListAsync();
        foreach (var e in entities) e.Code = "B" + e.Id;
    }
    else
    {
        entities = Enumerable.Range(1, rows).Select(id => new CharReproComputed { Id = id, Code = "A" + id }).ToList();
        ctx.ComputedRows.AddRange(entities);
    }
    OutputParameters.Last.Clear();
    try
    {
        await ctx.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   FLAG_COMPUTED          {Parameter("FLAG_COMPUTED")}");
        Console.WriteLine($"   SaveChanges failed: {ex.GetBaseException().Message}   WRONG");
        return;
    }
    var readBack = string.Join(",", entities.Select(e => Show(e.FlagComputed)));
    var expected = string.Join(",", entities.Select(e => Show(e.Code![..1])));
    var ok = entities.All(e => e.FlagComputed == e.Code![..1]);
    Console.WriteLine($"   FLAG_COMPUTED          {Parameter("FLAG_COMPUTED"),-52} read back {readBack,-8} (expected {expected})   {(ok ? "OK" : "WRONG")}");
    if (OutputParameters.Cursors > 0) Console.WriteLine($"   no output parameter per column: generated values read with {OutputParameters.Cursors} REF CURSOR(s)");
}

async Task Insert<T>(string title, int rows, Func<ReproContext, DbSet<T>> set, Func<int, T> create,
                     params (string Column, Func<T, string?> Value)[] columns) where T : class
{
    Console.WriteLine();
    Console.WriteLine(title);
    await using var ctx = new ReproContext(cs);
    await using var tx = await ctx.Database.BeginTransactionAsync();
    var entities = Enumerable.Range(1, rows).Select(create).ToList();
    set(ctx).AddRange(entities);
    OutputParameters.Last.Clear();
    try
    {
        await ctx.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        foreach (var (column, _) in columns) Console.WriteLine($"   {column,-22} {Parameter(column)}");
        Console.WriteLine($"   SaveChanges failed: {ex.GetBaseException().Message}   WRONG");
        return;
    }
    var inTable = await set(ctx).AsNoTracking().OrderBy(r => EF.Property<int>(r, "Id")).ToListAsync();
    foreach (var (column, value) in columns)
    {
        var readBack = string.Join(",", entities.Select(e => Show(value(e))));
        var stored = string.Join(",", inTable.Select(e => Show(value(e))));
        var ok = entities.All(e => value(e) == "N");
        Console.WriteLine($"   {column,-22} {Parameter(column),-52} read back {readBack,-8} (table {stored})   {(ok ? "OK" : "WRONG")}");
    }
    if (OutputParameters.Cursors > 0) Console.WriteLine($"   no output parameter per column: generated values read with {OutputParameters.Cursors} REF CURSOR(s)");
}

string Parameter(string column) => OutputParameters.Last.TryGetValue(column, out var p) ? p : "";

static string DropIfExists(string table) =>
    $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {table}'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;";

static string Version(Type t) =>
    t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

static string Show(string? s) => s == null ? "null" : $"'{s}'";

public class CharRepro
{
    public int Id { get; set; }
    public string? FlagFixedLength { get; set; }
    public string? FlagNotFixedLength { get; set; }
}

public class CharReproNotFixedLength
{
    public int Id { get; set; }
    public string? FlagNotFixedLength { get; set; }
}

// Before each INSERT runs, saves type and size of the output parameter that receives each RETURNING column
public class OutputParameters : DbCommandInterceptor
{
    public static readonly Dictionary<string, string> Last = new();
    public static int Cursors;

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    static void Record(DbCommand command)
    {
        if (!command.CommandText.Contains("INSERT") && !command.CommandText.Contains("UPDATE")) return;
        var outputs = command.Parameters.Cast<OracleParameter>()
                             .Where(p => p.Direction != ParameterDirection.Input && !p.ParameterName.Contains("cnt"))   // row counts
                             .ToList();
        Cursors = outputs.Count(p => p.OracleDbType == OracleDbType.RefCursor);
        var returning = Regex.Match(command.CommandText, @"RETURNING\s+(.+?)\s+(INTO|BULK)", RegexOptions.Singleline);
        if (!returning.Success || Cursors > 0) return;
        var columns = returning.Groups[1].Value.Split(',').Select(c => c.Trim().Trim('"')).ToList();
        for (var i = 0; i < columns.Count && i < outputs.Count; i++)
        {
            var p = outputs[i];
            Last[columns[i]] = p.CollectionType == OracleCollectionType.PLSQLAssociativeArray
                ? $"output {p.OracleDbType}, array, ArrayBindSize={(p.ArrayBindSize == null ? "null" : $"[{string.Join(",", p.ArrayBindSize)}]")}"
                : $"output {p.OracleDbType}, Size={p.Size}";
        }
    }
}

public class CharReproComputed
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public string? FlagComputed { get; set; }
}

public class ReproContext(string cs) : DbContext
{
    public DbSet<CharRepro> Rows => Set<CharRepro>();
    public DbSet<CharReproNotFixedLength> NotFixedLengthRows => Set<CharReproNotFixedLength>();
    public DbSet<CharReproComputed> ComputedRows => Set<CharReproComputed>();

    protected override void OnConfiguring(DbContextOptionsBuilder o) =>
        o.UseOracle(cs)
         .AddInterceptors(new OutputParameters())
         .LogTo(s => { if (Environment.GetEnvironmentVariable("REPRO_LOG") == "1") Console.WriteLine(s); },
                new[] { RelationalEventId.CommandExecuted, RelationalEventId.CommandError });

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CharRepro>(e =>
        {
            e.ToTable("CHAR_REPRO");
            e.Property(x => x.Id).HasColumnName("ID").ValueGeneratedNever();
            // Configuration generated by dotnet ef dbcontext scaffold for a CHAR(1) column with a default
            e.Property(x => x.FlagFixedLength).HasColumnName("FLAG_FIXED_LENGTH").HasMaxLength(1).IsUnicode(false)
             .IsFixedLength().HasDefaultValueSql("'N'").ValueGeneratedOnAdd();
            e.Property(x => x.FlagNotFixedLength).HasColumnName("FLAG_NOT_FIXED_LENGTH").HasMaxLength(1).IsUnicode(false)
             .HasDefaultValueSql("'N'").ValueGeneratedOnAdd();
        });
        b.Entity<CharReproNotFixedLength>(e =>
        {
            e.ToTable("CHAR_REPRO_NOT_FIXED_LENGTH");
            e.Property(x => x.Id).HasColumnName("ID").ValueGeneratedNever();
            e.Property(x => x.FlagNotFixedLength).HasColumnName("FLAG_NOT_FIXED_LENGTH").HasMaxLength(1).IsUnicode(false)
             .HasDefaultValueSql("'N'").ValueGeneratedOnAdd();
        });
        b.Entity<CharReproComputed>(e =>
        {
            e.ToTable("CHAR_REPRO_COMPUTED");
            e.Property(x => x.Id).HasColumnName("ID").ValueGeneratedNever();
            e.Property(x => x.Code).HasColumnName("CODE").HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.FlagComputed).HasColumnName("FLAG_COMPUTED").HasMaxLength(1).IsUnicode(false)
             .IsFixedLength().HasComputedColumnSql("CAST(SUBSTR(CODE, 1, 1) AS CHAR(1))");
        });
    }
}
