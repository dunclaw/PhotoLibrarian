#include "RawDecoder.h"

#include <libraw/libraw.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <vector>

namespace
{
    constexpr uint64_t MaximumInputBytes = 512ull * 1024 * 1024;
    constexpr uint64_t MaximumPixels = 24ull * 1024 * 1024;
    constexpr int MaximumRawMemoryMegabytes = 512;
    constexpr float ReferenceExposureScale = 0.25f;

    void SetError(char* destination, uint32_t capacity, const char* message)
    {
        if (destination == nullptr || capacity == 0)
            return;

        const size_t length = std::min<size_t>(std::strlen(message), capacity - 1);
        std::memcpy(destination, message, length);
        destination[length] = '\0';
    }

    void SetLibRawError(
        char* destination,
        uint32_t capacity,
        const char* stage,
        int errorCode)
    {
        SetError(
            destination,
            capacity,
            (std::string(stage) + ": " + LibRaw::strerror(errorCode)).c_str());
    }

    int CancelIfRequested(void* context, LibRaw_progress, int, int)
    {
        const auto* cancellationFlag = static_cast<const volatile int32_t*>(context);
        return cancellationFlag != nullptr && *cancellationFlag != 0 ? 1 : 0;
    }

    bool HasExceededPixelLimit(uint64_t width, uint64_t height)
    {
        return width == 0 || height == 0 || width > MaximumPixels / height;
    }

    uint32_t GetExifOrientation(int rawFlip)
    {
        constexpr std::array<uint32_t, 8> ExifOrientationByLibRawFlip = { 1, 2, 4, 3, 5, 8, 6, 7 };
        return rawFlip >= 0 && rawFlip < static_cast<int>(ExifOrientationByLibRawFlip.size())
            ? ExifOrientationByLibRawFlip[static_cast<size_t>(rawFlip)]
            : 1;
    }

    void CopyText(char* destination, size_t capacity, const char* value)
    {
        if (capacity == 0)
            return;

        const char* text = value == nullptr ? "" : value;
        const size_t length = std::min(capacity - 1, std::strlen(text));
        std::memcpy(destination, text, length);
        destination[length] = '\0';
    }

    void SetSensorLevels(const libraw_colordata_t& color, RawDecoderMetadata& metadata)
    {
        for (size_t channel = 0; channel < 4; ++channel)
        {
            const uint64_t black = static_cast<uint64_t>(color.black) + color.cblack[channel];
            metadata.blackLevels[channel] = static_cast<uint32_t>(
                std::min<uint64_t>(black, std::numeric_limits<uint32_t>::max()));

            const uint32_t dngWhite = color.dng_levels.dng_whitelevel[channel];
            const uint32_t linearWhite = color.linear_max[channel];
            metadata.whiteLevels[channel] =
                dngWhite != 0 ? dngWhite :
                linearWhite != 0 ? linearWhite :
                color.maximum;
        }
    }
}

struct RawDecodedImage
{
    std::vector<float> pixels;
};

