// Copyright (c) Duncan Lawler. All Rights Reserved.
#include "TileCodec.h"
#include <psapi.h>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cwchar>
#include <thread>
#include <vector>
#include <chrono>

static uint64_t PrivateBytes()
{
    PROCESS_MEMORY_COUNTERS_EX counters{};
    if (!GetProcessMemoryInfo(GetCurrentProcess(),
        reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters))) return 0;
    return counters.PrivateUsage;
}

static void RecordPeak(std::atomic<uint64_t>& peak)
{
    const auto value = PrivateBytes();
    auto previous = peak.load();
    while (previous < value && !peak.compare_exchange_weak(previous, value)) { }
}

// Separate process per operation: codec-internal allocations are not hidden by CLR/GPU caches.
int wmain(int argc, wchar_t** argv)
{
    if (argc != 6) return 2;
    const bool decode = std::wcsncmp(argv[1], L"decode", 6) == 0;
    const bool tiled = std::wcscmp(argv[1], L"decode-tiles") == 0;
    const bool reverse = std::wcscmp(argv[1], L"decode-reverse") == 0;
    const auto width = static_cast<uint32_t>(std::wcstoul(argv[3], nullptr, 10));
    const auto height = static_cast<uint32_t>(std::wcstoul(argv[4], nullptr, 10));
    const auto format = static_cast<uint32_t>(std::wcstoul(argv[5], nullptr, 10));
    if (width == 0 || height == 0 || static_cast<uint64_t>(width) * 4 > UINT_MAX) return 2;
    const uint32_t stride = (tiled || reverse ? std::min(width, 256u) : width) * 4;
    const uint32_t rows = std::max(1u, std::min(height, 262144u / stride));
    std::vector<uint8_t> pixels(static_cast<size_t>(stride) * rows, 255);
    for (size_t i = 0; i < pixels.size(); i += 4)
    {
        pixels[i] = static_cast<uint8_t>(i * 13);
        pixels[i + 1] = static_cast<uint8_t>(i * 7);
        pixels[i + 2] = static_cast<uint8_t>(i * 3);
    }
    const auto baseline = PrivateBytes();
    std::atomic<uint64_t> peak{ baseline };
    std::atomic<bool> stop{ false };
    std::thread sampler([&]
    {
        while (!stop)
        {
            RecordPeak(peak);
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    });
    const auto start = std::chrono::steady_clock::now();
    double longestCallMilliseconds = 0;
    auto measure = [&](auto call)
    {
        const auto callStart = std::chrono::steady_clock::now();
        const auto result = call();
        longestCallMilliseconds = std::max(longestCallMilliseconds,
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - callStart).count());
        return result;
    };
    void* handle = nullptr;
    uint32_t actualWidth = width, actualHeight = height;
    HRESULT result = measure([&]
    {
        return decode ? tile_open_source(argv[2], &handle, &actualWidth, &actualHeight)
            : tile_open_sink(argv[2], format, width, height, &handle);
    });
    const auto opened = PrivateBytes();
    RecordPeak(peak);
    if (SUCCEEDED(result) && (actualWidth != width || actualHeight != height)) result = E_INVALIDARG;
    if (tiled || reverse)
    {
        const uint32_t edge = 256;
        // Tile traversal can force codecs to revisit rows. Measure it, do not infer caching
        // behavior from CopyPixels returning only a small caller buffer.
        for (uint32_t row = 0; row < height && SUCCEEDED(result); row += edge)
        {
            const auto y = reverse ? height - std::min(height, row + edge) : row;
            const auto count = std::min(edge, height - y);
            for (uint32_t x = 0; x < width && SUCCEEDED(result); x += edge)
            {
                const auto columns = std::min(edge, width - x);
                result = measure([&]
                {
                    return tile_read(handle, x, y, columns, count, columns * 4,
                        static_cast<uint32_t>(pixels.size()), pixels.data());
                });
                RecordPeak(peak);
            }
        }
    }
    else for (uint32_t y = 0; y < height && SUCCEEDED(result); y += rows)
    {
        const auto count = std::min(rows, height - y);
        result = measure([&]
        {
            return decode
                ? tile_read(handle, 0, y, width, count, stride, static_cast<uint32_t>(pixels.size()), pixels.data())
                : tile_write_rows(handle, count, stride, static_cast<uint32_t>(pixels.size()), pixels.data());
        });
        RecordPeak(peak);
    }
    const auto processed = PrivateBytes();
    if (!decode && SUCCEEDED(result)) result = measure([&] { return tile_commit(handle); });
    const auto committed = PrivateBytes();
    RecordPeak(peak);
    if (decode) tile_close_source(handle); else tile_close_sink(handle);
    stop = true;
    sampler.join();
    PROCESS_MEMORY_COUNTERS_EX counters{};
    const bool memoryMeasured = GetProcessMemoryInfo(GetCurrentProcess(),
        reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters)) != FALSE;
    const double milliseconds = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - start).count();
    std::printf("{\"hresult\":%ld,\"baseline\":%llu,\"opened\":%llu,\"processed\":%llu,"
        "\"committed\":%llu,\"peak\":%llu,\"buffer\":%zu,\"milliseconds\":%.2f,"
        "\"longestCallMilliseconds\":%.2f,\"peakWorkingSet\":%zu}\n",
        static_cast<long>(result), baseline, opened, processed, committed,
        peak.load(), pixels.size(), milliseconds, longestCallMilliseconds, counters.PeakWorkingSetSize);
    return FAILED(result) || !memoryMeasured || baseline == 0 ? 1 : 0;
}
