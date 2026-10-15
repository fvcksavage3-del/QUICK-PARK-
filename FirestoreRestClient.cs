using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuickPark;

internal sealed class FirestoreRestClient
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly HashSet<string> TimestampFields =
    [
        "createdAt", "updatedAt", "startTime", "endTime", "cancelledAt",
        "completedAt", "expiredAt", "confirmedAt"
    ];

    private readonly FirebaseAuthService _authService = new();
    private readonly string _documentsUrl;
    private readonly string _documentsResourceName;

    public FirestoreRestClient()
    {
        var projectId = FirebaseProjectConfig.Load().ProjectId;
        _documentsUrl =
            $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents";
        _documentsResourceName = $"projects/{projectId}/databases/(default)/documents";
    }

    public async Task<List<JsonObject>> GetCollectionAsync(
        string collection,
        string? equalityField = null,
        string? equalityValue = null,
        int limit = 500,
        CancellationToken cancellationToken = default,
        string? orderByField = null,
        bool descendingOrder = false)
    {
        var structuredQuery = new JsonObject
        {
            ["from"] = new JsonArray(new JsonObject { ["collectionId"] = collection }),
            ["limit"] = limit
        };
        if (equalityField is not null)
        {
            structuredQuery["where"] = new JsonObject
            {
                ["fieldFilter"] = new JsonObject
                {
                    ["field"] = new JsonObject { ["fieldPath"] = equalityField },
                    ["op"] = "EQUAL",
                    ["value"] = new JsonObject { ["stringValue"] = equalityValue }
                }
            };
        }
        if (orderByField is not null)
        {
            structuredQuery["orderBy"] = new JsonArray(new JsonObject
            {
                ["field"] = new JsonObject { ["fieldPath"] = orderByField },
                ["direction"] = descendingOrder ? "DESCENDING" : "ASCENDING"
            });
        }

        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"{_documentsUrl}:runQuery",
            JsonContent.Create(new JsonObject { ["structuredQuery"] = structuredQuery }),
            cancellationToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response.StatusCode, body, $"read the {collection} collection");

        using var document = JsonDocument.Parse(body);
        var results = new List<JsonObject>();
        foreach (var result in document.RootElement.EnumerateArray())
        {
            if (result.TryGetProperty("document", out var firestoreDocument))
            {
                results.Add(DecodeDocument(firestoreDocument));
            }
        }
        return results;
    }

    public async Task<JsonObject?> GetDocumentAsync(
        string collection,
        string id,
        CancellationToken cancellationToken = default)
    {
        var documentId = NormalizeDocumentId(collection, id);
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            $"{_documentsUrl}/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(documentId)}",
            null,
            cancellationToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        EnsureSuccess(response.StatusCode, body, $"read the {collection} document");
        using var document = JsonDocument.Parse(body);
        return DecodeDocument(document.RootElement);
    }

    public JsonObject BuildUpdateWrite(
        string collection,
        string id,
        object values,
        bool createOnly = false,
        string? updateTime = null,
        IEnumerable<string>? additionalMaskFields = null)
    {
        var documentId = NormalizeDocumentId(collection, id);
        var fields = EncodeFields(values);
        var fieldPaths = fields.Select(pair => pair.Key)
            .Concat(additionalMaskFields ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var write = new JsonObject
        {
            ["update"] = new JsonObject
            {
                ["name"] = $"{_documentsResourceName}/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(documentId)}",
                ["fields"] = fields
            },
            ["updateMask"] = new JsonObject
            {
                ["fieldPaths"] = new JsonArray(fieldPaths.Select(field => (JsonNode?)JsonValue.Create(field)).ToArray())
            }
        };
        if (createOnly)
        {
            write["currentDocument"] = new JsonObject { ["exists"] = false };
        }
        else if (updateTime is not null)
        {
            write["currentDocument"] = new JsonObject { ["updateTime"] = updateTime };
        }
        return write;
    }

    public JsonObject BuildDeleteWrite(string collection, string id, string? updateTime = null)
    {
        var documentId = NormalizeDocumentId(collection, id);
        var write = new JsonObject
        {
            ["delete"] = $"{_documentsResourceName}/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(documentId)}"
        };
        if (updateTime is not null)
        {
            write["currentDocument"] = new JsonObject { ["updateTime"] = updateTime };
        }
        return write;
    }

    public JsonObject CreateNotificationWrite(
        string userId,
        string title,
        string message,
        string type,
        DateTime createdAt)
    {
        return BuildUpdateWrite("notifications", Guid.NewGuid().ToString("N"), new
        {
            userId,
            title,
            message,
            type,
            read = false,
            createdAt
        }, createOnly: true);
    }

    public async Task CommitAsync(
        IEnumerable<JsonObject> writes,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"{_documentsUrl}:commit",
            JsonContent.Create(new JsonObject
            {
                ["writes"] = new JsonArray(writes.Select(write => write.DeepClone()).ToArray())
            }),
            cancellationToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response.StatusCode, body, "save the Firestore changes");
    }

    public static T Deserialize<T>(JsonObject document)
    {
        return document.Deserialize<T>(JsonOptions)
            ?? throw new InvalidOperationException("Firestore returned an empty document.");
    }

    public static JsonElement ToJsonElement(JsonObject document)
    {
        using var parsed = JsonDocument.Parse(document.ToJsonString());
        return parsed.RootElement.Clone();
    }

    internal static string NormalizeDocumentId(string collection, string id)
    {
        var value = id.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal) &&
            value.EndsWith(")", StringComparison.Ordinal))
        {
            var linkStart = value.IndexOf("](", StringComparison.Ordinal);
            if (linkStart > 0)
            {
                value = value[(linkStart + 2)..^1];
            }
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https"))
            {
                throw InvalidDocumentId(collection);
            }

            value = uri.AbsolutePath;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        var documentsIndex = Array.IndexOf(segments, "documents");
        if (documentsIndex >= 0)
        {
            if (documentsIndex + 2 >= segments.Length ||
                !string.Equals(segments[documentsIndex + 1], collection, StringComparison.Ordinal) ||
                documentsIndex + 3 != segments.Length)
            {
                throw InvalidDocumentId(collection);
            }

            value = segments[documentsIndex + 2];
        }
        else if (segments.Length == 1)
        {
            value = segments[0];
        }
        else
        {
            throw InvalidDocumentId(collection);
        }

        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.Contains("/", StringComparison.Ordinal))
        {
            throw InvalidDocumentId(collection);
        }

        return value;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string uri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var token = await _authService.GetValidIdTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static JsonObject DecodeDocument(JsonElement document)
    {
        var documentName = document.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Firestore returned a document without a name.");
        var result = new JsonObject
        {
            ["id"] = documentName.Split('/').Last()
        };
        if (document.TryGetProperty("updateTime", out var updateTime))
        {
            result["__updateTime"] = updateTime.GetString();
        }
        if (!document.TryGetProperty("fields", out var fields))
        {
            return result;
        }

        foreach (var field in fields.EnumerateObject())
        {
            if (string.Equals(field.Name, "id", StringComparison.Ordinal))
            {
                continue;
            }
            result[field.Name] = DecodeValue(field.Value);
        }
        return result;
    }

    private static InvalidOperationException InvalidDocumentId(string collection)
    {
        return new InvalidOperationException(
            $"The {collection} document ID is invalid. Use the document ID or a Firestore document URL for that collection.");
    }

    private static JsonNode? DecodeValue(JsonElement value)
    {
        if (value.TryGetProperty("nullValue", out _))
        {
            return null;
        }
        if (value.TryGetProperty("stringValue", out var stringValue))
        {
            return JsonValue.Create(stringValue.GetString());
        }
        if (value.TryGetProperty("timestampValue", out var timestamp))
        {
            return JsonValue.Create(timestamp.GetString());
        }
        if (value.TryGetProperty("booleanValue", out var boolean))
        {
            return JsonValue.Create(boolean.GetBoolean());
        }
        if (value.TryGetProperty("integerValue", out var integer))
        {
            return JsonValue.Create(long.Parse(integer.GetString()!, CultureInfo.InvariantCulture));
        }
        if (value.TryGetProperty("doubleValue", out var number))
        {
            return JsonValue.Create(number.GetDouble());
        }
        if (value.TryGetProperty("arrayValue", out var array))
        {
            var result = new JsonArray();
            if (array.TryGetProperty("values", out var values))
            {
                foreach (var item in values.EnumerateArray())
                {
                    result.Add(DecodeValue(item));
                }
            }
            return result;
        }
        if (value.TryGetProperty("mapValue", out var map) &&
            map.TryGetProperty("fields", out var mapFields))
        {
            var result = new JsonObject();
            foreach (var field in mapFields.EnumerateObject())
            {
                result[field.Name] = DecodeValue(field.Value);
            }
            return result;
        }
        throw new InvalidOperationException("Firestore returned an unsupported field type.");
    }

    private static JsonObject EncodeFields(object values)
    {
        var source = JsonSerializer.SerializeToElement(values);
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Firestore document data must be an object.");
        }

        var result = new JsonObject();
        foreach (var field in source.EnumerateObject())
        {
            result[field.Name] = EncodeValue(field.Value, field.Name);
        }
        return result;
    }

    private static JsonObject EncodeValue(JsonElement value, string fieldName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString()!;
                if (TimestampFields.Contains(fieldName) &&
                    DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                {
                    return new JsonObject { ["timestampValue"] = timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) };
                }
                return new JsonObject { ["stringValue"] = text };
            case JsonValueKind.Number:
                return value.TryGetInt64(out var integer)
                    ? new JsonObject { ["integerValue"] = integer.ToString(CultureInfo.InvariantCulture) }
                    : new JsonObject { ["doubleValue"] = value.GetDouble() };
            case JsonValueKind.True:
            case JsonValueKind.False:
                return new JsonObject { ["booleanValue"] = value.GetBoolean() };
            case JsonValueKind.Null:
                return new JsonObject { ["nullValue"] = null };
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    array.Add(EncodeValue(item, fieldName));
                }
                return new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = array } };
            case JsonValueKind.Object:
                var map = new JsonObject();
                foreach (var field in value.EnumerateObject())
                {
                    map[field.Name] = EncodeValue(field.Value, field.Name);
                }
                return new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = map } };
            default:
                throw new InvalidOperationException("The Firestore value type is unsupported.");
        }
    }

    private static void EnsureSuccess(HttpStatusCode statusCode, string responseBody, string operation)
    {
        if ((int)statusCode is >= 200 and < 300)
        {
            return;
        }

        string? serverMessage = null;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var details))
            {
                serverMessage = details.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON error bodies fall back to a generic message.
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Your Firebase session expired. Sign in again.");
        }
        if (statusCode == HttpStatusCode.Forbidden)
        {
            throw new FirestorePermissionException(
                operation,
                $"Firestore denied permission to {operation}{(string.IsNullOrWhiteSpace(serverMessage) ? "." : $": {serverMessage}.")} " +
                (operation.Contains("userProfiles", StringComparison.Ordinal)
                    ? "The current Firestore rules must allow an authenticated user to read or create only their own userProfiles/{uid} document."
                    : operation.Contains("locations", StringComparison.Ordinal)
                        ? "Parking locations should be readable by active signed-in users. Verify that the current Firestore rules are deployed."
                        : "Verify that the current Firestore rules permit this signed-in user to access only their own data."));
        }
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(serverMessage)
                ? "The Firebase Firestore request failed. Please try again."
                : serverMessage);
    }
}

internal sealed class FirestorePermissionException(string operation, string message)
    : InvalidOperationException(message)
{
    public string Operation { get; } = operation;
}
