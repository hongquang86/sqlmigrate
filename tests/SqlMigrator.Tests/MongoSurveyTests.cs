using System.Collections.Generic;
using MongoDB.Bson;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.DataMove;
using Xunit;

namespace SqlMigrator.Tests;

public class MongoSurveyTests
{
    private static BsonDocument Doc(string json) => BsonDocument.Parse(json);

    [Fact]
    public void InferFields_BasicTypesAndNullable()
    {
        var sample = new List<BsonDocument>
        {
            Doc("{ _id: ObjectId('6569a1b2c3d4e5f60718293a'), name: 'An', age: 30, active: true }"),
            Doc("{ _id: ObjectId('6569a1b2c3d4e5f60718293b'), name: 'Binh' }")
        };
        var fields = MongoSurvey.InferFields(sample, out var warnings);

        Assert.Contains(fields, f => f.Name == "_id" && f.Type == CanonicalType.String && f.MaxLength == 24);
        Assert.Contains(fields, f => f.Name == "name" && f.Type == CanonicalType.String);
        var age = Assert.Single(fields, f => f.Name == "age");
        Assert.Equal(CanonicalType.Int32, age.Type);
        Assert.True(age.Nullable);
        Assert.Contains(fields, f => f.Name == "active" && f.Type == CanonicalType.Bool);
        Assert.Empty(warnings);
    }

    [Fact]
    public void InferFields_MixedAndNested_Warns()
    {
        var sample = new List<BsonDocument>
        {
            Doc("{ code: 123, address: { city: 'HCM' } }"),
            Doc("{ code: 'A1', tags: ['x', 'y'] }")
        };
        var fields = MongoSurvey.InferFields(sample, out var warnings);

        var code = Assert.Single(fields, f => f.Name == "code");
        Assert.Equal(CanonicalType.String, code.Type);
        Assert.NotNull(code.Warning);
        Assert.Contains(fields, f => f.Name == "address" && f.Type == CanonicalType.Json);
        Assert.Contains(fields, f => f.Name == "tags" && f.Type == CanonicalType.Json);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void ToRow_MissingField_Null()
    {
        var columns = new List<CanonicalColumn>
        {
            new() { Name = "_id", Type = CanonicalType.String },
            new() { Name = "name", Type = CanonicalType.String }
        };
        var row = MongoSurvey.ToRow(Doc("{ _id: 'abc' }"), columns);

        Assert.Equal("abc", row[0]);
        Assert.Null(row[1]);
    }

    [Fact]
    public void ToDocument_IdColumn_MapsToUnderscoreId()
    {
        var columns = new List<CanonicalColumn>
        {
            new() { Name = "Id", Type = CanonicalType.Int32 },
            new() { Name = "name", Type = CanonicalType.String }
        };
        var doc = MongoSurvey.ToDocument(columns, new object?[] { 7, "An" }, "Id");

        Assert.Equal(7, doc["_id"].AsInt32);
        Assert.Equal("An", doc["name"].AsString);
        Assert.False(doc.Contains("Id"));
    }

    [Theory]
    [InlineData("MONGO:orders", "orders")]
    [InlineData("CREATE TABLE `orders` (x INT);", "orders")]
    [InlineData("CREATE TABLE [dbo].[Orders] (x INT);", "Orders")]
    [InlineData("", null)]
    public void ExtractCollectionName_Cases(string ddl, string? expected)
    {
        Assert.Equal(expected, MongoEndpoint.ExtractCollectionName(ddl));
    }

    [Theory]
    [InlineData(CanonicalType.DateTimeTz, true)]
    [InlineData(CanonicalType.Guid, true)]
    [InlineData(CanonicalType.Int64, false)]
    [InlineData(CanonicalType.Json, false)]
    public void ConvertTo_Mongo_Warnings(CanonicalType type, bool expectWarning)
    {
        var (_, _, _, _, warning) = DbTypeMappers.ConvertTo(
            type, 0, 0, 0, DatabaseEngine.MongoDb);

        Assert.Equal(expectWarning, warning != null);
    }
}
