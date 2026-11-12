// Copyright (c) Duncan Lawler. All Rights Reserved.
#include "TileCodec.h"
#include <wincodec.h>
#include <wrl/client.h>
#include <memory>
#include <new>
#include <vector>
#include <limits>

using Microsoft::WRL::ComPtr;

namespace
{
    struct Apartment
    {
        HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        ~Apartment() { if (SUCCEEDED(result)) CoUninitialize(); }
    };

    struct Source
    {
        Apartment apartment;
        ComPtr<IWICImagingFactory> factory;
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;
        ComPtr<IWICFormatConverter> converter;
    };

    struct Sink
    {
        Apartment apartment;
        ComPtr<IWICImagingFactory> factory;
        ComPtr<IWICStream> stream;
        ComPtr<IWICBitmapEncoder> encoder;
        ComPtr<IWICBitmapFrameEncode> frame;
        WICPixelFormatGUID pixelFormat{};
        uint32_t width = 0, height = 0, written = 0;
        bool committed = false;
        std::vector<uint8_t> converted;
    };

    HRESULT CreateFactory(Apartment& apartment, IWICImagingFactory** factory)
    {
        if (FAILED(apartment.result) && apartment.result != RPC_E_CHANGED_MODE)
            return apartment.result;
        return CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(factory));
    }

    bool ValidBuffer(uint32_t width, uint32_t rows, uint32_t stride, uint32_t capacity)
    {
        return width != 0 && rows != 0 &&
            static_cast<uint64_t>(width) * 4 <= stride &&
            static_cast<uint64_t>(rows - 1) * stride + static_cast<uint64_t>(width) * 4 <= capacity;
    }
}

#define CHECK_HR(expression) { const HRESULT hr = (expression); if (FAILED(hr)) return hr; }

HRESULT __cdecl tile_open_source(const wchar_t* path, void** handle, uint32_t* width, uint32_t* height)
{
    if (!path || !handle || !width || !height) return E_INVALIDARG;
    *handle = nullptr;
    try
    {
        auto source = std::make_unique<Source>();
        CHECK_HR(CreateFactory(source->apartment, &source->factory));
        CHECK_HR(source->factory->CreateDecoderFromFilename(path, nullptr, GENERIC_READ,
            WICDecodeMetadataCacheOnDemand, &source->decoder));
        CHECK_HR(source->decoder->GetFrame(0, &source->frame));
        CHECK_HR(source->frame->GetSize(width, height));
        CHECK_HR(source->factory->CreateFormatConverter(&source->converter));
        CHECK_HR(source->converter->Initialize(source->frame.Get(), GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));
        *handle = source.release();
        return S_OK;
    }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
}

HRESULT __cdecl tile_read(void* handle, int32_t x, int32_t y, int32_t width, int32_t height,
    uint32_t stride, uint32_t capacity, uint8_t* pixels)
{
    if (!handle || !pixels || x < 0 || y < 0 || width <= 0 || height <= 0 ||
        !ValidBuffer(width, height, stride, capacity)) return E_INVALIDARG;
    WICRect rectangle{ x, y, width, height };
    return static_cast<Source*>(handle)->converter->CopyPixels(&rectangle, stride, capacity, pixels);
}

void __cdecl tile_close_source(void* handle) { delete static_cast<Source*>(handle); }

