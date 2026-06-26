#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <numeric>
#include <string>
#include <thread>
#include <vector>

#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

#if defined(__linux__)
#include <pthread.h>
#endif

using Clock = std::chrono::steady_clock;

struct FeedMessage {
    std::uint64_t sequence;
    std::uint64_t sendTimeNs;
    std::uint32_t priceLevel;
    std::int32_t quantity;
    std::uint8_t side;
    std::uint8_t type;
    std::uint16_t reserved;
};

static_assert(sizeof(FeedMessage) == 32);

static std::uint64_t now_ns() {
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now().time_since_epoch()).count());
}

static void pin_current_thread(int cpu) {
    if (cpu < 0) {
        return;
    }
#if defined(__linux__)
    cpu_set_t set;
    CPU_ZERO(&set);
    CPU_SET(cpu, &set);
    pthread_setaffinity_np(pthread_self(), sizeof(set), &set);
#else
    (void)cpu;
#endif
}

template <std::size_t Capacity>
class SpscRing {
public:
    static_assert((Capacity & (Capacity - 1)) == 0, "capacity must be a power of two");

    bool try_push(const FeedMessage& message) {
        const auto head = head_.load(std::memory_order_relaxed);
        const auto next = (head + 1) & mask_;
        if (next == tail_.load(std::memory_order_acquire)) {
            return false;
        }

        buffer_[head] = message;
        head_.store(next, std::memory_order_release);
        return true;
    }

    bool try_pop(FeedMessage& message) {
        const auto tail = tail_.load(std::memory_order_relaxed);
        if (tail == head_.load(std::memory_order_acquire)) {
            return false;
        }

        message = buffer_[tail];
        tail_.store((tail + 1) & mask_, std::memory_order_release);
        return true;
    }

private:
    static constexpr std::size_t mask_ = Capacity - 1;
    alignas(64) std::array<FeedMessage, Capacity> buffer_{};
    alignas(64) std::atomic<std::size_t> head_{0};
    alignas(64) std::atomic<std::size_t> tail_{0};
};

class OrderBook {
public:
    void apply(const FeedMessage& message) {
        const auto index = message.priceLevel & (levels_.size() - 1);
        auto& side = message.side == 0 ? bids_ : asks_;
        if (message.type == 2) {
            side[index] = 0;
        } else {
            side[index] += message.quantity;
        }
    }

    std::uint64_t checksum() const {
        std::uint64_t value = 0;
        for (std::size_t i = 0; i < levels_.size(); ++i) {
            value += static_cast<std::uint64_t>((bids_[i] + asks_[i]) * static_cast<int>(i + 1));
        }
        return value;
    }

private:
    std::array<int, 4096> levels_{};
    std::array<int, 4096> bids_{};
    std::array<int, 4096> asks_{};
};

static FeedMessage make_message(std::uint64_t sequence) {
    FeedMessage message{};
    message.sequence = sequence;
    message.sendTimeNs = now_ns();
    message.priceLevel = static_cast<std::uint32_t>((sequence * 2654435761u) & 4095u);
    message.quantity = static_cast<std::int32_t>((sequence % 17) + 1);
    message.side = static_cast<std::uint8_t>(sequence & 1u);
    message.type = static_cast<std::uint8_t>(sequence % 23 == 0 ? 2 : 1);
    return message;
}

static double percentile(std::vector<double>& values, double p) {
    if (values.empty()) return 0.0;
    std::sort(values.begin(), values.end());
    const auto index = std::min(values.size() - 1, static_cast<std::size_t>(std::ceil(values.size() * p) - 1));
    return values[index];
}

