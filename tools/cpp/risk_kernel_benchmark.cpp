#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iomanip>
#include <iostream>
#include <random>
#include <vector>

struct Point {
    double lat;
    double lon;
};

static inline double equirectangular_distance(double lat1, double lon1, double lat2, double lon2) {
    constexpr double earth_radius_metres = 6371000.0;
    constexpr double deg_to_rad = 3.14159265358979323846 / 180.0;
    const double lat1_rad = lat1 * deg_to_rad;
    const double lat2_rad = lat2 * deg_to_rad;
    const double x = (lon2 - lon1) * deg_to_rad * std::cos((lat1_rad + lat2_rad) * 0.5);
    const double y = (lat2 - lat1) * deg_to_rad;
    return std::sqrt(x * x + y * y) * earth_radius_metres;
}

static inline double dense_grid_lookup(const std::vector<double>& grid, double lat, double lon) {
    constexpr double min_lat = 52.38;
    constexpr double min_lon = -2.02;
    constexpr double inv_cell = 1.0 / 0.0009;
    const auto x = static_cast<int>((lon - min_lon) * inv_cell);
    const auto y = static_cast<int>((lat - min_lat) * inv_cell);
    const auto index = static_cast<std::uint32_t>(x * 73856093 ^ y * 19349663) % grid.size();
    return grid[index];
}

static double percentile(std::vector<double>& values, double p) {
    if (values.empty()) return 0.0;
    std::sort(values.begin(), values.end());
    const auto index = std::min(values.size() - 1, static_cast<std::size_t>(std::ceil(values.size() * p) - 1));
    return values[index];
}

int main(int argc, char** argv) {
    int queries = argc > 1 ? std::atoi(argv[1]) : 1000000;
    int batch_size = argc > 2 ? std::atoi(argv[2]) : 256;
    int grid_cells = argc > 3 ? std::atoi(argv[3]) : 262144;
    queries = std::max(1, queries);
    batch_size = std::max(1, batch_size);
    grid_cells = std::max(1024, grid_cells);

    std::mt19937_64 rng(42);
    std::uniform_real_distribution<double> lat_dist(52.38, 52.60);
    std::uniform_real_distribution<double> lon_dist(-2.02, -1.72);

    std::vector<Point> points;
    points.reserve(static_cast<std::size_t>(queries));
    for (int i = 0; i < queries; ++i) {
        points.push_back({lat_dist(rng), lon_dist(rng)});
    }

    std::vector<double> grid(static_cast<std::size_t>(grid_cells));
    for (int i = 0; i < grid_cells; ++i) {
        grid[static_cast<std::size_t>(i)] = static_cast<double>((static_cast<std::uint32_t>(i) * 1103515245u + 12345u) % 1000u) / 1000.0;
    }

    volatile double warmup = 0.0;
    for (int i = 0; i < std::min(queries, 10000); ++i) {
        const auto& a = points[static_cast<std::size_t>(i)];
        const auto& b = points[static_cast<std::size_t>((i + 97) % queries)];
        warmup += equirectangular_distance(a.lat, a.lon, b.lat, b.lon);
    }

    std::vector<double> latency_us;
    latency_us.reserve(static_cast<std::size_t>((queries + batch_size - 1) / batch_size));
    auto total_start = std::chrono::steady_clock::now();
    volatile double checksum = 0.0;
    for (int batch_start = 0; batch_start < queries; batch_start += batch_size) {
        const int batch_end = std::min(queries, batch_start + batch_size);
        auto start = std::chrono::steady_clock::now();
        for (int i = batch_start; i < batch_end; ++i) {
            const auto& a = points[static_cast<std::size_t>(i)];
            const auto& b = points[static_cast<std::size_t>((i + 97) % queries)];
            checksum += equirectangular_distance(a.lat, a.lon, b.lat, b.lon);
        }
        auto end = std::chrono::steady_clock::now();
        const double elapsed_us = std::chrono::duration<double, std::micro>(end - start).count();
        latency_us.push_back(elapsed_us / static_cast<double>(batch_end - batch_start));
    }
    auto total_end = std::chrono::steady_clock::now();
    std::vector<double> grid_latency_us;
    grid_latency_us.reserve(static_cast<std::size_t>((queries + batch_size - 1) / batch_size));
    auto grid_total_start = std::chrono::steady_clock::now();
    volatile double grid_checksum = 0.0;
    for (int batch_start = 0; batch_start < queries; batch_start += batch_size) {
        const int batch_end = std::min(queries, batch_start + batch_size);
        auto start = std::chrono::steady_clock::now();
        for (int i = batch_start; i < batch_end; ++i) {
            const auto& p = points[static_cast<std::size_t>(i)];
            grid_checksum += dense_grid_lookup(grid, p.lat, p.lon);
        }
        auto end = std::chrono::steady_clock::now();
        const double elapsed_us = std::chrono::duration<double, std::micro>(end - start).count();
        grid_latency_us.push_back(elapsed_us / static_cast<double>(batch_end - batch_start));
    }
    auto grid_total_end = std::chrono::steady_clock::now();

    const double elapsed_sec = std::chrono::duration<double>(total_end - total_start).count();
    const double grid_elapsed_sec = std::chrono::duration<double>(grid_total_end - grid_total_start).count();
    auto values = latency_us;
    auto grid_values = grid_latency_us;
    std::cout << std::fixed << std::setprecision(4)
              << "{\n"
              << "  \"kernel\": \"cpp-equirectangular-distance\",\n"
              << "  \"queries\": " << queries << ",\n"
              << "  \"batchSize\": " << batch_size << ",\n"
              << "  \"gridCells\": " << grid_cells << ",\n"
              << "  \"throughputOpsPerSecond\": " << (queries / elapsed_sec) << ",\n"
              << "  \"p50Microseconds\": " << percentile(values, 0.50) << ",\n"
              << "  \"p95Microseconds\": " << percentile(values, 0.95) << ",\n"
              << "  \"p99Microseconds\": " << percentile(values, 0.99) << ",\n"
              << "  \"denseGridLookup\": {\n"
              << "    \"throughputOpsPerSecond\": " << (queries / grid_elapsed_sec) << ",\n"
              << "    \"p50Microseconds\": " << percentile(grid_values, 0.50) << ",\n"
              << "    \"p95Microseconds\": " << percentile(grid_values, 0.95) << ",\n"
              << "    \"p99Microseconds\": " << percentile(grid_values, 0.99) << ",\n"
              << "    \"checksum\": " << grid_checksum << "\n"
              << "  },\n"
              << "  \"checksum\": " << checksum << "\n"
              << "}\n";
}