HRESULT __cdecl tile_open_sink(const wchar_t* path, uint32_t format,
    uint32_t width, uint32_t height, void** handle)
{
    if (!path || !handle || width == 0 || height == 0) return E_INVALIDARG;
    *handle = nullptr;
    const GUID* container = nullptr;
    switch (format)
    {
    case 0:
    case 4: container = &GUID_ContainerFormatPng; break;
    case 1: container = &GUID_ContainerFormatJpeg; break;
    case 2: container = &GUID_ContainerFormatTiff; break;
    case 3: container = &GUID_ContainerFormatBmp; break;
    default: return E_INVALIDARG;
    }
    try
    {
        auto sink = std::make_unique<Sink>();
        CHECK_HR(CreateFactory(sink->apartment, &sink->factory));
        CHECK_HR(sink->factory->CreateStream(&sink->stream));
        CHECK_HR(sink->stream->InitializeFromFilename(path, GENERIC_WRITE));
        CHECK_HR(sink->factory->CreateEncoder(*container, nullptr, &sink->encoder));
        CHECK_HR(sink->encoder->Initialize(sink->stream.Get(), WICBitmapEncoderNoCache));
        ComPtr<IPropertyBag2> options;
        CHECK_HR(sink->encoder->CreateNewFrame(&sink->frame, &options));
        if (format == 4)
        {
            PROPBAG2 property{};
            property.pstrName = const_cast<wchar_t*>(L"InterlaceOption");
            VARIANT value{};
            value.vt = VT_BOOL;
            value.boolVal = VARIANT_TRUE;
            CHECK_HR(options->Write(1, &property, &value));
        }
        CHECK_HR(sink->frame->Initialize(options.Get()));
        CHECK_HR(sink->frame->SetSize(width, height));
        sink->pixelFormat = format == 1 ? GUID_WICPixelFormat24bppBGR : GUID_WICPixelFormat32bppBGRA;
        CHECK_HR(sink->frame->SetPixelFormat(&sink->pixelFormat));
        sink->width = width;
        sink->height = height;
        *handle = sink.release();
        return S_OK;
    }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
}

HRESULT __cdecl tile_write_rows(void* handle, uint32_t rows, uint32_t stride,
    uint32_t capacity, uint8_t* pixels)
{
    if (!handle || !pixels) return E_INVALIDARG;
    auto& sink = *static_cast<Sink*>(handle);
    if (sink.committed || rows > sink.height - sink.written ||
        !ValidBuffer(sink.width, rows, stride, capacity)) return E_INVALIDARG;
    try
    {
        ComPtr<IWICBitmap> bitmap;
        CHECK_HR(sink.factory->CreateBitmapFromMemory(sink.width, rows,
            GUID_WICPixelFormat32bppPBGRA, stride, capacity, pixels, &bitmap));
        ComPtr<IWICFormatConverter> converter;
        CHECK_HR(sink.factory->CreateFormatConverter(&converter));
        CHECK_HR(converter->Initialize(bitmap.Get(), sink.pixelFormat, WICBitmapDitherTypeNone,
            nullptr, 0, WICBitmapPaletteTypeCustom));
        ComPtr<IWICComponentInfo> component;
        CHECK_HR(sink.factory->CreateComponentInfo(sink.pixelFormat, &component));
        ComPtr<IWICPixelFormatInfo> info;
        CHECK_HR(component.As(&info));
        UINT bits;
        CHECK_HR(info->GetBitsPerPixel(&bits));
        const uint64_t rowBytes = (static_cast<uint64_t>(sink.width) * bits + 7) / 8;
        const uint64_t bytes = rowBytes * rows;
        if (rowBytes > UINT_MAX || bytes > UINT_MAX) return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        sink.converted.resize(static_cast<size_t>(bytes));
        CHECK_HR(converter->CopyPixels(nullptr, static_cast<UINT>(rowBytes),
            static_cast<UINT>(bytes), sink.converted.data()));
        CHECK_HR(sink.frame->WritePixels(rows, static_cast<UINT>(rowBytes),
            static_cast<UINT>(bytes), sink.converted.data()));
        sink.written += rows;
        return S_OK;
    }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
}

HRESULT __cdecl tile_commit(void* handle)
{
    if (!handle) return E_INVALIDARG;
    auto& sink = *static_cast<Sink*>(handle);
    if (sink.committed || sink.written != sink.height) return WINCODEC_ERR_WRONGSTATE;
    CHECK_HR(sink.frame->Commit());
    CHECK_HR(sink.encoder->Commit());
    CHECK_HR(sink.stream->Commit(STGC_DEFAULT));
    sink.committed = true;
    return S_OK;
}

void __cdecl tile_close_sink(void* handle) { delete static_cast<Sink*>(handle); }