extern "C" int32_t RAWDECODER_CALL raw_decoder_decode(
    const void* input,
    uint64_t inputLength,
    const volatile int32_t* cancellationFlag,
    RawDecodedImage** result,
    RawDecoderMetadata* metadata,
    char* errorMessage,
    uint32_t errorMessageCapacity)
{
    if (result != nullptr)
        *result = nullptr;

    if (metadata != nullptr)
        std::memset(metadata, 0, sizeof(*metadata));

    if (input == nullptr || inputLength == 0 || result == nullptr || metadata == nullptr)
    {
        SetError(errorMessage, errorMessageCapacity, "The RAW input or output pointer is invalid.");
        return RawDecoderFailed;
    }

    if (inputLength > MaximumInputBytes)
    {
        SetError(errorMessage, errorMessageCapacity, "The RAW file exceeds the 512 MiB input limit.");
        return RawDecoderTooLarge;
    }

    if (cancellationFlag != nullptr && *cancellationFlag != 0)
    {
        SetError(errorMessage, errorMessageCapacity, "RAW decoding was cancelled.");
        return RawDecoderCancelled;
    }

    try
    {
        LibRaw decoder;
        decoder.imgdata.rawparams.max_raw_memory_mb = MaximumRawMemoryMegabytes;
        decoder.set_progress_handler(CancelIfRequested, const_cast<int32_t*>(cancellationFlag));

        int status = decoder.open_buffer(input, static_cast<size_t>(inputLength));
        if (status != LIBRAW_SUCCESS)
        {
            SetLibRawError(errorMessage, errorMessageCapacity, "LibRaw open_buffer", status);
            return status == LIBRAW_FILE_UNSUPPORTED ? RawDecoderUnsupported : RawDecoderCorrupt;
        }

        const auto& sourceSizes = decoder.imgdata.sizes;
        if (decoder.imgdata.idata.raw_count == 0)
        {
            SetError(errorMessage, errorMessageCapacity, "The file contains no supported sensor RAW image.");
            return RawDecoderUnsupported;
        }

        if (HasExceededPixelLimit(sourceSizes.raw_width, sourceSizes.raw_height))
        {
            SetError(errorMessage, errorMessageCapacity, "The sensor dimensions exceed the 24-megapixel decode limit.");
            return RawDecoderTooLarge;
        }

        metadata->rawWidth = sourceSizes.raw_width;
        metadata->rawHeight = sourceSizes.raw_height;
        metadata->visibleWidth = sourceSizes.width;
        metadata->visibleHeight = sourceSizes.height;
        metadata->orientation = GetExifOrientation(sourceSizes.flip);
        CopyText(metadata->make, sizeof(metadata->make), decoder.imgdata.idata.make);
        CopyText(metadata->model, sizeof(metadata->model), decoder.imgdata.idata.model);

        status = decoder.unpack();
        if (status != LIBRAW_SUCCESS)
        {
            SetLibRawError(errorMessage, errorMessageCapacity, "LibRaw unpack", status);
            return status == LIBRAW_CANCELLED_BY_CALLBACK ? RawDecoderCancelled : RawDecoderCorrupt;
        }
        if (cancellationFlag != nullptr && *cancellationFlag != 0)
        {
            SetError(errorMessage, errorMessageCapacity, "RAW decoding was cancelled.");
            return RawDecoderCancelled;
        }

        SetSensorLevels(decoder.imgdata.color, *metadata);

        auto& parameters = decoder.imgdata.params;
        parameters.output_bps = 16;
        parameters.output_color = 4;
        parameters.gamm[0] = 1.0;
        parameters.gamm[1] = 1.0;
        parameters.no_auto_bright = 1;
        parameters.use_auto_wb = 0;
        parameters.use_camera_wb = 1;
        parameters.use_camera_matrix = 1;
        parameters.user_qual = 3;
        parameters.user_flip = -1;
        parameters.highlight = 0;
        parameters.exp_correc = 1;
        parameters.exp_shift = ReferenceExposureScale;
        parameters.exp_preser = 0.0f;
        parameters.adjust_maximum_thr = 0.0f;
        parameters.output_color = 4;

        status = decoder.dcraw_process();
        if (status != LIBRAW_SUCCESS)
        {
            SetLibRawError(errorMessage, errorMessageCapacity, "LibRaw dcraw_process", status);
            return status == LIBRAW_CANCELLED_BY_CALLBACK ? RawDecoderCancelled : RawDecoderCorrupt;
        }
        if (cancellationFlag != nullptr && *cancellationFlag != 0)
        {
            SetError(errorMessage, errorMessageCapacity, "RAW decoding was cancelled.");
            return RawDecoderCancelled;
        }

        int imageError = LIBRAW_SUCCESS;
        std::unique_ptr<libraw_processed_image_t, decltype(&LibRaw::dcraw_clear_mem)> processed(
            decoder.dcraw_make_mem_image(&imageError), &LibRaw::dcraw_clear_mem);
        if (imageError != LIBRAW_SUCCESS || processed == nullptr)
        {
            SetLibRawError(errorMessage, errorMessageCapacity, "LibRaw dcraw_make_mem_image", imageError);
            return RawDecoderCorrupt;
        }

        if (processed->type != LIBRAW_IMAGE_BITMAP || processed->bits != 16 || processed->colors != 3)
        {
            SetError(errorMessage, errorMessageCapacity, "LibRaw did not produce 16-bit, three-channel sensor-derived RGB.");
            return RawDecoderUnsupported;
        }

        const uint64_t width = processed->width;
        const uint64_t height = processed->height;
        if (HasExceededPixelLimit(width, height))
        {
            SetError(errorMessage, errorMessageCapacity, "The developed image exceeds the 24-megapixel decode limit.");
            return RawDecoderTooLarge;
        }

        const uint64_t sampleCount = width * height * 3;
        const uint64_t expectedByteCount = sampleCount * sizeof(uint16_t);
        if (expectedByteCount != processed->data_size ||
            sampleCount > std::numeric_limits<size_t>::max() / sizeof(float))
        {
            SetError(errorMessage, errorMessageCapacity, "The decoded image has an inconsistent or unsupported buffer size.");
            return RawDecoderCorrupt;
        }

        auto image = std::make_unique<RawDecodedImage>();
        image->pixels.resize(static_cast<size_t>(sampleCount));
        const auto* samples = reinterpret_cast<const uint16_t*>(processed->data);
        constexpr float inverseScale = 1.0f / (65535.0f * ReferenceExposureScale);
        for (uint64_t index = 0; index < sampleCount; ++index)
            image->pixels[static_cast<size_t>(index)] = samples[index] * inverseScale;

        metadata->width = processed->width;
        metadata->height = processed->height;
        metadata->exposureScale = ReferenceExposureScale;
        *result = image.release();
        return RawDecoderSuccess;
    }
    catch (const std::bad_alloc&)
    {
        SetError(errorMessage, errorMessageCapacity, "Not enough memory to develop this RAW image.");
        return RawDecoderTooLarge;
    }
    catch (const std::exception& exception)
    {
        SetError(errorMessage, errorMessageCapacity, exception.what());
        return RawDecoderFailed;
    }
    catch (...)
    {
        SetError(errorMessage, errorMessageCapacity, "An unexpected native error occurred during RAW decoding.");
        return RawDecoderFailed;
    }
}

extern "C" const float* RAWDECODER_CALL raw_decoder_pixels(const RawDecodedImage* image)
{
    return image == nullptr ? nullptr : image->pixels.data();
}

extern "C" void RAWDECODER_CALL raw_decoder_destroy(RawDecodedImage* image)
{
    delete image;
}
