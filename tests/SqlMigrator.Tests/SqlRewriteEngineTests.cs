using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class SqlRewriteEngineTests
{
    private static SqlRewriteEngine CreateEngine()
    {
        var configPath = System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SqlMigrator.Core", "rewrite-rules.json");
        configPath = System.IO.Path.GetFullPath(configPath);
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory().CreateLogger("test");
        return SqlRewriteEngine.FromFile(configPath, logger);
    }

    // -------------------------------------------------------
    // STRING_SPLIT
    // -------------------------------------------------------

    [Fact]
    public void Rewrite_StringSplit_InjectsFnAndReplaces()
    {
        var engine = CreateEngine();
        var script = "SELECT value FROM STRING_SPLIT(@tags, ',');";
        var result = engine.Rewrite(script);

        Assert.Contains("fn_SplitString", result.RewrittenScript);
        Assert.Contains("CREATE FUNCTION", result.RewrittenScript);
        Assert.DoesNotContain("STRING_SPLIT", result.RewrittenScript);
        Assert.Contains("string_split", result.AppliedRules);
    }

    [Fact]
    public void Rewrite_StringSplit_WithQuotedDelimiter()
    {
        var engine = CreateEngine();
        var script = "SELECT * FROM STRING_SPLIT(@input, ';');";
        var result = engine.Rewrite(script);

        Assert.Contains("fn_SplitString", result.RewrittenScript);
        Assert.DoesNotContain("STRING_SPLIT", result.RewrittenScript);
    }

    // -------------------------------------------------------
    // STRING_ESCAPE
    // -------------------------------------------------------

    [Fact]
    public void Rewrite_StringEscape_ReplacesWithReplaceChain()
    {
        var engine = CreateEngine();
        var script = "SELECT STRING_ESCAPE(@val, 'json');";
        var result = engine.Rewrite(script);

        Assert.Contains("REPLACE(", result.RewrittenScript);
        Assert.DoesNotContain("STRING_ESCAPE", result.RewrittenScript);
        Assert.Contains("string_escape_json", result.AppliedRules);
    }

    // -------------------------------------------------------
    // DROP IF EXISTS
    // -------------------------------------------------------

    [Theory]
    [InlineData("DROP TABLE IF EXISTS dbo.MyTable;", "IF OBJECT_ID(dbo.MyTable) IS NOT NULL DROP TABLE dbo.MyTable;")]
    [InlineData("DROP VIEW IF EXISTS [dbo].[MyView];", "IF OBJECT_ID([dbo].[MyView]) IS NOT NULL DROP VIEW [dbo].[MyView];")]
    [InlineData("DROP PROCEDURE IF EXISTS dbo.MyProc;", "IF OBJECT_ID(dbo.MyProc) IS NOT NULL DROP PROCEDURE dbo.MyProc;")]
    [InlineData("DROP FUNCTION IF EXISTS dbo.MyFn;", "IF OBJECT_ID(dbo.MyFn) IS NOT NULL DROP FUNCTION dbo.MyFn;")]
    [InlineData("DROP TRIGGER IF EXISTS dbo.MyTrg;", "IF OBJECT_ID(dbo.MyTrg) IS NOT NULL DROP TRIGGER dbo.MyTrg;")]
    public void Rewrite_DropIfExists_ReplacesCorrectly(string input, string expectedContains)
    {
        var engine = CreateEngine();
        var result = engine.Rewrite(input);

        Assert.Contains(expectedContains, result.RewrittenScript);
    }

    // -------------------------------------------------------
    // Unsupported features
    // -------------------------------------------------------

    [Theory]
    [InlineData("SELECT * FROM t FOR JSON AUTO;", "for_json")]
    [InlineData("SELECT JSON_MODIFY(@json, '$.a', 1);", "json_modify")]
    [InlineData("SELECT * FROM OPENJSON(@json) WITH (a int);", "openjson_with")]
    [InlineData("CREATE TABLE t (id INT) WITH (SYSTEM_VERSIONING = ON);", "temporal_table")]
    public void Rewrite_UnsupportedFeatures_Detected(string input, string expectedRuleId)
    {
        var engine = CreateEngine();
        var result = engine.Rewrite(input);

        Assert.Contains(expectedRuleId, result.UnsupportedRules);
        Assert.False(result.IsFullyRewritable);
    }

    // -------------------------------------------------------
    // Compat shims (thư viện tương thích tự bung lên đích)
    // -------------------------------------------------------

    [Theory]
    [InlineData("SELECT JSON_VALUE(@json, '$.name');",
        "Compat.JsonValue(@json, '$.name')", "json_value", "Json")]
    [InlineData("SELECT JSON_QUERY(@json, '$.addr');",
        "Compat.JsonQuery(@json, '$.addr')", "json_query", "Json")]
    [InlineData("SELECT ISJSON(@x);",
        "Compat.IsJson(@x)", "isjson", "Json")]
    [InlineData("SELECT * FROM OPENJSON(@json);",
        "Compat.OpenJson(@json)", "openjson", "Json")]
    [InlineData("SELECT DATEDIFF_BIG(ms, @a, @b);",
        "Compat.DatediffBig(ms, @a, @b)", "datediff_big", "Date")]
    [InlineData("SELECT SESSION_CONTEXT(N'key');",
        "Compat.SessionContext_Get(N'key')", "session_context", "Session")]
    public void Rewrite_CompatShim_ReplacesAndRequiresShim(
        string input, string expectedFragment, string expectedRuleId, string expectedShim)
    {
        var engine = CreateEngine();
        var result = engine.Rewrite(input);

        Assert.Contains(expectedRuleId, result.AppliedRules);
        Assert.Contains(expectedFragment, result.RewrittenScript);
        Assert.Contains(expectedShim, result.RequiredShims);
        Assert.True(result.IsFullyRewritable);
    }

    [Fact]
    public void Rewrite_AtTimeZoneSingle_MapsToConvert()
    {
        var engine = CreateEngine();
        var result = engine.Rewrite("SELECT dt AT TIME ZONE 'UTC';");

        Assert.Contains("at_time_zone", result.AppliedRules);
        Assert.Contains("Compat.ConvertTimeZone(dt, 'UTC', NULL)", result.RewrittenScript);
        Assert.Contains("TimeZone", result.RequiredShims);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Rewrite_AtTimeZoneDouble_MapsToConvert()
    {
        var engine = CreateEngine();
        var result = engine.Rewrite("SELECT CreatedAt AT TIME ZONE 'UTC' AT TIME ZONE 'SE Asia Standard Time';");

        Assert.Contains("at_time_zone_convert", result.AppliedRules);
        Assert.Contains("Compat.ConvertTimeZone(CreatedAt, 'UTC', 'SE Asia Standard Time')",
            result.RewrittenScript);
        Assert.DoesNotContain("AT TIME ZONE", result.RewrittenScript);
    }

    [Fact]
    public void Rewrite_SpSetSessionContext_MapsToShim()
    {
        var engine = CreateEngine();
        var result = engine.Rewrite("EXEC sp_set_session_context @key=N'k', @value=@v;");

        Assert.Contains("sp_set_session_context", result.AppliedRules);
        Assert.Contains("Compat.SessionContext_Set @key = N'k', @value = @v", result.RewrittenScript);
        Assert.Contains("Session", result.RequiredShims);
        Assert.NotEmpty(result.Warnings);
    }

    // -------------------------------------------------------
    // AnalyzeScript
    // -------------------------------------------------------

    [Fact]
    public void AnalyzeScript_DetectsMultipleIssues()
    {
        var engine = CreateEngine();
        var script = @"
CREATE PROCEDURE dbo.MyProc AS
BEGIN
    SELECT value FROM STRING_SPLIT(@tags, ',');
    SELECT JSON_VALUE(@data, '$.id');
    SELECT * FROM t FOR JSON AUTO;
END";

        var issues = engine.AnalyzeScript(script);

        Assert.Contains(issues, i => i.RuleId == "string_split");
        Assert.Contains(issues, i => i.RuleId == "json_value");
        Assert.Contains(issues, i => i.RuleId == "for_json");

        Assert.True(issues.First(i => i.RuleId == "string_split").IsSupported);
        Assert.True(issues.First(i => i.RuleId == "json_value").IsSupported);
        Assert.Equal("Json", issues.First(i => i.RuleId == "json_value").RequiresShim);
    }

    // -------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------

    [Fact]
    public void Rewrite_EmptyScript_ReturnsEmpty()
    {
        var engine = CreateEngine();
        var result = engine.Rewrite("");
        Assert.Equal("", result.RewrittenScript);
        Assert.Empty(result.AppliedRules);
    }

    [Fact]
    public void Rewrite_NullScript_ReturnsNull()
    {
        var engine = CreateEngine();
        var result = engine.Rewrite(null!);
        Assert.Equal("", result.RewrittenScript);
    }

    [Fact]
    public void Rewrite_NoMatch_ReturnsUnchanged()
    {
        var engine = CreateEngine();
        var script = "SELECT * FROM dbo.Users WHERE Id = 1;";
        var result = engine.Rewrite(script);

        Assert.Equal(script, result.RewrittenScript);
        Assert.Empty(result.AppliedRules);
        Assert.Empty(result.UnsupportedRules);
    }

    [Fact]
    public void Rewrite_CombinesMultipleRules()
    {
        var engine = CreateEngine();
        var script = @"
DROP TABLE IF EXISTS dbo.Tmp;
SELECT STRING_SPLIT(@tags, ',');
SELECT STRING_ESCAPE(@val, 'json');";

        var result = engine.Rewrite(script);

        Assert.Contains("drop_table_if_exists", result.AppliedRules);
        Assert.Contains("string_split", result.AppliedRules);
        Assert.Contains("string_escape_json", result.AppliedRules);
        Assert.DoesNotContain("DROP TABLE IF EXISTS", result.RewrittenScript);
        Assert.DoesNotContain("STRING_SPLIT", result.RewrittenScript);
        Assert.DoesNotContain("STRING_ESCAPE", result.RewrittenScript);
    }

    // -------------------------------------------------------
    // CREATE OR ALTER (2016 SP1)
    // -------------------------------------------------------

    [Theory]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.MyProc AS SELECT 1;",
        "IF OBJECT_ID(N'dbo.MyProc') IS NOT NULL DROP PROCEDURE dbo.MyProc",
        "CREATE PROCEDURE dbo.MyProc AS SELECT 1;")]
    [InlineData("create or alter view [dbo].[V] as select 1 as a;",
        "DROP view [dbo].[V]",
        "CREATE view [dbo].[V] as select 1 as a;")]
    public void Rewrite_CreateOrAlter_ProducesDropGoCreate(
        string input, string expectedDrop, string expectedCreate)
    {
        var engine = CreateEngine();
        var result = engine.Rewrite(input);

        Assert.Contains("create_or_alter", result.AppliedRules);
        Assert.Contains(expectedDrop, result.RewrittenScript);
        Assert.Contains("GO", result.RewrittenScript);
        Assert.Contains(expectedCreate, result.RewrittenScript);
        Assert.DoesNotContain("OR ALTER", result.RewrittenScript);
        Assert.True(result.IsFullyRewritable);
    }

    // -------------------------------------------------------
    // Các feature mới phát hiện nhưng không rewrite được
    // -------------------------------------------------------
    [Theory]
    [InlineData("SELECT COMPRESS(@data);", "compress")]
    [InlineData("SELECT DECOMPRESS(@blob);", "decompress")]
    [InlineData("TRUNCATE TABLE dbo.T WITH (PARTITIONS (1, 2));", "truncate_with_partitions")]
    [InlineData("SELECT * FROM dbo.T FOR SYSTEM_TIME AS OF '2024-01-01';", "for_system_time")]
    public void Rewrite_NewlyCoveredFeatures_DetectedAsUnsupported(string input, string expectedRuleId)
    {
        var engine = CreateEngine();
        var result = engine.Rewrite(input);

        Assert.Contains(expectedRuleId, result.UnsupportedRules);
        Assert.False(result.IsFullyRewritable);
        Assert.NotEmpty(result.UnsupportedMessages);
    }

    // -------------------------------------------------------
    // Lột Dynamic Data Masking + cảnh báo kèm rewrite
    // -------------------------------------------------------

    [Fact]
    public void Rewrite_MaskedColumn_StripsMaskingAndWarns()
    {
        var engine = CreateEngine();
        var script = "CREATE TABLE dbo.Users(Id int, Email nvarchar(200) MASKED WITH (FUNCTION = 'email()') NULL);";
        var result = engine.Rewrite(script);

        Assert.Contains("dynamic_data_masking", result.AppliedRules);
        Assert.DoesNotContain("MASKED WITH", result.RewrittenScript);
        Assert.Contains("Email nvarchar(200)", result.RewrittenScript);
        Assert.True(result.IsFullyRewritable);
        Assert.NotEmpty(result.Warnings);
    }
}
