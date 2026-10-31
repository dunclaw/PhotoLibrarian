#pragma once

#include <cstddef>
#include <cstdint>

#ifdef _WIN32
#ifdef PHOTO_LIBRARIAN_RAWDECODER_EXPORTS
#define RAWDECODER_API __declspec(dllexport)
#else
#define RAWDECODER_API __declspec(dllimport)
#endif
#define RAWDECODER_CALL __cdecl
#else
#define RAWDECODER_API
#define RAWDECODER_CALL
#endif

struct RawDecoderMetadata
{
    uint32_t width;
    uint32_t height;
    uint32_t rawWidth;
    uint32_t rawHeight;
    uint32_t visibleWidth;
    uint32_t visibleHeight;
    uint32_t orientation;
    uint32_t blackLevels[4];
    uint32_t whiteLevels[4];
    float exposureScale;
    char make[64];
    char model[64];
};

struct RawDecodedImage;

enum RawDecoderStatus : int32_t
{
    RawDecoderSuccess = 0,
    RawDecoderUnsupported = 1,
    RawDecoderCorrupt = 2,
    RawDecoderTooLarge = 3,
    RawDecoderCancelled = 4,
    RawDecoderFailed = 5
};

extern "C"
{
    RAWDECODER_API int32_t RAWDECODER_CALL raw_decoder_decode(
        const void* input,
        uint64_t inputLength,
        const volatile int32_t* cancellationFlag,
        RawDecodedImage** result,
        RawDecoderMetadata* metadata,
        char* errorMessage,
        uint32_t errorMessageCapacity);

    RAWDECODER_API const float* RAWDECODER_CALL raw_decoder_pixels(
        const RawDecodedImage* image);

    RAWDECODER_API void RAWDECODER_CALL raw_decoder_destroy(RawDecodedImage* image);
}