static void print_report(
    const std::string& mode,
    int messages,
    int received,
    double elapsed_seconds,
    std::vector<double>& latencies_ns,
    std::uint64_t checksum) {
    auto latency_copy = latencies_ns;
    auto p50 = percentile(latency_copy, 0.50);
    latency_copy = latencies_ns;
    auto p95 = percentile(latency_copy, 0.95);
    latency_copy = latencies_ns;
    auto p99 = percentile(latency_copy, 0.99);
    const auto max_it = std::max_element(latencies_ns.begin(), latencies_ns.end());
    const auto max = max_it == latencies_ns.end() ? 0.0 : *max_it;
    std::cout << std::fixed << std::setprecision(2)
              << "{\n"
              << "  \"mode\": \"" << mode << "\",\n"
              << "  \"messages\": " << messages << ",\n"
              << "  \"received\": " << received << ",\n"
              << "  \"losses\": " << (messages - received) << ",\n"
              << "  \"elapsedSeconds\": " << elapsed_seconds << ",\n"
              << "  \"throughputMessagesPerSecond\": " << (received / elapsed_seconds) << ",\n"
              << "  \"latencyNanoseconds\": {\n"
              << "    \"p50\": " << p50 << ",\n"
              << "    \"p95\": " << p95 << ",\n"
              << "    \"p99\": " << p99 << ",\n"
              << "    \"max\": " << max << "\n"
              << "  },\n"
              << "  \"checksum\": " << checksum << "\n"
              << "}\n";
}

static int run_replay(int messages, int producer_cpu, int consumer_cpu) {
    SpscRing<1 << 10> ring;
    OrderBook book;
    std::vector<double> latencies;
    latencies.reserve(static_cast<std::size_t>(messages));
    std::atomic<bool> producer_done{false};
    const auto started = Clock::now();

    std::thread consumer([&] {
        pin_current_thread(consumer_cpu);
        FeedMessage message{};
        int received = 0;
        while (received < messages) {
            if (!ring.try_pop(message)) {
                if (producer_done.load(std::memory_order_acquire)) {
                    continue;
                }
                std::this_thread::yield();
                continue;
            }
            book.apply(message);
            latencies.push_back(static_cast<double>(now_ns() - message.sendTimeNs));
            received++;
        }
    });

    std::thread producer([&] {
        pin_current_thread(producer_cpu);
        for (int i = 0; i < messages; ++i) {
            auto message = make_message(static_cast<std::uint64_t>(i + 1));
            while (!ring.try_push(message)) {
                std::this_thread::yield();
            }
        }
        producer_done.store(true, std::memory_order_release);
    });

    producer.join();
    consumer.join();
    const auto elapsed = std::chrono::duration<double>(Clock::now() - started).count();
    print_report("spsc_replay", messages, static_cast<int>(latencies.size()), elapsed, latencies, book.checksum());
    return 0;
}

static int run_udp(int messages, int port, int sender_cpu, int receiver_cpu) {
    OrderBook book;
    std::vector<double> latencies;
    latencies.reserve(static_cast<std::size_t>(messages));
    std::atomic<bool> ready{false};

    std::thread receiver([&] {
        pin_current_thread(receiver_cpu);
        const int fd = socket(AF_INET, SOCK_DGRAM, 0);
        int buffer_bytes = 8 * 1024 * 1024;
        timeval timeout{};
        timeout.tv_sec = 2;
        setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &buffer_bytes, sizeof(buffer_bytes));
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(static_cast<std::uint16_t>(port));
        bind(fd, reinterpret_cast<sockaddr*>(&address), sizeof(address));
        ready.store(true, std::memory_order_release);

        FeedMessage message{};
        for (int i = 0; i < messages; ++i) {
            const auto n = recv(fd, &message, sizeof(message), 0);
            if (n != static_cast<ssize_t>(sizeof(message))) {
                break;
            }
            book.apply(message);
            latencies.push_back(static_cast<double>(now_ns() - message.sendTimeNs));
        }
        close(fd);
    });

    while (!ready.load(std::memory_order_acquire)) {
        std::this_thread::yield();
    }

    pin_current_thread(sender_cpu);
    const int fd = socket(AF_INET, SOCK_DGRAM, 0);
    int buffer_bytes = 8 * 1024 * 1024;
    setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &buffer_bytes, sizeof(buffer_bytes));
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(static_cast<std::uint16_t>(port));
    const auto started = Clock::now();
    for (int i = 0; i < messages; ++i) {
        auto message = make_message(static_cast<std::uint64_t>(i + 1));
        sendto(fd, &message, sizeof(message), 0, reinterpret_cast<sockaddr*>(&address), sizeof(address));
    }
    close(fd);
    receiver.join();
    const auto elapsed = std::chrono::duration<double>(Clock::now() - started).count();
    print_report("udp_loopback", messages, static_cast<int>(latencies.size()), elapsed, latencies, book.checksum());
    return 0;
}

