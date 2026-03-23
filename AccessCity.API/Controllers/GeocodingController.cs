using System.Globalization;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace AccessCity.API.Controllers
{
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/geocoding")]
    public class GeocodingController : ControllerBase
    {
        private static readonly TimeSpan GeocodeCacheTtl = TimeSpan.FromMinutes(10);
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<GeocodingController> _logger;

        public GeocodingController(
            IHttpClientFactory httpClientFactory,
            IMemoryCache memoryCache,
            ILogger<GeocodingController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _memoryCache = memoryCache;
            _logger = logger;
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(new { error = "Query is required" });

            var cacheKey = $"geocoding:search:{query.Trim().ToLowerInvariant()}";
            if (_memoryCache.TryGetValue(cacheKey, out List<NominatimResult>? cached) && cached is not null)
                return Ok(cached);

            var client = _httpClientFactory.CreateClient("Nominatim");
            var url = $"search?q={Uri.EscapeDataString(query)}&format=json&limit=5";

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Nominatim search non-success {Status} for query length {Len} (attempt {Attempt})",
                            (int)response.StatusCode,
                            query.Length,
                            attempt + 1);
                        if (attempt == 0)
                        {
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        return StatusCode(
                            StatusCodes.Status503ServiceUnavailable,
                            new
                            {
                                error = "Nominatim search failed",
                                upstreamStatus = (int)response.StatusCode,
                                upstream = "nominatim",
                                detail = body.Length > 400 ? body[..400] + "…" : body
                            });
                    }

                    List<NominatimResult>? results;
                    try
                    {
                        results = JsonSerializer.Deserialize<List<NominatimResult>>(body);
                    }
                    catch (JsonException jx)
                    {
                        _logger.LogWarning(jx, "Nominatim search JSON parse failed");
                        return StatusCode(
                            StatusCodes.Status503ServiceUnavailable,
                            new { error = "Nominatim returned unparsable JSON", reason = jx.Message });
                    }

                    results ??= new List<NominatimResult>();
                    _memoryCache.Set(cacheKey, results, GeocodeCacheTtl);
                    return Ok(results);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Nominatim search timed out (attempt {Attempt})", attempt + 1);
                    if (attempt == 0)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return StatusCode(
                        StatusCodes.Status503ServiceUnavailable,
                        new { error = "Nominatim search timed out", upstream = "timeout" });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Nominatim search error (attempt {Attempt})", attempt + 1);
                    if (attempt == 0)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return StatusCode(
                        StatusCodes.Status503ServiceUnavailable,
                        new { error = "Geocoding search failed", reason = ex.Message });
                }
            }

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Nominatim search exhausted retries" });
        }

        [HttpGet("reverse")]
        public async Task<IActionResult> Reverse([FromQuery] double lat, [FromQuery] double lon, CancellationToken cancellationToken)
        {
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            {
                return BadRequest(new
                {
                    error = "Invalid WGS-84 coordinates (lat in [-90,90], lon in [-180,180]).",
                    lat,
                    lon
                });
            }

            var cacheKey = $"geocoding:reverse:{lat:F4}:{lon:F4}";
            if (_memoryCache.TryGetValue(cacheKey, out NominatimResult? cached) && cached is not null)
                return Ok(cached);

            var client = _httpClientFactory.CreateClient("Nominatim");
            var url = $"reverse?lat={lat.ToString(CultureInfo.InvariantCulture)}&lon={lon.ToString(CultureInfo.InvariantCulture)}&format=json";

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Nominatim reverse non-success {Status} for ({Lat},{Lon}) (attempt {Attempt})",
                            (int)response.StatusCode,
                            lat,
                            lon,
                            attempt + 1);
                        if (attempt == 0)
                        {
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        return StatusCode(
                            StatusCodes.Status503ServiceUnavailable,
                            new
                            {
                                error = "Nominatim reverse geocoding failed",
                                upstreamStatus = (int)response.StatusCode,
                                lat,
                                lon,
                                upstream = "nominatim",
                                detail = body.Length > 400 ? body[..400] + "…" : body
                            });
                    }

                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var errProp))
                    {
                        var msg = errProp.GetString() ?? "unknown";
                        _logger.LogWarning("Nominatim reverse logical error for ({Lat},{Lon}): {Msg}", lat, lon, msg);
                        return StatusCode(
                            StatusCodes.Status503ServiceUnavailable,
                            new
                            {
                                error = "Nominatim could not reverse this location",
                                reason = msg,
                                lat,
                                lon,
                                upstream = "nominatim"
                            });
                    }

                    NominatimResult? result;
                    try
                    {
                        result = JsonSerializer.Deserialize<NominatimResult>(body);
                    }
                    catch (JsonException jx)
                    {
                        _logger.LogWarning(jx, "Nominatim reverse JSON parse failed for ({Lat},{Lon})", lat, lon);
                        return StatusCode(
                            StatusCodes.Status503ServiceUnavailable,
                            new { error = "Nominatim returned unparsable JSON", reason = jx.Message, lat, lon });
                    }

                    if (result is null)
                    {
                        return StatusCode(
                            StatusCodes.Status503ServiceUnavailable,
                            new { error = "Empty reverse geocoding result", lat, lon });
                    }

                    _memoryCache.Set(cacheKey, result, GeocodeCacheTtl);
                    return Ok(result);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Nominatim reverse timed out (attempt {Attempt}) for ({Lat},{Lon})", attempt + 1, lat, lon);
                    if (attempt == 0)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return StatusCode(
                        StatusCodes.Status503ServiceUnavailable,
                        new { error = "Nominatim reverse timed out", lat, lon, upstream = "timeout" });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Nominatim reverse error (attempt {Attempt})", attempt + 1);
                    if (attempt == 0)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return StatusCode(
                        StatusCodes.Status503ServiceUnavailable,
                        new { error = "Reverse geocoding failed", reason = ex.Message, lat, lon });
                }
            }

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Nominatim reverse exhausted retries", lat, lon });
        }
    }

    public class NominatimResult
    {
        public long place_id { get; set; }
        public string licence { get; set; } = string.Empty;
        public string osm_type { get; set; } = string.Empty;
        public long osm_id { get; set; }
        public List<string> boundingbox { get; set; } = new();
        public string lat { get; set; } = string.Empty;
        public string lon { get; set; } = string.Empty;
        public string display_name { get; set; } = string.Empty;
        public string @class { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public double importance { get; set; }
    }
}
