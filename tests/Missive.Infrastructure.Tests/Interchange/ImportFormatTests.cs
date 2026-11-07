using Missive.Application.Common;
using Missive.Application.Interchange;
using Missive.Domain.Requests;
using Missive.Infrastructure.Interchange;

namespace Missive.Infrastructure.Tests.Interchange;

public class ImportFormatTests
{
    private readonly CollectionImporter _importer = new();

    private ParsedCollection Parse(string text, string fileName = "export.json")
    {
        var result = _importer.Parse(text, fileName);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void Postman21_ReadsFoldersRequestsAndQuery()
    {
        var parsed = Parse("""
            {
              "info": { "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                {
                  "name": "Users",
                  "item": [
                    {
                      "name": "Get user",
                      "request": {
                        "method": "GET",
                        "header": [
                          { "key": "Accept", "value": "application/json" },
                          { "key": "X-Debug", "value": "1", "disabled": true }
                        ],
                        "url": {
                          "raw": "{{baseUrl}}/users/:id?expand=orders&q=a%20b",
                          "host": ["{{baseUrl}}"],
                          "path": ["users", ":id"],
                          "query": [
                            { "key": "expand", "value": "orders" },
                            { "key": "q", "value": "a%20b" },
                            { "key": "page", "value": "2", "disabled": true }
                          ],
                          "variable": [{ "key": "id", "value": "42" }]
                        }
                      }
                    }
                  ]
                },
                { "name": "Health", "request": "https://api.example.com/health?full" }
              ],
              "variable": [{ "key": "baseUrl", "value": "https://api.example.com" }]
            }
            """);

        Assert.Equal("Postman collection v2.1", parsed.Format);
        var root = parsed.Collection.Root;
        Assert.Equal("Shop", root.Name);
        var health = Assert.Single(root.Requests);
        Assert.Equal("https://api.example.com/health", health.Url);
        Assert.Equal([new KeyValueEntry("full", "")], health.Params);

        var users = Assert.Single(root.Folders);
        Assert.Equal("Users", users.Name);
        var get = Assert.Single(users.Requests);
        Assert.Equal(RequestMethod.Get, get.Method);
        Assert.Equal("{{baseUrl}}/users/42", get.Url);
        Assert.Equal(
            [new KeyValueEntry("expand", "orders"), new KeyValueEntry("q", "a b"), new KeyValueEntry("page", "2", false)],
            get.Params);
        Assert.Equal([new KeyValueEntry("Accept", "application/json"), new KeyValueEntry("X-Debug", "1", false)], get.Headers);

        var environment = Assert.Single(parsed.Collection.Environments);
        Assert.Equal("Shop", environment.Name);
        Assert.Equal("https://api.example.com", Assert.Single(environment.Variables).Value);
    }

    [Fact]
    public void Postman_ReadsDescriptionsAsTextOrObjects()
    {
        var parsed = Parse("""
            {
              "info": { "name": "Docs", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "Text", "request": { "method": "GET", "url": "https://x.test/a", "description": "Lists *things*." } },
                { "name": "Object", "request": { "method": "GET", "url": "https://x.test/b",
                  "description": { "content": "# B", "type": "text/markdown" } } },
                { "name": "On item", "description": "Old style.", "request": { "method": "GET", "url": "https://x.test/c" } },
                { "name": "None", "request": { "method": "GET", "url": "https://x.test/d" } }
              ]
            }
            """);

        Assert.Equal(["Lists *things*.", "# B", "Old style.", ""], parsed.Collection.Root.Requests.Select(r => r.Description));
    }

    [Fact]
    public void OpenApi_ReadsOperationDescriptions()
    {
        var parsed = Parse("""
            {
              "openapi": "3.0.0",
              "info": { "title": "Pets", "version": "1" },
              "paths": { "/pets": { "get": { "summary": "List pets", "description": "Returns **all** pets." } } }
            }
            """);

        var list = Assert.Single(parsed.Collection.Root.Folders.SelectMany(f => f.Requests).Concat(parsed.Collection.Root.Requests));
        Assert.Equal("List pets", list.Name);
        Assert.Equal("Returns **all** pets.", list.Description);
    }

    [Fact]
    public void Postman_InheritsAuthFromFoldersAndHonorsNoAuth()
    {
        var parsed = Parse("""
            {
              "info": { "name": "Auth", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "{{token}}", "type": "string" }] },
              "item": [
                { "name": "Inherits", "request": { "method": "GET", "url": "https://x.test/a" } },
                { "name": "Public", "request": { "method": "GET", "url": "https://x.test/b", "auth": { "type": "noauth" } } },
                {
                  "name": "Admin",
                  "auth": { "type": "basic", "basic": [{ "key": "username", "value": "root" }, { "key": "password", "value": "pw" }] },
                  "item": [
                    { "name": "Key", "request": { "method": "GET", "url": "https://x.test/c",
                      "auth": { "type": "apikey", "apikey": [{ "key": "key", "value": "X-Key" }, { "key": "value", "value": "k" }, { "key": "in", "value": "query" }] } } },
                    { "name": "Basic", "request": { "method": "DELETE", "url": "https://x.test/d" } },
                    { "name": "Digest", "request": { "method": "GET", "url": "https://x.test/e", "auth": { "type": "digest" } } }
                  ]
                }
              ]
            }
            """);

        var root = parsed.Collection.Root;
        Assert.Equal(new BearerAuth("{{token}}"), root.Requests[0].Auth);
        Assert.Equal(RequestAuth.None, root.Requests[1].Auth);
        var admin = root.Folders[0];
        Assert.Equal(new ApiKeyAuth("X-Key", "k", ApiKeyLocation.Query), admin.Requests[0].Auth);
        Assert.Equal(new BasicAuth("root", "pw"), admin.Requests[1].Auth);
        Assert.Equal(RequestAuth.None, admin.Requests[2].Auth);
        Assert.Contains(parsed.Warnings, w => w.Contains("digest"));
    }

    [Fact]
    public void Postman20_ReadsObjectAuthAndBodies()
    {
        var parsed = Parse("""
            {
              "info": { "name": "Old", "schema": "https://schema.getpostman.com/json/collection/v2.0.0/collection.json" },
              "item": [
                { "name": "Login", "request": {
                    "method": "POST", "url": "https://x.test/login",
                    "auth": { "type": "basic", "basic": { "username": "u", "password": "p" } },
                    "header": [{ "key": "Content-Type", "value": "application/x-www-form-urlencoded" }],
                    "body": { "mode": "urlencoded", "urlencoded": [
                      { "key": "user name", "value": "Ada & co" },
                      { "key": "skip", "value": "1", "disabled": true }
                    ] } } },
                { "name": "Upload", "request": {
                    "method": "POST", "url": "https://x.test/upload",
                    "body": { "mode": "formdata", "formdata": [
                      { "key": "title", "value": "Report", "type": "text" },
                      { "key": "file", "src": "/tmp/a.pdf", "type": "file" }
                    ] } } },
                { "name": "Create", "request": {
                    "method": "POST", "url": "https://x.test/items",
                    "body": { "mode": "raw", "raw": "{\"a\":1}", "options": { "raw": { "language": "json" } } } } },
                { "name": "Query", "request": {
                    "method": "POST", "url": "https://x.test/graphql",
                    "body": { "mode": "graphql", "graphql": { "query": "{ me { id } }", "variables": "{\"x\":1}" } } } },
                { "name": "Purge", "request": { "method": "PURGE", "url": "https://x.test/cache" } }
              ]
            }
            """);

        Assert.Equal("Postman collection v2.0", parsed.Format);
        var requests = parsed.Collection.Root.Requests;
        Assert.Equal(4, requests.Count);

        Assert.Equal(new BasicAuth("u", "p"), requests[0].Auth);
        Assert.Equal(new RequestBody(BodyType.UrlEncoded, "user%20name=Ada%20%26%20co"), requests[0].Body);
        Assert.Empty(requests[0].Headers); // Missive sets this Content-Type itself.

        Assert.Equal(new RequestBody(BodyType.Form, "title=Report"), requests[1].Body);
        Assert.Contains(parsed.Warnings, w => w.Contains("file field 'file'"));

        Assert.Equal(new RequestBody(BodyType.Json, "{\"a\":1}"), requests[2].Body);

        Assert.Equal(BodyType.Json, requests[3].Body.Type);
        Assert.Contains("\"query\": \"{ me { id } }\"", requests[3].Body.Content);
        Assert.Contains("\"x\": 1", requests[3].Body.Content);

        Assert.Contains(parsed.Warnings, w => w.Contains("PURGE"));
    }

    [Fact]
    public void PostmanEnvironment_KeepsSecretsAsSecrets()
    {
        var parsed = Parse("""
            {
              "name": "Staging",
              "values": [
                { "key": "baseUrl", "value": "https://staging.test", "enabled": true },
                { "key": "token", "value": "s3cr3t", "type": "secret", "enabled": true },
                { "key": "old", "value": "x", "enabled": false }
              ],
              "_postman_variable_scope": "environment"
            }
            """);

        Assert.Equal("Postman environment", parsed.Format);
        Assert.True(parsed.Collection.Root.IsEmpty);
        var environment = Assert.Single(parsed.Collection.Environments);
        Assert.Equal("Staging", environment.Name);
        Assert.Equal(2, environment.Variables.Count);
        Assert.True(environment.Variables[1].Secret);
        Assert.Equal("s3cr3t", environment.Variables[1].Value);
    }

    [Fact]
    public void Insomnia_ReadsResourcesVariablesAndEnvironments()
    {
        var parsed = Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk_1", "_type": "workspace", "parentId": null, "name": "Billing" },
                { "_id": "fld_1", "_type": "request_group", "parentId": "wrk_1", "name": "Invoices" },
                { "_id": "req_1", "_type": "request", "parentId": "fld_1", "name": "List invoices",
                  "method": "GET", "url": "{{ _.baseUrl }}/invoices?status=open",
                  "parameters": [{ "name": "limit", "value": "10" }, { "name": "debug", "value": "1", "disabled": true }],
                  "headers": [{ "name": "X-Tenant", "value": "{{ _.tenant }}" }],
                  "authentication": { "type": "bearer", "token": "{{ _.token }}" },
                  "body": {} },
                { "_id": "req_2", "_type": "request", "parentId": "wrk_1", "name": "Create invoice",
                  "method": "POST", "url": "{{ _.baseUrl }}/invoices",
                  "headers": [{ "name": "Content-Type", "value": "application/json" }],
                  "authentication": { "type": "apikey", "key": "X-Api-Key", "value": "k", "addTo": "queryParams" },
                  "body": { "mimeType": "application/json", "text": "{ \"total\": 10 }" } },
                { "_id": "grpc_1", "_type": "grpc_request", "parentId": "wrk_1", "name": "Stream" },
                { "_id": "env_base", "_type": "environment", "parentId": "wrk_1", "name": "Base Environment",
                  "data": { "baseUrl": "https://billing.test", "api": { "version": 2 } } },
                { "_id": "env_dev", "_type": "environment", "parentId": "env_base", "name": "Dev",
                  "data": { "baseUrl": "http://localhost:8080", "token": "dev" } }
              ]
            }
            """);

        Assert.Equal("Insomnia export v4", parsed.Format);
        var root = parsed.Collection.Root;
        Assert.Equal("Billing", root.Name);

        var create = Assert.Single(root.Requests);
        Assert.Equal("{{baseUrl}}/invoices", create.Url);
        Assert.Equal(new ApiKeyAuth("X-Api-Key", "k", ApiKeyLocation.Query), create.Auth);
        Assert.Equal(new RequestBody(BodyType.Json, "{ \"total\": 10 }"), create.Body);
        Assert.Empty(create.Headers);

        var list = Assert.Single(Assert.Single(root.Folders).Requests);
        Assert.Equal("{{baseUrl}}/invoices", list.Url);
        Assert.Equal(
            [new KeyValueEntry("status", "open"), new KeyValueEntry("limit", "10"), new KeyValueEntry("debug", "1", false)],
            list.Params);
        Assert.Equal("{{tenant}}", list.Headers[0].Value);
        Assert.Equal(new BearerAuth("{{token}}"), list.Auth);
        Assert.Equal(RequestBody.None, list.Body);

        var dev = Assert.Single(parsed.Collection.Environments);
        Assert.Equal("Dev", dev.Name);
        Assert.Equal(
            ["baseUrl=http://localhost:8080", "api.version=2", "token=dev"],
            dev.Variables.Select(v => $"{v.Name}={v.Value}"));
        Assert.Contains(parsed.Warnings, w => w.Contains("grpc request"));
    }

    [Fact]
    public void Har_GroupsRequestsByHostAndDropsConnectionHeaders()
    {
        var parsed = Parse("""
            {
              "log": {
                "version": "1.2",
                "entries": [
                  { "request": {
                      "method": "GET", "url": "https://api.test/v1/items?page=2&q=a+b",
                      "headers": [
                        { "name": ":authority", "value": "api.test" },
                        { "name": "Host", "value": "api.test" },
                        { "name": "Accept", "value": "application/json" }
                      ],
                      "queryString": [{ "name": "page", "value": "2" }, { "name": "q", "value": "a+b" }] } },
                  { "request": {
                      "method": "POST", "url": "https://cdn.test/upload",
                      "headers": [{ "name": "Content-Type", "value": "multipart/form-data; boundary=xyz" }],
                      "postData": { "mimeType": "multipart/form-data; boundary=xyz",
                        "params": [{ "name": "title", "value": "Hi" }, { "name": "file", "fileName": "a.png" }] } } },
                  { "request": {
                      "method": "PUT", "url": "https://api.test/v1/items/1",
                      "headers": [{ "name": "Content-Type", "value": "application/xml" }],
                      "postData": { "mimeType": "application/xml", "text": "<item/>" } } }
                ]
              }
            }
            """, "capture.har");

        Assert.Equal("HAR archive", parsed.Format);
        var root = parsed.Collection.Root;
        Assert.Equal("capture", root.Name);
        Assert.Equal(["api.test", "cdn.test"], root.Folders.Select(f => f.Name));

        var api = root.Folders[0].Requests;
        Assert.Equal("GET /v1/items", api[0].Name);
        Assert.Equal("https://api.test/v1/items", api[0].Url);
        Assert.Equal([new KeyValueEntry("page", "2"), new KeyValueEntry("q", "a b")], api[0].Params);
        Assert.Equal([new KeyValueEntry("Accept", "application/json")], api[0].Headers);
        Assert.Equal(new RequestBody(BodyType.Text, "<item/>"), api[1].Body);
        Assert.Equal([new KeyValueEntry("Content-Type", "application/xml")], api[1].Headers);

        var upload = Assert.Single(root.Folders[1].Requests);
        Assert.Equal(new RequestBody(BodyType.Form, "title=Hi"), upload.Body);
        Assert.Empty(upload.Headers);
    }

    [Fact]
    public void OpenApi3Yaml_BuildsRequestsEnvironmentsAndAuth()
    {
        var parsed = Parse("""
            openapi: 3.0.3
            info:
              title: Pet Store
              version: 1.0.0
            servers:
              - url: https://{region}.pets.test/v1/
                description: Production
                variables:
                  region:
                    default: eu
              - url: http://localhost:3000
                description: Local
            security:
              - bearerAuth: []
            tags:
              - name: pets
            paths:
              /pets:
                get:
                  tags: [pets]
                  summary: List pets
                  parameters:
                    - $ref: '#/components/parameters/Limit'
                    - name: status
                      in: query
                      required: true
                      schema:
                        type: string
                        enum: [available, sold]
                post:
                  tags: [pets]
                  operationId: createPet
                  requestBody:
                    content:
                      application/json:
                        schema:
                          $ref: '#/components/schemas/Pet'
              /pets/{petId}:
                parameters:
                  - name: petId
                    in: path
                    required: true
                    schema: { type: integer }
                delete:
                  security: []
                  responses: {}
              /health:
                get:
                  security:
                    - apiKey: []
            components:
              parameters:
                Limit:
                  name: limit
                  in: query
                  schema: { type: integer, default: 20 }
              schemas:
                Pet:
                  type: object
                  properties:
                    id: { type: integer, readOnly: true }
                    name: { type: string, example: Rex }
                    tags:
                      type: array
                      items: { type: string }
                    owner:
                      $ref: '#/components/schemas/Owner'
                Owner:
                  type: object
                  properties:
                    email: { type: string, format: email }
                    pets:
                      type: array
                      items: { $ref: '#/components/schemas/Pet' }
              securitySchemes:
                bearerAuth: { type: http, scheme: bearer }
                apiKey: { type: apiKey, in: header, name: X-API-Key }
            """, "petstore.yaml");

        Assert.Equal("OpenAPI 3.0.3 description", parsed.Format);
        var root = parsed.Collection.Root;
        Assert.Equal("Pet Store", root.Name);

        var pets = Assert.Single(root.Folders);
        Assert.Equal("pets", pets.Name);
        var list = pets.Requests[0];
        Assert.Equal("List pets", list.Name);
        Assert.Equal("{{baseUrl}}/pets", list.Url);
        Assert.Equal([new KeyValueEntry("limit", "20", false), new KeyValueEntry("status", "available")], list.Params);
        Assert.Equal(new BearerAuth("{{token}}"), list.Auth);

        var create = pets.Requests[1];
        Assert.Equal("createPet", create.Name);
        Assert.Equal(RequestMethod.Post, create.Method);
        Assert.Equal(BodyType.Json, create.Body.Type);
        Assert.DoesNotContain("\"id\"", create.Body.Content);
        Assert.Contains("\"name\": \"Rex\"", create.Body.Content);
        Assert.Contains("\"email\": \"user@example.com\"", create.Body.Content);

        var delete = root.Requests.Single(r => r.Method == RequestMethod.Delete);
        Assert.Equal("{{baseUrl}}/pets/{{petId}}", delete.Url);
        Assert.Equal(RequestAuth.None, delete.Auth);
        var health = root.Requests.Single(r => r.Url.EndsWith("/health"));
        Assert.Equal(new ApiKeyAuth("X-API-Key", "{{apiKey}}"), health.Auth);

        Assert.Equal(["Pet Store (Production)", "Pet Store (Local)"], parsed.Collection.Environments.Select(e => e.Name));
        var production = parsed.Collection.Environments[0];
        Assert.Equal("https://eu.pets.test/v1", production.Variables[0].Value);
        Assert.Equal(["baseUrl", "token", "apiKey"], production.Variables.Select(v => v.Name));
        Assert.True(production.Variables[1].Secret);
    }

    [Fact]
    public void Swagger2_ReadsHostBodyAndFormParameters()
    {
        var parsed = Parse("""
            {
              "swagger": "2.0",
              "info": { "title": "Legacy", "version": "1" },
              "host": "legacy.test",
              "basePath": "/api",
              "schemes": ["http", "https"],
              "consumes": ["application/json"],
              "securityDefinitions": { "basic": { "type": "basic" } },
              "paths": {
                "/users": {
                  "post": {
                    "summary": "Create user",
                    "security": [{ "basic": [] }],
                    "parameters": [
                      { "name": "body", "in": "body", "schema": { "$ref": "#/definitions/User" } },
                      { "name": "X-Trace", "in": "header", "type": "string", "x-example": "abc" }
                    ]
                  }
                },
                "/login": {
                  "post": {
                    "summary": "Log in",
                    "consumes": ["application/x-www-form-urlencoded"],
                    "parameters": [
                      { "name": "user", "in": "formData", "type": "string", "required": true },
                      { "name": "remember", "in": "formData", "type": "boolean", "default": true }
                    ]
                  }
                }
              },
              "definitions": {
                "User": { "type": "object", "properties": { "name": { "type": "string" }, "age": { "type": "integer" } } }
              }
            }
            """);

        Assert.Equal("Swagger 2.0 description", parsed.Format);
        var requests = parsed.Collection.Root.Requests;
        var create = requests[0];
        Assert.Equal(new BasicAuth("{{username}}", "{{password}}"), create.Auth);
        Assert.Equal([new KeyValueEntry("X-Trace", "abc", false)], create.Headers);
        Assert.Equal(BodyType.Json, create.Body.Type);
        Assert.Contains("\"age\": 0", create.Body.Content);

        var login = requests[1];
        Assert.Equal(new RequestBody(BodyType.UrlEncoded, "user=\nremember=true"), login.Body);

        var environment = Assert.Single(parsed.Collection.Environments);
        Assert.Equal("https://legacy.test/api", environment.Variables[0].Value);
    }

    [Theory]
    [InlineData("{ \"hello\": \"world\" }")]
    [InlineData("just: yaml")]
    public void UnknownFormats_AreRefused(string text)
    {
        var result = _importer.Parse(text, "file.json");

        Assert.Equal(ErrorCodes.InvalidFile, result.Error?.Code);
        Assert.Contains("doesn't recognize", result.Error!.Message);
    }

    [Theory]
    [InlineData("{ \"info\": ")]
    [InlineData("a: [unclosed")]
    public void BrokenFiles_SayWhy(string text)
    {
        var result = _importer.Parse(text, "file.json");

        Assert.Equal(ErrorCodes.InvalidFile, result.Error?.Code);
    }
}
