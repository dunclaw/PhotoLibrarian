// Copyright (c) Duncan Lawler. All Rights Reserved.
#include <opencv2/calib3d.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/stitching.hpp>
#include <opencv2/stitching/detail/blenders.hpp>
#include <opencv2/stitching/detail/exposure_compensate.hpp>
#include <opencv2/stitching/detail/motion_estimators.hpp>
#include <opencv2/stitching/detail/seam_finders.hpp>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <vector>

namespace
{
    constexpr int max_sources = 64;
    constexpr int64_t max_decoded_pixels = 48000000;
    constexpr int64_t max_output_pixels = 16000000;
    constexpr int max_output_dimension = 32000;
    constexpr double confidence_threshold = 1.0;
    struct Cancelled {};
    struct Disconnected : std::runtime_error
    {
        uint64_t missing;
        explicit Disconnected(uint64_t missing) :
            std::runtime_error("Not all selected photos have reliable overlap. No photos were omitted."),
            missing(missing) {}
    };
    using Progress = int(__cdecl*)(int stage, int percent);

    struct Input
    {
        const uint8_t* pixels;
        int width;
        int height;
    };

    struct Source
    {
        double transform[9];
        double intrinsic[9];
        double rotation[9];
        double gains[3];
    };

    struct Output
    {
        const uint8_t* pixels;
        const Source* sources;
        int width;
        int height;
        int matches;
        int inliers;
        int spherical;
        int offset_x;
        int offset_y;
        double warp_scale;
        double output_scale;
        double retained_area;
        uint64_t missing_sources;
    };

    struct Result
    {
        cv::Mat pixels;
        std::vector<Source> sources;
        Output info{};
    };

    void report(Progress progress, int stage, int percent)
    {
        if (progress && progress(stage, percent) != 0)
            throw Cancelled{};
    }

    cv::Point2d project(const cv::Mat& matrix, cv::Point2d point)
    {
        const double denominator = matrix.at<double>(2, 0) * point.x +
            matrix.at<double>(2, 1) * point.y + matrix.at<double>(2, 2);
        if (!std::isfinite(denominator) || std::abs(denominator) < 1e-9)
            throw std::runtime_error("The alignment projects an image to infinity.");
        return {
            (matrix.at<double>(0, 0) * point.x + matrix.at<double>(0, 1) * point.y +
                matrix.at<double>(0, 2)) / denominator,
            (matrix.at<double>(1, 0) * point.x + matrix.at<double>(1, 1) * point.y +
                matrix.at<double>(1, 2)) / denominator };
    }

    cv::Mat translate(double x, double y)
    {
        return (cv::Mat_<double>(3, 3) << 1, 0, x, 0, 1, y, 0, 0, 1);
    }

    cv::Mat actual_homography(
        const cv::detail::MatchesInfo& match, const std::vector<cv::detail::ImageFeatures>& features)
    {
        const auto a = features[match.src_img_idx].img_size;
        const auto b = features[match.dst_img_idx].img_size;
        return translate(b.width * 0.5, b.height * 0.5) * match.H *
            translate(-a.width * 0.5, -a.height * 0.5);
    }

    bool sufficient_coverage(
        const cv::detail::MatchesInfo& match, const std::vector<cv::detail::ImageFeatures>& features)
    {
        for (int side = 0; side < 2; ++side)
        {
            const auto& feature = features[side == 0 ? match.src_img_idx : match.dst_img_idx];
            double min_x = feature.img_size.width, min_y = feature.img_size.height, max_x = 0, max_y = 0;
            for (size_t index = 0; index < match.matches.size(); ++index)
            {
                if (!match.inliers_mask[index])
                    continue;
                const auto& pair = match.matches[index];
                const auto point = feature.keypoints[side == 0 ? pair.queryIdx : pair.trainIdx].pt;
                min_x = std::min(min_x, static_cast<double>(point.x));
                max_x = std::max(max_x, static_cast<double>(point.x));
                min_y = std::min(min_y, static_cast<double>(point.y));
                max_y = std::max(max_y, static_cast<double>(point.y));
            }
            if (max_x - min_x < feature.img_size.width * 0.08 ||
                max_y - min_y < feature.img_size.height * 0.08)
                return false;
        }
        return true;
    }

