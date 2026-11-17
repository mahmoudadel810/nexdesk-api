using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class OpenApiDocumentTests(ApiFactory factory)
{
    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    [Fact]
    public async Task The_document_describes_every_operation_and_carries_request_examples()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        document.GetProperty("info").GetProperty("title").GetString().ShouldBe("NexDesk API");
        document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer")
            .GetProperty("scheme").GetString().ShouldBe("bearer");

        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => HttpMethods.Contains(operation.Name))
                .Select(operation => (Route: $"{operation.Name.ToUpperInvariant()} {path.Name}", Operation: operation.Value)))
            .ToList();
        operations.Count.ShouldBeGreaterThan(25);

        var undocumented = operations
            .Where(o => !o.Operation.TryGetProperty("summary", out var summary) || string.IsNullOrWhiteSpace(summary.GetString())
                || !o.Operation.TryGetProperty("operationId", out _))
            .Select(o => o.Route)
            .ToList();
        undocumented.ShouldBeEmpty("every endpoint needs a name and a summary");

        var schemas = document.GetProperty("components").GetProperty("schemas");
        var bodiesWithoutExample = operations
            .Where(o => o.Operation.TryGetProperty("requestBody", out var body)
                && body.GetProperty("content").TryGetProperty("application/json", out _))
            .Select(o => (o.Route, Schema: o.Operation.GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema")))
            .Where(body => !HasExample(body.Schema, schemas))
            .Select(body => body.Route)
            .ToList();
        bodiesWithoutExample.ShouldBeEmpty("every request body needs an example");

        schemas.GetProperty("LoginRequest").GetProperty("examples")[0].GetProperty("email").GetString()
            .ShouldBe("olivia@acme.example");
    }

    [Fact]
    public async Task Protected_operations_declare_bearer_security_and_public_ones_do_not()
    {
        var document = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = document.GetProperty("paths");

        paths.GetProperty("/api/v1/auth/me").GetProperty("get").TryGetProperty("security", out _).ShouldBeTrue();
        paths.GetProperty("/api/v1/users").GetProperty("get").TryGetProperty("security", out _).ShouldBeTrue();
        paths.GetProperty("/api/v1/auth/login").GetProperty("post").TryGetProperty("security", out _).ShouldBeFalse();

        // The public API is called with an API key, not a staff token.
        paths.GetProperty("/api/v1/public/tickets").GetProperty("get").GetProperty("security")[0]
            .TryGetProperty("ApiKey", out _).ShouldBeTrue();
        document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey")
            .GetProperty("name").GetString().ShouldBe("X-Api-Key");
    }

    [Fact]
    public async Task Every_templated_path_segment_is_a_declared_path_parameter()
    {
        var document = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        var missing = new List<string>();
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            var names = System.Text.RegularExpressions.Regex.Matches(path.Name, @"\{([^}:]+)").Select(m => m.Groups[1].Value).ToList();
            foreach (var operation in path.Value.EnumerateObject().Where(o => HttpMethods.Contains(o.Name)))
            {
                var declared = operation.Value.TryGetProperty("parameters", out var parameters)
                    ? parameters.EnumerateArray()
                        .Where(p => p.GetProperty("in").GetString() == "path")
                        .Select(p => p.GetProperty("name").GetString())
                        .ToHashSet()
                    : [];
                missing.AddRange(names.Where(n => !declared.Contains(n)).Select(n => $"{operation.Name.ToUpperInvariant()} {path.Name}: {n}"));
            }
        }

        missing.ShouldBeEmpty("client generators reject a path whose parameters are not declared");
    }

    private static bool HasExample(JsonElement schema, JsonElement schemas)
    {
        if (schema.TryGetProperty("examples", out _))
        {
            return true;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!.Split('/')[^1];
            return schemas.GetProperty(name).TryGetProperty("examples", out _);
        }

        return false;
    }
}
