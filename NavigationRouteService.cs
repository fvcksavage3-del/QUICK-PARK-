using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuickPark;

internal sealed class NavigationRouteService
{
    private const string FunctionsRegion = "asia-southeast1";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FirebaseAuthService _authService = new();

    public async Task<NavigationRoute> GetDrivingRouteAsync(
        string reservationId,
        Location origin,
        AdminLocation destination,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
        {
            throw new NavigationRouteException("A confirmed booking is required to calculate a parking route.");
        }

        var projectId = FirebaseProjectConfig.Load().ProjectId;
        var endpoint = new Uri(
            $"https://{FunctionsRegion}-{projectId}.cloudfunctions.net/computeParkingRoute");
        var idToken = await _authService.GetValidIdTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                reservationId,
                origin = new { latitude = origin.Latitude, longitude = origin.Longitude },
            }, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return await GetOpenStreetMapRouteAsync(origin, destination, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NavigationRouteException(ReadServiceError(body, response.StatusCode));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("route", out var routeElement) ||
                routeElement.ValueKind != JsonValueKind.Object)
            {
                throw new NavigationRouteException("The routing service returned no route. Try again or check the parking entrance coordinates.");
            }

            return ParseRoute(routeElement);
        }
        catch (JsonException exception)
        {
            throw new NavigationRouteException("The routing service returned an unreadable route.", exception);
        }
    }

    private static async Task<NavigationRoute> GetOpenStreetMapRouteAsync(
        Location origin,
        AdminLocation destination,
        CancellationToken cancellationToken)
    {
        if (destination.Latitude is not { } latitude || destination.Longitude is not { } longitude)
        {
            throw new NavigationRouteException("The parking destination has no valid coordinates.");
        }

        var originLongitude = origin.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var originLatitude = origin.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var destinationLongitude = longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var destinationLatitude = latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var endpoint = new Uri(
            $"https://router.project-osrm.org/route/v1/driving/{originLongitude},{originLatitude};{destinationLongitude},{destinationLatitude}?overview=full&geometries=polyline&steps=true");

        using var response = await HttpClient.GetAsync(endpoint, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NavigationRouteException(
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "The public route service is busy. Try again shortly."
                    : "Could not load a road route. Check your internet connection and parking coordinates.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetString() != "Ok" ||
                !root.TryGetProperty("routes", out var routes) ||
                routes.ValueKind != JsonValueKind.Array ||
                routes.GetArrayLength() == 0)
            {
                throw new NavigationRouteException("No driving route was found to this parking entrance.");
            }

            var route = routes[0];
            var distance = (int)Math.Round(route.GetProperty("distance").GetDouble());
            var duration = route.GetProperty("duration").GetDouble();
            var encodedPolyline = route.GetProperty("geometry").GetString();
            if (distance <= 0 || duration <= 0 || string.IsNullOrWhiteSpace(encodedPolyline))
            {
                throw new NavigationRouteException("The route service returned incomplete directions.");
            }

            var routeSteps = new List<(string Instruction, int DistanceMeters, Location? ManeuverLocation)>();
            if (route.TryGetProperty("legs", out var legs) && legs.ValueKind == JsonValueKind.Array)
            {
                foreach (var leg in legs.EnumerateArray())
                {
                    if (!leg.TryGetProperty("steps", out var legSteps) || legSteps.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var step in legSteps.EnumerateArray())
                    {
                        if (!step.TryGetProperty("maneuver", out var maneuver))
                        {
                            continue;
                        }

                        var type = maneuver.TryGetProperty("type", out var typeElement)
                            ? typeElement.GetString() ?? string.Empty
                            : string.Empty;
                        var modifier = maneuver.TryGetProperty("modifier", out var modifierElement)
                            ? modifierElement.GetString() ?? string.Empty
                            : string.Empty;
                        var roadName = step.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString() ?? string.Empty
                            : string.Empty;
                        var instruction = FormatInstruction(type, modifier, roadName);
                        if (instruction.Length == 0)
                        {
                            continue;
                        }

                        var stepDistance = step.TryGetProperty("distance", out var stepDistanceElement)
                            ? (int)Math.Round(stepDistanceElement.GetDouble())
                            : 0;
                        routeSteps.Add((
                            instruction,
                            Math.Max(stepDistance, 0),
                            ReadManeuverLocation(maneuver)));
                    }
                }
            }

            var steps = new List<NavigationStep>(routeSteps.Count);
            Location? nextManeuver = new Location(latitude, longitude);
            for (var index = routeSteps.Count - 1; index >= 0; index--)
            {
                var routeStep = routeSteps[index];
                steps.Add(new NavigationStep(routeStep.Instruction, routeStep.DistanceMeters, nextManeuver));
                nextManeuver = routeStep.ManeuverLocation ?? nextManeuver;
            }
            steps.Reverse();

            return new NavigationRoute(
                distance,
                duration,
                DecodePolyline(encodedPolyline),
                steps,
                "Road route powered by OpenStreetMap. Internet is required for live routing.");
        }
        catch (JsonException exception)
        {
            throw new NavigationRouteException("The public routing service returned unreadable directions.", exception);
        }
    }

    private static string FormatInstruction(string type, string modifier, string roadName)
    {
        var road = string.IsNullOrWhiteSpace(roadName) ? string.Empty : $" onto {roadName}";
        return type switch
        {
            "depart" => $"Head{road}",
            "arrive" => "Arrive at the parking destination",
            "turn" => $"Turn {modifier}{road}",
            "new name" or "continue" => $"Continue{road}",
            "merge" => $"Merge {modifier}{road}",
            "on ramp" => $"Take the ramp{road}",
            "off ramp" => $"Take the exit{road}",
            "fork" => $"Keep {modifier}{road}",
            "end of road" => $"At the end of the road, turn {modifier}{road}",
            "roundabout" or "rotary" => $"Enter the roundabout{road}",
            "roundabout turn" => $"At the roundabout, take the {modifier} exit{road}",
            "notification" => string.IsNullOrWhiteSpace(roadName) ? string.Empty : $"Continue{road}",
            "use lane" => $"Use the {modifier} lane{road}",
            "uturn" => $"Make a U-turn{road}",
            _ => string.IsNullOrWhiteSpace(roadName) ? string.Empty : $"Continue{road}"
        };
    }

    private static NavigationRoute ParseRoute(JsonElement route)
    {
        var distance = route.GetProperty("distanceMeters").GetInt32();
        var duration = ParseDuration(route.GetProperty("duration").GetString());
        var encodedPolyline = route.GetProperty("encodedPolyline").GetString();
        if (distance <= 0 || duration <= 0 || string.IsNullOrWhiteSpace(encodedPolyline))
        {
            throw new NavigationRouteException("The routing service returned incomplete route data.");
        }

        var points = DecodePolyline(encodedPolyline);
        if (points.Count < 2)
        {
            throw new NavigationRouteException("The routing service returned an invalid route line.");
        }

        var steps = new List<NavigationStep>();
        if (route.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in stepsElement.EnumerateArray())
            {
                var instruction = step.TryGetProperty("instruction", out var instructionElement)
                    ? instructionElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    continue;
                }

                var stepDistance = step.TryGetProperty("distanceMeters", out var distanceElement)
                    ? distanceElement.GetInt32()
                    : 0;
                var end = ReadCoordinate(step, "endLocation");
                steps.Add(new NavigationStep(instruction, Math.Max(stepDistance, 0), end));
            }
        }

        var advisory = route.TryGetProperty("travelAdvisory", out var advisoryElement) &&
            advisoryElement.ValueKind == JsonValueKind.String
                ? advisoryElement.GetString()
                : null;
        return new NavigationRoute(distance, duration, points, steps, advisory);
    }

    private static Location? ReadCoordinate(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var coordinate) ||
            !coordinate.TryGetProperty("latitude", out var latitude) ||
            !coordinate.TryGetProperty("longitude", out var longitude))
        {
            return null;
        }

        return new Location(latitude.GetDouble(), longitude.GetDouble());
    }

    private static Location? ReadManeuverLocation(JsonElement maneuver)
    {
        if (!maneuver.TryGetProperty("location", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array ||
            coordinates.GetArrayLength() < 2 ||
            !coordinates[0].TryGetDouble(out var longitude) ||
            !coordinates[1].TryGetDouble(out var latitude) ||
            !double.IsFinite(latitude) ||
            !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            return null;
        }

        return new Location(latitude, longitude);
    }

    private static double ParseDuration(string? duration)
    {
        return duration is not null && duration.EndsWith('s') &&
            double.TryParse(duration.AsSpan(0, duration.Length - 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
                    ? seconds
                    : 0;
    }

    private static List<Location> DecodePolyline(string encodedPolyline)
    {
        var points = new List<Location>();
        var index = 0;
        var latitude = 0;
        var longitude = 0;

        while (index < encodedPolyline.Length)
        {
            latitude += DecodePolylineValue(encodedPolyline, ref index);
            longitude += DecodePolylineValue(encodedPolyline, ref index);
            points.Add(new Location(latitude / 1E5, longitude / 1E5));
        }

        return points;
    }

    private static int DecodePolylineValue(string encodedPolyline, ref int index)
    {
        var result = 0;
        var shift = 0;
        int chunk;
        do
        {
            if (index >= encodedPolyline.Length || shift > 30)
            {
                throw new NavigationRouteException("The routing service returned a malformed route line.");
            }

            chunk = encodedPolyline[index++] - 63;
            if (chunk is < 0 or > 63)
            {
                throw new NavigationRouteException("The routing service returned a malformed route line.");
            }
            result |= (chunk & 0x1f) << shift;
            shift += 5;
        } while (chunk >= 0x20);

        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }

    private static string ReadServiceError(string body, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString()))
            {
                return error.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Your sign-in expired. Sign in again to get a route.",
            System.Net.HttpStatusCode.NotFound => "The QuickPark routing service is not deployed yet.",
            System.Net.HttpStatusCode.ServiceUnavailable => "The routing service is not configured. Try again later.",
            System.Net.HttpStatusCode.BadGateway => "Google could not calculate a driving route for these coordinates.",
            _ => "Could not calculate a driving route. Check your connection and try again."
        };
    }
}

internal sealed record NavigationRoute(
    int DistanceMeters,
    double DurationSeconds,
    IReadOnlyList<Location> Points,
    IReadOnlyList<NavigationStep> Steps,
    string? TravelAdvisory);

internal sealed record NavigationStep(string Instruction, int DistanceMeters, Location? EndLocation);

internal sealed class NavigationRouteException(string message, Exception? innerException = null)
    : Exception(message, innerException);