    bool conflicting_planes(
        const cv::detail::MatchesInfo& match, const std::vector<cv::detail::ImageFeatures>& features)
    {
        std::vector<cv::Point2f> first, second;
        for (size_t index = 0; index < match.matches.size(); ++index)
        {
            if (match.inliers_mask[index])
                continue;
            const auto& pair = match.matches[index];
            first.push_back(features[match.src_img_idx].keypoints[pair.queryIdx].pt);
            second.push_back(features[match.dst_img_idx].keypoints[pair.trainIdx].pt);
        }
        const auto support = std::max<size_t>(12, static_cast<size_t>(std::ceil(match.matches.size() * 0.2)));
        if (first.size() < support)
            return false;
        std::vector<uint8_t> inliers;
        const auto alternate = cv::findHomography(first, second, cv::RANSAC, 3, inliers, 500);
        if (alternate.empty() || cv::countNonZero(inliers) < static_cast<int>(support))
            return false;
        cv::detail::MatchesInfo competing;
        competing.src_img_idx = match.src_img_idx;
        competing.dst_img_idx = match.dst_img_idx;
        competing.matches = match.matches;
        competing.inliers_mask.assign(match.matches.size(), 0);
        size_t outlier = 0;
        double error = 0;
        const auto primary = actual_homography(match, features);
        for (size_t index = 0; index < match.matches.size(); ++index)
        {
            if (match.inliers_mask[index])
                continue;
            if (inliers[outlier])
            {
                competing.inliers_mask[index] = 1;
                error += cv::norm(project(primary, first[outlier]) - cv::Point2d(second[outlier]));
            }
            ++outlier;
        }
        // Graph-cut seams accommodate modest local motion; a second widely displaced plane is unsafe.
        return sufficient_coverage(competing, features) && error / cv::countNonZero(inliers) >
            std::max(16.0, std::max(features[match.dst_img_idx].img_size.width,
                features[match.dst_img_idx].img_size.height) * 0.1);
    }

    cv::Rect largest_rectangle(const cv::Mat& mask, Progress progress)
    {
        std::vector<int> heights(mask.cols), stack(mask.cols);
        cv::Rect best;
        for (int y = 0; y < mask.rows; ++y)
        {
            if (y % 32 == 0)
                report(progress, 6, 96);
            const auto row = mask.ptr<uint8_t>(y);
            for (int x = 0; x < mask.cols; ++x)
                heights[x] = row[x] ? heights[x] + 1 : 0;
            int top = -1;
            for (int x = 0; x <= mask.cols; ++x)
            {
                const int height = x == mask.cols ? 0 : heights[x];
                while (top >= 0 && heights[stack[top]] > height)
                {
                    const int rectangle_height = heights[stack[top--]];
                    const int left = top < 0 ? 0 : stack[top] + 1;
                    if (static_cast<int64_t>(x - left) * rectangle_height > best.area())
                        best = { left, y - rectangle_height + 1, x - left, rectangle_height };
                }
                if (x < mask.cols)
                    stack[++top] = x;
            }
        }
        return best;
    }

    class Geometry
    {
    public:
        bool spherical = false;
        double focal = 1;
        std::vector<cv::Mat> matrices;
        std::vector<cv::detail::CameraParams> cameras;

        cv::Ptr<cv::detail::RotationWarper> warper(double scale) const
        {
            return cv::makePtr<cv::SphericalWarper>()->create(static_cast<float>(focal * scale));
        }

        cv::Point2d point(int index, cv::Point2d point, double scale) const
        {
            if (!spherical)
                return project(matrices[index], point) * scale;
            cv::Mat intrinsic;
            cameras[index].K().convertTo(intrinsic, CV_32F);
            return warper(scale)->warpPoint(point, intrinsic, cameras[index].R);
        }