static int run_tcp(int messages, int port, int sender_cpu, int receiver_cpu) {
    OrderBook book;
    std::vector<double> latencies;
    latencies.reserve(static_cast<std::size_t>(messages));
    std::atomic<bool> ready{false};

    std::thread receiver([&] {
        pin_current_thread(receiver_cpu);
        const int listen_fd = socket(AF_INET, SOCK_STREAM, 0);
        int one = 1;
        setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(static_cast<std::uint16_t>(port));
        bind(listen_fd, reinterpret_cast<sockaddr*>(&address), sizeof(address));
        listen(listen_fd, 1);
        ready.store(true, std::memory_order_release);
        const int fd = accept(listen_fd, nullptr, nullptr);
        FeedMessage message{};
        char* cursor = reinterpret_cast<char*>(&message);
        std::size_t filled = 0;
        while (static_cast<int>(latencies.size()) < messages) {
            const auto n = recv(fd, cursor + filled, sizeof(message) - filled, 0);
            if (n <= 0) {
                break;
            }
            filled += static_cast<std::size_t>(n);
            if (filled == sizeof(message)) {
                book.apply(message);
                latencies.push_back(static_cast<double>(now_ns() - message.sendTimeNs));
                filled = 0;
            }
        }
        close(fd);
        close(listen_fd);
    });

    while (!ready.load(std::memory_order_acquire)) {
        std::this_thread::yield();
    }

    pin_current_thread(sender_cpu);
    const int fd = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(static_cast<std::uint16_t>(port));
    connect(fd, reinterpret_cast<sockaddr*>(&address), sizeof(address));
    const auto started = Clock::now();
    for (int i = 0; i < messages; ++i) {
        auto message = make_message(static_cast<std::uint64_t>(i + 1));
        send(fd, &message, sizeof(message), 0);
    }
    shutdown(fd, SHUT_WR);
    close(fd);
    receiver.join();
    const auto elapsed = std::chrono::duration<double>(Clock::now() - started).count();
    print_report("tcp_loopback", messages, static_cast<int>(latencies.size()), elapsed, latencies, book.checksum());
    return 0;
}

int main(int argc, char** argv) {
    const std::string mode = argc > 1 ? argv[1] : "replay";
    const int messages = argc > 2 ? std::max(1, std::atoi(argv[2])) : 1000000;
    const int port = argc > 3 ? std::atoi(argv[3]) : 39091;
    const int producer_cpu = argc > 4 ? std::atoi(argv[4]) : -1;
    const int consumer_cpu = argc > 5 ? std::atoi(argv[5]) : -1;

    if (mode == "replay") {
        return run_replay(messages, producer_cpu, consumer_cpu);
    }
    if (mode == "udp") {
        return run_udp(messages, port, producer_cpu, consumer_cpu);
    }
    if (mode == "tcp") {
        return run_tcp(messages, port, producer_cpu, consumer_cpu);
    }

    std::cerr << "Usage: " << argv[0] << " replay|udp|tcp [messages] [port] [producer_cpu|-1] [consumer_cpu|-1]\n";
    return 2;
}
