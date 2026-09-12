using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.DataMove;
using Xunit;

namespace SqlMigrator.Tests;

public class DbTypeMapperTests
{
    [Theory]
    [InlineData("int", CanonicalType.Int32)]
    [InlineData("bigint", CanonicalType.Int64)]
    [InlineData("[dbo].[MyType]", CanonicalType.Unknown)]
    [InlineData("nvarchar(50)", CanonicalType.String)]
    [InlineData("decimal(10,2)", CanonicalType.Decimal)]
    [InlineData("datetime2(7)", CanonicalType.DateTime)]
    [InlineData("datetimeoffset(3)", CanonicalType.DateTimeTz)]
    [InlineData("uniqueidentifier", CanonicalType.Guid)]
    [InlineData("bit", CanonicalType.Bool)]
    [InlineData("varbinary(max)", CanonicalType.Binary)]
    [InlineData("xml", CanonicalType.String)]
    [InlineData("float", CanonicalType.Float)]
    public void ParseSqlServer_MapsCommonTypes(string dbType, CanonicalType expected)
    {
        Assert.Equal(expected, DbTypeMappers.ParseSqlServer(dbType).Type);
    }

    [Fact]
    public void ParseSqlServer_NvarcharLengthAndDecimalPrecision()
    {
        var s = DbTypeMappers.ParseSqlServer("nvarchar(50)");
        Assert.Equal(50, s.MaxLength);
        var d = DbTypeMappers.ParseSqlServer("decimal(10,2)");
        Assert.Equal((byte)10, d.Precision);
        Assert.Equal((byte)2, d.Scale);
        var m = DbTypeMappers.ParseSqlServer("nvarchar(max)");
        Assert.Equal(-1, m.MaxLength);
    }

    [Theory]
    [InlineData("integer", CanonicalType.Int32)]
    [InlineData("bigint", CanonicalType.Int64)]
    [InlineData("numeric(12,4)", CanonicalType.Decimal)]
    [InlineData("double precision", CanonicalType.Double)]
    [InlineData("character varying(100)", CanonicalType.String)]
    [InlineData("timestamp with time zone", CanonicalType.DateTimeTz)]
    [InlineData("timestamp without time zone", CanonicalType.DateTime)]
    [InlineData("time with time zone", CanonicalType.Time)]
    [InlineData("uuid", CanonicalType.Guid)]
    [InlineData("jsonb", CanonicalType.Json)]
    [InlineData("bytea", CanonicalType.Binary)]
    [InlineData("boolean", CanonicalType.Bool)]
    [InlineData("interval", CanonicalType.Unknown)]
    public void ParsePostgres_MapsCommonTypes(string dbType, CanonicalType expected)
    {
        Assert.Equal(expected, DbTypeMappers.ParsePostgres(dbType).Type);
    }

    [Theory]
    [InlineData("INTEGER", CanonicalType.Int64)]
    [InlineData("TEXT", CanonicalType.String)]
    [InlineData("VARCHAR(20)", CanonicalType.String)]
    [InlineData("REAL", CanonicalType.Double)]
    [InlineData("BLOB", CanonicalType.Binary)]
    [InlineData("NUMERIC", CanonicalType.Decimal)]
    [InlineData("DATETIME", CanonicalType.DateTime)]
    [InlineData("BOOLEAN", CanonicalType.Bool)]
    public void ParseSqlite_AffinityRules(string dbType, CanonicalType expected)
    {
        Assert.Equal(expected, DbTypeMappers.ParseSqlite(dbType).Type);
    }

    [Fact]
    public void EmitPostgres_IdentityAndJson()
    {
        Assert.Equal("INTEGER", DbTypeMappers.EmitPostgres(CanonicalType.Int32, 0, 0, 0));
        Assert.Equal("VARCHAR(50)", DbTypeMappers.EmitPostgres(CanonicalType.String, 50, 0, 0));
        Assert.Equal("TEXT", DbTypeMappers.EmitPostgres(CanonicalType.String, -1, 0, 0));
        Assert.Equal("JSONB", DbTypeMappers.EmitPostgres(CanonicalType.Json, 0, 0, 0));
        Assert.Equal("NUMERIC(19,4)", DbTypeMappers.EmitPostgres(CanonicalType.Money, 0, 0, 0));
    }

    [Fact]
    public void EmitSqlite_DateAndGuidAsText()
    {
        Assert.Equal("TEXT", DbTypeMappers.EmitSqlite(CanonicalType.DateTime, 0, 0, 0));
        Assert.Equal("TEXT", DbTypeMappers.EmitSqlite(CanonicalType.Guid, 0, 0, 0));
        Assert.Equal("INTEGER", DbTypeMappers.EmitSqlite(CanonicalType.Int64, 0, 0, 0));
    }

    [Fact]
    public void ConvertTo_MoneyWarnsOnPostgresAndSqlite()
    {
        var pg = DbTypeMappers.ConvertTo(CanonicalType.Money, 0, 0, 0, DatabaseEngine.PostgreSql);
        Assert.Equal(CanonicalType.Decimal, pg.Type);
        Assert.NotNull(pg.Warning);

        var lite = DbTypeMappers.ConvertTo(CanonicalType.Money, 0, 0, 0, DatabaseEngine.Sqlite);
        Assert.NotNull(lite.Warning);
    }

    [Fact]
    public void ConvertTo_UnknownAlwaysWarns()
    {
        var r = DbTypeMappers.ConvertTo(CanonicalType.Unknown, 0, 0, 0, DatabaseEngine.PostgreSql);
        Assert.Equal(CanonicalType.String, r.Type);
        Assert.NotNull(r.Warning);
    }

    [Fact]
    public void ConvertTo_CompatibleTypesNoWarning()
    {
        var r = DbTypeMappers.ConvertTo(CanonicalType.Int32, 0, 0, 0, DatabaseEngine.PostgreSql);
        Assert.Equal(CanonicalType.Int32, r.Type);
        Assert.Null(r.Warning);
    }
}