        cv::Rect roi(int index, cv::Size size, double scale) const
        {
            if (spherical)
            {
                cv::Mat intrinsic;
                cameras[index].K().convertTo(intrinsic, CV_32F);
                return warper(scale)->warpRoi(size, intrinsic, cameras[index].R);
            }
            std::vector<cv::Point2d> points;
            for (const auto& p : std::vector<cv::Point2d>{
                {0, 0}, {static_cast<double>(size.width - 1), 0},
                {static_cast<double>(size.width - 1), static_cast<double>(size.height - 1)},
                {0, static_cast<double>(size.height - 1)}})
            {
                const auto& matrix = matrices[index];
                const double denominator = matrix.at<double>(2, 0) * p.x +
                    matrix.at<double>(2, 1) * p.y + matrix.at<double>(2, 2);
                if (denominator * matrix.at<double>(2, 2) <= 0)
                    throw std::runtime_error("The alignment crosses the projective horizon.");
                points.push_back(point(index, p, scale));
            }
            double area = 0;
            for (size_t corner = 0; corner < points.size(); ++corner)
            {
                const auto a = points[corner], b = points[(corner + 1) % points.size()];
                area += a.x * b.y - a.y * b.x;
            }
            const double area_ratio = area / (2 * scale * scale * (size.width - 1.0) * (size.height - 1.0));
            if (!std::isfinite(area_ratio) || area_ratio < 0.025 || area_ratio > 40)
                throw std::runtime_error("The alignment is reflected, collapsed, or excessively stretched.");
            double min_x = points[0].x, min_y = points[0].y, max_x = min_x, max_y = min_y;
            for (auto p : points)
            {
                min_x = std::min(min_x, p.x); max_x = std::max(max_x, p.x);
                min_y = std::min(min_y, p.y); max_y = std::max(max_y, p.y);
            }
            if (!std::isfinite(min_x + min_y + max_x + max_y) ||
                std::max({std::abs(min_x), std::abs(min_y), std::abs(max_x), std::abs(max_y)}) > 10000000)
                throw std::runtime_error("The panorama geometry is unstable or excessively stretched.");
            return { static_cast<int>(std::floor(min_x)), static_cast<int>(std::floor(min_y)),
                static_cast<int>(std::ceil(max_x) - std::floor(min_x) + 1),
                static_cast<int>(std::ceil(max_y) - std::floor(min_y) + 1) };
        }

        cv::Mat warp(int index, const cv::Mat& image, double scale, int interpolation, int border) const
        {
            cv::Mat output;
            if (spherical)
            {
                cv::Mat intrinsic;
                cameras[index].K().convertTo(intrinsic, CV_32F);
                warper(scale)->warp(image, intrinsic, cameras[index].R, interpolation, border, output);
            }
            else
            {
                const auto bounds = roi(index, image.size(), scale);
                cv::Mat scaling = (cv::Mat_<double>(3, 3) << scale, 0, 0, 0, scale, 0, 0, 0, 1);
                const cv::Mat transform = translate(-bounds.x, -bounds.y) * scaling * matrices[index];
                cv::warpPerspective(image, output, transform, bounds.size(), interpolation, border);
            }
            return output;
        }
    };

