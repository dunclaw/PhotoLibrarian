// Copyright (c) Duncan Lawler. All Rights Reserved.
#pragma once
#include <Windows.h>
#include <cstdint>

#ifdef TILE_CODEC_EXPORTS
#define TILE_CODEC_API __declspec(dllexport)
#else
#define TILE_CODEC_API __declspec(dllimport)
#endif

// Test-only, synchronous, apartment-affine WIC boundary. Pixels are premultiplied BGRA8.
extern "C"
{
    TILE_CODEC_API HRESULT __cdecl tile_open_source(
        const wchar_t* path, void** handle, uint32_t* width, uint32_t* height);
    TILE_CODEC_API HRESULT __cdecl tile_read(
        void* handle, int32_t x, int32_t y, int32_t width, int32_t height,
        uint32_t stride, uint32_t capacity, uint8_t* pixels);
    TILE_CODEC_API void __cdecl tile_close_source(void* handle);
    TILE_CODEC_API HRESULT __cdecl tile_open_sink(
        const wchar_t* path, uint32_t format, uint32_t width, uint32_t height, void** handle);
    TILE_CODEC_API HRESULT __cdecl tile_write_rows(
        void* handle, uint32_t rows, uint32_t stride, uint32_t capacity, uint8_t* pixels);
    TILE_CODEC_API HRESULT __cdecl tile_commit(void* handle);
    TILE_CODEC_API void __cdecl tile_close_sink(void* handle);
}
