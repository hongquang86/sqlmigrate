using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class MaskingConverterTests
{
    private const string MaskedDdl = @"CREATE TABLE [dbo].[Users](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[Email] [nvarchar](200) MASKED WITH (FUNCTION = 'email()') NULL,
	[Phone] [nvarchar](20) MASKED WITH (FUNCTION = 'partial(1,""XX"",2)') NULL,
	[Salary] [int] MASKED WITH (FUNCTION = 'random(1, 9)') NULL,
	[Nick] [nvarchar](50) MASKED WITH (FUNCTION = 'default()') NULL,
	[Age] [int] NULL,
	CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([Id] ASC)
)";

    [Fact]
    public void ExtractMasks_FindsAllMaskedColumns()
    {
        var masks = MaskingConverter.ExtractMasks(MaskedDdl);

        Assert.Equal(4, masks.Count);
        Assert.Contains(masks, m => m.Column == "Email" && m.Function == "email()");
        Assert.Contains(masks, m => m.Column == "Phone");
        Assert.Contains(masks, m => m.Column == "Salary");
        Assert.Contains(masks, m => m.Column == "Nick");
    }

    [Fact]
    public void StripMasking_RemovesClausesKeepsColumns()
    {
        var stripped = MaskingConverter.StripMasking(MaskedDdl);

        Assert.DoesNotContain("MASKED WITH", stripped);
        Assert.Contains("[Email] [nvarchar](200) NULL", stripped);
        Assert.Contains("CONSTRAINT [PK_Users]", stripped);
    }

    [Fact]
    public void Markers_RoundTrip()
    {
        var masks = MaskingConverter.ExtractMasks(MaskedDdl);
        var marked = MaskingConverter.AttachMarkers(MaskingConverter.StripMasking(MaskedDdl), masks);
        var parsed = MaskingConverter.TryParseMarkers(marked);

        Assert.Equal(masks.Count, parsed.Count);
        Assert.Contains(parsed, m => m.Column == "Email" && m.Function == "email()");
    }

    [Fact]
    public void BuildMaskedView_MapsAllFourFunctions()
    {
        var masks = MaskingConverter.ExtractMasks(MaskedDdl);
        var stripped = MaskingConverter.StripMasking(MaskedDdl);
        var view = MaskingConverter.BuildMaskedView("dbo", "Users", stripped, masks);

        Assert.NotNull(view);
        Assert.Equal("Users_Masked", view!.ViewName);
        Assert.Contains("CREATE VIEW [dbo].[Users_Masked]", view.Script);
        Assert.Contains("LEFT([Email], 1) + 'XXX@XXXX.com'", view.Script);
        Assert.Contains("LEFT([Phone], 1) + 'XX' + RIGHT([Phone], 2)", view.Script);
        Assert.Contains("ABS(CHECKSUM(NEWID()))", view.Script);
        Assert.Contains("'XXXX' AS [Nick]", view.Script);
        // Cột thường giữ nguyên, cột khóa vẫn đầy đủ.
        Assert.Contains("[Age]", view.Script);
        Assert.Contains("[Id]", view.Script);
        Assert.Contains("FROM [dbo].[Users]", view.Script);
    }

    [Fact]
    public void BuildMaskedView_NoMasks_ReturnsNull()
    {
        var view = MaskingConverter.BuildMaskedView(
            "dbo", "T", "CREATE TABLE [dbo].[T]([Id] [int] NOT NULL);",
            MaskingConverter.ExtractMasks("CREATE TABLE [dbo].[T]([Id] [int] NOT NULL);"));

        Assert.Null(view);
    }

    [Fact]
    public void DefaultMaskLiteral_MatchesNativeSemantics()
    {
        Assert.Equal("'XXXX'", MaskingConverter.DefaultMaskLiteral("nvarchar"));
        Assert.Equal("0", MaskingConverter.DefaultMaskLiteral("int"));
        Assert.Equal("0", MaskingConverter.DefaultMaskLiteral("bit"));
        Assert.Equal("'1900-01-01'", MaskingConverter.DefaultMaskLiteral("datetime2"));
        Assert.Equal("'00000000-0000-0000-0000-000000000000'", MaskingConverter.DefaultMaskLiteral("uniqueidentifier"));
    }
}