    Geometry estimate_geometry(
        const std::vector<cv::detail::ImageFeatures>& features,
        const std::vector<cv::detail::MatchesInfo>& matches, double work_scale, Progress progress)
    {
        const int count = static_cast<int>(features.size());
        std::vector<bool> placed(count);
        std::vector<cv::Mat> matrices(count);
        matrices[0] = cv::Mat::eye(3, 3, CV_64F);
        placed[0] = true;
        bool spherical = false;
        for (int remaining = count - 1; remaining > 0; --remaining)
        {
            const cv::detail::MatchesInfo* best = nullptr;
            for (const auto& match : matches)
            {
                if (match.src_img_idx < 0 || match.dst_img_idx < 0 ||
                    match.confidence < confidence_threshold ||
                    placed[match.src_img_idx] == placed[match.dst_img_idx])
                    continue;
                if (!best || match.num_inliers > best->num_inliers)
                    best = &match;
            }
            if (!best)
            {
                uint64_t missing = 0;
                for (int index = 0; index < count; ++index)
                    if (!placed[index])
                        missing |= uint64_t{1} << index;
                throw Disconnected(missing);
            }
            const auto& match = *best;
            cv::Mat homography = actual_homography(match, features);
            const double normalizer = std::abs(match.H.at<double>(2, 2));
            const auto size = features[match.src_img_idx].img_size;
            spherical |= (std::abs(match.H.at<double>(2, 0)) * size.width +
                std::abs(match.H.at<double>(2, 1)) * size.height) / normalizer > 0.1;
            if ((std::abs(match.H.at<double>(2, 0)) * size.width +
                std::abs(match.H.at<double>(2, 1)) * size.height) / normalizer < 0.01)
            {
                std::vector<cv::Point2f> a, b;
                for (size_t index = 0; index < match.matches.size(); ++index)
                {
                    if (!match.inliers_mask[index])
                        continue;
                    a.push_back(features[match.src_img_idx].keypoints[match.matches[index].queryIdx].pt);
                    b.push_back(features[match.dst_img_idx].keypoints[match.matches[index].trainIdx].pt);
                }
                const cv::Mat similarity = cv::estimateAffinePartial2D(a, b);
                if (!similarity.empty())
                {
                    similarity.copyTo(homography(cv::Rect(0, 0, 3, 2)));
                    homography.at<double>(2, 0) = homography.at<double>(2, 1) = 0;
                    homography.at<double>(2, 2) = 1;
                }
            }
            if (placed[match.src_img_idx])
            {
                matrices[match.dst_img_idx] = matrices[match.src_img_idx] * homography.inv();
                placed[match.dst_img_idx] = true;
            }
            else
            {
                matrices[match.src_img_idx] = matrices[match.dst_img_idx] * homography;
                placed[match.src_img_idx] = true;
            }
        }

        report(progress, 3, 56);
        Geometry geometry;
        geometry.spherical = spherical;
        if (spherical)
        {
            cv::detail::HomographyBasedEstimator estimator;
            if (!estimator(features, matches, geometry.cameras))
                throw std::runtime_error("Camera rotation estimation failed.");
            for (auto& camera : geometry.cameras)
                camera.R.convertTo(camera.R, CV_32F);
            cv::detail::BundleAdjusterRay adjuster;
            adjuster.setConfThresh(confidence_threshold);
            adjuster.setTermCriteria({cv::TermCriteria::COUNT | cv::TermCriteria::EPS, 100, 1e-6});
            if (!adjuster(features, matches, geometry.cameras))
                throw std::runtime_error("The camera alignment could not be refined reliably.");
            std::vector<double> focals;
            for (auto& camera : geometry.cameras)
            {
                if (!cv::checkRange(camera.R) || !std::isfinite(camera.focal) ||
                    cv::norm(camera.R.t() * camera.R - cv::Mat::eye(3, 3, CV_32F)) > 0.05 ||
                    std::abs(cv::determinant(camera.R) - 1) > 0.05 ||
                    camera.focal < 0.1 * features[0].img_size.width ||
                    camera.focal > 100 * features[0].img_size.width)
                    throw std::runtime_error("The estimated camera calibration is unstable.");
                camera.focal /= work_scale;
                camera.ppx /= work_scale;
                camera.ppy /= work_scale;
                focals.push_back(camera.focal);
            }
            std::sort(focals.begin(), focals.end());
            geometry.focal = focals[focals.size() / 2];
        }
        else
        {
            const cv::Mat down = (cv::Mat_<double>(3, 3) << work_scale, 0, 0, 0, work_scale, 0, 0, 0, 1);
            const cv::Mat up = down.inv();
            for (const auto& matrix : matrices)
                geometry.matrices.push_back(up * matrix * down);
        }

        // Check the final camera model, not just the independently fitted pair homographies.
        for (const auto& match : matches)
        {
            if (match.src_img_idx < 0 || match.dst_img_idx < 0 ||
                match.confidence < confidence_threshold)
                continue;
            std::vector<double> errors;
            for (size_t index = 0; index < match.matches.size(); ++index)
            {
                if (!match.inliers_mask[index])
                    continue;
                const auto& pair = match.matches[index];
                const auto a = features[match.src_img_idx].keypoints[pair.queryIdx].pt;
                const auto b = features[match.dst_img_idx].keypoints[pair.trainIdx].pt;
                errors.push_back(cv::norm(geometry.point(match.src_img_idx, cv::Point2d(a) / work_scale, 1) -
                    geometry.point(match.dst_img_idx, cv::Point2d(b) / work_scale, 1)) * work_scale);
            }
            std::sort(errors.begin(), errors.end());
            if (errors.empty() || errors[errors.size() * 4 / 5] > 6)
                throw std::runtime_error("Overlapping photos disagree after camera alignment (drift or parallax).");
        }
        return geometry;
    }

    std::unique_ptr<Result> stitch(const Input* inputs, int count, Progress progress)
    {
        if (!inputs || count < 2 || count > max_sources)
            throw std::runtime_error("Supply 2 to 64 photos.");
        int64_t decoded_pixels = 0;
        double largest_area = 0;
        std::vector<cv::Mat> images;
        for (int index = 0; index < count; ++index)
        {
            const auto& input = inputs[index];
            decoded_pixels += static_cast<int64_t>(input.width) * input.height;
            if (!input.pixels || input.width < 32 || input.height < 32 ||
                input.width > 2400 || input.height > 2400 || decoded_pixels > max_decoded_pixels)
                throw std::runtime_error("Invalid source dimensions or decoded memory limit exceeded.");
            cv::Mat image(input.height, input.width, CV_8UC3);
            for (int y = 0; y < input.height; ++y)
            {
                report(progress, 1, 15 + index * 10 / count);
                auto destination = image.ptr<cv::Vec3b>(y);
                const auto source = input.pixels + static_cast<size_t>(y) * input.width * 4;
                for (int x = 0; x < input.width; ++x)
                    for (int channel = 0; channel < 3; ++channel)
                        destination[x][channel] = static_cast<uint8_t>(
                            std::min(255, source[x * 4 + channel] + 255 - source[x * 4 + 3]));
            }
            largest_area = std::max(largest_area, static_cast<double>(image.total()));
            images.push_back(image);
        }
        const double work_scale = std::min(1.0, std::sqrt(600000.0 / largest_area));
        std::vector<cv::detail::ImageFeatures> features(count);
        const auto finder = cv::SIFT::create(3000);
        for (int index = 0; index < count; ++index)
        {
            report(progress, 1, 15 + index * 10 / count);
            cv::Mat resized;
            cv::resize(images[index], resized, {}, work_scale, work_scale, cv::INTER_AREA);
            cv::detail::computeImageFeatures(finder, resized, features[index]);
            features[index].img_idx = index;
            if (features[index].keypoints.size() < 12)
                throw std::runtime_error("A selected photo has too little distinct detail to align safely.");
        }
        std::vector<cv::detail::MatchesInfo> matches(count * count);
        cv::detail::BestOf2NearestMatcher matcher(false, 0.3f, 12, 12, 10);
        int checked_pairs = 0, match_count = 0, inlier_count = 0;
        const int pair_count = count * (count - 1) / 2;
        for (int first = 0; first < count; ++first)
        {
            for (int second = first + 1; second < count; ++second)
            {
                report(progress, 2, 25 + checked_pairs++ * 30 / pair_count);
                auto& forward = matches[first * count + second];
                matcher(features[first], features[second], forward);
                forward.src_img_idx = first;
                forward.dst_img_idx = second;
                if (forward.H.empty() || forward.num_inliers < 12 ||
                    forward.num_inliers < forward.matches.size() * 0.3 ||
                    forward.confidence < confidence_threshold || !sufficient_coverage(forward, features))
                {
                    forward = cv::detail::MatchesInfo();
                    continue;
                }
                if (conflicting_planes(forward, features))
                    throw std::runtime_error("The photos contain conflicting parallax planes that cannot align safely.");
                match_count += static_cast<int>(forward.matches.size());
                inlier_count += forward.num_inliers;
                auto& reverse = matches[second * count + first];
                reverse = forward;
                reverse.src_img_idx = second;
                reverse.dst_img_idx = first;
                reverse.H = forward.H.inv();
                for (auto& pair : reverse.matches)
                    std::swap(pair.queryIdx, pair.trainIdx);
            }
        }
        matcher.collectGarbage();
        const auto geometry = estimate_geometry(features, matches, work_scale, progress);
        features.clear();
        matches.clear();

        std::vector<cv::Point> corners(count);
        std::vector<cv::Size> sizes(count);
        const auto measure = [&](double scale)
        {
            int64_t warped_pixels = 0;
            for (int index = 0; index < count; ++index)
            {
                const auto bounds = geometry.roi(index, images[index].size(), scale);
                corners[index] = bounds.tl();
                sizes[index] = bounds.size();
                warped_pixels += static_cast<int64_t>(bounds.width) * bounds.height;
            }
            return std::pair<cv::Rect, int64_t>{cv::detail::resultRoi(corners, sizes), warped_pixels};
        };
        auto bounds = measure(1);
        double output_scale = std::min({1.0,
            (max_output_dimension - 32.0) / std::max(bounds.first.width, bounds.first.height),
            std::sqrt((max_output_pixels - 100000.0) /
                (static_cast<double>(bounds.first.width) * bounds.first.height)),
            std::sqrt(64000000.0 / bounds.second)});
        if (!std::isfinite(output_scale) || output_scale < 0.02)
            throw std::runtime_error("The panorama would be excessively stretched.");
        const double seam_scale = std::min(output_scale, std::sqrt(1500000.0 / decoded_pixels));
        auto seam_bounds = measure(seam_scale);
        if (seam_bounds.second > 4000000)
            throw std::runtime_error("The warped seam images exceed the working memory limit.");
        std::vector<cv::UMat> seam_images(count), seam_masks(count), seam_float(count);
        std::vector<cv::Point> seam_corners = corners;
        for (int index = 0; index < count; ++index)
        {
            report(progress, 4, 60 + index * 8 / count);
            geometry.warp(index, images[index], seam_scale, cv::INTER_LINEAR, cv::BORDER_REFLECT)
                .copyTo(seam_images[index]);
            const cv::Mat mask(images[index].size(), CV_8U, cv::Scalar(255));
            geometry.warp(index, mask, seam_scale, cv::INTER_NEAREST, cv::BORDER_CONSTANT)
                .copyTo(seam_masks[index]);
            seam_images[index].convertTo(seam_float[index], CV_32F);
        }
        cv::detail::ChannelsCompensator compensator;
        static_cast<cv::detail::ExposureCompensator&>(compensator).feed(
            seam_corners, seam_images, seam_masks);
        auto gains = compensator.gains();
        for (const auto& gain : gains)
            for (int channel = 0; channel < 3; ++channel)
                if (!std::isfinite(gain[channel]) || gain[channel] < 0.25 || gain[channel] > 4)
                    throw std::runtime_error("The exposure difference is too large to blend reliably.");
        report(progress, 4, 69);
        cv::detail::GraphCutSeamFinder seam_finder(cv::detail::GraphCutSeamFinderBase::COST_COLOR_GRAD);
        seam_finder.find(seam_float, seam_corners, seam_masks);
        seam_float.clear();
        seam_images.clear();

        report(progress, 5, 73);
        bounds = measure(output_scale);
        const auto panorama_bounds = bounds.first;
        if (panorama_bounds.width > max_output_dimension || panorama_bounds.height > max_output_dimension ||
            static_cast<int64_t>(panorama_bounds.width) * panorama_bounds.height > max_output_pixels ||
            bounds.second > 65000000)
            throw std::runtime_error("The aligned panorama exceeds the output memory limit.");
        cv::detail::MultiBandBlender blender(false, 5);
        blender.prepare(panorama_bounds);
        for (int index = 0; index < count; ++index)
        {
            report(progress, 5, 74 + index * 19 / count);
            cv::Mat warped = geometry.warp(index, images[index], output_scale, cv::INTER_LINEAR, cv::BORDER_REFLECT);
            const cv::Mat mask(images[index].size(), CV_8U, cv::Scalar(255));
            cv::Mat warped_mask = geometry.warp(index, mask, output_scale, cv::INTER_NEAREST, cv::BORDER_CONSTANT);
            compensator.apply(index, corners[index], warped, warped_mask);
            cv::Mat dilated, seam_mask;
            cv::dilate(seam_masks[index].getMat(cv::ACCESS_READ), dilated, {});
            cv::resize(dilated, seam_mask, warped_mask.size(), 0, 0, cv::INTER_LINEAR);
            cv::bitwise_and(warped_mask, seam_mask, warped_mask);
            cv::Mat signed_image;
            warped.convertTo(signed_image, CV_16S);
            blender.feed(signed_image, warped_mask, corners[index]);
        }
        cv::Mat composed, mask;
        blender.blend(composed, mask);
        const auto crop = largest_rectangle(mask, progress);
        if (crop.width < 64 || crop.height < 64 ||
            static_cast<double>(crop.area()) / panorama_bounds.area() < 0.35)
            throw std::runtime_error("The alignment leaves too little clean, rectangular image area.");
        auto result = std::make_unique<Result>();
        cv::Mat bytes;
        composed(crop).convertTo(bytes, CV_8U);
        cv::cvtColor(bytes, result->pixels, cv::COLOR_BGR2BGRA);
        result->sources.resize(count);
        for (int index = 0; index < count; ++index)
        {
            int retained = 0;
            for (int y = 1; y <= 5; ++y)
                for (int x = 1; x <= 5; ++x)
                {
                    auto p = geometry.point(index, {inputs[index].width * x / 6.0,
                        inputs[index].height * y / 6.0}, output_scale);
                    p -= cv::Point2d(panorama_bounds.tl() + crop.tl());
                    retained += p.x >= 0 && p.y >= 0 && p.x < crop.width && p.y < crop.height;
                }
            if (retained < 3)
                throw std::runtime_error("The clean crop would discard a selected photo. No photo was silently omitted.");
            auto& source = result->sources[index];
            for (int channel = 0; channel < 3; ++channel)
                source.gains[channel] = gains[index][channel];
            if (geometry.spherical)
            {
                cv::Mat intrinsic = geometry.cameras[index].K();
                cv::Mat rotation;
                geometry.cameras[index].R.convertTo(rotation, CV_64F);
                std::memcpy(source.intrinsic, intrinsic.ptr<double>(), 9 * sizeof(double));
                std::memcpy(source.rotation, rotation.ptr<double>(), 9 * sizeof(double));
            }
            else
            {
                const cv::Mat scaling = (cv::Mat_<double>(3, 3) << output_scale, 0, 0, 0, output_scale, 0, 0, 0, 1);
                const cv::Mat normalize = (cv::Mat_<double>(3, 3) <<
                    inputs[index].width - 1, 0, 0, 0, inputs[index].height - 1, 0, 0, 0, 1);
                const cv::Mat matrix = translate(-panorama_bounds.x - crop.x, -panorama_bounds.y - crop.y) *
                    scaling * geometry.matrices[index] * normalize;
                std::memcpy(source.transform, matrix.ptr<double>(), 9 * sizeof(double));
            }
        }
        result->info = { result->pixels.data, result->sources.data(), crop.width, crop.height,
            match_count, inlier_count, geometry.spherical ? 1 : 0,
            panorama_bounds.x + crop.x, panorama_bounds.y + crop.y, geometry.focal * output_scale,
            output_scale, static_cast<double>(crop.area()) / panorama_bounds.area() };
        report(progress, 7, 100);
        return result;
    }
}

extern "C" __declspec(dllexport) int photolibrarian_panorama_stitch(
    const Input* images, int count, Progress progress, void** handle, Output* output, char* error, int error_capacity)
{
    if (!handle || !output || !error || error_capacity < 1)
        return 1;
    *handle = nullptr;
    *output = {};
    error[0] = '\0';
    try
    {
        auto result = stitch(images, count, progress);
        *output = result->info;
        *handle = result.release();
        return 0;
    }
    catch (const Cancelled&)
    {
        return 2;
    }
    catch (const Disconnected& exception)
    {
        output->missing_sources = exception.missing;
        strncpy_s(error, error_capacity, exception.what(), _TRUNCATE);
        return 1;
    }
    catch (const std::exception& exception)
    {
        strncpy_s(error, error_capacity, exception.what(), _TRUNCATE);
        return 1;
    }
}

extern "C" __declspec(dllexport) void photolibrarian_panorama_free(void* handle)
{
    delete static_cast<Result*>(handle);
}
